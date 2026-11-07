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
    public async Task In_game_report_methods_keep_the_client_payload_and_return_none()
    {
        var reports = new List<InGameReportSubmission>();
        IService service = new InGameReportService((report, _) =>
        {
            reports.Add(report);
            return ValueTask.CompletedTask;
        });
        var methods = new (string Method, PyValue[] Args)[]
        {
            ("report_inappropriate_language", [new PyLong(9001), new PyWString("reason"), new PyWString("time"), new PyWString("channel"), new PyWString("content")]),
            ("report_offensive_mail", [new PyLong(9002), new PyWString("mail reason")]),
            ("report_macro_use", [new PyLong(9003), new PyWString("macro reason")]),
            ("report_questionable_transaction", [new PyLong(9004), new PyWString("transaction reason")]),
            ("report_inappropriate_character_name", [new PyLong(9005), new PyWString("name reason")]),
            ("report_exploit_abuse", [new PyLong(9006), new PyWString("exploit reason")]),
            ("report_gambling", [new PyLong(9007), new PyWString("gambling reason")]),
            ("report_other", [new PyLong(9008), new PyWString("other reason")]),
        };
        var session = new ClientSession { CharacterId = 42, UserId = 77 };

        foreach (var (method, args) in methods)
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

        Assert.Equal(methods.Select(method => method.Method), reports.Select(report => report.MethodName));
        Assert.Equal("inappropriate_language", reports[0].ReportType);
        Assert.Equal(42L, reports[0].ReporterCharacterId);
        Assert.Equal<long?>(77L, reports[0].ReporterAccountId);
        Assert.Equal(9001L, reports[0].TargetCharacterId);
        Assert.Equal("reason", reports[0].Comments);
        Assert.Equal("time", reports[0].ChatTime);
        Assert.Equal("channel", reports[0].ChatChannel);
        Assert.Equal("content", reports[0].ChatContent);
    }

    [Fact]
    public async Task In_game_report_without_a_sink_does_not_acknowledge_a_lost_report()
    {
        IService service = new InGameReportService();

        var error = await Assert.ThrowsAsync<MachoWrappedException>(() => service.CallAsync(new ServiceCall
        {
            Method = "report_other",
            Args = [new PyLong(9001), new PyWString("reason")],
            Session = new ClientSession { CharacterId = 42, UserId = 77 },
        }).AsTask());

        Assert.Equal("INGAME_REPORT_STORAGE_UNAVAILABLE", error.Error.UserErrorMessage);
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
