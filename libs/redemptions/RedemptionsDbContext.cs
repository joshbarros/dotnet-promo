using Promo.Shared.Messaging;
using Microsoft.EntityFrameworkCore;

namespace Promo.Redemptions;

public class RedemptionsDbContext(DbContextOptions<RedemptionsDbContext> options) : ModuleDbContext(options, SchemaName)
{
    public const string SchemaName = "redemptions";

    public DbSet<Redemption> Redemptions => Set<Redemption>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);

        b.Entity<Redemption>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.Reference).HasMaxLength(RedemptionRules.MaxReferenceLength);
            e.HasIndex(x => new { x.CampaignId, x.Reference }).IsUnique();
            e.HasIndex(x => x.OwnerId);
        });
    }
}
