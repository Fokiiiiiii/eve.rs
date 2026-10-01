// Port of EVE.js server/src/services/market/marketRules.js (828823a), ADR 0009.
// Double arithmetic and operation order here are the external fee contract; settlement uses decimal cents.
namespace Eve.Market;

public sealed record MarketSkillLevels(int Trade = 0, int Retail = 0, int Broker = 0, int Visibility = 0,
    int Daytrading = 0, int Wholesale = 0, int Margin = 0, int Marketing = 0, int Procurement = 0,
    int Accounting = 0, int Tycoon = 0);
public sealed record MarketLimits(int Count, double TransactionTaxRate, int SellRange, int BuyRange, int Visibility, int ModifyRange);
public sealed record MarketContext(double BrokerCommissionRate, double SccSurchargeRate, double ModificationFeeDiscount, MarketLimits Limits);
public sealed record FeeInfo(double Amount, double RawPercentage, bool UsingMinimumValue);

public static class MarketRules
{
    public const double MaximumOrderPrice = 9223372036854.0;
    public const int RangeStation = -1;
    public const int RangeSolarSystem = 0;
    public const int RangeConstellation = 4;
    public const int RangeRegion = 32767;

    public static bool IsAllowedDuration(int days) => days is 0 or 1 or 3 or 7 or 14 or 30 or 90;
    public static bool IsAllowedBuyRange(int range) => range is -1 or 0 or 1 or 2 or 3 or 4 or 5 or 10 or 20 or 30 or 40 or RangeRegion;
    private static int Skill(int level) => Math.Clamp(level, 0, 5);
    private static int RangeOf(int level) => Skill(level) switch { 0 => -1, 1 => 0, 2 => 5, 3 => 10, 4 => 20, _ => RangeRegion };

    /// <summary>JavaScript Math.round, including negative half values and its non-finite fallback.</summary>
    public static double RoundIsk(double amount) => double.IsFinite(amount) ? Math.Floor(amount * 100 + 0.5) / 100 : 0;

    public static MarketContext Context(MarketSkillLevels skills, bool isStation, double factionStanding = 0,
        double corpStanding = 0, int warFaction = 0, int solarSystem = 0)
    {
        var broker = 3.0 / 100;
        if (isStation)
        {
            broker -= Skill(skills.Broker) * 0.3 / 100;
            broker -= factionStanding * 0.0003 + corpStanding * 0.0002;
        }
        var tax = 8.0 / 100;
        tax *= 1 - Skill(skills.Accounting) * 0.11;
        tax *= 1 - (solarSystem == 30100000 && warFaction is 500018 or 500019 ? 0.5 : 0);
        return new MarketContext(broker, isStation ? 0 : 0.5 / 100, 0.5 + 0.06 * Skill(skills.Margin),
            new MarketLimits(5 + Skill(skills.Trade) * 4 + Skill(skills.Retail) * 8
                + Skill(skills.Wholesale) * 16 + Skill(skills.Tycoon) * 32, tax,
                RangeOf(skills.Marketing), RangeOf(skills.Procurement), RangeOf(skills.Visibility), RangeOf(skills.Daytrading)));
    }

    public static FeeInfo BrokerFee(MarketContext context, double? oldAmount, double newAmount)
    {
        newAmount = RoundIsk(newAmount);
        if (newAmount < 0)
        {
            return new FeeInfo(0, context.BrokerCommissionRate, false);
        }
        var fee = 0.0;
        var modification = 0.0;
        if (oldAmount is null)
        {
            fee = newAmount * context.BrokerCommissionRate;
        }
        else
        {
            modification = newAmount * context.BrokerCommissionRate * (1 - context.ModificationFeeDiscount);
            if (newAmount > RoundIsk(oldAmount.Value))
            {
                fee = (newAmount - RoundIsk(oldAmount.Value)) * context.BrokerCommissionRate;
            }
        }
        var total = RoundIsk(fee + modification);
        return new FeeInfo(total <= 100 ? 100 : total, context.BrokerCommissionRate, total <= 100);
    }

    public static FeeInfo SccSurcharge(MarketContext context, double? oldAmount, double newAmount)
    {
        newAmount = RoundIsk(newAmount);
        var rate = context.SccSurchargeRate;
        if (newAmount < 0 || rate <= 0)
        {
            return new FeeInfo(0, rate, false);
        }
        var fee = 0.0;
        var modification = 0.0;
        if (oldAmount is null)
        {
            fee = newAmount * rate;
        }
        else
        {
            modification = newAmount * rate * (1 - context.ModificationFeeDiscount);
            if (newAmount > RoundIsk(oldAmount.Value))
            {
                fee = (newAmount - RoundIsk(oldAmount.Value)) * rate;
            }
        }
        var total = RoundIsk(fee + modification);
        return new FeeInfo(total <= 25 ? 25 : total, rate, total <= 25);
    }

    public static double SalesTax(MarketContext context, double grossAmount)
    {
        grossAmount = RoundIsk(grossAmount);
        return grossAmount > 0 ? RoundIsk(grossAmount * context.Limits.TransactionTaxRate) : 0;
    }
}
