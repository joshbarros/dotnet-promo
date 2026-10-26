using Promo.Api.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Promo.Api.Modules.Coupons;

public record CouponDto(Guid RedemptionId, string? Number, string Status, string? Reason, DateTimeOffset CreatedAt);

[ApiController]
[Route("api/coupons")]
public class CouponsController(PromoDbContext db) : ControllerBase
{
    [HttpGet("{redemptionId:guid}")]
    public async Task<ActionResult<CouponDto>> Get(Guid redemptionId, CancellationToken ct)
    {
        var doc = await db.Coupons.AsNoTracking().FirstOrDefaultAsync(d => d.RedemptionId == redemptionId, ct);
        return doc is null
            ? NotFound()
            : new CouponDto(doc.RedemptionId, doc.Number, doc.Status.ToString(), doc.Reason, doc.CreatedAt);
    }
}
