using Promo.Shared;
using Promo.Shared.Auth;
using Promo.Shared.Contracts;
using Promo.Shared.Messaging;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Promo.Redemptions;

/// <summary>HTTP side of the saga: writes the redemption and its first outbox row in one local transaction.</summary>
public class RedemptionService(RedemptionsDbContext db, ICampaignCatalog campaigns, ICurrentUser user)
{
    public async Task<Redemption> CreateAsync(CreateRedemptionRequest request, CancellationToken ct)
    {
        var ownerId = user.Id ?? throw new DomainException(401, "Sign in to book.");
        if (string.IsNullOrWhiteSpace(request.CampaignCode))
            throw new DomainException(400, "Campaign code is required.");
        var error = RedemptionRules.Validate(request.Reference, request.Uses);
        if (error is not null)
            throw new DomainException(400, error);

        var campaign = await campaigns.FindAsync(request.CampaignCode.Trim(), ct)
            ?? throw new DomainException(404, $"Campaign '{request.CampaignCode}' does not exist.");

        var uses = (int)request.Uses;
        var reference = request.Reference!.Trim();
        var now = DateTimeOffset.UtcNow;
        var redemption = new Redemption
        {
            Id = Guid.CreateVersion7(),
            OwnerId = ownerId,
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
        db.Enqueue("redemptions", Topology.ReserveKey, redemption.Id, new ReserveUses(redemption.Id, ownerId, campaign.Id, uses, reference));
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

    /// <summary>The owner or staff only. Anyone else gets a 404, so ids cannot be probed.</summary>
    public async Task<Redemption> GetAsync(Guid id, CancellationToken ct)
    {
        var redemption = await db.Redemptions.AsNoTracking().FirstOrDefaultAsync(b => b.Id == id, ct);
        if (redemption is null || !OwnerAccess.CanAccess(user, redemption.OwnerId))
            throw new DomainException(404, "Redemption not found.");
        return redemption;
    }

    public async Task<(List<Redemption> Items, int Total)> ListAsync(string? campaignCode, int page, int pageSize, CancellationToken ct)
    {
        var query = db.Redemptions.AsNoTracking();
        if (!user.IsStaff)
        {
            var me = user.Id ?? throw new DomainException(401, "Sign in to see redemptions.");
            query = query.Where(b => b.OwnerId == me);
        }
        if (!string.IsNullOrWhiteSpace(campaignCode))
            query = query.Where(b => b.CampaignCode == campaignCode);
        var total = await query.CountAsync(ct);
        var items = await query.OrderByDescending(b => b.CreatedAt).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return (items, total);
    }

    /// <summary>Moves Held or Redeemed to Revoked and asks Campaigns to give the uses back. The coupon is not touched.</summary>
    public async Task<Redemption> RevokeAsync(Guid id, CancellationToken ct)
    {
        await GetAsync(id, ct); // 404 unless the caller may see it

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
            var current = await db.Redemptions.AsNoTracking().FirstAsync(b => b.Id == id, ct);
            throw new DomainException(409, $"A redemption in status {current.Status} cannot be revoked.");
        }

        db.Enqueue("redemptions", Topology.ReleaseKey, id, new ReleaseUses(id, "revocation"));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return await db.Redemptions.AsNoTracking().FirstAsync(b => b.Id == id, ct);
    }
}
