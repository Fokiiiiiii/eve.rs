using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;

namespace Eve.Host.Tests;

/// <summary>machoNet's answers, called directly: the values the client reads at start-up.</summary>
public class MachoNetServiceTests
{
    private const long NowMs = 1_790_646_377_123;

    private sealed class Info(params (string Name, string? Location)[] services) : IMachoNetInfo
    {
        public PyValue Statistics { get; set; } = Py.Tuple(new PyDict([]), new PyDict([]), new PyInt(1));
        public IReadOnlyList<KeyValuePair<string, string?>> Services() =>
            [.. services.Select(s => new KeyValuePair<string, string?>(s.Name, s.Location))];

        public PyDict GlobalConfig() => Py.Dict(("imageserverurl", new PyStr("http://x/")), ("defaultPortraitSaveSize", new PyInt(1024)), ("flag", new PyInt(0)));

        public int UserCount() => 0;

        public PyValue ClusterGameStatisticsForClient() => Statistics;
    }

    private static async Task<PyValue> Call(string method, PyValue[]? args = null, PyValue? kwargs = null, IMachoNetInfo? info = null)
    {
        IService service = new MachoNetService(info ?? new Info(("machoNet", null), ("invbroker", "station"), ("beyonce", "solarsystem2")), () => NowMs);
        return await service.CallAsync(new ServiceCall
        {
            Method = method,
            Args = args ?? [],
            Kwargs = kwargs ?? PyNone.Instance,
            Session = new ClientSession(),
        });
    }

    private static string? Text(PyValue? value) => Eve.Macho.JsSemantics.StringValue(value);

    [Fact]
    public async Task GetTime_is_the_clock_as_a_long_filetime()
    {
        Assert.Equal(new PyLong((NowMs * 10_000) + 116_444_736_000_000_000), await Call("GetTime"));
    }

    [Fact]
    public async Task GetInitVals_is_the_service_table_and_the_global_config()
    {
        var pair = Assert.IsType<PyTuple>(await Call("GetInitVals"));
        Assert.Equal(2, pair.Items.Count);
        var services = Assert.IsType<PyDict>(pair.Items[0]).Entries.ToDictionary(e => Text(e.Key)!, e => e.Value);
        Assert.Equal(PyNone.Instance, services["machoNet"]);
        Assert.Equal("station", Text(services["invbroker"]));
        Assert.Equal("solarsystem2", Text(services["beyonce"]));
        Assert.Equal(3, services.Count);
        var config = Assert.IsType<PyDict>(pair.Items[1]);
        Assert.Contains(config.Entries, e => Text(e.Key) == "defaultPortraitSaveSize");
    }

    [Fact]
    public async Task GetServiceInfo_and_GetGlobalConfig_are_the_two_halves_of_GetInitVals()
    {
        var pair = Assert.IsType<PyTuple>(await Call("GetInitVals"));
        Assert.Equal(pair.Items[0], await Call("GetServiceInfo"));
        Assert.Equal(pair.Items[1], await Call("GetGlobalConfig"));
    }

    [Fact]
    public async Task GetGlobalConfigValue_returns_the_key_and_its_value()
    {
        var found = Assert.IsType<PyTuple>(await Call("GetGlobalConfigValue", [new PyStr("imageserverurl")]));
        Assert.Equal("imageserverurl", Text(found.Items[0]));
        Assert.Equal("http://x/", Text(found.Items[1]));

        // a zero value is still a value
        var zero = Assert.IsType<PyTuple>(await Call("GetGlobalConfigValue", [new PyStr("flag")]));
        Assert.Equal(new PyInt(0), zero.Items[1]);
    }

