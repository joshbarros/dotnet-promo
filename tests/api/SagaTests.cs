using Promo.Api.Messaging;
using Promo.Api.Modules.Redemptions;
using Promo.Api.Modules.Campaigns;
using Promo.Api.Modules.Coupons;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace Promo.Api.Tests;

public class SagaTests
{
    private static RedemptionsHandler Redemptions() =>
        new(TestDatabase.Create(), Options.Create(new RedemptionOptions()));

    [Fact]
    public async Task PoisonReference_IsCommittedRejected_ThenCompensatingCommandIsQueued()
    {
        var db = await TestDatabase.OpenAsync();
        var campaign = await TestDatabase.AddCampaignAsync(db, 5);
        var redemption = await TestDatabase.AddRedemptionAsync(db, campaign, "CUS-POISON-1", 2, RedemptionStatus.Held);
        var held = new UsesHeld(redemption.Id, campaign.Id, 2, redemption.Reference);

        var handler = new CouponsHandler(TestDatabase.Create());
        var ex = await Assert.ThrowsAsync<PoisonMessageException>(() =>
            handler.HandleAsync(new Delivery(Guid.CreateVersion7(), Topology.HeldKey, Json.Serialize(held)), default));
        Assert.Contains("poison", ex.Message, StringComparison.OrdinalIgnoreCase);

        var check = TestDatabase.Create();
        var doc = await check.Coupons.SingleAsync(d => d.RedemptionId == redemption.Id);
        Assert.Equal(CouponStatus.Rejected, doc.Status); // committed before the dead-letter
        Assert.Equal(1, await check.Outbox.CountAsync(o => o.RoutingKey == Topology.ReleaseKey));
        Assert.Equal(1, await check.Outbox.CountAsync(o => o.RoutingKey == Topology.RejectedKey));
        Assert.Equal(0, await check.Outbox.CountAsync(o => o.RoutingKey == Topology.IssuedKey));
    }

    [Fact]
    public async Task Revocation_ThenFinalMark_LeavesRedemptionRevokedAndCouponIssued()
    {
        var db = await TestDatabase.OpenAsync();
        var campaign = await TestDatabase.AddCampaignAsync(db, 5);
        var redemption = await TestDatabase.AddRedemptionAsync(db, campaign, "CUS-1", 2, RedemptionStatus.Held);

        // The coupon is issued; the final mark has not landed yet.
        var number = "CP-TEST";
        await new CouponsHandler(TestDatabase.Create()).IssueAsync(
            Guid.CreateVersion7(), new UsesHeld(redemption.Id, campaign.Id, 2, redemption.Reference));

        // The revocation gets there first.
        await new RedemptionService(TestDatabase.Create()).RevokeAsync(redemption.Id, default);

        var mark = await Redemptions().MarkRedeemedAsync(Guid.CreateVersion7(), new CouponIssued(redemption.Id, number));

        var check = TestDatabase.Create();
        Assert.Equal(MarkOutcome.Skipped, mark);
        Assert.Equal(RedemptionStatus.Revoked, (await check.Redemptions.SingleAsync()).Status);
        Assert.Equal(CouponStatus.Issued, (await check.Coupons.SingleAsync()).Status);
    }

    [Fact]
    public async Task FinalMark_RunsOnlyFromHeld()
    {
        var db = await TestDatabase.OpenAsync();
        var campaign = await TestDatabase.AddCampaignAsync(db, 5);
        var held = await TestDatabase.AddRedemptionAsync(db, campaign, "CUS-A", 1, RedemptionStatus.Held);
        var rejected = await TestDatabase.AddRedemptionAsync(db, campaign, "CUS-B", 1, RedemptionStatus.Rejected);
        var requested = await TestDatabase.AddRedemptionAsync(db, campaign, "CUS-C", 1, RedemptionStatus.Requested);

        Assert.Equal(MarkOutcome.Marked, await Redemptions().MarkRedeemedAsync(Guid.CreateVersion7(), new CouponIssued(held.Id, "CP-1")));
        Assert.Equal(MarkOutcome.Skipped, await Redemptions().MarkRedeemedAsync(Guid.CreateVersion7(), new CouponIssued(rejected.Id, "CP-2")));
        // Not Held yet: the message is retried later instead of being dropped.
        await Assert.ThrowsAsync<OutOfOrderException>(() =>
            Redemptions().MarkRedeemedAsync(Guid.CreateVersion7(), new CouponIssued(requested.Id, "CP-3")));

        var statuses = (await TestDatabase.Create().Redemptions.ToListAsync()).ToDictionary(b => b.Reference, b => b.Status);
        Assert.Equal(RedemptionStatus.Redeemed, statuses["CUS-A"]);
        Assert.Equal(RedemptionStatus.Rejected, statuses["CUS-B"]);
        Assert.Equal(RedemptionStatus.Requested, statuses["CUS-C"]);
    }

    [Fact]
    public async Task Revocation_OfARejectedRedemption_Conflicts()
    {
        var db = await TestDatabase.OpenAsync();
        var campaign = await TestDatabase.AddCampaignAsync(db, 5);
        var redemption = await TestDatabase.AddRedemptionAsync(db, campaign, "CUS-1", 1, RedemptionStatus.Rejected);

        var ex = await Assert.ThrowsAsync<DomainException>(() => new RedemptionService(TestDatabase.Create()).RevokeAsync(redemption.Id, default));
        Assert.Equal(409, ex.Status);
    }
}
