using RabbitMQ.Client;

namespace Promo.Shared.Messaging;

public static class Topology
{
    public const string Exchange = "promo.work";
    public const string DeadLetterExchange = "promo.dlx";
    public const string RequeueExchange = "promo.requeue";
    public const string KafkaTopic = "promo.events";

    public const int RetryDelayMs = 2000;
    public const int MaxAttempts = 3;

    public const string ReserveKey = "uses.reserve";
    public const string ReleaseKey = "uses.release";
    public const string HeldKey = "uses.held";
    public const string ShortKey = "uses.short";
    public const string ReleasedKey = "uses.released";
    public const string IssuedKey = "coupon.issued";
    public const string RejectedKey = "coupon.rejected";

    public const string CampaignsQueue = "campaigns.commands";
    public const string CouponsQueue = "coupons.work";
    public const string RedemptionsQueue = "redemptions.events";

    public static async Task DeclareAsync(IChannel channel, string queue, string[] routingKeys, CancellationToken ct)
    {
        await channel.ExchangeDeclareAsync(Exchange, ExchangeType.Direct, durable: true, cancellationToken: ct);
        await channel.ExchangeDeclareAsync(DeadLetterExchange, ExchangeType.Direct, durable: true, cancellationToken: ct);
        await channel.ExchangeDeclareAsync(RequeueExchange, ExchangeType.Direct, durable: true, cancellationToken: ct);

        await channel.QueueDeclareAsync($"{queue}.dlq", durable: true, exclusive: false, autoDelete: false, cancellationToken: ct);
        await channel.QueueBindAsync($"{queue}.dlq", DeadLetterExchange, queue, cancellationToken: ct);

        await channel.QueueDeclareAsync($"{queue}.retry", durable: true, exclusive: false, autoDelete: false,
            arguments: new Dictionary<string, object?>
            {
                ["x-message-ttl"] = RetryDelayMs,
                ["x-dead-letter-exchange"] = RequeueExchange,
                ["x-dead-letter-routing-key"] = queue,
            }, cancellationToken: ct);

        await channel.QueueDeclareAsync(queue, durable: true, exclusive: false, autoDelete: false,
            arguments: new Dictionary<string, object?>
            {
                ["x-dead-letter-exchange"] = DeadLetterExchange,
                ["x-dead-letter-routing-key"] = queue,
            }, cancellationToken: ct);
        await channel.QueueBindAsync(queue, RequeueExchange, queue, cancellationToken: ct);
        foreach (var key in routingKeys)
            await channel.QueueBindAsync(queue, Exchange, key, cancellationToken: ct);
    }
}
