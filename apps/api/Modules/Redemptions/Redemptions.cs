using Promo.Api.Data;
using Promo.Api.Messaging;
using Promo.Api.Modules.Campaigns;
using Promo.Api.Modules.Coupons;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Promo.Api.Modules.Redemptions;

public enum RedemptionStatus { Requested, Held, Redeemed, Revoked, Rejected }

public class Redemption
{
    public Guid Id { get; set; }
    public Guid CampaignId { get; set; }
    public string CampaignCode { get; set; } = "";
    public string Reference { get; set; } = "";
    public int Uses { get; set; }
    public long ValueCents { get; set; }
    public RedemptionStatus Status { get; set; }
    public string? Reason { get; set; }
    public string? CouponNumber { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? UsesReleasedAt { get; set; }
}

public class RedemptionOptions
{
    /// <summary>Latency knob for the final mark. Compose sets it so the revocation race window is wide enough to hit on purpose.</summary>
    public int FinalMarkDelayMs { get; set; }
}

public class DomainException(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
}

public record CreateRedemptionRequest(string? CampaignCode, string? Reference, long Uses);

public record RedemptionDto(
    Guid Id, string CampaignCode, string Reference, int Uses, long ValueCents, string Status,
    string? Reason, string? CouponNumber, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    DateTimeOffset? UsesReleasedAt)
{
    public static RedemptionDto From(Redemption b) => new(
        b.Id, b.CampaignCode, b.Reference, b.Uses, b.ValueCents, b.Status.ToString(),
        b.Reason, b.CouponNumber, b.CreatedAt, b.UpdatedAt, b.UsesReleasedAt);
}

/// <summary>HTTP side of the saga: writes the redemption and its first outbox row in one local transaction.</summary>
public class RedemptionService(PromoDbContext db)
{
    public async Task<Redemption> CreateAsync(CreateRedemptionRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.CampaignCode))
            throw new DomainException(400, "Campaign code is required.");
        var error = RedemptionRules.Validate(request.Reference, request.Uses);
        if (error is not null)
            throw new DomainException(400, error);

        var campaign = await db.Campaigns.AsNoTracking().FirstOrDefaultAsync(s => s.Code == request.CampaignCode.Trim(), ct)
            ?? throw new DomainException(404, $"Campaign '{request.CampaignCode}' does not exist.");

        var uses = (int)request.Uses;
        var reference = request.Reference!.Trim();
        var now = DateTimeOffset.UtcNow;
        var redemption = new Redemption
        {
            Id = Guid.CreateVersion7(),
            CampaignId = campaign.Id,
            CampaignCode = campaign.Code,
            Reference = reference,
            Uses = uses,
            ValueCents = RedemptionRules.ValueCents(campaign.DiscountCents, uses),
            Status = RedemptionStatus.Requested,
            CreatedAt = now,
            UpdatedAt = now,
        };

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        db.Redemptions.Add(redemption);
        db.Enqueue("redemptions", Topology.ReserveKey, redemption.Id, new ReserveUses(redemption.Id, campaign.Id, uses, reference));
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            throw new DomainException(409, $"Reference '{reference}' has already redeemed {campaign.Code}.");
        }
        await tx.CommitAsync(ct);
        return redemption;
    }

    /// <summary>Moves Held or Redeemed to Revoked and asks Campaigns to give the uses back. The coupon is not touched.</summary>
    public async Task<Redemption> RevokeAsync(Guid id, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var now = DateTimeOffset.UtcNow;
        var moved = await db.Redemptions
            .Where(b => b.Id == id && (b.Status == RedemptionStatus.Held || b.Status == RedemptionStatus.Redeemed))
            .ExecuteUpdateAsync(s => s
                .SetProperty(b => b.Status, RedemptionStatus.Revoked)
                .SetProperty(b => b.Reason, "Revoked by request.")
                .SetProperty(b => b.UpdatedAt, now), ct);
        if (moved != 1)
        {
            var current = await db.Redemptions.AsNoTracking().FirstOrDefaultAsync(b => b.Id == id, ct)
                ?? throw new DomainException(404, "Redemption not found.");
            throw new DomainException(409, $"A redemption in status {current.Status} cannot be revoked.");
        }

        db.Enqueue("redemptions", Topology.ReleaseKey, id, new ReleaseUses(id, "revocation"));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return await db.Redemptions.AsNoTracking().FirstAsync(b => b.Id == id, ct);
    }
}

public enum MarkOutcome { Marked, Skipped, Duplicate }

/// <summary>Consumes the events that move a redemption forward. Each step runs only from the status it expects.</summary>
public class RedemptionsHandler(PromoDbContext db, IOptions<RedemptionOptions> options) : IMessageHandler
{
    public const string Name = "redemptions";

