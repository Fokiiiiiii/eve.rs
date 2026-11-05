using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;

namespace Eve.Services.Core.Tests;

public sealed class FastCheckoutServiceTests
{
    [Fact]
    public async Task Checkout_is_disabled_and_offers_are_empty_until_payment_settlement_exists()
    {
        IService service = new FastCheckoutService(NewEdenStoreCatalog.Default);

        Assert.Equal(PyBool.False, await service.CallAsync(Call("IsFastCheckoutEnabledForUser")));
        Assert.Equal(PyBool.True, await service.CallAsync(Call("ClearFastCheckoutCacheForUser")));
        Assert.Empty(Assert.IsType<PyList>(Field(
            Assert.IsType<PyDict>(await service.CallAsync(Call("GetOffersForUser"))), "plex")).Items);
    }

    [Theory]
    [InlineData("IsFastCheckoutEnabledForUser")]
    [InlineData("ClearFastCheckoutCacheForUser")]
    [InlineData("GetTestingConfiguration")]
    [InlineData("GetOffersForUser")]
    public async Task Fixed_fast_checkout_reads_refuse_client_arguments(string method)
    {
        IService service = new FastCheckoutService(NewEdenStoreCatalog.Default);
        foreach (var call in new[]
        {
            new ServiceCall { Method = method, Args = [new PyInt(42)], Session = new ClientSession() },
            new ServiceCall { Method = method, Args = [], Kwargs = Py.Dict(("userID", new PyInt(42))), Session = new ClientSession() },
        })
        {
            var error = await Assert.ThrowsAsync<MachoWrappedException>(() => service.CallAsync(call).AsTask());
            Assert.Equal("FAST_CHECKOUT_ARGUMENTS_INVALID", error.Error.UserErrorMessage);
        }
    }

    [Fact]
    public async Task Buy_offer_fails_closed_even_for_a_valid_offer()
    {
        IService service = new FastCheckoutService(NewEdenStoreCatalog.Default);
        var exception = await Assert.ThrowsAsync<MachoWrappedException>(async () =>
            await service.CallAsync(new ServiceCall
            {
                Method = "BuyOffer",
                Args = [],
                Kwargs = Py.Dict(("offer", Py.KeyVal(("id", new PyInt(400100)))), ("password", new PyStr("ignored"))),
                Session = new ClientSession { CharacterId = 100, UserId = 9001 },
                CallId = new PyInt(77),
            }));

        Assert.Equal("FAST_CHECKOUT_UNAVAILABLE", exception.Error.UserErrorMessage);
    }

    private static ServiceCall Call(string method) => new()
    {
        Method = method,
        Args = [],
        Session = new ClientSession { CharacterId = 100, UserId = 9001 },
    };

    private static PyValue Field(PyDict dict, string name) =>
        dict.Entries.Single(entry => JsSemantics.StringValue(entry.Key) == name).Value;
}
