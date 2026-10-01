namespace Eve.Wallet;

/// <summary>The owner's requested ISK balance ceiling (2026-10-01); kept exact as decimal.</summary>
public static class WalletLimits
{
    public const decimal MaximumIsk = 1_145_141_919_364_364m;
}
public sealed class MoneyLimitException() : Exception("the ISK balance ceiling would be exceeded");
