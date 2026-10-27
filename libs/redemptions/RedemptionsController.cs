using System.Text.Json;
using Promo.Shared.Auth;
using Promo.Shared.Messaging;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Promo.Redemptions;

[ApiController]
[Route("api/redemptions")]
[Authorize]
public class RedemptionsController(RedemptionService service, KafkaLog kafka) : ControllerBase
{
    /// <summary>Accepts the redemption for the signed-in user. Campaigns and the coupon follow asynchronously; poll GET to see them.</summary>
    [HttpPost]
    public async Task<ActionResult<RedemptionDto>> Create(CreateRedemptionRequest request, CancellationToken ct)
    {
        var redemption = await service.CreateAsync(request, ct);
        return Accepted($"/api/redemptions/{redemption.Id}", RedemptionDto.From(redemption));
    }

    /// <summary>Your redemptions, newest first, a page at a time (default 100, at most 100). Staff see everyone's. The total is in X-Total-Count.</summary>
    [HttpGet]
    public async Task<IEnumerable<RedemptionDto>> List([FromQuery] string? campaignCode, [FromQuery] int page = 1, [FromQuery] int pageSize = 100, CancellationToken ct = default)
    {
        var (items, total) = await service.ListAsync(campaignCode, Math.Max(page, 1), Math.Clamp(pageSize, 1, 100), ct);
        Response.Headers["X-Total-Count"] = total.ToString();
        return items.Select(RedemptionDto.From);
    }

    [HttpGet("{id:guid}")]
    public async Task<RedemptionDto> Get(Guid id, CancellationToken ct) => RedemptionDto.From(await service.GetAsync(id, ct));

    [HttpPost("{id:guid}/revocation")]
    public async Task<ActionResult<RedemptionDto>> Revoke(Guid id, CancellationToken ct) =>
        Accepted($"/api/redemptions/{id}", RedemptionDto.From(await service.RevokeAsync(id, ct)));

    /// <summary>The Kafka event log for one redemption, read from the start of the topic. Staff only.</summary>
    [HttpGet("{id:guid}/events")]
    [Authorize(Policy = Policies.ViewAll)]
    public async Task<IEnumerable<JsonElement>> Events(Guid id, CancellationToken ct) =>
        (await Task.Run(() => kafka.Read(id.ToString(), TimeSpan.FromSeconds(10)), ct))
            .Select(v => JsonDocument.Parse(v).RootElement.Clone());
}
