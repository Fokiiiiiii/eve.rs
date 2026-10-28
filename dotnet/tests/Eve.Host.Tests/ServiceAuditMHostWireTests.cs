using System.Text.Json.Nodes;
using Eve.Host;
using Eve.Macho;
using Eve.Marshal;
using Eve.Scheduler;
using Eve.Services;
using Eve.Services.Core;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [Fact]
    public async Task Milestone_calls_without_an_account_return_none_without_touching_storage()
    {
        using var timers = new LocalTimers();
        Start(configure: registry => registry.Register(new MilestoneMgrService(null!, timers, () => FixedNowMs)));

        var front = await Handshaken(await ConnectAsync());
        var open = Open(userId: 0);
        await front.SendAsync(open);
        _ = await front.ReceiveAsync();

        Assert.Same(PyNone.Instance, await HostRpcAsync(front, open, 1, "milestoneMgr", "ProcessCharacterLogon"));
        Assert.Same(PyNone.Instance, await HostRpcAsync(front, open, 2, "milestoneMgr", "ClaimRewards", new PyInt(1)));
    }

    [Fact]
    public async Task Mining_scan_returns_sorted_three_value_rows_over_host_wire()
    {
        var callbacks = new GameRpcCallbacks(ReadMiningScanResultsAsync: (_, _) =>
            ValueTask.FromResult<IReadOnlyList<MiningScanResult>>(
                [new(20, 34, 8, 5), new(10, 35, 9, 5)]));
        Start(gameRpcCallbacks: callbacks);
        var (front, open) = await OpenFrontAsync();

        var scans = Assert.IsType<PyList>(await HostRpcAsync(front, open, 1, "miningScanMgr", "perform_scan"));
        Assert.Equal(2, scans.Items.Count);
        Assert.Equal([10, 35, 9], Assert.IsType<PyList>(scans.Items[0]).Items.Select(WireInteger));
        Assert.Equal([20, 34, 8], Assert.IsType<PyList>(scans.Items[1]).Items.Select(WireInteger));
    }

    [Fact]
    public async Task Mission_objectives_return_a_list_and_the_active_dungeon_reply_is_a_bool_over_host_wire()
    {
        const long pilot = 90_001;
        const long agent = 500;
        var mission = new JsonObject { ["agentID"] = agent, ["runtimeStatus"] = "accepted", ["objectiveMode"] = "agent" };
        var service = new MissionTrackerMgrService(
            readAllMissionObjectives: (_, agentId, _, _) =>
                ValueTask.FromResult<PyValue?>(MissionTrackerGameStoreProjection.ReadAllObjectives(mission, agentId, false)),
            isInActiveDungeon: (_, _) => ValueTask.FromResult<bool?>(true));
        Start(configure: registry => registry.Register(service),
            sessionOpened: session => session.Set("charid", new PyInt(pilot)));

        var (front, open) = await OpenFrontAsync();
        var objectives = Assert.IsType<PyList>(await HostRpcAsync(front, open, 1, "missionTrackerMgr",
            "GetAllMissionObjectives", new PyInt(agent)));
        Assert.Equal(2, objectives.Items.Count);
        Assert.Equal("TalkToAgent", WireText(Assert.IsType<PyTuple>(objectives.Items[0]).Items[0]));
        Assert.Equal(agent, WireInteger(Assert.IsType<PyTuple>(objectives.Items[0]).Items[1]));
        Assert.Same(PyBool.True, await HostRpcAsync(front, open, 2, "missionTrackerMgr", "IsInActiveDungeonID",
            new PyInt(agent)));
    }
}
