using System.Text.Json;
using Promo.Api.Data;
using Promo.Api.Messaging;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Promo.Api.Modules.Redemptions;

[ApiController]
[Route("api/redemptions")]
public class RedemptionsController(PromoDbContext db, RedemptionService service, KafkaLog kafka) : ControllerBase
{
    /// <summary>Accepts the redemption. The use hold and the coupon follow asynchronously; poll GET to see them.</summary>
    [HttpPost]
    public async Task<ActionResult<RedemptionDto>> Create(CreateRedemptionRequest request, CancellationToken ct)
    {
        var redemption = await service.CreateAsync(request, ct);
        return Accepted($"/api/redemptions/{redemption.Id}", RedemptionDto.From(redemption));
    }

    [HttpGet]
    public async Task<IEnumerable<RedemptionDto>> List([FromQuery] string? campaignCode, CancellationToken ct)
    {
        var query = db.Redemptions.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(campaignCode))
            query = query.Where(b => b.CampaignCode == campaignCode);
        return (await query.OrderByDescending(b => b.CreatedAt).Take(100).ToListAsync(ct)).Select(RedemptionDto.From);
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<RedemptionDto>> Get(Guid id, CancellationToken ct)
    {
        var redemption = await db.Redemptions.AsNoTracking().FirstOrDefaultAsync(b => b.Id == id, ct);
        return redemption is null ? NotFound() : RedemptionDto.From(redemption);
    }

    [HttpPost("{id:guid}/revocation")]
    public async Task<ActionResult<RedemptionDto>> Revoke(Guid id, CancellationToken ct) =>
        Accepted($"/api/redemptions/{id}", RedemptionDto.From(await service.RevokeAsync(id, ct)));

    /// <summary>The Kafka event log for one redemption, read from the start of the topic.</summary>
    [HttpGet("{id:guid}/events")]
    public async Task<IEnumerable<JsonElement>> Events(Guid id, CancellationToken ct) =>
        (await Task.Run(() => kafka.Read(id.ToString(), TimeSpan.FromSeconds(10)), ct))
            .Select(v => JsonDocument.Parse(v).RootElement.Clone());
}
