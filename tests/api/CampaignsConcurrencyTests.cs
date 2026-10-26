using System.Collections.Concurrent;
using Promo.Api.Messaging;
using Promo.Api.Modules.Campaigns;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Promo.Api.Tests;

public class CampaignsConcurrencyTests
{
    [Fact]
    public async Task LastUse_TwoConcurrentRedemptions_OneHeldOneShortNoHoldForTheLoser()
    {
        var db = await TestDatabase.OpenAsync();
        var campaign = await TestDatabase.AddCampaignAsync(db, usesTotal: 1);

        var outcomes = new ConcurrentBag<(Guid Redemption, ReserveOutcome Outcome)>();
        var gate = new TaskCompletionSource();
        var tasks = Enumerable.Range(0, 2).Select(n => Task.Run(async () =>
        {
            await gate.Task;
            var redemptionId = Guid.CreateVersion7();
            var handler = new CampaignsHandler(TestDatabase.Create());
            var outcome = await handler.ReserveAsync(
                Guid.CreateVersion7(), new ReserveUses(redemptionId, campaign.Id, 1, $"CUS-{n}"));
            outcomes.Add((redemptionId, outcome));
        })).ToArray();
        gate.SetResult();
        await Task.WhenAll(tasks);

        var check = TestDatabase.Create();
        var remaining = await check.Campaigns.Where(s => s.Id == campaign.Id).Select(s => s.UsesLeft).FirstAsync();
        var holds = await check.Holds.ToListAsync();
        var loser = outcomes.Single(o => o.Outcome == ReserveOutcome.Short).Redemption;

        Assert.Single(outcomes, o => o.Outcome == ReserveOutcome.Held);
        Assert.Single(outcomes, o => o.Outcome == ReserveOutcome.Short);
        Assert.Equal(0, remaining);
        Assert.Single(holds);
        Assert.DoesNotContain(holds, h => h.RedemptionId == loser);
        Assert.Equal(1, await check.Outbox.CountAsync(o => o.RoutingKey == Topology.HeldKey));
        Assert.Equal(1, await check.Outbox.CountAsync(o => o.RoutingKey == Topology.ShortKey));
    }

    [Fact]
    public async Task Release_GivesTheUsesBackExactlyOnce()
    {
        var db = await TestDatabase.OpenAsync();
        var campaign = await TestDatabase.AddCampaignAsync(db, usesTotal: 4);
        var redemptionId = Guid.CreateVersion7();
        var handler = new CampaignsHandler(TestDatabase.Create());
        await handler.ReserveAsync(Guid.CreateVersion7(), new ReserveUses(redemptionId, campaign.Id, 3, "CUS-1"));

        // Two different commands (poison compensation and a revocation) both ask for the same uses back.
        var first = await new CampaignsHandler(TestDatabase.Create())
            .ReleaseAsync(Guid.CreateVersion7(), new ReleaseUses(redemptionId, "poison"));
        var second = await new CampaignsHandler(TestDatabase.Create())
            .ReleaseAsync(Guid.CreateVersion7(), new ReleaseUses(redemptionId, "revocation"));

        var remaining = await TestDatabase.Create().Campaigns.Where(s => s.Id == campaign.Id).Select(s => s.UsesLeft).FirstAsync();
        Assert.True(first);
        Assert.False(second);
        Assert.Equal(4, remaining);
    }

    [Fact]
    public async Task SameMessageDeliveredTwice_IsClaimedOnce()
    {
        var db = await TestDatabase.OpenAsync();
        var campaign = await TestDatabase.AddCampaignAsync(db, usesTotal: 5);
        var messageId = Guid.CreateVersion7();
        var command = new ReserveUses(Guid.CreateVersion7(), campaign.Id, 2, "CUS-1");

        var first = await new CampaignsHandler(TestDatabase.Create()).ReserveAsync(messageId, command);
        var second = await new CampaignsHandler(TestDatabase.Create()).ReserveAsync(messageId, command);

        var remaining = await TestDatabase.Create().Campaigns.Where(s => s.Id == campaign.Id).Select(s => s.UsesLeft).FirstAsync();
        Assert.Equal(ReserveOutcome.Held, first);
        Assert.Equal(ReserveOutcome.Duplicate, second);
        Assert.Equal(3, remaining);
    }
}
