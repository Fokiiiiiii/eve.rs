using System.Text.Json.Nodes;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;

namespace Eve.Services.Core.Tests;

public sealed class ClientStartupServicesTests
{
    [Fact]
    public void Security_manager_returns_the_empty_indexed_rowset()
    {
        var service = new SecurityMgrService();
        var result = service.GetModifiedSystems(null!);
        var descriptor = Descriptor.FromHeader(new PyObjectEx(ObjectExKind.Ex1,
            Py.Tuple(new PyToken("blue.DBRowDescriptor"), Py.Tuple(Py.Tuple(
                Py.Tuple(new PyStr("solarSystemID"), new PyInt(0x03)),
                Py.Tuple(new PyStr("security"), new PyInt(0x05)),
                Py.Tuple(new PyStr("modifiedSecurity"), new PyInt(0x05))))), [], []));
        var expected = new PyObjectEx(ObjectExKind.Ex2,
            Py.Tuple(Py.Tuple(new PyToken("carbon.common.script.sys.crowset.CIndexedRowset")),
                Py.Dict(("header", descriptor.Header), ("columnName", new PyStr("solarSystemID")))), [], []);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void Alert_beancount_requests_a_stack_trace_with_a_pair()
    {
        var service = new AlertService();
        var result = service.BeanCount(new ServiceCall
        {
            Method = "BeanCount", Args = [new PyInt(123)], Session = new ClientSession(),
        });

        Assert.Equal(new PyTuple([PyNone.Instance, PyNone.Instance]), result);
    }

    [Fact]
    public void Alert_stacktrace_response_reuses_ids_by_hash_and_uses_detail_mode()
    {
        var service = new AlertService();
        ServiceCall Call(int hash) => new()
        {
            Method = "SendClientStackTraceAlert",
            Args = [new PyTuple([new PyInt(hash), new PyStr("stack-key")]), new PyStr("trace"), new PyStr("Error")],
            Session = new ClientSession(),
        };
        var first = Assert.IsType<PyTuple>(service.SendClientStackTraceAlert(Call(123)));
        var repeated = Assert.IsType<PyTuple>(service.SendClientStackTraceAlert(Call(123)));
        var distinct = Assert.IsType<PyTuple>(service.SendClientStackTraceAlert(Call(124)));

        Assert.IsType<PyLong>(first.Items[0]);
        Assert.Equal(first.Items[0], repeated.Items[0]);
        Assert.NotEqual(first.Items[0], distinct.Items[0]);
        Assert.Equal(new PyStr("D"), first.Items[1]);
    }

    [Fact]
    public async Task Alert_stacktrace_rpc_refuses_malformed_and_oversized_inputs()
    {
        var service = (IService)new AlertService();
        var session = new ClientSession();
        async Task Refused(ServiceCall call)
        {
            var error = await Assert.ThrowsAsync<MachoWrappedException>(() => service.CallAsync(call).AsTask());
            Assert.Equal("ALERT_ARGUMENTS_INVALID", error.Error.UserErrorMessage);
        }

        await Refused(new ServiceCall { Method = "BeanCount", Args = [], Session = session });
        await Refused(new ServiceCall
        {
            Method = "SendClientStackTraceAlert", Session = session,
            Args = [new PyTuple([new PyInt(1), new PyStr("key")]), new PyStr("trace"), new PyStr("Error"), new PyInt(2), new PyInt(3)],
        });
        await Refused(new ServiceCall
        {
            Method = "SendClientStackTraceAlert", Session = session,
            Args = [new PyList(Enumerable.Repeat((PyValue)new PyInt(1), 1_000).ToArray()), new PyStr("trace"), new PyStr("Error")],
        });
        await Refused(new ServiceCall
        {
            Method = "SendClientStackTraceAlert", Session = session,
            Args = [new PyTuple([new PyInt(1), new PyStr("key")]), new PyBytes(new byte[16_385]), new PyStr("Error")],
        });
        await Refused(new ServiceCall
        {
            Method = "SendClientStackTraceAlert", Session = session,
            Args = [new PyTuple([new PyInt(1), new PyStr("key")]), new PyStr("trace"), new PyStr("Error")],
            Kwargs = new PyDict([]),
        });
    }

    [Fact]
    public async Task Fighter_manager_refuses_each_fighter_when_no_provider_is_installed()
    {
        var service = new FighterMgrService();
        var session = new ClientSession();
        ServiceCall Call(string method, params PyValue[] args) => new() { Method = method, Args = args, Session = session };

        // a dictionary answer names each id with a user error: never a success (None, False, or an empty answer)
        foreach (var (method, args) in new (string, PyValue[])[]
        {
            ("RecallFightersToTubes", [new PyList([new PyInt(903)])]),
            ("CmdActivateAbilitySlots", [new PyList([new PyInt(903)]), new PyInt(1)]),
            ("CmdDeactivateAbilitySlots", [new PyList([new PyInt(903)]), new PyInt(1)]),
            ("LaunchFightersFromTubes", [new PyList([new PyInt(159)])]),
        })
        {
            var answer = Assert.IsType<PyDict>(await ((IService)service).CallAsync(Call(method, args)));
            var entry = Assert.Single(answer.Entries);
            Assert.Equal(FighterMgrService.UnavailableText, UserMessage(entry.Value));
        }

        // a call that answers one thing (a bool, None, the fighters of the ship) refuses the whole call with a user error
        foreach (var (method, args) in new (string, PyValue[])[]
        {
            ("GetFightersForShip", []),
            ("LoadFightersToTube", [new PyInt(903), new PyInt(159)]),
            ("UnloadTubeToFighterBay", [new PyInt(159)]),
            ("ExecuteMovementCommandOnFighters", [new PyList([new PyInt(903)]), new PyStr("STOP")]),
            ("CmdAbandonFighter", [new PyInt(903)]),
            ("CmdScoopAbandonedFighterFromSpace", [new PyInt(903), new PyInt(158)]),
        })
        {
            var thrown = await Assert.ThrowsAsync<MachoWrappedException>(() => ((IService)service).CallAsync(Call(method, args)).AsTask());
            Assert.Equal(FighterMgrService.UnavailableText, thrown.Error.UserErrorMessage);
        }
    }

    [Fact]
    public async Task Fighter_manager_routes_each_call_to_the_provider_and_answers_what_it_answers()
    {
        var provider = new RecordingFighters();
        var service = new FighterMgrService { Provider = provider };

        var answer = await ((IService)service).CallAsync(new ServiceCall
        {
            Method = "CmdAbandonFighter",
            Args = [new PyInt(903)],
            Session = new ClientSession(),
        });

        Assert.Equal(new PyStr("CmdAbandonFighterAsync"), answer);
        Assert.Equal(new[] { "CmdAbandonFighterAsync" }, provider.Calls);
    }

    private static string UserMessage(PyValue payload) =>
        new MachoError(MachoErrorType.WrappedException, payload).UserErrorMessage;

    /// <summary>A provider that answers each call with its own name and records the calls it was given.</summary>
    private sealed class RecordingFighters : ISpaceFighters
    {
        public List<string> Calls { get; } = [];

        private ValueTask<PyValue> Answer(string name)
        {
            Calls.Add(name);
            return ValueTask.FromResult<PyValue>(new PyStr(name));
        }

        public ValueTask<PyValue> GetFightersForShipAsync(ServiceCall call) => Answer(nameof(GetFightersForShipAsync));
        public ValueTask<PyValue> LoadFightersToTubeAsync(ServiceCall call) => Answer(nameof(LoadFightersToTubeAsync));
        public ValueTask<PyValue> UnloadTubeToFighterBayAsync(ServiceCall call) => Answer(nameof(UnloadTubeToFighterBayAsync));
        public ValueTask<PyValue> LaunchFightersFromTubesAsync(ServiceCall call) => Answer(nameof(LaunchFightersFromTubesAsync));
        public ValueTask<PyValue> RecallFightersToTubesAsync(ServiceCall call) => Answer(nameof(RecallFightersToTubesAsync));
        public ValueTask<PyValue> CmdActivateAbilitySlotsAsync(ServiceCall call) => Answer(nameof(CmdActivateAbilitySlotsAsync));
        public ValueTask<PyValue> CmdDeactivateAbilitySlotsAsync(ServiceCall call) => Answer(nameof(CmdDeactivateAbilitySlotsAsync));
        public ValueTask<PyValue> ExecuteMovementCommandOnFightersAsync(ServiceCall call) => Answer(nameof(ExecuteMovementCommandOnFightersAsync));
        public ValueTask<PyValue> CmdAbandonFighterAsync(ServiceCall call) => Answer(nameof(CmdAbandonFighterAsync));
        public ValueTask<PyValue> CmdScoopAbandonedFighterFromSpaceAsync(ServiceCall call) => Answer(nameof(CmdScoopAbandonedFighterFromSpaceAsync));
    }

    [Fact]
    public async Task Missing_ship_kill_counter_provider_fails_closed()
    {
        IService service = new ShipKillCounterService();
        var error = await Assert.ThrowsAsync<MachoWrappedException>(() => service.CallAsync(new ServiceCall
        {
            Method = "GetItemKillCountPlayer",
            Args = [new PyInt(74000001)],
            Session = new ClientSession(),
        }).AsTask());

        Assert.Equal("SHIP_KILL_COUNTER_UNAVAILABLE", error.Error.UserErrorMessage);
    }

    [Fact]
    public async Task Ship_kill_counter_refuses_nonpositive_item_ids_before_provider_access()
    {
        var queried = false;
        IService service = new ShipKillCounterService(_ =>
        {
            queried = true;
            return Task.FromResult(new System.Text.Json.Nodes.JsonObject());
        });
        var error = await Assert.ThrowsAsync<MachoWrappedException>(() => service.CallAsync(new ServiceCall
        {
            Method = "GetItemKillCountPlayer",
            Args = [new PyInt(0)],
            Session = new ClientSession(),
        }).AsTask());

        Assert.Equal("SHIP_KILL_COUNTER_ITEM_ID_INVALID", error.Error.UserErrorMessage);
        Assert.False(queried);
    }

    [Fact]
    public async Task Ship_kill_counter_refuses_malformed_wire_shapes_before_provider_access()
    {
        var queried = false;
        IService service = new ShipKillCounterService(_ =>
        {
            queried = true;
            return Task.FromResult(new JsonObject());
        });
        var session = new ClientSession();
        foreach (var call in new[]
        {
            new ServiceCall { Method = "GetItemKillCountPlayer", Args = [], Session = session },
            new ServiceCall { Method = "GetItemKillCountPlayer", Args = [new PyInt(42), new PyInt(43)], Session = session },
            new ServiceCall { Method = "GetItemKillCountPlayer", Args = [new PyInt(42)], Kwargs = Py.Dict(("itemID", new PyInt(42))), Session = session },
        })
        {
            var error = await Assert.ThrowsAsync<MachoWrappedException>(() => service.CallAsync(call).AsTask());
            Assert.Equal("SHIP_KILL_COUNTER_ARGUMENTS_INVALID", error.Error.UserErrorMessage);
        }

        var wrongType = await Assert.ThrowsAsync<MachoWrappedException>(() => service.CallAsync(new ServiceCall
        {
            Method = "GetItemKillCountPlayer",
            Args = [new PyReal(42)],
            Session = session,
        }).AsTask());
        Assert.Equal("SHIP_KILL_COUNTER_ITEM_ID_INVALID", wrongType.Error.UserErrorMessage);

        Assert.False(queried);
    }

    [Theory]
    [InlineData(37d, 37)]
    [InlineData(1002d, 999)]
    [InlineData(-2d, 0)]
    [InlineData(12.9d, 12)]
    public async Task Ship_kill_counter_reads_and_normalizes_EveJS_GameStore_counts(double storedCount, int expected)
    {
        var table = new JsonObject
        {
            ["ships"] = new JsonObject
            {
                ["74000001"] = new JsonObject
                {
                    ["playerKills"] = JsonValue.Create(storedCount),
                },
            },
        };
        var service = new ShipKillCounterService(_ => Task.FromResult(table));
        var result = await ((IService)service).CallAsync(new ServiceCall
        {
            Method = "GetItemKillCountPlayer",
            Args = [new PyLong(74000001)],
            Session = new ClientSession(),
        });

        Assert.Equal(new PyInt(expected), result);
    }

    [Fact]
    public async Task Empty_login_campaigns_return_client_safe_no_campaign_shapes()
    {
        var callbacks = new EmptyLoginRewardCallbacks();
        var session = new ClientSession();
        var login = await new LoginCampaignMgrService(callbacks).CallAsync(new ServiceCall
        {
            Method = "get_client_campaign_state",
            Args = [],
            Session = session,
        });
        var seasonal = await new SeasonalLoginCampaignMgrService(callbacks).CallAsync(new ServiceCall
        {
            Method = "get_active_campaign",
            Args = [],
            Session = session,
        });

        Assert.Same(PyNone.Instance, login);
        Assert.Equal(new PyTuple([PyNone.Instance, PyNone.Instance, PyNone.Instance, PyNone.Instance]), seasonal);
    }
}
