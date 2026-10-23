using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;

namespace Eve.Services.Core.Tests;

public sealed class MiningAndAbyssalRpcTests
{
    [Fact]
    public void Core_factory_registers_both_exact_service_names_and_methods()
    {
        var services = CoreServices.Create(new NoAccounts(), () => 0);

        var mining = Assert.Single(services, service => service.Name == "miningScanMgr");
        Assert.True(mining.HasMethod("perform_scan"));
        var abyssal = Assert.Single(services, service => service.Name == "abyssal_content_manager");
        Assert.True(abyssal.HasMethod("generate_abyss_npc_spawn_report"));
    }

    [Fact]
    public async Task Core_factory_wires_mining_and_abyssal_callbacks()
    {
        var session = new ClientSession { CharacterId = 42 };
        session.Set("accountRole", new PyLong(GameMasterRoles.Staff));
        AbyssalSpawnReportRequest? receivedReport = null;
        var report = new PyDict([Field("from_backend", PyBool.True)]);
        var callbacks = new GameRpcCallbacks(
            ReadMiningScanResultsAsync: (_, _) => ValueTask.FromResult<IReadOnlyList<MiningScanResult>>(
                [new(700, 123, 44, 8)]),
            AbyssalSpawnReportAsync: (request, _) =>
            {
                receivedReport = request;
                return ValueTask.FromResult<PyValue?>(report);
            });
        var services = CoreServices.Create(new NoAccounts(), () => 0,
            gameRpcCallbacks: callbacks);
        var mining = services.Single(service => service.Name == "miningScanMgr");
        var abyssal = services.Single(service => service.Name == "abyssal_content_manager");

        Assert.Equal(new PyList([new PyList([new PyInt(700), new PyInt(123), new PyInt(44)])]),
            await mining.CallAsync(Call("perform_scan", session)));
        Assert.Same(report, await abyssal.CallAsync(Call("generate_abyss_npc_spawn_report", session,
            new PyInt(172), new PyInt(77), new PyInt(2))));
        Assert.NotNull(receivedReport);
        Assert.Same(session, receivedReport.Session);
        Assert.Equal((172, 77, 2), (receivedReport.DifficultyTier, receivedReport.SpawnTableId, receivedReport.Repetitions));
    }

    [Fact]
    public async Task Mining_scan_returns_sorted_three_integer_rows_and_empty_without_backend()
    {
        var session = new ClientSession { CharacterId = 42 };
        var service = (IService)new MiningScanMgrService((actualSession, cancellationToken) =>
        {
            Assert.Same(session, actualSession);
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<IReadOnlyList<MiningScanResult>>(
            [
                new(9, 200, 50, 5),
                new(2, 101, 70, 5),
                new(1, 300, 25, 3),
            ]);
        });

        var rows = Assert.IsType<PyList>(await service.CallAsync(Call("perform_scan", session)));
        Assert.Equal(new PyList([
            new PyList([new PyInt(1), new PyInt(300), new PyInt(25)]),
            new PyList([new PyInt(2), new PyInt(101), new PyInt(70)]),
            new PyList([new PyInt(9), new PyInt(200), new PyInt(50)]),
        ]), rows);

        var unwired = (IService)new MiningScanMgrService();
        Assert.Empty(Assert.IsType<PyList>(await unwired.CallAsync(Call("perform_scan"))).Items);
    }

    [Fact]
    public async Task Abyssal_report_without_a_provider_refuses_instead_of_returning_a_fabricated_report()
    {
        var service = (IService)new AbyssalContentManagerService();
        var session = StaffSession();

        var error = await Assert.ThrowsAsync<MachoWrappedException>(() => service.CallAsync(Call(
            "generate_abyss_npc_spawn_report", session, new PyInt(172), new PyInt(777), new PyInt(1000))).AsTask());
        Assert.Equal("ABYSSAL_SPAWN_REPORT_UNAVAILABLE", error.Error.UserErrorMessage);
    }

    [Fact]
    public async Task Abyssal_report_callback_receives_normalized_arguments_and_session()
    {
        var session = StaffSession();
        AbyssalSpawnReportRequest? received = null;
        var payload = new PyDict([Field("provider", PyBool.True)]);
        var service = (IService)new AbyssalContentManagerService((request, _) =>
        {
            received = request;
            return ValueTask.FromResult<PyValue?>(payload);
        });

        var result = await service.CallAsync(Call("generate_abyss_npc_spawn_report", session,
            new PyInt(178), new PyLong(987654321), new PyInt(0)));

        Assert.Same(payload, result);
        Assert.NotNull(received);
        Assert.Same(session, received.Session);
        Assert.Equal(178, received.DifficultyTier);
        Assert.Equal(987654321, received.SpawnTableId);
        Assert.Equal(0, received.Repetitions);
    }

    private static ClientSession StaffSession()
    {
        var session = new ClientSession { CharacterId = 77 };
        session.Set("accountRole", new PyLong(GameMasterRoles.Staff));
        return session;
    }

    [Fact]
    public async Task Abyssal_lifecycle_rpc_refuses_malformed_client_targets_before_the_host_callback()
    {
        var callbacks = 0;
        var service = new AbyssalMgrService((_, _) =>
        {
            callbacks++;
            return ValueTask.FromResult<PyValue>(PyNone.Instance);
        });
        var session = new ClientSession { CharacterId = 42 };

        foreach (var call in new[]
        {
            Call("ClientIsReady", session, new PyInt(42)),
            Call("AbyssalEntranceDeployment", session, new PyReal(12.5), new PyInt(1)),
            Call("AbyssalEntranceDeployment", session, new PyLong(12), new PyInt(4)),
            Call("AbyssalEntranceGateActivation", session, new PyInt(0)),
            new ServiceCall
            {
                Method = "AbyssalGateActivation", Session = session, Args = [new PyInt(10)],
                Kwargs = new PyDict([new(new PyStr("otherFlag"), PyBool.True)]),
            },
        })
        {
            var error = await Assert.ThrowsAsync<MachoWrappedException>(() => service.CallAsync(call).AsTask());
            Assert.Equal("ABYSSAL_ARGUMENTS_INVALID", error.Error.UserErrorMessage);
        }

        Assert.Equal(0, callbacks);
    }

    private static ServiceCall Call(string method, params PyValue[] args) => Call(method, new ClientSession(), args);

    private static ServiceCall Call(string method, ClientSession session, params PyValue[] args) => new()
    {
        Method = method,
        Args = args,
        Session = session,
    };

    private static KeyValuePair<PyValue, PyValue> Field(string key, PyValue value) => new(new PyStr(key), value);

    private sealed class NoAccounts : IOmegaAccountStore
    {
        public string? GetOmegaExpiry(long accountId) => OmegaLicenses.DefaultOmegaExpiryText;
    }
}
