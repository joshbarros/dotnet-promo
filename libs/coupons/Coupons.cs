using Promo.Shared;
using Promo.Shared.Messaging;
using Microsoft.EntityFrameworkCore;

namespace Promo.Coupons;

public enum CouponStatus { Issued, Rejected }

public class Coupon
{
    public Guid RedemptionId { get; set; }
    public Guid OwnerId { get; set; }
    public string? Number { get; set; }
    public CouponStatus Status { get; set; }
    public string? Reason { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public class CouponsDbContext(DbContextOptions<CouponsDbContext> options) : ModuleDbContext(options, SchemaName)
{
    public const string SchemaName = "coupons";

    public DbSet<Coupon> Coupons => Set<Coupon>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);

        b.Entity<Coupon>(e =>
        {
            e.ToTable("Coupons");
            e.HasKey(x => x.RedemptionId);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
        });
    }
}

public enum IssueOutcome { Issued, Rejected, Duplicate }

/// <summary>Issues the coupon once the uses are held. A poison reference is committed as rejected.</summary>
public class CouponsHandler(CouponsDbContext db) : IMessageHandler
{
    public const string Name = "coupons";

    public async Task HandleAsync(Delivery delivery, CancellationToken ct)
    {
        if (delivery.RoutingKey != Topology.HeldKey)
            throw new InvalidOperationException($"Coupons does not handle '{delivery.RoutingKey}'.");

        var outcome = await IssueAsync(delivery.MessageId, Json.Parse<UsesHeld>(delivery.Body), ct);
        if (outcome == IssueOutcome.Rejected)
            throw new PoisonMessageException("Reference carries the poison token; rejection is committed.");
    }

    public async Task<IssueOutcome> IssueAsync(Guid messageId, UsesHeld held, CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (!await db.ClaimAsync(Name, messageId, ct))
        {
            await tx.CommitAsync(ct);
            return IssueOutcome.Duplicate;
        }

        IssueOutcome outcome;
        if (PoisonRule.IsPoison(held.Reference))
        {
            const string reason = "Reference contains the poison token.";
            db.Coupons.Add(new Coupon
            {
                RedemptionId = held.RedemptionId,
                OwnerId = held.OwnerId,
                Status = CouponStatus.Rejected,
                Reason = reason,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            db.Enqueue("coupons", Topology.ReleaseKey, held.RedemptionId, new ReleaseUses(held.RedemptionId, "poison"));
            db.Enqueue("coupons", Topology.RejectedKey, held.RedemptionId, new CouponRejected(held.RedemptionId, reason));
            outcome = IssueOutcome.Rejected;
        }
        else
        {
            var number = $"CP-{held.RedemptionId.ToString("N")[^12..].ToUpperInvariant()}";
            db.Coupons.Add(new Coupon
            {
                RedemptionId = held.RedemptionId,
                OwnerId = held.OwnerId,
                Number = number,
                Status = CouponStatus.Issued,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            db.Enqueue("coupons", Topology.IssuedKey, held.RedemptionId, new CouponIssued(held.RedemptionId, number));
            outcome = IssueOutcome.Issued;
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return outcome;
    }
}
