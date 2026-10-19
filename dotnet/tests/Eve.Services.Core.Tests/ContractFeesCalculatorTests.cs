using Eve.Services.Core;
using Xunit;

namespace Eve.Services.Core.Tests;

public sealed class ContractFeesCalculatorTests
{
    [Fact]
    public void Public_item_exchange_uses_minimum_charges_and_no_deposit()
    {
        var fees = ContractFeesCalculator.Calculate(1, 1, 1_000_000m, 0m, 1440, 0, 0, 1m);

        Assert.Equal(0.004m, fees.BrokerFeeRate);
        Assert.Equal(10_000L, fees.BrokerFee);
        Assert.Equal(0.01m, fees.SalesTaxRate);
        Assert.Equal(10_000L, fees.SalesTax);
        Assert.Equal(0L, fees.Deposit);
    }

    [Fact]
    public void Private_courier_uses_tenth_cost_and_deposit_minimum()
    {
        var fees = ContractFeesCalculator.Calculate(3, 0, 10_000_000m, 0m, 1440, 0, 0, 1m);

        Assert.Equal(0.0004m, fees.BrokerFeeRate);
        Assert.Equal(10_000L, fees.BrokerFee);
        Assert.Equal(0.001m, fees.SalesTaxRate);
        Assert.Equal(10_000L, fees.SalesTax);
        Assert.Equal(10_000L, fees.Deposit);
    }

    [Fact]
    public void Extra_expiry_day_applies_broker_percentage_and_multiplier()
    {
        var fees = ContractFeesCalculator.Calculate(1, 1, 10_000_000m, 0m, 2880, 0, 0, 1m);

        Assert.Equal(0.0045m, fees.BrokerFeeRate);
        Assert.Equal(47_250L, fees.BrokerFee);
    }

    [Theory]
    [InlineData(0, 1, 1440)]
    [InlineData(4, 1, 1440)]
    [InlineData(1, 4, 1440)]
    [InlineData(1, 1, 0)]
    public void Invalid_type_availability_or_expiry_is_rejected(short contractType, short availability, int minutes)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ContractFeesCalculator.Calculate(contractType, availability, 0m, 0m, minutes, 0, 0, 1m));
    }
}
