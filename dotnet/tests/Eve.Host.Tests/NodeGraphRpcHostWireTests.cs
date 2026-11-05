using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [Fact]
    public async Task Node_graph_calls_keep_client_wire_replies_through_the_default_core_host()
    {
        Start(sessionOpened: session =>
        {
            session.Set("accountRole", new PyLong(GameMasterRoles.Gml));
            session.Set("rolesAtAll", new PyLong(GameMasterRoles.Staff));
        });
        var (front, open) = await OpenFrontAsync();

        Assert.Same(PyNone.Instance,
            await HostRpcAsync(front, open, 1, "node_graph", "process_client_graph_message",
                new PyInt(100), new PyInt(200), new PyStr("client_action"), new PyStr("warp")));
        Assert.Same(PyNone.Instance,
            await HostRpcAsync(front, open, 2, "node_graph", "process_client_blackboard_request",
                new PyInt(100), new PyInt(200), new PyStr("mission_state")));
        Assert.Empty(Assert.IsType<PyDict>(
            await HostRpcAsync(front, open, 3, "node_graph", "qa_subscribe")).Entries);
        Assert.Same(PyNone.Instance,
            await HostRpcAsync(front, open, 4, "node_graph", "qa_unsubscribe"));
        Assert.Same(PyNone.Instance,
            await HostRpcAsync(front, open, 5, "node_graph", "qa_start_node_graph",
                new PyInt(100), new PyDict([])));
        Assert.Same(PyNone.Instance,
            await HostRpcAsync(front, open, 6, "node_graph", "qa_start_node_graph", new PyInt(100)));
        Assert.Same(PyNone.Instance,
            await HostRpcAsync(front, open, 7, "node_graph", "qa_stop_node_graph", new PyInt(200)));
        Assert.Same(PyNone.Instance,
            await HostRpcAsync(front, open, 8, "node_graph", "qa_start_node", new PyInt(200), new PyInt(7)));
        Assert.Same(PyNone.Instance,
            await HostRpcAsync(front, open, 9, "node_graph", "qa_stop_node", new PyInt(200), new PyInt(7)));
        Assert.Same(PyNone.Instance,
            await HostRpcAsync(front, open, 10, "node_graph", "qa_stop_active_nodes", new PyInt(200)));
    }
}
