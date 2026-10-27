using Promo.Shared.Messaging;
using Microsoft.EntityFrameworkCore;

namespace Promo.Campaigns;

public class CampaignsDbContext(DbContextOptions<CampaignsDbContext> options) : ModuleDbContext(options, SchemaName)
{
    public const string SchemaName = "uses";

    public DbSet<Campaign> Campaigns => Set<Campaign>();
    public DbSet<UseHold> Holds => Set<UseHold>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);

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
    }
}
