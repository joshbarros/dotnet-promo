namespace Promo.Redemptions;

public enum RedemptionStatus { Requested, Held, Redeemed, Revoked, Rejected }

public class Redemption
{
    public Guid Id { get; set; }
    public Guid OwnerId { get; set; }
    public Guid CampaignId { get; set; }
    public string CampaignCode { get; set; } = "";
    public string Reference { get; set; } = "";
    public int Uses { get; set; }
    public long ValueCents { get; set; }
    public RedemptionStatus Status { get; set; }
    public string? Reason { get; set; }
    public string? CouponNumber { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? UsesReleasedAt { get; set; }
}

public class RedemptionOptions
{
    /// <summary>Latency knob for the final mark. Compose sets it so the revocation race window is wide enough to hit on purpose.</summary>
    public int FinalMarkDelayMs { get; set; }
}

public record CreateRedemptionRequest(string? CampaignCode, string? Reference, long Uses);

public record RedemptionDto(
    Guid Id, Guid OwnerId, string CampaignCode, string Reference, int Uses, long ValueCents, string Status,
    string? Reason, string? CouponNumber, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    DateTimeOffset? UsesReleasedAt)
{
    public static RedemptionDto From(Redemption b) => new(
        b.Id, b.OwnerId, b.CampaignCode, b.Reference, b.Uses, b.ValueCents, b.Status.ToString(),
        b.Reason, b.CouponNumber, b.CreatedAt, b.UpdatedAt, b.UsesReleasedAt);
}
