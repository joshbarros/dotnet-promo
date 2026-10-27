using Promo.Redemptions;
using Promo.Coupons;
using Promo.Shared;
using Promo.Shared.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace Promo.IntegrationTests;

public class SagaTests
{
    private static RedemptionsHandler Handler(RedemptionsDbContext db) => new(db, Options.Create(new RedemptionOptions()));

    [Fact]
    public async Task PoisonReference_IsCommittedRejected_ThenCompensatingCommandIsQueued()
    {
        await TestDatabase.OpenAsync();
        var campaign = await TestDatabase.AddCampaignAsync(5);
        var redemption = await TestDatabase.AddRedemptionAsync(campaign, "CUS-POISON-1", 2, RedemptionStatus.Held);
        var held = new UsesHeld(redemption.Id, redemption.OwnerId, campaign.Id, 2, redemption.Reference);

        await using var docs = TestDatabase.Coupons();
        var ex = await Assert.ThrowsAsync<PoisonMessageException>(() =>
            new CouponsHandler(docs).HandleAsync(new Delivery(Guid.CreateVersion7(), Topology.HeldKey, Json.Serialize(held)), default));
        Assert.Contains("poison", ex.Message, StringComparison.OrdinalIgnoreCase);

        await using var check = TestDatabase.Coupons();
        var doc = await check.Coupons.SingleAsync(d => d.RedemptionId == redemption.Id);
        Assert.Equal(CouponStatus.Rejected, doc.Status); // committed before the dead-letter
        Assert.Equal(redemption.OwnerId, doc.OwnerId);
        Assert.Equal(1, await check.Outbox.CountAsync(o => o.RoutingKey == Topology.ReleaseKey));
        Assert.Equal(1, await check.Outbox.CountAsync(o => o.RoutingKey == Topology.RejectedKey));
        Assert.Equal(0, await check.Outbox.CountAsync(o => o.RoutingKey == Topology.IssuedKey));
    }

    [Fact]
    public async Task Revocation_ThenFinalMark_LeavesRedemptionRevokedAndCouponIssued()
    {
        await TestDatabase.OpenAsync();
        var campaign = await TestDatabase.AddCampaignAsync(5);
        var owner = Guid.CreateVersion7();
        var redemption = await TestDatabase.AddRedemptionAsync(campaign, "CUS-1", 2, RedemptionStatus.Held, owner);

        // The coupon is issued; the final mark has not landed yet.
        await using (var docs = TestDatabase.Coupons())
            await new CouponsHandler(docs).IssueAsync(Guid.CreateVersion7(), new UsesHeld(redemption.Id, owner, campaign.Id, 2, redemption.Reference));

        // The owner reverses first.
        await using (var redemptions = TestDatabase.Redemptions())
            await new RedemptionService(redemptions, new FixedCatalog(campaign), new TestUser(owner)).RevokeAsync(redemption.Id, default);

        await using var late = TestDatabase.Redemptions();
        var mark = await Handler(late).MarkRedeemedAsync(Guid.CreateVersion7(), new CouponIssued(redemption.Id, "CP-TEST"));

        await using var check = TestDatabase.Redemptions();
        await using var checkDocs = TestDatabase.Coupons();
        Assert.Equal(MarkOutcome.Skipped, mark);
        Assert.Equal(RedemptionStatus.Revoked, (await check.Redemptions.SingleAsync()).Status);
        Assert.Equal(CouponStatus.Issued, (await checkDocs.Coupons.SingleAsync()).Status);
    }

    [Fact]
    public async Task FinalMark_RunsOnlyFromHeld()
    {
        await TestDatabase.OpenAsync();
        var campaign = await TestDatabase.AddCampaignAsync(5);
        var held = await TestDatabase.AddRedemptionAsync(campaign, "CUS-A", 1, RedemptionStatus.Held);
        var rejected = await TestDatabase.AddRedemptionAsync(campaign, "CUS-B", 1, RedemptionStatus.Rejected);
        var requested = await TestDatabase.AddRedemptionAsync(campaign, "CUS-C", 1, RedemptionStatus.Requested);

        await using (var a = TestDatabase.Redemptions())
            Assert.Equal(MarkOutcome.Marked, await Handler(a).MarkRedeemedAsync(Guid.CreateVersion7(), new CouponIssued(held.Id, "CP-1")));
        await using (var b = TestDatabase.Redemptions())
            Assert.Equal(MarkOutcome.Skipped, await Handler(b).MarkRedeemedAsync(Guid.CreateVersion7(), new CouponIssued(rejected.Id, "CP-2")));
        // Not Held yet: the message is retried later instead of being dropped.
        await using (var c = TestDatabase.Redemptions())
            await Assert.ThrowsAsync<OutOfOrderException>(() => Handler(c).MarkRedeemedAsync(Guid.CreateVersion7(), new CouponIssued(requested.Id, "CP-3")));

        await using var check = TestDatabase.Redemptions();
        var statuses = (await check.Redemptions.ToListAsync()).ToDictionary(b => b.Reference, b => b.Status);
        Assert.Equal(RedemptionStatus.Redeemed, statuses["CUS-A"]);
        Assert.Equal(RedemptionStatus.Rejected, statuses["CUS-B"]);
        Assert.Equal(RedemptionStatus.Requested, statuses["CUS-C"]);
    }

