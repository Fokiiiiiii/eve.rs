using System.Text.Json.Nodes;
using Eve.Marshal;
using Eve.Services;

namespace Eve.Services.Core.Tests;

public sealed class ClientStartupServicesTests
{
    [Fact]
    public void Security_manager_returns_empty_modified_systems_dict()
    {
        var service = new SecurityMgrService();
        var result = service.GetModifiedSystems(null!);

        Assert.Empty(Assert.IsType<PyDict>(result).Entries);
    }

    [Fact]
    public void Alert_beancount_requests_a_stack_trace_with_a_pair()
    {
        var service = new AlertService();
        var result = service.BeanCount(null!);

        Assert.Equal(new PyTuple([PyNone.Instance, PyNone.Instance]), result);
    }

    [Fact]
    public void Alert_stacktrace_response_has_a_unique_id_and_detail_mode()
    {
        var service = new AlertService();
        var result = Assert.IsType<PyTuple>(service.SendClientStackTraceAlert(new ServiceCall
        {
            Method = "SendClientStackTraceAlert",
            Args = [new PyTuple([new PyInt(123), new PyStr("stack-key")]), new PyStr("trace"), new PyStr("Error")],
            Session = new ClientSession(),
        }));

        Assert.IsType<PyLong>(result.Items[0]);
        Assert.Equal(new PyStr("D"), result.Items[1]);
    }

    [Fact]
    public async Task Fighter_manager_returns_empty_tube_space_and_ability_state()
    {
        var result = Assert.IsType<PyTuple>(await new FighterMgrService().GetFightersForShip(null!));

        Assert.Equal(3, result.Items.Count);
        Assert.Empty(Assert.IsType<PyList>(result.Items[0]).Items);
        Assert.Empty(Assert.IsType<PyList>(result.Items[1]).Items);
        Assert.Empty(Assert.IsType<PyDict>(result.Items[2]).Entries);
    }

    [Fact]
    public void Fighter_manager_exposes_all_EveLogic_fighter_rpc_methods()
    {
        var methods = typeof(FighterMgrService).GetMethods()
            .Where(method => method.DeclaringType == typeof(FighterMgrService))
            .Select(method => method.Name)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Contains("CmdAbandonFighter", methods);
        Assert.Contains("CmdScoopAbandonedFighterFromSpace", methods);
        Assert.Contains("ExecuteMovementCommandOnFighters", methods);
        Assert.Contains("LaunchFightersFromTubes", methods);
        Assert.Contains("LoadFightersToTube", methods);
        Assert.Contains("RecallFightersToTubes", methods);
        Assert.Contains("UnloadTubeToFighterBay", methods);
    }

    [Fact]
    public async Task Fighter_manager_matches_EveJS_no_space_fallbacks_and_dispatches_asynchronously()
    {
        var service = new FighterMgrService();
        static ServiceCall Call(string method, params PyValue[] args) => new()
        {
            Method = method,
            Args = args,
            Session = new ClientSession(),
        };

        var launch = Assert.IsType<PyDict>(await service.LaunchFightersFromTubes(Call(
            "LaunchFightersFromTubes", new PyList([new PyInt(901), new PyInt(902)]))));
        Assert.Empty(launch.Entries);

        var recall = Assert.IsType<PyDict>(await service.RecallFightersToTubes(Call(
            "RecallFightersToTubes", new PyList([new PyInt(903), new PyInt(903), new PyInt(0)]))));
        var recalled = Assert.Single(recall.Entries);
        Assert.Equal(new PyInt(903), recalled.Key);
        Assert.Same(PyNone.Instance, recalled.Value);

        Assert.Same(PyNone.Instance, await service.ExecuteMovementCommandOnFighters(Call(
            "ExecuteMovementCommandOnFighters",
            new PyList([new PyInt(903)]), new PyStr("ORBIT"), new PyInt(901), new PyInt(5000))));
        Assert.Same(PyBool.False, await service.CmdAbandonFighter(Call("CmdAbandonFighter", new PyInt(903))));
        Assert.Same(PyBool.False, await service.CmdScoopAbandonedFighterFromSpace(Call(
            "CmdScoopAbandonedFighterFromSpace", new PyInt(903), new PyInt(5))));
        Assert.Same(PyBool.False, await service.LoadFightersToTube(Call(
            "LoadFightersToTube", new PyInt(903), new PyInt(5))));
        Assert.Same(PyBool.False, await service.UnloadTubeToFighterBay(Call(
            "UnloadTubeToFighterBay", new PyInt(5))));

        string? dispatchedMethod = null;
        service.RpcHandler = async call =>
        {
            await Task.Yield();
            dispatchedMethod = call.Method;
            return PyBool.True;
        };
        var handled = await ((IService)service).CallAsync(Call("CmdAbandonFighter", new PyInt(903)));
        Assert.Same(PyBool.True, handled);
        Assert.Equal("CmdAbandonFighter", dispatchedMethod);
    }

