using Promo.Shared.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Promo.Redemptions;

public enum MarkOutcome { Marked, Skipped, Duplicate }

/// <summary>Consumes the events that move a redemption forward. Each step runs only from the status it expects.</summary>
public class RedemptionsHandler(RedemptionsDbContext db, IOptions<RedemptionOptions> options) : IMessageHandler
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
