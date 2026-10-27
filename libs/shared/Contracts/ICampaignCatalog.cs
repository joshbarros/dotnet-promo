namespace Promo.Shared.Contracts;

public record CampaignInfo(Guid Id, string Code, long DiscountCents);

/// <summary>
/// What Redemptions may ask Campaigns, without referencing it. Campaigns implements it over its own tables;
/// Redemptions never reads another module's schema.
/// </summary>
public interface ICampaignCatalog
{
    Task<CampaignInfo?> FindAsync(string code, CancellationToken ct);
}