    [Fact]
    public async Task GetGlobalConfigValue_takes_the_key_from_the_keyword_when_there_is_no_argument_and_trims_it()
    {
        var byKeyword = Assert.IsType<PyTuple>(await Call("GetGlobalConfigValue", kwargs: Py.Dict(("key", new PyStr("  imageserverurl ")))));
        Assert.Equal("imageserverurl", Text(byKeyword.Items[0]));
        var trimmed = Assert.IsType<PyTuple>(await Call("GetGlobalConfigValue", [new PyStr(" defaultPortraitSaveSize ")]));
        Assert.Equal(new PyInt(1024), trimmed.Items[1]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("noSuchKey")]
    public async Task GetGlobalConfigValue_answers_empty_strings_for_an_empty_or_unknown_key(string key)
    {
        var answer = Assert.IsType<PyTuple>(await Call("GetGlobalConfigValue", [new PyStr(key)]));
        Assert.Equal("", Text(answer.Items[0]));
        Assert.Equal("", Text(answer.Items[1]));
    }

    [Fact]
    public async Task GetGlobalConfigValue_without_a_key_answers_empty_strings()
    {
        var answer = Assert.IsType<PyTuple>(await Call("GetGlobalConfigValue"));
        Assert.Equal("", Text(answer.Items[0]));
    }

    [Fact]
    public async Task GetServerStatus_reports_the_release_and_at_least_one_user()
    {
        var status = Assert.IsType<PyTuple>(await Call("GetServerStatus"));
        var label = Assert.IsType<PyTuple>(status.Items[0]);
        Assert.Equal("/Carbon/MachoNet/ServerStatus/OK", Text(label.Items[0]));
        var details = Assert.IsType<PyDict>(status.Items[1]).Entries.ToDictionary(e => Text(e.Key)!, e => e.Value);
        Assert.Equal(new PyInt(496), details["macho_version"]);
        Assert.Equal(new PyInt(3_396_210), details["boot_build"]);
        Assert.Equal(new PyInt(1), details["cluster_usercount"]);
        Assert.Equal("OK", Text(details["status"]));
    }

    [Fact]
    public async Task GetClusterGameStatisticsForClient_returns_eve_counts_and_other_cluster_default()
    {
        var systemCounts = new PyDict([new KeyValuePair<PyValue, PyValue>(new PyInt(123), new PyInt(2))]);
        var stationCounts = new PyDict([new KeyValuePair<PyValue, PyValue>(new PyInt(123), new PyInt(1))]);
        var statistics = Py.Tuple(systemCounts, stationCounts, new PyInt(1));
        var info = new Info(("machoNet", null)) { Statistics = statistics };
        var actual = await Call("GetClusterGameStatisticsForClient", [new PyStr("EVE"), PyNone.Instance], info: info);
        Assert.Equal(statistics, actual);

        var fallback = Py.Dict(("fallback", new PyInt(1)));
        Assert.Same(fallback, await Call("GetClusterGameStatisticsForClient", [new PyStr("OTHER"), fallback], info: info));
    }

    [Fact]
    public async Task GetNodeID_is_the_proxy_node()
    {
        Assert.Equal(new PyInt(0xFFAA), await Call("GetNodeID"));
    }

    [Fact]
    public async Task An_unknown_method_is_none_from_the_service_itself_and_the_dispatcher_answers_the_error()
    {
        // The service's own CallAsync has nothing for it; PacketDispatcher checks HasMethod first (see HostServerTests).
        Assert.Equal(PyNone.Instance, await Call("NoSuchMethod"));
    }

    [Fact]
    public void The_global_config_carries_the_reference_defaults_and_follows_the_options()
    {
        var defaults = GlobalConfig.Build(new ClusterOptions()).Entries.ToDictionary(e => Text(e.Key)!, e => e.Value);
        Assert.Equal("http://127.0.0.1:26001/", Text(defaults["imageserverurl"]));
        Assert.Equal("http://127.0.0.1:26004/api", Text(defaults["bugReporting_BugReportServer"]));
        Assert.Equal(new PyInt(1024), defaults["defaultPortraitSaveSize"]);
        Assert.Equal(new PyInt(0), defaults["air_npe_enabled"]);
        Assert.Equal(new PyInt(0), defaults["HyperNetKillSwitch"]);
        Assert.Equal(new PyInt(3_500_000), defaults["HyperNetPlexPriceOverride"]);
        Assert.Equal(new PyInt(1), defaults["SkillPurchaseEnabled"]);
        Assert.Equal(new PyInt(1), defaults["expert_system_feature_enabled"]);
        // the 8 base keys and the 7 New Eden Store keys (their values: NewEdenStoreHostTests)
        Assert.Equal(15, defaults.Count);

        var changed = GlobalConfig.Build(new ClusterOptions
        {
            ImageServerUrl = "http://img/",
            NewCharacterExperienceEnabled = true,
            HyperNetKillSwitch = true,
            SkillPurchaseEnabled = false,
            ExpertSystemsEnabled = false,
            HyperNetPlexPriceOverride = 5,
        }).Entries.ToDictionary(e => Text(e.Key)!, e => e.Value);
        Assert.Equal("http://img/", Text(changed["imageserverurl"]));
        Assert.Equal(new PyInt(1), changed["air_npe_enabled"]);
        Assert.Equal(new PyInt(1), changed["HyperNetKillSwitch"]);
        Assert.Equal(new PyInt(0), changed["SkillPurchaseEnabled"]);
        Assert.Equal(new PyInt(0), changed["expert_system_feature_enabled"]);
        Assert.Equal(new PyInt(5), changed["HyperNetPlexPriceOverride"]);

          var cinematic = GlobalConfig.Build(new ClusterOptions { NewCharacterExperienceMode = "cinematic" })
              .Entries.ToDictionary(e => Text(e.Key)!, e => e.Value);
          Assert.Equal(new PyInt(1), cinematic["air_npe_enabled"]);
    }

    private static ClientSession AdminSession()
    {
        var session = new ClientSession();
        session.Set("accountRole", new PyLong(1L << 56)); // ROLE_ADMIN
        return session;
    }

    [Fact]
    public async Task SetGlobalConfigValue_without_a_write_backend_is_refused_not_dropped()
    {
        IService service = new MachoNetService(new Info(), () => NowMs);
        Assert.True(service.HasMethod("SetGlobalConfigValue"));

        var refused = await Assert.ThrowsAsync<MachoWrappedException>(() => service.CallAsync(new ServiceCall
        {
            Method = "SetGlobalConfigValue",
            Args = [new PyStr("new_mission_giver_enabled"), new PyStr("1")],
            Session = AdminSession(),
        }).AsTask());
        Assert.Equal("exceptions.RuntimeError", refused.Error.ClassName);
    }

    [Fact]
    public async Task SetGlobalConfigValue_is_for_admins_and_services_only()
    {
        var updates = new List<MachoNetGlobalConfigUpdate>();
        IService service = new MachoNetService(new Info(), () => NowMs, (update, _, _) =>
        {
            updates.Add(update);
            return ValueTask.CompletedTask;
        });

        var player = new ClientSession();
        player.Set("accountRole", new PyLong(1L << 61)); // ROLE_PLAYER: an ordinary account, no admin or service bit
        player.Set("role", new PyLong((1L << 56) | (1L << 60))); // forged ROLE_ADMIN and ROLE_SERVICE
        var refused = await Assert.ThrowsAsync<MachoWrappedException>(() => service.CallAsync(new ServiceCall
        {
            Method = "SetGlobalConfigValue",
            Args = [new PyStr("imageserverurl"), new PyStr("http://evil/")],
            Session = player,
        }).AsTask());
        Assert.Equal("exceptions.RuntimeError", refused.Error.ClassName);
        Assert.Empty(updates);

        Assert.Same(PyNone.Instance, await service.CallAsync(new ServiceCall
        {
            Method = "SetGlobalConfigValue",
            Args = [new PyStr("imageserverurl"), new PyStr("http://ok/")],
            Session = AdminSession(),
        }));
        Assert.Single(updates);

        var serviceAccount = new ClientSession();
        serviceAccount.Set("accountRole", new PyLong(1L << 60)); // ROLE_SERVICE
        Assert.Same(PyNone.Instance, await service.CallAsync(new ServiceCall
        {
            Method = "SetGlobalConfigValue",
            Args = [new PyStr("imageserverurl"), new PyStr("http://service/")],
            Session = serviceAccount,
        }));
        Assert.Equal(2, updates.Count);
    }

    [Fact]
    public async Task SetGlobalConfigValue_forwards_positional_and_keyword_flags_to_its_typed_backend()
    {
        var updates = new List<MachoNetGlobalConfigUpdate>();
        var session = new ClientSession();
        IService service = new MachoNetService(new Info(), () => NowMs, (update, caller, _) =>
        {
            Assert.Same(session, caller);
            updates.Add(update);
            return ValueTask.CompletedTask;
        });
        session.Set("accountRole", new PyLong(1L << 56)); // ROLE_ADMIN

        Assert.Same(PyNone.Instance, await service.CallAsync(new ServiceCall
        {
            Method = "SetGlobalConfigValue",
            Args = [new PyStr("ignored"), new PyStr("ignored")],
            Kwargs = Py.Dict(
                ("key", new PyStr(" new_mission_giver_enabled ")),
                ("value", new PyStr("1")),
                ("isUpdateClients", new PyStr("off"))),
            Session = session,
        }));
        Assert.Same(PyNone.Instance, await service.CallAsync(new ServiceCall
        {
            Method = "SetGlobalConfigValue",
            Args = [new PyStr("delete-me"), PyNone.Instance, new PyStr("yes"), new PyStr("off"), new PyInt(0)],
            Session = session,
        }));

        Assert.Equal(2, updates.Count);
        Assert.Equal(new MachoNetGlobalConfigUpdate("new_mission_giver_enabled", new PyStr("1"), false, true, false), updates[0]);
        Assert.Equal(new MachoNetGlobalConfigUpdate("delete-me", PyNone.Instance, true, false, false), updates[1]);
    }

    [Fact]
    public void Every_location_the_client_routes_by_is_in_the_table()
    {
        string[] station = ["station", "ship", "invbroker", "trademgr", "tradeMgr", "repairSvc", "reprocessingSvc", "insuranceSvc", "LPSvc", "LPStoreMgr"];
        string[] solarSystem = ["beyonce", "eject", "planetMgr", "planetOrbitalRegistryBroker", "posMgr"];
        Assert.All(station, name => Assert.Equal("station", ServiceLocations.Of(name)));
        Assert.All(solarSystem, name => Assert.Equal("solarsystem2", ServiceLocations.Of(name)));
        Assert.Equal("character", ServiceLocations.Of("dogmaIM"));
    }
}
