using Promo.Redemptions;
using Promo.Campaigns;
using Promo.Coupons;
using Promo.Shared.Auth;
using Promo.Shared.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Promo.IntegrationTests;

public static class TestDatabase
{
    public static readonly string ConnectionString =
        Environment.GetEnvironmentVariable("PROMO_TEST_DB")
        ?? "Host=localhost;Port=5480;Database=promo_test;Username=promo;Password=promo";

    private static DbContextOptions<T> Options<T>(string schema) where T : DbContext =>
        new DbContextOptionsBuilder<T>()
            .UseNpgsql(ConnectionString, n => n.MigrationsHistoryTable("__EFMigrationsHistory", schema))
            .Options;

    public static RedemptionsDbContext Redemptions() => new(Options<RedemptionsDbContext>(RedemptionsDbContext.SchemaName));
    public static CampaignsDbContext Campaigns() => new(Options<CampaignsDbContext>(CampaignsDbContext.SchemaName));
    public static CouponsDbContext Coupons() => new(Options<CouponsDbContext>(CouponsDbContext.SchemaName));

    /// <summary>Migrates the business modules and empties their tables. The identity schema is left alone: the API tests share it.</summary>
    public static async Task OpenAsync()
    {
        await using var redemptions = Redemptions();
        await using var uses = Campaigns();
        await using var coupons = Coupons();
        await redemptions.Database.MigrateAsync();
        await uses.Database.MigrateAsync();
        await coupons.Database.MigrateAsync();
        await redemptions.Database.ExecuteSqlRawAsync(
            "TRUNCATE redemptions.\"InboxMessages\", redemptions.\"OutboxMessages\", redemptions.\"Redemptions\", " +
            "uses.\"InboxMessages\", uses.\"OutboxMessages\", uses.\"UseHolds\", uses.\"Campaigns\", " +
            "coupons.\"InboxMessages\", coupons.\"OutboxMessages\", coupons.\"Coupons\" RESTART IDENTITY CASCADE;");
    }

    public static async Task<Campaign> AddCampaignAsync(int usesTotal, long discountCents = 5_000)
    {
        await using var db = Campaigns();
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

    public static async Task<Redemption> AddRedemptionAsync(Campaign campaign, string reference, int uses, RedemptionStatus status, Guid? ownerId = null)
    {
        await using var db = Redemptions();
        var redemption = new Redemption
        {
            Id = Guid.CreateVersion7(),
            OwnerId = ownerId ?? Guid.CreateVersion7(),
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

public sealed class TestUser(Guid? id, bool staff = false) : ICurrentUser
{
    public bool IsAuthenticated => id is not null;
    public Guid? Id => id;
    public bool IsStaff => staff;
}

public sealed class FixedCatalog(params Campaign[] campaigns) : ICampaignCatalog
{
    public Task<CampaignInfo?> FindAsync(string code, CancellationToken ct) =>
        Task.FromResult(campaigns.Where(s => s.Code == code).Select(s => new CampaignInfo(s.Id, s.Code, s.DiscountCents)).FirstOrDefault());
}
