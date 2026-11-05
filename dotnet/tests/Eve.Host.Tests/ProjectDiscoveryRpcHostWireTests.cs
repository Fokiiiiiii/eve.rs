using System.Text;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [Fact]
    public async Task Project_discovery_tutorial_routes_use_the_host_wire_and_return_client_shapes()
    {
        var state = new ProjectDiscoveryPlayerState();
        var service = new ProjectDiscoveryService(new ProjectDiscoveryRpcCallbacks(
            ReadPlayerStateAsync: (_, _) => ValueTask.FromResult<ProjectDiscoveryPlayerState?>(state),
            TransitionTutorialAsync: (_, action, _) =>
            {
                state = action switch
                {
                    "reset" => state with { FinishedTutorial = false, TutorialLevel = 0 },
                    "skip" => state with { FinishedTutorial = true, TutorialLevel = 8 },
                    "increase" => state with
                    {
                        TutorialLevel = Math.Min(8, (int)state.TutorialLevel + 1),
                        FinishedTutorial = Math.Min(8, (int)state.TutorialLevel + 1) == 8,
                    },
                    _ => throw new InvalidOperationException(),
                };
                return ValueTask.FromResult<ProjectDiscoveryPlayerState?>(state);
            }));
        Start(configure: registry => registry.Register(service), sessionOpened: session => session.CharacterId = 900_681, strictRpc: true);

        using var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        _ = await front.ReceiveAsync();

        async Task<PyValue?> Call(uint id, string method, params PyValue[] args)
        {
            var request = Packets.CallWithKwargs(open, id, "ProjectDiscovery", method, Py.Tuple(args), ClientKwargs());
            await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(request)));
            var reply = MarshalCodec.Decode(Assert.IsType<Send>(await front.ReceiveAsync()).Payload);
            var replyState = ((PyTuple)((PyObject)reply).Args).Items;
            if (replyState[0] is PyInt { Value: not 7 })
                throw new InvalidOperationException(Encoding.ASCII.GetString(MarshalCodec.Encode(reply)));
            return Result(reply);
        }

        Assert.Equal(new PyInt(1), await Call(1, "get_project_id"));
        Assert.Equal(new PyInt(300), await Call(2, "get_total_needed_xp", new PyInt(3)));
        Assert.Equal(new PyInt(100), await Call(3, "get_tutorial_xp_reward"));
        Assert.Equal(PyBool.False, await Call(4, "get_is_player_entitled_to_tutorial_reward"));
        Assert.Equal(PyBool.False, await Call(5, "give_tutorial_rewards"));
        var skipped = await Call(6, "skip_tutorial");
        var skippedState = Assert.IsType<PyObject>(skipped);
        Assert.Equal("util.KeyVal", JsSemantics.StringValue(skippedState.Name));
        var stateFields = Assert.IsType<PyDict>(skippedState.Args).Entries
            .ToDictionary(entry => JsSemantics.StringValue(entry.Key)!, entry => entry.Value);
        Assert.Equal(new PyInt(1), stateFields["rank"]);
        Assert.Equal(new PyInt(0), stateFields["experience"]);
        Assert.Equal(new PyInt(0), stateFields["analysisKredits"]);
        Assert.Equal(PyBool.True, stateFields["finishedTutorial"]);
        Assert.Equal(new PyInt(8), stateFields["tutorialLevel"]);
        var stateNotification = Assert.IsType<Send>(await front.ReceiveAsync());
        var kreditsNotification = Assert.IsType<Send>(await front.ReceiveAsync());
        var notifications = Encoding.ASCII.GetString(stateNotification.Payload.Span) + Encoding.ASCII.GetString(kreditsNotification.Payload.Span);
        Assert.Contains("OnPlayerStateUpdated", notifications);
        Assert.Contains("OnAnalysisKreditsChange", notifications);
        Assert.Equal(PyBool.True, await Call(7, "get_tutorial_completion_status"));
        Assert.Equal(new PyInt(8), await Call(8, "get_tutorial_level"));
        _ = await Call(9, "reset_tutorial");
        Assert.Equal(new PyInt(0), await Call(10, "get_tutorial_level"));
        _ = await Call(11, "increase_tutorial_level");
        Assert.Equal(new PyInt(1), await Call(12, "get_tutorial_level"));
    }

    [Fact]
    public async Task Project_discovery_task_id_fix_is_registered_and_fails_closed_without_a_backend()
    {
        const long characterId = 900_679;
        Start(sessionOpened: session =>
        {
            session.CharacterId = characterId;
            session.Set("charid", new PyInt(characterId));
            session.Set("accountRole", new PyLong(Eve.Services.GameMasterRoles.Qa));
        });

        using var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        _ = await front.ReceiveAsync();

        await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(Packets.CallWithKwargs(
            open, 1, "ProjectDiscovery", "fix_task_id", Py.Tuple(new PyLong(123)), ClientKwargs()))));
        var response = MarshalCodec.Decode(Assert.IsType<Send>(await front.ReceiveAsync()).Payload);
        Assert.Equal(MachoMessage.ErrorResponse, Assert.IsType<MachoPacket>(MachoPacket.Decode(response)).Type);
        Assert.Contains("PROJECT_DISCOVERY_TASK_FIX_UNAVAILABLE", Encoding.ASCII.GetString(MarshalCodec.Encode(response)));
    }
}
