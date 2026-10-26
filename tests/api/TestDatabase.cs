using Promo.Api.Data;
using Promo.Api.Modules.Redemptions;
using Promo.Api.Modules.Campaigns;
using Microsoft.EntityFrameworkCore;

namespace Promo.Api.Tests;

public static class TestDatabase
{
    public static readonly string ConnectionString =
        Environment.GetEnvironmentVariable("PROMO_TEST_DB")
        ?? "Host=localhost;Port=5480;Database=promo_test;Username=promo;Password=promo";

    private static readonly DbContextOptions<PromoDbContext> Options =
        new DbContextOptionsBuilder<PromoDbContext>().UseNpgsql(ConnectionString).Options;

    public static PromoDbContext Create() => new(Options);

    public static async Task<PromoDbContext> OpenAsync()
    {
        var db = Create();
        await db.Database.MigrateAsync();
        await db.Database.ExecuteSqlRawAsync(
            "TRUNCATE \"InboxMessages\", \"OutboxMessages\", \"Coupons\", \"UseHolds\", \"Redemptions\", \"Campaigns\" RESTART IDENTITY CASCADE;");
        return db;
    }

    public static async Task<Campaign> AddCampaignAsync(PromoDbContext db, int usesTotal, long discountCents = 1_500)
    {
        var campaign = new Campaign
        {
            Id = Guid.CreateVersion7(),
            Code = $"T-{Guid.NewGuid():N}"[..14],
            Name = "Test Project",
            Channel = "Email",
            Market = "US",
            EndsOn = DateTimeOffset.UtcNow.AddDays(7),
            DiscountCents = discountCents,
            UsesTotal = usesTotal,
            UsesLeft = usesTotal,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Campaigns.Add(campaign);
        await db.SaveChangesAsync();
        return campaign;
    }

    public static async Task<Redemption> AddRedemptionAsync(PromoDbContext db, Campaign campaign, string reference, int uses, RedemptionStatus status)
    {
        var redemption = new Redemption
        {
            Id = Guid.CreateVersion7(),
            CampaignId = campaign.Id,
            CampaignCode = campaign.Code,
            Reference = reference,
            Uses = uses,
            ValueCents = RedemptionRules.ValueCents(campaign.DiscountCents, uses),
            Status = status,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.Redemptions.Add(redemption);
        await db.SaveChangesAsync();
        return redemption;
    }
}
