using Eve.Services.Slash;

namespace Eve.Services.Dogma.Tests;

public class IskCommandTests
{
    [Theory]
    [InlineData("1000000", 1000000)]
    [InlineData("me 500", 500)]
    [InlineData("ME -250.50", -250.50)]
    [InlineData("0.01", 0.01)]
    public void The_amount_is_a_whole_number_of_cents(string argument, double expected)
    {
        Assert.Equal((decimal)expected, IskCommands.ParseAmount(argument));
    }

    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("abc")]
    [InlineData("1.001")]
    [InlineData("1 2")]
    [InlineData("other 5")]
    public void Anything_else_is_the_usage(string argument)
    {
        Assert.Null(IskCommands.ParseAmount(argument));
    }
}
