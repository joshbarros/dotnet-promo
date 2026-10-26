namespace Promo.Api.Modules.Redemptions;

public static class RedemptionRules
{
    public const int MaxUsesPerRedemption = 3;
    public const int MaxReferenceLength = 40;
    public const string PoisonToken = "POISON";

    /// <summary>Returns an error message, or null when the request is acceptable.</summary>
    public static string? Validate(string? reference, long uses)
    {
        if (string.IsNullOrWhiteSpace(reference))
            return "Customer reference is required.";
        if (reference.Trim().Length > MaxReferenceLength)
            return $"Customer reference must be at most {MaxReferenceLength} characters.";
        if (uses < 1)
            return "A redemption needs at least 1 use.";
        // Compared as long on purpose: narrowing 4294967297 to int would wrap to 1 and pass.
        if (uses > MaxUsesPerRedemption)
            return $"A redemption takes at most {MaxUsesPerRedemption} uses.";
        return null;
    }

    /// <summary>Case-sensitive substring match: a longer coupon that merely contains the poison matches too.</summary>
    public static bool IsPoison(string reference) => reference.Contains(PoisonToken, StringComparison.Ordinal);

    /// <summary>Discount per use times the uses redeemed, in cents. Overflow throws instead of wrapping.</summary>
    public static long ValueCents(long discountCents, int uses) => checked(discountCents * uses);
}
