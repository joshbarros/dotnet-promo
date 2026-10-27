namespace Promo.Campaigns;

public class Campaign
{
    public Guid Id { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string Channel { get; set; } = "";
    public string Market { get; set; } = "";
    public DateTimeOffset EndsOn { get; set; }
    public long DiscountCents { get; set; }
    public int UsesTotal { get; set; }
    public int UsesLeft { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public enum HoldStatus { Held, Released }

public class UseHold
{
    public Guid RedemptionId { get; set; }
    public Guid CampaignId { get; set; }
    public int Uses { get; set; }
    public HoldStatus Status { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ReleasedAt { get; set; }
    public string? ReleaseReason { get; set; }
}
