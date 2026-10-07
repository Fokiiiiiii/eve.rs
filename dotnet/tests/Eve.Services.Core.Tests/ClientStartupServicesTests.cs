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
