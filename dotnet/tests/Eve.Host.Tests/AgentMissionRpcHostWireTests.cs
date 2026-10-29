using System.Text.Json.Nodes;
using Eve.Marshal;
using Eve.Services.Core;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [Fact]
    public async Task Agent_mgr_plausible_mission_ids_keep_the_client_list_shape_over_host_wire()
    {
        var requestedAgentIds = new List<long>();
        var service = new AgentMissionService(
            (_, _) => ValueTask.FromResult<JsonObject?>(null),
            (_, _, _) => ValueTask.FromResult<IReadOnlyDictionary<long, JsonObject>>(new Dictionary<long, JsonObject>()),
            (_, _) => ValueTask.FromResult<JsonArray?>(null),
            rpcCallbacks: new AgentMissionRpcCallbacks(
                ReadPlausibleMissionIds: (agentId, _) =>
                {
                    requestedAgentIds.Add(agentId);
                    return ValueTask.FromResult<IReadOnlyList<long>>(agentId == 7004 ? [6101, 6102] : []);
                }));

        StartWithCoreServicesOverridden(registry => registry.Register(service), strictRpc: true);
        var (front, open) = await OpenFrontAsync();

        var missions = Assert.IsType<PyList>(
            await HostRpcAsync(front, open, 1, "agentMgr", "GetPlausibleMissionIDs", new PyInt(7004)));
        Assert.Equal(new PyList([new PyInt(6101), new PyInt(6102)]), missions);

        var noMissions = Assert.IsType<PyList>(
            await HostRpcAsync(front, open, 2, "agentMgr", "GetPlausibleMissionIDs", new PyInt(7005)));
        Assert.Empty(noMissions.Items);

        var invalidAgent = Assert.IsType<PyList>(
            await HostRpcAsync(front, open, 3, "agentMgr", "GetPlausibleMissionIDs", new PyInt(0)));
        Assert.Empty(invalidAgent.Items);
        Assert.Equal(new long[] { 7004, 7005 }, requestedAgentIds);
    }
}
