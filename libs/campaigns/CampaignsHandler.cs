using Promo.Shared.Messaging;
using Microsoft.EntityFrameworkCore;

namespace Promo.Campaigns;

public enum ReserveOutcome { Held, Short, Duplicate }

/// <summary>
/// The only writer of Campaign.UsesLeft and of use holds.
/// The redemption is one conditional UPDATE: it matches only while the remaining Uses still covers the request.
/// </summary>
public class CampaignsHandler(CampaignsDbContext db) : IMessageHandler
{
    public const string Name = "campaigns";

    public Task HandleAsync(Delivery delivery, CancellationToken ct) => delivery.RoutingKey switch
    {
        Topology.ReserveKey => ReserveAsync(delivery.MessageId, Json.Parse<ReserveUses>(delivery.Body), ct),
        Topology.ReleaseKey => ReleaseAsync(delivery.MessageId, Json.Parse<ReleaseUses>(delivery.Body), ct),
        _ => throw new InvalidOperationException($"Campaigns does not handle '{delivery.RoutingKey}'."),
    };

    public async Task<ReserveOutcome> ReserveAsync(Guid messageId, ReserveUses command, CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (!await db.ClaimAsync(Name, messageId, ct))
        {
            await tx.CommitAsync(ct);
            return ReserveOutcome.Duplicate;
        }

        var updated = await db.Campaigns
            .Where(s => s.Id == command.CampaignId && s.UsesLeft >= command.Uses)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.UsesLeft, x => x.UsesLeft - command.Uses), ct);

        ReserveOutcome outcome;
        if (updated == 1)
        {
            db.Holds.Add(new UseHold
            {
                RedemptionId = command.RedemptionId,
                CampaignId = command.CampaignId,
                Uses = command.Uses,
                Status = HoldStatus.Held,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            db.Enqueue("campaigns", Topology.HeldKey, command.RedemptionId,
                new UsesHeld(command.RedemptionId, command.OwnerId, command.CampaignId, command.Uses, command.Reference));
            outcome = ReserveOutcome.Held;
        }
        else
        {
            var remaining = await db.Campaigns.Where(s => s.Id == command.CampaignId)
                .Select(s => s.UsesLeft).FirstOrDefaultAsync(ct);
            db.Enqueue("campaigns", Topology.ShortKey, command.RedemptionId,
                new UsesShort(command.RedemptionId, command.Reference, command.Uses, remaining));
            outcome = ReserveOutcome.Short;
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return outcome;
    }

    /// <summary>Gives held uses back exactly once. A second release for the same redemption changes nothing.</summary>
    public async Task<bool> ReleaseAsync(Guid messageId, ReleaseUses command, CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (!await db.ClaimAsync(Name, messageId, ct))
        {
            await tx.CommitAsync(ct);
            return false;
        }

        var hold = await db.Holds.AsNoTracking().FirstOrDefaultAsync(h => h.RedemptionId == command.RedemptionId, ct);
        var released = false;
        if (hold is not null)
        {
            var now = DateTimeOffset.UtcNow;
            var flipped = await db.Holds
                .Where(h => h.RedemptionId == command.RedemptionId && h.Status == HoldStatus.Held)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(h => h.Status, HoldStatus.Released)
                    .SetProperty(h => h.ReleasedAt, now)
                    .SetProperty(h => h.ReleaseReason, command.Reason), ct);
            if (flipped == 1)
            {
                await db.Campaigns
                    .Where(s => s.Id == hold.CampaignId && s.UsesLeft + hold.Uses <= s.UsesTotal)
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.UsesLeft, x => x.UsesLeft + hold.Uses), ct);
                db.Enqueue("campaigns", Topology.ReleasedKey, command.RedemptionId,
                    new UsesReleased(command.RedemptionId, hold.Uses, command.Reason));
                released = true;
            }
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return released;
    }
}
