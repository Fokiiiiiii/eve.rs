namespace Eve.Wallet;

/// <summary>What a member credit gave each side: the member's share and the corporation's loyalty point tax.</summary>
public sealed record LoyaltyMemberCredit(long MemberAmount, long CorporationTax);

/// <summary>The corporation loyalty point tax of a credit (the Viridian expansion notes: corporation tax on member LP from factional warfare, incursions and missions).</summary>
public static class LoyaltyTax
{
    /// <summary>
    /// The tax of an amount at the corporation's rate, rounded down. A rate above 1 is a percentage (the convention of the corporation tax fields);
    /// no corporation (0) or a rate of 0 or less is no tax.
    /// </summary>
    public static long TaxOf(long amount, decimal corporationTaxRate, long corporationId)
    {
        if (amount <= 0 || corporationTaxRate <= 0 || corporationId <= 0)
            return 0;
        var fraction = corporationTaxRate > 1 ? corporationTaxRate / 100m : corporationTaxRate;
        return Math.Min(amount, (long)Math.Floor(amount * fraction));
    }
}
