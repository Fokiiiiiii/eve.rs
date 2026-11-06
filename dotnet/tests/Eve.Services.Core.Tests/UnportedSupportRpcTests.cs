using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;

namespace Eve.Services.Core.Tests;

public sealed class UnportedSupportRpcTests
{
    [Fact]
    public void Core_routes_register_the_missing_support_services()
    {
        var services = CoreServices.Create(new NoAccounts(), () => 0);

        Assert.Contains(services, service => service.Name == "ingamereport");
        Assert.Contains(services, service => service.Name == "nonDiminishingInjectionMgr");
    }

    [Fact]
    public async Task In_game_report_methods_forward_the_exact_wire_call_and_return_none()
    {
        var calls = new List<(string Method, ServiceCall Call)>();
        IService service = new InGameReportService((method, call, _) =>
        {
            calls.Add((method, call));
            return ValueTask.CompletedTask;
        });
        var methods = new[]
        {
            "report_inappropriate_language",
            "report_offensive_mail",
            "report_macro_use",
            "report_questionable_transaction",
            "report_inappropriate_character_name",
            "report_exploit_abuse",
            "report_gambling",
            "report_other",
        };
        var session = new ClientSession { CharacterId = 42 };
        var args = new PyValue[]
        {
            new PyInt(9001), new PyStr("reason"), new PyStr("time"), new PyStr("channel"), new PyStr("content"),
        };

        foreach (var method in methods)
        {
            Assert.True(service.HasMethod(method));
            var result = await service.CallAsync(new ServiceCall
            {
                Method = method,
                Args = args,
                Session = session,
            });
            Assert.Same(PyNone.Instance, result);
        }

        Assert.Equal(methods, calls.Select(call => call.Method));
        Assert.All(calls, entry =>
        {
            Assert.Same(session, entry.Call.Session);
            Assert.Equal(args, entry.Call.Args);
        });

        IService withoutBackend = new InGameReportService();
        Assert.Same(PyNone.Instance, await withoutBackend.CallAsync(new ServiceCall
        {
            Method = methods[0],
            Args = args,
            Session = session,
        }));
    }

    [Fact]
    public async Task Non_diminishing_injection_count_reads_the_session_character_and_defaults_to_zero()
    {
        long observedCharacter = 0;
        IService service = new NonDiminishingInjectionMgrService((characterId, _) =>
        {
            observedCharacter = characterId;
            return ValueTask.FromResult(4);
        });
        var session = new ClientSession { CharacterId = 42 };
        var call = new ServiceCall
        {
            Method = "GetAvailableNonDiminishingInjections",
            Args = [],
            Session = session,
        };

        Assert.True(service.HasMethod(call.Method));
        Assert.Equal(new PyInt(4), await service.CallAsync(call));
        Assert.Equal(42, observedCharacter);

        IService empty = new NonDiminishingInjectionMgrService();
        Assert.Equal(new PyInt(0), await empty.CallAsync(call));
        Assert.Equal(new PyInt(0), await service.CallAsync(new ServiceCall
        {
            Method = "GetAvailableNonDiminishingInjections",
            Args = [],
            Session = new ClientSession(),
        }));
    }

    private sealed class NoAccounts : IOmegaAccountStore
    {
        public string? GetOmegaExpiry(long accountId) => OmegaLicenses.DefaultOmegaExpiryText;
    }
}
