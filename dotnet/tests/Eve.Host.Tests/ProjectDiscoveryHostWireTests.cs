using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [Fact]
    public async Task Project_discovery_analysis_kredits_reads_character_state_through_registered_host_wire()
    {
        var analysisKredits = 12.8;
        long? readCharacterId = null;
        var service = new ProjectDiscoveryService(new ProjectDiscoveryRpcCallbacks(
            ReadPlayerStateAsync: (characterId, _) =>
            {
                readCharacterId = characterId;
                return ValueTask.FromResult<ProjectDiscoveryPlayerState?>(
                    new ProjectDiscoveryPlayerState(AnalysisKredits: analysisKredits));
            }));

        ClientSession? openedSession = null;
        Start(
            configure: registry => registry.Register(service),
            sessionOpened: session => openedSession = session,
            strictRpc: true);
        var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        _ = await front.ReceiveAsync();
        Assert.NotNull(openedSession);
        openedSession!.CharacterId = 42;

        async Task<PyValue?> ReadKredits(uint callId)
        {
            var request = Packets.CallWithKwargs(
                open,
                callId,
                "ProjectDiscovery",
                "get_player_analysis_kredits",
                new PyTuple([]),
                Py.Dict(("machoVersion", new PyInt(1))));
            await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(request)));
            return Result(MarshalCodec.Decode(Assert.IsType<Send>(await front.ReceiveAsync()).Payload));
        }

        Assert.Equal(new PyInt(12), await ReadKredits(1));
        Assert.Equal(42, readCharacterId);

        analysisKredits = -1;
        Assert.Equal(new PyInt(0), await ReadKredits(2));
        Assert.Equal(42, readCharacterId);
    }
}