    [Fact]
    public async Task Fighter_ability_slot_rpcs_return_per_fighter_none_entries_when_unwired()
    {
        IService service = new FighterMgrService();
        var session = new ClientSession();
        foreach (var method in new[] { "CmdActivateAbilitySlots", "CmdDeactivateAbilitySlots" })
        {
            Assert.True(service.HasMethod(method));
            var result = Assert.IsType<PyDict>(await service.CallAsync(new ServiceCall
            {
                Method = method,
                Args = [new PyList([new PyInt(901), new PyInt(901), new PyInt(0)]), new PyInt(1)],
                Session = session,
            }));
            var entry = Assert.Single(result.Entries);
            Assert.Equal(new PyInt(901), entry.Key);
            Assert.Same(PyNone.Instance, entry.Value);
        }
    }

    [Fact]
    public async Task Fighter_ability_slot_callback_receives_EveJS_arguments_and_returns_error_map()
    {
        var service = new FighterMgrService();
        var session = new ClientSession();
        FighterMgrService.AbilitySlotRequest? observed = null;
        service.RpcHandler = _ => throw new InvalidOperationException("Use the typed ability-slot callback when installed.");
        service.AbilitySlotHandler = (request, _) =>
        {
            observed = request;
            IReadOnlyDictionary<long, PyValue> result = new Dictionary<long, PyValue>
            {
                [901] = PyNone.Instance,
                [902] = new PyStr("slot error"),
            };
            return ValueTask.FromResult<IReadOnlyDictionary<long, PyValue>?>(result);
        };

        var response = Assert.IsType<PyDict>(await ((IService)service).CallAsync(new ServiceCall
        {
            Method = "CmdActivateAbilitySlots",
            Args = [new PyList([new PyInt(901), new PyInt(902)]), new PyInt(3), new PyInt(777), new PyStr("ignored")],
            Session = session,
        }));

        Assert.NotNull(observed);
        Assert.Equal(FighterMgrService.AbilitySlotOperation.Activate, observed.Operation);
        Assert.Same(session, observed.Session);
        Assert.Equal(new long[] { 901, 902 }, observed.FighterIds);
        Assert.Equal(new PyInt(3), observed.AbilitySlotId);
        Assert.Equal(new PyInt(777), observed.Target);
        Assert.Equal(2, response.Entries.Count);
        Assert.Contains(response.Entries, entry => entry.Key == new PyInt(901) && entry.Value == PyNone.Instance);
        Assert.Contains(response.Entries, entry => entry.Key == new PyInt(902) && entry.Value == new PyStr("slot error"));
    }

    [Fact]
    public async Task Missing_ship_kill_counter_returns_zero_for_hangar_rendering()
    {
        IService service = new ShipKillCounterService();
        var result = await service.CallAsync(new ServiceCall
        {
            Method = "GetItemKillCountPlayer",
            Args = [PyNone.Instance],
            Session = new ClientSession(),
        });

        Assert.Equal(new PyInt(0), result);
    }

    [Fact]
    public async Task Ship_kill_counter_does_not_query_nonpositive_item_ids()
    {
        var queried = false;
        IService service = new ShipKillCounterService(_ =>
        {
            queried = true;
            return Task.FromResult(new System.Text.Json.Nodes.JsonObject());
        });
        var result = await service.CallAsync(new ServiceCall
        {
            Method = "GetItemKillCountPlayer",
            Args = [new PyInt(0)],
            Session = new ClientSession(),
        });

        Assert.Equal(new PyInt(0), result);
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
