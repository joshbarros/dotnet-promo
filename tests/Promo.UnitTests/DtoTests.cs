using Promo.Redemptions;
using Promo.Campaigns;
using Xunit;

namespace Promo.UnitTests;

public class DtoTests
{
    [Fact]
    public void RedemptionDto_MapsEveryFieldAndTheStatusName()
    {
        var now = DateTimeOffset.UtcNow;
        var redemption = new Redemption
        {
            Id = Guid.NewGuid(),
            OwnerId = Guid.NewGuid(),
            CampaignId = Guid.NewGuid(),
            CampaignCode = "S-1",
            Reference = "CUS-1",
            Uses = 3,
            ValueCents = 277_500,
            Status = RedemptionStatus.Redeemed,
            CouponNumber = "CP-1",
            CreatedAt = now,
            UpdatedAt = now,
        };

        var dto = RedemptionDto.From(redemption);

        Assert.Equal(redemption.Id, dto.Id);
        Assert.Equal(redemption.OwnerId, dto.OwnerId);
        Assert.Equal("Redeemed", dto.Status);
        Assert.Equal(277_500, dto.ValueCents);
        Assert.Equal("CP-1", dto.CouponNumber);
    }

    [Fact]
    public void CampaignDto_MapsTheScarceNumber()
    {
        var campaign = new Campaign { Id = Guid.NewGuid(), Code = "S-1", Name = "v", Channel = "o", Market = "d", UsesTotal = 10, UsesLeft = 4 };
        var dto = CampaignDto.From(campaign);
        Assert.Equal(10, dto.UsesTotal);
        Assert.Equal(4, dto.UsesLeft);
    }
}