    [Fact]
    public async Task Revocation_OfARejectedRedemption_Conflicts()
    {
        await TestDatabase.OpenAsync();
        var campaign = await TestDatabase.AddCampaignAsync(5);
        var owner = Guid.CreateVersion7();
        var redemption = await TestDatabase.AddRedemptionAsync(campaign, "CUS-1", 1, RedemptionStatus.Rejected, owner);

        await using var db = TestDatabase.Redemptions();
        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            new RedemptionService(db, new FixedCatalog(campaign), new TestUser(owner)).RevokeAsync(redemption.Id, default));
        Assert.Equal(409, ex.Status);
    }

    [Fact]
    public async Task Revocation_ByAnotherCustomer_IsNotFound_AndChangesNothing()
    {
        await TestDatabase.OpenAsync();
        var campaign = await TestDatabase.AddCampaignAsync(5);
        var redemption = await TestDatabase.AddRedemptionAsync(campaign, "CUS-1", 1, RedemptionStatus.Held);

        await using var db = TestDatabase.Redemptions();
        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            new RedemptionService(db, new FixedCatalog(campaign), new TestUser(Guid.NewGuid())).RevokeAsync(redemption.Id, default));

        Assert.Equal(404, ex.Status);
        await using var check = TestDatabase.Redemptions();
        Assert.Equal(RedemptionStatus.Held, (await check.Redemptions.SingleAsync()).Status);
        Assert.Equal(0, await check.Outbox.CountAsync());
    }

    [Fact]
    public async Task Staff_CanRevokeAnyonesRedemption()
    {
        await TestDatabase.OpenAsync();
        var campaign = await TestDatabase.AddCampaignAsync(5);
        var redemption = await TestDatabase.AddRedemptionAsync(campaign, "CUS-1", 1, RedemptionStatus.Held);

        await using var db = TestDatabase.Redemptions();
        var revoked = await new RedemptionService(db, new FixedCatalog(campaign), new TestUser(Guid.NewGuid(), staff: true)).RevokeAsync(redemption.Id, default);
        Assert.Equal(RedemptionStatus.Revoked, revoked.Status);
    }

    [Fact]
    public async Task Create_RecordsTheCallerAsTheOwner_AndQueuesTheReservation()
    {
        await TestDatabase.OpenAsync();
        var campaign = await TestDatabase.AddCampaignAsync(5);
        var owner = Guid.CreateVersion7();

        await using var db = TestDatabase.Redemptions();
        var redemption = await new RedemptionService(db, new FixedCatalog(campaign), new TestUser(owner))
            .CreateAsync(new CreateRedemptionRequest(campaign.Code, "CUS-1", 2), default);

        await using var check = TestDatabase.Redemptions();
        Assert.Equal(owner, (await check.Redemptions.SingleAsync()).OwnerId);
        var message = await check.Outbox.SingleAsync();
        Assert.Equal(Topology.ReserveKey, message.RoutingKey);
        Assert.Equal(owner, Json.Parse<ReserveUses>(message.Payload).OwnerId);
        Assert.Equal(RedemptionStatus.Requested, redemption.Status);
    }

    [Fact]
    public async Task Create_WithoutASignedInUser_IsRefused()
    {
        await TestDatabase.OpenAsync();
        var campaign = await TestDatabase.AddCampaignAsync(5);
        await using var db = TestDatabase.Redemptions();
        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            new RedemptionService(db, new FixedCatalog(campaign), new TestUser(null)).CreateAsync(new CreateRedemptionRequest(campaign.Code, "CUS-1", 1), default));
        Assert.Equal(401, ex.Status);
    }

    [Fact]
    public async Task Create_ForAnUnknownCampaign_IsNotFound()
    {
        await TestDatabase.OpenAsync();
        await using var db = TestDatabase.Redemptions();
        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            new RedemptionService(db, new FixedCatalog(), new TestUser(Guid.NewGuid())).CreateAsync(new CreateRedemptionRequest("NOPE", "CUS-1", 1), default));
        Assert.Equal(404, ex.Status);
    }
}