    public Task HandleAsync(Delivery d, CancellationToken ct) => d.RoutingKey switch
    {
        Topology.HeldKey => MarkHeldAsync(d.MessageId, Json.Parse<UsesHeld>(d.Body).RedemptionId, ct),
        Topology.ShortKey => MarkShortAsync(d.MessageId, Json.Parse<UsesShort>(d.Body), ct),
        Topology.IssuedKey => MarkRedeemedAsync(d.MessageId, Json.Parse<CouponIssued>(d.Body), ct),
        Topology.RejectedKey => MarkRejectedAsync(d.MessageId, Json.Parse<CouponRejected>(d.Body), ct),
        Topology.ReleasedKey => MarkReleasedAsync(d.MessageId, Json.Parse<UsesReleased>(d.Body), ct),
        _ => throw new InvalidOperationException($"Redemptions does not handle '{d.RoutingKey}'."),
    };

    public Task<MarkOutcome> MarkHeldAsync(Guid messageId, Guid redemptionId, CancellationToken ct = default) =>
        StepAsync(messageId, ct, () => db.Redemptions
            .Where(b => b.Id == redemptionId && b.Status == RedemptionStatus.Requested)
            .ExecuteUpdateAsync(s => s
                .SetProperty(b => b.Status, RedemptionStatus.Held)
                .SetProperty(b => b.UpdatedAt, DateTimeOffset.UtcNow), ct));

    public Task<MarkOutcome> MarkShortAsync(Guid messageId, UsesShort m, CancellationToken ct = default) =>
        StepAsync(messageId, ct, () => db.Redemptions
            .Where(b => b.Id == m.RedemptionId && b.Status == RedemptionStatus.Requested)
            .ExecuteUpdateAsync(s => s
                .SetProperty(b => b.Status, RedemptionStatus.Rejected)
                .SetProperty(b => b.Reason, $"Short: {m.RequestedUses} uses wanted, {m.UsesLeft} left.")
                .SetProperty(b => b.UpdatedAt, DateTimeOffset.UtcNow), ct));

    /// <summary>The final mark. If a revocation got there first the row is no longer Held and nothing changes.</summary>
    public async Task<MarkOutcome> MarkRedeemedAsync(Guid messageId, CouponIssued m, CancellationToken ct = default)
    {
        if (options.Value.FinalMarkDelayMs > 0)
            await Task.Delay(options.Value.FinalMarkDelayMs, ct);

        return await StepAsync(messageId, ct, () => db.Redemptions
            .Where(b => b.Id == m.RedemptionId && b.Status == RedemptionStatus.Held)
            .ExecuteUpdateAsync(s => s
                .SetProperty(b => b.Status, RedemptionStatus.Redeemed)
                .SetProperty(b => b.CouponNumber, m.Number)
                .SetProperty(b => b.UpdatedAt, DateTimeOffset.UtcNow), ct),
            redemptionIdForOrderCheck: m.RedemptionId);
    }

    public Task<MarkOutcome> MarkRejectedAsync(Guid messageId, CouponRejected m, CancellationToken ct = default) =>
        StepAsync(messageId, ct, () => db.Redemptions
            .Where(b => b.Id == m.RedemptionId && (b.Status == RedemptionStatus.Held || b.Status == RedemptionStatus.Requested))
            .ExecuteUpdateAsync(s => s
                .SetProperty(b => b.Status, RedemptionStatus.Rejected)
                .SetProperty(b => b.Reason, m.Reason)
                .SetProperty(b => b.UpdatedAt, DateTimeOffset.UtcNow), ct));

    public Task<MarkOutcome> MarkReleasedAsync(Guid messageId, UsesReleased m, CancellationToken ct = default) =>
        StepAsync(messageId, ct, () => db.Redemptions
            .Where(b => b.Id == m.RedemptionId && b.UsesReleasedAt == null)
            .ExecuteUpdateAsync(s => s
                .SetProperty(b => b.UsesReleasedAt, DateTimeOffset.UtcNow), ct));

    private async Task<MarkOutcome> StepAsync(Guid messageId, CancellationToken ct, Func<Task<int>> update, Guid? redemptionIdForOrderCheck = null)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (!await db.ClaimAsync(Name, messageId, ct))
        {
            await tx.CommitAsync(ct);
            return MarkOutcome.Duplicate;
        }

        var changed = await update();
        if (changed == 0 && redemptionIdForOrderCheck is { } id)
        {
            // Skipped for a reason we can name? Requested means the "Held" mark has not landed yet: retry, do not drop.
            var status = await db.Redemptions.AsNoTracking().Where(b => b.Id == id).Select(b => b.Status).FirstOrDefaultAsync(ct);
            if (status == RedemptionStatus.Requested)
                throw new OutOfOrderException("Final mark arrived before the Held mark.");
        }

        await tx.CommitAsync(ct);
        return changed == 1 ? MarkOutcome.Marked : MarkOutcome.Skipped;
    }
}
