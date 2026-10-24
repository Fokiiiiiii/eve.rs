using Eve.Host;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [Fact]
    public async Task Character_energy_state_reaches_the_client_through_the_registered_host_route()
    {
        const long characterId = 90_000_164;
        var service = new CharacterEnergyMgrRpcService(nowUnixMs: () => FixedNowMs);
        Start(
            strictRpc: true,
            configure: registry => registry.Register(service),
            sessionOpened: session => session.CharacterId = characterId);

        var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        await front.ReceiveAsync();
        await front.SendAsync(new Packet(open.SessionId,
            MarshalCodec.Encode(Packets.Call(open, 1, "characterEnergyMgr", "GetMyEnergyState"))));

        var reply = Assert.IsType<Send>(await front.ReceiveAsync());
        var expected = new PyObjectEx(
            ObjectExKind.Ex1,
            Py.Tuple(
                new PyToken("characterenergy.characterEnergyState.CharacterEnergyState"),
                Py.List(
                    Py.Number(100),
                    new PyLong(Py.FileTimeFromUnixMs(FixedNowMs)),
                    Py.Number(1),
                    Py.Number(792),
                    Py.Number(0),
                    Py.Number(100))),
            [],
            []);

        Assert.Equal(MarshalCodec.Decode(MarshalCodec.Encode(expected)),
            Result(MarshalCodec.Decode(reply.Payload)));
    }

    [Fact]
    public async Task Faction_warfare_enlistment_reads_and_writes_persisted_state_through_host_routes()
    {
        const long characterId = 90_000_165;
        const long corporationId = 98_000_165;
        const long corporationFactionId = 500_002;
        const long directFactionId = 500_001;
        const long cooldown = 133_000_000_000_000_000;
        FwCharacterWarfareState? character = new(null, 0);
        IReadOnlyList<long> allowedFactions = [500_001, 500_002];

        var service = new FwCharacterEnlistmentMgrService(new FwCharacterEnlistmentRpcCallbacks(
            ReadCharacterStateAsync: (id, _) => ValueTask.FromResult(id == characterId ? character : null),
            ReadCorporationFactionIdAsync: (id, _) => ValueTask.FromResult<long?>(id == corporationId ? corporationFactionId : null),
            ReadAllowedFactionsAsync: (id, _) => ValueTask.FromResult<IReadOnlyList<long>>(id == corporationId ? allowedFactions : []),
            SetAllowedFactionsAsync: (actor, corp, factions, _) =>
            {
                Assert.Equal(characterId, actor);
                Assert.Equal(corporationId, corp);
                allowedFactions = factions;
                return ValueTask.FromResult(true);
            },
            SaveCharacterStateAsync: (id, faction, until, _) =>
            {
                if (id != characterId) return ValueTask.FromResult(false);
                character = new FwCharacterWarfareState(faction, until);
                return ValueTask.FromResult(true);
            },
            CurrentFileTime: () => cooldown));
        Start(
            strictRpc: true,
            configure: registry => registry.Register(service),
            sessionOpened: session =>
            {
                session.CharacterId = characterId;
                session.Set("corpid", new PyInt(corporationId));
            });

        var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        await front.ReceiveAsync();

        var sessionChanges = 0;
        async Task<PyValue?> Call(long id, string method, params PyValue[] args)
        {
            await front.SendAsync(new Packet(open.SessionId,
                MarshalCodec.Encode(Packets.Call(open, id, "fwCharacterEnlistmentMgr", method, args))));
            while (true)
            {
                var decoded = MarshalCodec.Decode(Assert.IsType<Send>(await front.ReceiveAsync()).Payload);
                var packet = Assert.IsType<PyObject>(decoded);
                var packetType = Assert.IsType<PyInt>(Assert.IsType<PyTuple>(packet.Args).Items[0]).Value;
                if (packetType == MachoMessage.SessionChangeNotification)
                {
                    sessionChanges++;
                    continue;
                }
                Assert.Equal(7, packetType);
                return Result(decoded);
            }
        }

        Assert.Equal(new PyList([new PyInt(corporationFactionId), PyNone.Instance, new PyInt(corporationFactionId)]),
            await Call(1, "GetMyEnlistment"));
        Assert.Equal(new PyList([new PyInt(500_001), new PyInt(500_002)]),
            await Call(2, "GetCorpAllowedEnlistmentFactions", new PyInt(corporationId)));
        Assert.Equal(new PyLong(0), await Call(3, "GetMyDirectEnlistmentCooldownTimestamp"));

        var updatedPolicy = new PyList([new PyInt(500_002)]);
        Assert.Equal(updatedPolicy,
            await Call(4, "SetMyCorpAllowedEnlistmentFactions", updatedPolicy));
        Assert.Equal(updatedPolicy,
            await Call(5, "GetCorpAllowedEnlistmentFactions", new PyInt(corporationId)));

        Assert.Equal(PyNone.Instance, await Call(6, "CreateMyDirectEnlistment", new PyInt(directFactionId)));
        Assert.Equal(new PyList([new PyInt(directFactionId), new PyInt(directFactionId), new PyInt(corporationFactionId)]),
            await Call(7, "GetMyEnlistment"));

        Assert.Equal(PyNone.Instance, await Call(8, "RemoveMyDirectEnlistment"));
        Assert.Equal(new PyList([new PyInt(corporationFactionId), PyNone.Instance, new PyInt(corporationFactionId)]),
            await Call(9, "GetMyEnlistment"));
        Assert.Equal(new PyLong(cooldown + TimeSpan.TicksPerDay),
            await Call(10, "GetMyDirectEnlistmentCooldownTimestamp"));
        Assert.Equal(2, sessionChanges);
    }
}
