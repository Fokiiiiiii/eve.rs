using Eve.Marshal;
using Eve.Services;
using Eve.Services.Space;

namespace Eve.Services.Space.Tests;

public sealed class DynamicResourceCacheMgrTests
{
    [Fact]
    public async Task Cache_manager_exposes_reference_methods_and_shapes_without_a_backend()
    {
        var service = (IService)new DynamicResourceCacheMgrService();
        string[] methods = ["GetDynamicResourceSettings", "GetDBSMapData", "GetESSAgencyData", "GetESSSystemDetails", "GetESSReserveBankKeys"];
        Assert.All(methods, method => Assert.True(service.HasMethod(method), method));
        Assert.False(service.HasMethod("Unknown"));
        var session = new ClientSession();

        var settings = Assert.IsType<PyDict>(await Call(service, session, "GetDynamicResourceSettings"));
        Assert.Equal(17, settings.Entries.Count);
        Assert.Equal(new PyInt(9900), Value(settings, "autopaymentIntervalSeconds"));
        Assert.Equal(new PyReal(0.975), Value(settings, "mainBankPayRatio"));
        Assert.Equal(new PyDict([]), await Call(service, session, "GetDBSMapData"));
        Assert.Equal(new PyDict([]), await Call(service, session, "GetESSAgencyData"));
        Assert.Equal(
            new PyDict([
                new(new PyStr("autopaymentWindowState"), new PyInt(0)),
                new(new PyStr("activeLinks"), new PyInt(0)),
                new(new PyStr("totalPulses"), new PyInt(0)),
                new(new PyStr("reserveTimeWindowState"), new PyInt(0)),
                new(new PyStr("reserveTimeChosen"), new PyInt(0)),
            ]),
            await Call(service, session, "GetESSSystemDetails", new PyInt(30000142)));
        Assert.Equal(new PyList([]), await Call(service, session, "GetESSReserveBankKeys", new PyInt(30000142)));
    }

    [Fact]
    public async Task Cache_manager_returns_an_injected_backend_value()
    {
        var value = new PyDict([new(new PyStr("systems"), new PyList([new PyInt(30000142)]))]);
        var service = (IService)new DynamicResourceCacheMgrService((call, _) =>
            ValueTask.FromResult<PyValue?>(call.Method == "GetESSAgencyData" ? value : null));

        Assert.Same(value, await Call(service, new ClientSession(), "GetESSAgencyData"));
    }

    private static Task<PyValue> Call(IService service, ClientSession session, string method, params PyValue[] args) =>
        service.CallAsync(new ServiceCall { Method = method, Args = args, Session = session }).AsTask();

    private static PyValue Value(PyDict dict, string key) =>
        dict.Entries.Single(entry => entry.Key == new PyStr(key)).Value;
}