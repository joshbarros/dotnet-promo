using Promo.Shared.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Promo.Coupons;

public record CouponDto(Guid RedemptionId, string? Number, string Status, string? Reason, DateTimeOffset CreatedAt);

[ApiController]
[Route("api/coupons")]
[Authorize]
public class CouponsController(CouponsDbContext db, ICurrentUser user) : ControllerBase
{
    /// <summary>The owner or staff only; anyone else gets a 404.</summary>
    [HttpGet("{redemptionId:guid}")]
    public async Task<ActionResult<CouponDto>> Get(Guid redemptionId, CancellationToken ct)
    {
        var doc = await db.Coupons.AsNoTracking().FirstOrDefaultAsync(d => d.RedemptionId == redemptionId, ct);
        return doc is null || !OwnerAccess.CanAccess(user, doc.OwnerId)
            ? NotFound()
            : new CouponDto(doc.RedemptionId, doc.Number, doc.Status.ToString(), doc.Reason, doc.CreatedAt);
    }
}
