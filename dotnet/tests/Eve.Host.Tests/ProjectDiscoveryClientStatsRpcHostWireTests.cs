using System.Text;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [Fact]
    public async Task Project_discovery_statistics_accepts_client_argument_forms_through_default_core_host_wire()
    {
        static PyBytes Bytes(string value) => new(Encoding.UTF8.GetBytes(value));
        static KeyValuePair<PyValue, PyValue> Pair(string key, PyValue value) => new(Bytes(key), value);

        var readCharacterIds = new List<long>();
        var state = new ProjectDiscoveryPlayerState(
            Rank: 3.7, Experience: 250.9, AnalysisKredits: 12.8, FinishedTutorial: true, TutorialLevel: 8.4);
        ClientSession? openedSession = null;
        Start(
            sessionOpened: session => openedSession = session,
            strictRpc: true,
            gameRpcCallbacks: new GameRpcCallbacks(ReadProjectDiscoveryState: (characterId, _) =>
            {
                readCharacterIds.Add(characterId);
                return ValueTask.FromResult<ProjectDiscoveryPlayerState?>(state);
            }));
        var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        _ = await front.ReceiveAsync();
        Assert.NotNull(openedSession);
        openedSession!.CharacterId = 42;

        async Task<PyValue?> Call(uint callId, string method, PyTuple args, PyDict kwargs)
        {
            var request = Packets.CallWithKwargs(open, callId, "ProjectDiscovery", method, args, kwargs);
            await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(request)));
            return Result(MarshalCodec.Decode(Assert.IsType<Send>(await front.ReceiveAsync()).Payload));
        }

        var transportKwargs = Py.Dict(("machoVersion", new PyInt(1)));
        var playerState = Assert.IsType<PyObject>(
            await Call(1, "get_player_state", new PyTuple([]), transportKwargs));
        Assert.Equal(Bytes("util.KeyVal"), playerState.Name);
        Assert.Equal(new PyDict(
        [
            Pair("rank", new PyInt(3)),
            Pair("experience", new PyInt(250)),
            Pair("analysisKredits", new PyInt(12)),
            Pair("finishedTutorial", PyBool.True),
            Pair("tutorialLevel", new PyInt(8)),
        ]), playerState.Args);
        Assert.Equal(new PyInt(12), await Call(2, "get_player_analysis_kredits", new PyTuple([]), transportKwargs));
        Assert.Equal(PyBool.True, await Call(3, "get_tutorial_completion_status", new PyTuple([]), transportKwargs));
        Assert.Equal(PyBool.True, await Call(4, "is_enabled", new PyTuple([]), transportKwargs));
        Assert.All(readCharacterIds, characterId => Assert.Equal(42, characterId));

        var expectedStatistics = new PyDict(
        [
            Pair("message", Bytes("Project Discovery API is not backed by live MMOS data in this emulator")),
            Pair("code", new PyInt(103010)),
            Pair("projectID", Bytes("mcgill-polyseg")),
        ]);
        Assert.Equal(expectedStatistics,
            await Call(5, "get_player_statistics", new PyTuple([]), transportKwargs));
        Assert.Equal(expectedStatistics,
            await Call(6, "get_player_statistics", new PyTuple([PyBool.False]), transportKwargs));
        Assert.Equal(expectedStatistics,
            await Call(7, "get_player_statistics", new PyTuple([PyBool.True]), transportKwargs));
        Assert.Equal(expectedStatistics,
            await Call(8, "get_player_statistics", new PyTuple([]),
                Py.Dict(("machoVersion", new PyInt(1)), ("get_history", PyBool.True))));
    }
}
