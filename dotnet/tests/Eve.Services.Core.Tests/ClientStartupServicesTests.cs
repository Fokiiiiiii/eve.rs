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
    public void Fighter_manager_returns_empty_tube_space_and_ability_state()
    {
        var result = Assert.IsType<PyTuple>(new FighterMgrService().GetFightersForShip(null!));

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
    public void Fighter_manager_keeps_EveJS_wire_shapes_until_the_fighter_backend_is_wired()
    {
        var service = new FighterMgrService();
        static ServiceCall Call(string method, params PyValue[] args) => new()
        {
            Method = method,
            Args = args,
            Session = new ClientSession(),
        };
        static void AssertUnavailable(PyDict errors, int id)
        {
            var entry = Assert.Single(errors.Entries, entry => entry.Key.Equals(new PyInt(id)));
            var error = Assert.IsType<PyTuple>(entry.Value);
            Assert.Equal(new PyStr("CustomNotify"), error.Items[0]);
            var details = Assert.IsType<PyDict>(error.Items[1]);
            Assert.Contains(new KeyValuePair<PyValue, PyValue>(
                new PyStr("notify"),
                new PyStr("Fighter commands are not connected to the space simulation.")), details.Entries);
        }

        var launchErrors = Assert.IsType<PyDict>(service.LaunchFightersFromTubes(Call(
            "LaunchFightersFromTubes", new PyList([new PyInt(901), new PyInt(902)]))));
        Assert.Equal(2, launchErrors.Entries.Count);
        AssertUnavailable(launchErrors, 901);
        AssertUnavailable(launchErrors, 902);

        var recallErrors = Assert.IsType<PyDict>(service.RecallFightersToTubes(Call(
            "RecallFightersToTubes", new PyList([new PyInt(903)]))));
        AssertUnavailable(recallErrors, 903);

        Assert.Same(PyNone.Instance, service.ExecuteMovementCommandOnFighters(Call(
            "ExecuteMovementCommandOnFighters",
            new PyList([new PyInt(903)]), new PyStr("ORBIT"), new PyInt(901), new PyInt(5000))));
        Assert.Same(PyBool.False, service.CmdAbandonFighter(Call("CmdAbandonFighter", new PyInt(903))));
        Assert.Same(PyBool.False, service.CmdScoopAbandonedFighterFromSpace(Call(
            "CmdScoopAbandonedFighterFromSpace", new PyInt(903), new PyInt(5))));
        Assert.Same(PyBool.False, service.LoadFightersToTube(Call(
            "LoadFightersToTube", new PyInt(903), new PyInt(5))));
        Assert.Same(PyBool.False, service.UnloadTubeToFighterBay(Call(
            "UnloadTubeToFighterBay", new PyInt(5))));
    }

    [Fact]
    public void Missing_ship_kill_counter_returns_zero_for_hangar_rendering()
    {
        Assert.Equal(new PyInt(0), new ShipKillCounterService().GetItemKillCountPlayer(null!));
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
