using Promo.Shared.Messaging;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Promo.UnitTests;

public class ModuleDbContextTests
{
    private sealed class Probe(DbContextOptions options, string schema) : ModuleDbContext(options, schema);

    private static DbContextOptions Options() => new DbContextOptionsBuilder<Probe>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;

    [Theory]
    [InlineData("redemptions")]
    [InlineData("a_b")]
    public void AValidSchemaName_IsAccepted(string schema) => Assert.Equal(schema, new Probe(Options(), schema).Schema);

    [Theory]
    [InlineData("")]
    [InlineData("Redemptions")]
    [InlineData("a;drop table x")]
    [InlineData("a\"b")]
    [InlineData("a b")]
    [InlineData("this_schema_name_is_much_longer_than_thirty_two")]
    public void AnythingThatCouldCarrySql_IsRefused(string schema) =>
        Assert.Throws<ArgumentException>(() => new Probe(Options(), schema));

    [Fact]
    public void Enqueue_AddsAnUnsentOutboxRowWithTheSerializedPayload()
    {
        using var db = new Probe(Options(), "probe");
        var redemptionId = Guid.NewGuid();

        db.Enqueue("redemptions", Topology.ReleaseKey, redemptionId, new ReleaseUses(redemptionId, "poison"));

        var row = Assert.Single(db.ChangeTracker.Entries<OutboxMessage>()).Entity;
        Assert.Equal(Topology.ReleaseKey, row.RoutingKey);
        Assert.Equal(redemptionId.ToString(), row.Key);
        Assert.Null(row.SentAt);
        Assert.NotEqual(Guid.Empty, row.MessageId);
        Assert.Equal(new ReleaseUses(redemptionId, "poison"), Json.Parse<ReleaseUses>(row.Payload));
    }

    [Fact]
    public void EachEnqueuedMessage_GetsItsOwnId()
    {
        using var db = new Probe(Options(), "probe");
        db.Enqueue("m", "k", Guid.NewGuid(), new { a = 1 });
        db.Enqueue("m", "k", Guid.NewGuid(), new { a = 1 });
        Assert.Equal(2, db.ChangeTracker.Entries<OutboxMessage>().Select(e => e.Entity.MessageId).Distinct().Count());
    }
}
