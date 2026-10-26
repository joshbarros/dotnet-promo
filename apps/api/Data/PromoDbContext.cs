using Promo.Api.Messaging;
using Promo.Api.Modules.Redemptions;
using Promo.Api.Modules.Campaigns;
using Promo.Api.Modules.Coupons;
using Microsoft.EntityFrameworkCore;

namespace Promo.Api.Data;

public class PromoDbContext(DbContextOptions<PromoDbContext> options) : DbContext(options)
{
    public DbSet<Campaign> Campaigns => Set<Campaign>();
    public DbSet<UseHold> Holds => Set<UseHold>();
    public DbSet<Redemption> Redemptions => Set<Redemption>();
    public DbSet<Coupon> Coupons => Set<Coupon>();
    public DbSet<OutboxMessage> Outbox => Set<OutboxMessage>();
    public DbSet<InboxMessage> Inbox => Set<InboxMessage>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Campaign>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.Code).IsUnique();
            e.Property(x => x.Code).HasMaxLength(20);
            e.ToTable(t => t.HasCheckConstraint("CK_Campaigns_Remaining", "\"UsesLeft\" >= 0 AND \"UsesLeft\" <= \"UsesTotal\""));
        });

        b.Entity<UseHold>(e =>
        {
            e.ToTable("UseHolds");
            e.HasKey(x => x.RedemptionId);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
            e.HasOne<Campaign>().WithMany().HasForeignKey(x => x.CampaignId);
        });

        b.Entity<Redemption>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.Reference).HasMaxLength(RedemptionRules.MaxReferenceLength);
            e.HasIndex(x => new { x.CampaignId, x.Reference }).IsUnique();
            e.HasOne<Campaign>().WithMany().HasForeignKey(x => x.CampaignId);
        });

        b.Entity<Coupon>(e =>
        {
            e.ToTable("Coupons");
            e.HasKey(x => x.RedemptionId);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
        });

        b.Entity<OutboxMessage>(e =>
        {
            e.ToTable("OutboxMessages");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).UseIdentityAlwaysColumn();
            e.Property(x => x.Payload).HasColumnType("jsonb");
            e.HasIndex(x => x.SentAt).HasFilter("\"SentAt\" IS NULL");
        });

        b.Entity<InboxMessage>(e =>
        {
            e.ToTable("InboxMessages");
            e.HasKey(x => new { x.Consumer, x.MessageId });
        });
    }
}
