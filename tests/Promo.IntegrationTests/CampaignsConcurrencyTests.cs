using System.Collections.Concurrent;
using Promo.Campaigns;
using Promo.Shared.Messaging;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Promo.IntegrationTests;

public class CampaignsConcurrencyTests
{
    [Fact]
    public async Task LastUse_TwoConcurrentRedemptions_OneHeldOneShortNoHoldForTheLoser()
    {
        await TestDatabase.OpenAsync();
        var campaign = await TestDatabase.AddCampaignAsync(usesTotal: 1);

        var outcomes = new ConcurrentBag<(Guid Redemption, ReserveOutcome Outcome)>();
        var gate = new TaskCompletionSource();
        var tasks = Enumerable.Range(0, 2).Select(n => Task.Run(async () =>
        {
            await gate.Task;
            var redemptionId = Guid.CreateVersion7();
            await using var db = TestDatabase.Campaigns();
            var outcome = await new CampaignsHandler(db).ReserveAsync(
                Guid.CreateVersion7(), new ReserveUses(redemptionId, Guid.NewGuid(), campaign.Id, 1, $"CUS-{n}"));
            outcomes.Add((redemptionId, outcome));
        })).ToArray();
        gate.SetResult();
        await Task.WhenAll(tasks);

        await using var check = TestDatabase.Campaigns();
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
        await TestDatabase.OpenAsync();
        var campaign = await TestDatabase.AddCampaignAsync(usesTotal: 4);
        var redemptionId = Guid.CreateVersion7();
        await using (var db = TestDatabase.Campaigns())
            await new CampaignsHandler(db).ReserveAsync(Guid.CreateVersion7(), new ReserveUses(redemptionId, Guid.NewGuid(), campaign.Id, 3, "CUS-1"));

        // Two different commands (poison compensation and a revocation) both ask for the same uses back.
        await using var first = TestDatabase.Campaigns();
        await using var second = TestDatabase.Campaigns();
        var one = await new CampaignsHandler(first).ReleaseAsync(Guid.CreateVersion7(), new ReleaseUses(redemptionId, "poison"));
        var two = await new CampaignsHandler(second).ReleaseAsync(Guid.CreateVersion7(), new ReleaseUses(redemptionId, "revocation"));

        await using var check = TestDatabase.Campaigns();
        Assert.True(one);
        Assert.False(two);
        Assert.Equal(4, await check.Campaigns.Where(s => s.Id == campaign.Id).Select(s => s.UsesLeft).FirstAsync());
    }

    [Fact]
    public async Task SameMessageDeliveredTwice_IsClaimedOnce()
    {
        await TestDatabase.OpenAsync();
        var campaign = await TestDatabase.AddCampaignAsync(usesTotal: 5);
        var messageId = Guid.CreateVersion7();
        var command = new ReserveUses(Guid.CreateVersion7(), Guid.NewGuid(), campaign.Id, 2, "CUS-1");

        await using var a = TestDatabase.Campaigns();
        await using var b = TestDatabase.Campaigns();
        var first = await new CampaignsHandler(a).ReserveAsync(messageId, command);
        var second = await new CampaignsHandler(b).ReserveAsync(messageId, command);

        await using var check = TestDatabase.Campaigns();
        Assert.Equal(ReserveOutcome.Held, first);
        Assert.Equal(ReserveOutcome.Duplicate, second);
        Assert.Equal(3, await check.Campaigns.Where(s => s.Id == campaign.Id).Select(s => s.UsesLeft).FirstAsync());
    }

    [Fact]
    public async Task TheDatabaseItself_RefusesANegativeRemainder()
    {
        await TestDatabase.OpenAsync();
        var campaign = await TestDatabase.AddCampaignAsync(usesTotal: 2);
        await using var db = TestDatabase.Campaigns();

        // A bug that skipped the WHERE clause would hit the check constraint instead of overselling.
        await Assert.ThrowsAnyAsync<Exception>(() => db.Campaigns.Where(s => s.Id == campaign.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.UsesLeft, x => x.UsesLeft - 3)));
    }
}
