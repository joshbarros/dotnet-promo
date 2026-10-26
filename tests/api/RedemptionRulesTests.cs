using Promo.Api.Modules.Redemptions;
using Xunit;

namespace Promo.Api.Tests;

public class RedemptionRulesTests
{
    [Theory]
    [InlineData(null, 1)]
    [InlineData("", 1)]
    [InlineData("   ", 1)]
    [InlineData("CUS-1", 0)]
    [InlineData("CUS-1", -3)]
    public void EmptyInput_IsRejected(string? reference, long uses) =>
        Assert.NotNull(RedemptionRules.Validate(reference, uses));

    [Fact]
    public void OverLimit_IsRejected()
    {
        Assert.Null(RedemptionRules.Validate("CUS-1", RedemptionRules.MaxUsesPerRedemption));
        Assert.NotNull(RedemptionRules.Validate("CUS-1", RedemptionRules.MaxUsesPerRedemption + 1));
        Assert.NotNull(RedemptionRules.Validate(new string('x', RedemptionRules.MaxReferenceLength + 1), 1));
    }

    [Theory]
    [InlineData(int.MaxValue)]
    [InlineData((long)int.MaxValue + 1)]
    [InlineData(4_294_967_297L)] // (int) of this wraps to 1; it must not pass as one use
    [InlineData(long.MaxValue)]
    public void IntegerOverflow_IsRejectedNotWrapped(long uses)
    {
        Assert.NotNull(RedemptionRules.Validate("CUS-1", uses));
        Assert.Equal(1, unchecked((int)4_294_967_297L)); // documents what the naive cast would have done
    }

    [Fact]
    public void HappyValue_IsDiscountTimesUses()
    {
        Assert.Null(RedemptionRules.Validate("CUS-1", 3));
        Assert.Equal(3 * 1_000L, RedemptionRules.ValueCents(1_000, 3)); // three uses of a $10.00 discount
        Assert.Equal(1_000L, RedemptionRules.ValueCents(1_000, 1));
        Assert.Equal(3 * 100_000L, RedemptionRules.ValueCents(100_000, RedemptionRules.MaxUsesPerRedemption));
    }

    [Fact]
    public void ValueArithmetic_ThrowsInsteadOfWrapping() =>
        Assert.Throws<OverflowException>(() => RedemptionRules.ValueCents(long.MaxValue, 2));

    [Theory]
    [InlineData("POISON")]
    [InlineData("CUS-POISON-7")]
    [InlineData("xxPOISONyy")]
    [InlineData("A-VERY-LONG-TOKEN-THAT-MERELY-CONTAINS-POISON-INSIDE")]
    public void PoisonSubstring_MatchesInsideLongerTokens(string reference) =>
        Assert.True(RedemptionRules.IsPoison(reference));

    [Theory]
    [InlineData("CUS-1")]
    [InlineData("POISO")]
    [InlineData("poison")]
    public void NonPoison_DoesNotMatch(string reference) =>
        Assert.False(RedemptionRules.IsPoison(reference));
}
