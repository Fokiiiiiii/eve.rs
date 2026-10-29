using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [Fact]
    public async Task Faction_warfare_enlistment_methods_return_client_shapes_over_registered_host_routes()
    {
        const long characterId = 42;
        const long corporationId = 99;
        const long factionId = 500001;
        const long corporationFactionId = 500002;
        const long now = 133000000000000000L;
        FwCharacterWarfareState state = new(factionId, 0);
        IReadOnlyList<long> allowedFactions = [factionId];
        var saves = 0;
        var service = new FwCharacterEnlistmentMgrService(new FwCharacterEnlistmentRpcCallbacks(
            ReadCharacterStateAsync: (id, _) => ValueTask.FromResult<FwCharacterWarfareState?>(id == characterId ? state : null),
            ReadCorporationFactionIdAsync: (id, _) => ValueTask.FromResult<long?>(id == corporationId ? corporationFactionId : null),
            ReadAllowedFactionsAsync: (id, _) => ValueTask.FromResult<IReadOnlyList<long>>(id == corporationId ? allowedFactions : []),
            SetAllowedFactionsAsync: (actor, corporation, factions, _) =>
            {
                Assert.Equal(characterId, actor);
                Assert.Equal(corporationId, corporation);
                allowedFactions = factions.ToArray();
                return ValueTask.FromResult(true);
            },
            SaveCharacterStateAsync: (id, directFactionId, cooldown, _) =>
            {
                Assert.Equal(characterId, id);
                saves++;
                state = new FwCharacterWarfareState(directFactionId, cooldown);
                return ValueTask.FromResult(true);
            },
            CurrentFileTime: () => now));

        Start(strictRpc: true, configure: registry => registry.Register(service), sessionOpened: session =>
        {
            session.CharacterId = characterId;
            session.Set("charid", new PyLong(characterId));
            session.Set("corpid", new PyLong(corporationId));
        });

        var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        await front.ReceiveAsync();
        var kwargs = new PyDict([new KeyValuePair<PyValue, PyValue>(new PyStr("machoVersion"), new PyInt(1))]);

        async Task<PyValue> Call(long callId, string method, params PyValue[] args)
        {
            await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
                Packets.CallWithKwargs(open, callId, "fwCharacterEnlistmentMgr", method, new PyTuple(args), kwargs))));
            while (true)
            {
                var decoded = MarshalCodec.Decode(Assert.IsType<Send>(await front.ReceiveAsync()).Payload);
                var response = Assert.IsType<MachoPacket>(MachoPacket.Decode(decoded));
                if (response.Type is MachoMessage.Notification or MachoMessage.SessionChangeNotification)
                    continue;
                Assert.True(response.Type == MachoMessage.CallRsp, $"{response.TypeName}: {response.Payload}");
                return Result(decoded)!;
            }
        }

        await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
            Packets.CallWithKwargs(open, 0, "fwCharacterEnlistmentMgr", "CreateMyDirectEnlistment",
                new PyTuple([new PyInt(corporationFactionId)]), kwargs))));
        var alreadyEnlisted = Assert.IsType<Send>(await front.ReceiveAsync());
        AssertWrappedError(alreadyEnlisted, "eveexceptions.UserError", "CustomInfo");
        Assert.Contains("UI/FactionWarfare/AlreadyInMilitia", System.Text.Encoding.ASCII.GetString(alreadyEnlisted.Payload.Span));
        Assert.Equal(new FwCharacterWarfareState(factionId, 0), state);
        Assert.Equal(0, saves);

        Assert.Equal(
            new PyList([new PyInt(factionId), new PyInt(factionId), new PyInt(corporationFactionId)]),
            await Call(1, "GetMyEnlistment"));
        var requested = new PyList([new PyInt(corporationFactionId), new PyInt(factionId)]);
        Assert.Equal(requested, await Call(2, "SetMyCorpAllowedEnlistmentFactions", requested));
        Assert.Equal(requested, await Call(3, "GetCorpAllowedEnlistmentFactions", new PyLong(corporationId)));
        Assert.Equal(PyNone.Instance, await Call(4, "RemoveMyDirectEnlistment"));
        Assert.Equal(new PyLong(now + TimeSpan.TicksPerDay), await Call(5, "GetMyDirectEnlistmentCooldownTimestamp"));
        Assert.Equal(
            new PyList([new PyInt(corporationFactionId), PyNone.Instance, new PyInt(corporationFactionId)]),
            await Call(6, "GetMyEnlistment"));
        await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
            Packets.CallWithKwargs(open, 7, "fwCharacterEnlistmentMgr", "CreateMyDirectEnlistment",
                new PyTuple([new PyInt(factionId)]), kwargs))));
        var refusal = Assert.IsType<Send>(await front.ReceiveAsync());
        AssertWrappedError(refusal, "eveexceptions.UserError", "CustomInfo");
        Assert.Contains("UI/FactionWarfare/CooldownActive", System.Text.Encoding.ASCII.GetString(refusal.Payload.Span));
        Assert.Equal(new FwCharacterWarfareState(null, now + TimeSpan.TicksPerDay), state);
        Assert.Equal(1, saves);
    }
}
