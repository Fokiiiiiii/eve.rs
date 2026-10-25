using Eve.FactionWarfare;
using Eve.Host;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [Fact]
    public async Task Faction_warfare_rank_calls_round_trip_through_host_registration()
    {
        const long characterId = 90_000_001;
        const long factionId = 500_001;
        var service = new FacWarMgrService(new FwService(null!, null!), new FacWarMgrCallbacks(
            ReadCharacterAsync: (id, _) => ValueTask.FromResult<FwCharacterWarfareState?>(
                id == characterId ? new FwCharacterWarfareState(factionId, 0) : null),
            SaveCharacterAsync: (_, _, _, _) => ValueTask.FromResult(true),
            ReadStandingAsync: (_, _, _) => ValueTask.FromResult(3.5),
            NowMs: () => FixedNowMs));
        Start(
            configure: registry => registry.Register(service),
            sessionOpened: session =>
            {
                session.Set("charid", new PyLong(characterId));
                session.Set("warfactionid", new PyInt(factionId));
            });

        var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        _ = await front.ReceiveAsync();

        async Task<PyValue?> Call(long id, string method, params PyValue[] args)
        {
            await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
                Packets.Call(open, id, "facWarMgr", method, args))));
            return Result(MarshalCodec.Decode(Assert.IsType<Send>(await front.ReceiveAsync()).Payload));
        }

        var expectedRank = new PyInt(FwRules.RankOf(3.5));
        Assert.Equal(expectedRank, await Call(1, "CheckForRankChange"));
        Assert.IsType<PyObject>(await Call(2, "GetCharacterRankInfo", new PyLong(characterId)));
        Assert.IsType<PyObject>(await Call(3, "GetMyCharacterRankInfo"));
        var overview = Assert.IsType<PyList>(await Call(4, "GetMyCharacterRankOverview"));
        Assert.NotEmpty(overview.Items);
    }
}
