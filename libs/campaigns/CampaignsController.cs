using Promo.Shared;
using Promo.Shared.Auth;
using Promo.Shared.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Promo.Campaigns;

public record CreateCampaignRequest(string? Code, string? Name, string? Channel, string? Market, DateTimeOffset? EndsOn, long UsesTotal, long DiscountCents);

public record CampaignDto(Guid Id, string Code, string Name, string Channel, string Market, DateTimeOffset EndsOn, long DiscountCents, int UsesTotal, int UsesLeft)
{
    public static CampaignDto From(Campaign s) => new(s.Id, s.Code, s.Name, s.Channel, s.Market, s.EndsOn, s.DiscountCents, s.UsesTotal, s.UsesLeft);
}

/// <summary>What other modules may ask Campaigns, over Campaigns's own tables.</summary>
public sealed class CampaignCatalog(CampaignsDbContext db) : ICampaignCatalog
{
    public async Task<CampaignInfo?> FindAsync(string code, CancellationToken ct) =>
        await db.Campaigns.AsNoTracking().Where(s => s.Code == code).Select(s => new CampaignInfo(s.Id, s.Code, s.DiscountCents)).FirstOrDefaultAsync(ct);
}

[ApiController]
[Route("api/campaigns")]
[Authorize]
public class CampaignsController(CampaignsDbContext db) : ControllerBase
{
    private const int MaxUsesTotal = 1_000_000;
    private const long MaxDiscountCents = 100_000;

    [HttpGet]
    public async Task<IEnumerable<CampaignDto>> List(CancellationToken ct) =>
        (await db.Campaigns.AsNoTracking().OrderBy(s => s.EndsOn).ThenBy(s => s.Code).ToListAsync(ct)).Select(CampaignDto.From);

    [HttpGet("{code}")]
    public async Task<ActionResult<CampaignDto>> Get(string code, CancellationToken ct)
    {
        var campaign = await db.Campaigns.AsNoTracking().FirstOrDefaultAsync(s => s.Code == code, ct);
        return campaign is null ? NotFound() : CampaignDto.From(campaign);
    }

    /// <summary>Opens a campaign. Staff only. This is the only place UsesLeft is initialised; after that only the Campaigns handler writes it.</summary>
    [HttpPost]
    [Authorize(Policy = Policies.ManageCatalog)]
    public async Task<ActionResult<CampaignDto>> Create(CreateCampaignRequest request, CancellationToken ct)
    {
        var code = request.Code?.Trim();
        if (string.IsNullOrEmpty(code) || code.Length > 20)
            throw new DomainException(400, "Code is required and at most 20 characters.");
        if (string.IsNullOrWhiteSpace(request.Name) || string.IsNullOrWhiteSpace(request.Channel) || string.IsNullOrWhiteSpace(request.Market))
            throw new DomainException(400, "Name, channel and market are required.");
        if (request.UsesTotal < 1 || request.UsesTotal > MaxUsesTotal)
            throw new DomainException(400, $"Redemption cap must be between 1 and {MaxUsesTotal} uses.");
        if (request.DiscountCents < 1 || request.DiscountCents > MaxDiscountCents)
            throw new DomainException(400, $"Price must be between 1 and {MaxDiscountCents} cents per use.");
        if (await db.Campaigns.AnyAsync(s => s.Code == code, ct))
            throw new DomainException(409, $"Campaign '{code}' already exists.");

        var campaign = new Campaign
        {
            Id = Guid.CreateVersion7(),
            Code = code,
            Name = request.Name.Trim(),
            Channel = request.Channel.Trim(),
            Market = request.Market.Trim(),
            EndsOn = request.EndsOn ?? DateTimeOffset.UtcNow.AddDays(14),
            DiscountCents = request.DiscountCents,
            UsesTotal = (int)request.UsesTotal,
            UsesLeft = (int)request.UsesTotal,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Campaigns.Add(campaign);
        await db.SaveChangesAsync(ct);
        return Created($"/api/campaigns/{campaign.Code}", CampaignDto.From(campaign));
    }
}
