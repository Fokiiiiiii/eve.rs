using Eve.FactionWarfare;
using Eve.Host;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    private static void AssertRankInfo(PyValue? value, long characterId, long factionId, long currentRank)
    {
        var fields = Assert.IsType<PyDict>(Assert.IsType<PyObject>(value).Args);
        Assert.Equal(4, fields.Entries.Count);
        PyValue? Field(string name) => fields.Entries
            .Where(entry => JsSemantics.StringValue(entry.Key) == name)
            .Select(entry => entry.Value)
            .SingleOrDefault();
        Assert.Equal(new PyInt(characterId), Field("characterID"));
        Assert.Equal(new PyInt(factionId), Field("factionID"));
        Assert.Equal(new PyInt(factionId), Field("warFactionID"));
        Assert.Equal(new PyInt(currentRank), Field("currentRank"));
    }

    [Fact]
    public async Task Faction_warfare_rank_and_enlistment_calls_round_trip_through_host_registration()
    {
        const long characterId = 90_000_001;
        const long corporationEnlistedCharacterId = 90_000_002;
        const long factionId = 500_001;
        const long corporationFactionId = 500_002;
        var service = new FacWarMgrService(new FwService(null!, null!), new FacWarMgrCallbacks(
            ReadCharacterAsync: (id, _) => ValueTask.FromResult<FwCharacterWarfareState?>(
                id == characterId ? new FwCharacterWarfareState(factionId, 0)
                    : id == corporationEnlistedCharacterId ? new FwCharacterWarfareState(null, 0)
                    : null),
            SaveCharacterAsync: (_, _, _, _) => ValueTask.FromResult(true),
            ReadStandingAsync: (_, _, _) => ValueTask.FromResult(3.5),
            NowMs: () => FixedNowMs,
            ReadWarFactionAsync: (id, _) => ValueTask.FromResult(id switch
            {
                characterId => factionId,
                corporationEnlistedCharacterId => corporationFactionId,
                _ => 0,
            })));
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

        async Task<PyValue?> CallWithKwargs(long id, string method, PyValue kwargs, params PyValue[] args)
        {
            await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
                Packets.CallWithKwargs(open, id, "facWarMgr", method, new PyTuple(args), kwargs))));
            return Result(MarshalCodec.Decode(Assert.IsType<Send>(await front.ReceiveAsync()).Payload));
        }

        Task<PyValue?> Call(long id, string method, params PyValue[] args) =>
            CallWithKwargs(id, method, PyNone.Instance, args);

        var expectedRank = new PyInt(FwRules.RankOf(3.5));
        Assert.Equal(expectedRank, await Call(1, "CheckForRankChange"));
        AssertRankInfo(await CallWithKwargs(2, "GetMyCharacterRankInfo", PyNone.Instance),
            characterId, factionId, expectedRank.Value);
        AssertRankInfo(await CallWithKwargs(3, "GetCharacterRankInfo",
            Py.Dict(("machoVersion", new PyInt(1))), new PyLong(characterId)),
            characterId, factionId, expectedRank.Value);
        AssertRankInfo(await Call(4, "GetCharacterRankInfo", new PyInt(corporationEnlistedCharacterId)),
            corporationEnlistedCharacterId, corporationFactionId, expectedRank.Value);
        Assert.Same(PyNone.Instance, await Call(5, "GetCharacterRankInfo", PyNone.Instance));

        var overview = Assert.IsType<PyList>(await Call(6, "GetMyCharacterRankOverview"));
        Assert.Single(overview.Items);
        AssertRankInfo(Assert.Single(overview.Items), characterId, factionId, expectedRank.Value);
        var externalOverview = Assert.IsType<PyList>(await Call(7, "GetCharacterRankOverview", new PyLong(corporationEnlistedCharacterId)));
        Assert.Single(externalOverview.Items);
        AssertRankInfo(Assert.Single(externalOverview.Items), corporationEnlistedCharacterId, corporationFactionId, expectedRank.Value);
        Assert.Equal(new PyInt(1000180), await Call(8, "GetFactionMilitiaCorporation", new PyInt(factionId)));

        // The client omits the Infrastructure Hub entry when no item is returned from the ballpark.
        Assert.Same(PyNone.Instance, await Call(9, "GetControlBunkerIDInSystem"));
        Assert.False(service.HasMethod("RefreshCorps"));
    }

    [Fact]
    public async Task Character_enlistment_uses_the_dispatch_clock_and_notifies_before_the_rpc_reply()
    {
        const long characterId = 90_000_003;
        const long factionId = 500_001;
        var state = new FwCharacterWarfareState(null, Py.FileTimeFromUnixMs(FixedNowMs + 1_000));
        var saveCalls = 0;
        var saveSucceeds = false;
        ClientSession? session = null;
        var service = new FacWarMgrService(new FwService(null!, null!), new FacWarMgrCallbacks(
            ReadCharacterAsync: (_, _) => ValueTask.FromResult<FwCharacterWarfareState?>(state),
            SaveCharacterAsync: (_, enlistedFactionId, cooldown, _) =>
            {
                saveCalls++;
                if (!saveSucceeds)
                    return ValueTask.FromResult(false);
                state = new FwCharacterWarfareState(enlistedFactionId, cooldown);
                return ValueTask.FromResult(true);
            },
            ReadStandingAsync: (_, _, _) => ValueTask.FromResult(3.5),
            NowMs: () => FixedNowMs));
        Start(
            configure: registry => registry.Register(service),
            sessionOpened: opened =>
            {
                opened.Set("charid", new PyLong(characterId));
                session = opened;
            });

        var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        _ = await front.ReceiveAsync();

        async Task<Send> Join(long id)
        {
            await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
                Packets.CallWithKwargs(open, id, "facWarMgr", "JoinFactionAsCharacter",
                    new PyTuple([new PyInt(factionId)]),
                    Py.Dict(("machoVersion", new PyInt(1)))))));
            return Assert.IsType<Send>(await front.ReceiveAsync());
        }

        var refused = await Join(1);
        AssertWrappedError(refused, "eveexceptions.UserError", "CooldownActive");
        var refusalWire = System.Text.Encoding.ASCII.GetString(refused.Payload.Span);
        Assert.Contains("CustomInfo", refusalWire);
        Assert.Contains("UI/FactionWarfare/CooldownActive", refusalWire);
        Assert.Equal(0, saveCalls);

        state = new FwCharacterWarfareState(null, Py.FileTimeFromUnixMs(FixedNowMs - 1));
        var expiredState = state;
        var failedSave = await Join(2);
        AssertWrappedError(failedSave, "exceptions.RuntimeError", "facWarMgr.JoinFactionAsCharacter");
        Assert.Equal(expiredState, state);
        Assert.Null(session!.Get("warfactionid"));
        Assert.Equal(1, saveCalls);

        saveSucceeds = true;
        var accepted = await Join(3);
        var notification = Assert.IsType<MachoPacket>(MachoPacket.Decode(MarshalCodec.Decode(accepted.Payload)));
        Assert.Equal(MachoMessage.SessionChangeNotification, notification.Type);
        Assert.Equal(2, saveCalls);
        Assert.Equal(new FwCharacterWarfareState(factionId, 0), state);
        var fields = ((PyDict)((PyTuple)((PyTuple)notification.Payload).Items[1]).Items[1]).Entries;
        Assert.Equal(["warfactionid"], fields.Select(entry => T(entry.Key)));
        Assert.Equal(factionId, session!.GetInt("warfactionid"));

        var reply = Assert.IsType<Send>(await front.ReceiveAsync());
        Assert.Equal(MachoMessage.CallRsp, Assert.IsType<MachoPacket>(MachoPacket.Decode(MarshalCodec.Decode(reply.Payload))).Type);
        Assert.Same(PyNone.Instance, Result(MarshalCodec.Decode(reply.Payload)));
    }
}
