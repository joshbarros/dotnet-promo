using System.Text.Json;

namespace Promo.Shared.Messaging;

public record ReserveUses(Guid RedemptionId, Guid OwnerId, Guid CampaignId, int Uses, string Reference);
public record UsesHeld(Guid RedemptionId, Guid OwnerId, Guid CampaignId, int Uses, string Reference);
public record UsesShort(Guid RedemptionId, string Reference, int RequestedUses, int UsesLeft);
public record ReleaseUses(Guid RedemptionId, string Reason);
public record UsesReleased(Guid RedemptionId, int Uses, string Reason);
public record CouponIssued(Guid RedemptionId, string Number);
public record CouponRejected(Guid RedemptionId, string Reason);

public record Delivery(Guid MessageId, string RoutingKey, string Body);

public interface IMessageHandler
{
    Task HandleAsync(Delivery delivery, CancellationToken ct);
}

/// <summary>Committed as rejected; the message must be dead-lettered, not retried.</summary>
public class PoisonMessageException(string message) : Exception(message);

/// <summary>The message arrived before the state it depends on; retry later.</summary>
public class OutOfOrderException(string message) : Exception(message);

public static class Json
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T Parse<T>(string body) =>
        JsonSerializer.Deserialize<T>(body, Options) ?? throw new InvalidOperationException("Empty message body.");
}
