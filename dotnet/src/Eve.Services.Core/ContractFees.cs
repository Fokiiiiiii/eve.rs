namespace Eve.Services.Core;

public sealed record ContractFees(decimal BrokerFeeRate, decimal BrokerFee, decimal SalesTaxRate, long SalesTax, long Deposit);

/// <summary>Calculates the V24.01 contract creation charges in whole ISK.</summary>
public static class ContractFeesCalculator
{
    private const decimal BrokerMinimum = 10_000m;
    private const decimal BrokerMaximum = 100_000_000m;
    private const decimal SalesTaxMinimum = 10_000m;
    private const decimal SalesTaxMaximum = 1_000_000_000m;
    private const decimal DepositMinimum = 100_000m;
    private const decimal CourierDepositMinimum = 10_000m;
    private const decimal DepositMaximum = 100_000_000m;

    public static ContractFees Calculate(
        short contractType,
        short availability,
        decimal price,
        decimal reward,
        int expiryMinutes,
        int brokerRelationsLevel,
        int accountingLevel,
        decimal brokerFeeAdjustment)
    {
        if (contractType is < 1 or > 3 || availability is < 0 or > 3 ||
            price < 0 || reward < 0 || expiryMinutes < 1 ||
            brokerRelationsLevel is < 0 or > 5 || accountingLevel is < 0 or > 5 ||
            brokerFeeAdjustment <= 0)
            throw new ArgumentOutOfRangeException(nameof(contractType), "contract fee inputs are outside the V24 contract limits");

        var multiplier = contractType == 3 ? 0.1m : 1m;
        var baseValue = Math.Max(price, reward);
        var additionalDays = expiryMinutes / 1440 - 1;
        var salesRate = (0.01m - accountingLevel * 0.001m) * multiplier;
        var maxTax = SalesTaxMaximum * (1m - accountingLevel * 0.1m);
        var salesTax = TruncateWholeIsk(Math.Min(Math.Max(baseValue * salesRate, SalesTaxMinimum), maxTax));

        var brokerRate = (0.004m - brokerRelationsLevel * 0.0002m) * brokerFeeAdjustment * multiplier;
        brokerRate += additionalDays * 0.0005m;
        var maxBrokerFee = BrokerMaximum * (1m - brokerRelationsLevel * 0.05m);
        var brokerFee = TruncateWholeIsk(Math.Min(Math.Max(baseValue * brokerRate, BrokerMinimum), maxBrokerFee)) *
            (1m + additionalDays * 0.05m);

        var deposit = 0L;
        if (availability == 0)
        {
            var minimum = contractType == 3 ? CourierDepositMinimum : DepositMinimum;
            deposit = TruncateWholeIsk(Math.Min(Math.Max(baseValue * 0.01m * multiplier, minimum), DepositMaximum));
        }

        return new ContractFees(brokerRate, brokerFee, salesRate, salesTax, deposit);
    }

    private static long TruncateWholeIsk(decimal amount) => checked((long)decimal.Truncate(amount));
}
