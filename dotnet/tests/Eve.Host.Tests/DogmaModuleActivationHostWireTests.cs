using System.Text;
using Eve.Characters;
using Eve.Dogma;
using Eve.Inventory;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Dogma;
using Eve.Ships;
using Xunit;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [Fact]
    public async Task Module_activation_uses_the_registered_bound_host_route_for_docked_and_in_space_client_contexts()
    {
        const long stationId = 600;
        const long solarSystemId = 30000142;
        const int groupStation = 15;
        const int groupSolarSystem = 5;

        var modules = new RecordingModules();
        var service = new DogmaIMService(
            new DogmaStatic([], [], []), null!, () => FixedNowMs,
            (_, _, _, _, _, _) => ValueTask.FromResult<bool?>(null),
            space: modules, modules: modules);
        ClientSession? client = null;
        Start(
            configure: registry => registry.Register(service),
            sessionOpened: session =>
            {
                client = session;
                session.Set("charid", new PyInt(42));
                session.Set("stationid", new PyInt(stationId));
                session.Set("solarsystemid", new PyLong(solarSystemId));
            });

        var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        _ = await front.ReceiveAsync();

        async Task<Send> Bind(uint callId, long locationId, int groupId)
        {
            await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(Packets.CallWithKwargs(
                open, callId, "dogmaIM", "MachoBindObject",
                new PyTuple([
                    new PyTuple([new PyLong(locationId), new PyInt(groupId)]),
                    new PyTuple([new PyStr("GetRequiredSkillLevels"), new PyTuple([new PyInt(21857)]), PyNone.Instance]),
                ]),
                MachoVersion()))));
            return Assert.IsType<Send>(await front.ReceiveAsync());
        }

        static string BoundId(Send reply)
        {
            var result = Assert.IsType<PyTuple>(Result(MarshalCodec.Decode(reply.Payload)));
            var bound = Assert.IsType<PySubStruct>(result.Items[0]);
            var boundTuple = Assert.IsType<PyTuple>(Assert.IsType<PySubStream>(bound.Value).Decoded);
            return Encoding.UTF8.GetString(Assert.IsType<PyBytes>(boundTuple.Items[0]).Value.Span);
        }

        async Task<Send> Activate(uint callId, string oid, PyValue target, PyValue repeat)
        {
            await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(Packets.BoundCall(
                open, callId, oid, "Activate",
                new PyTuple([new PyLong(9988400002220), new PyStr("targetAttack"), target, repeat]),
                MachoVersion()))));
            return Assert.IsType<Send>(await front.ReceiveAsync());
        }

        var dockedBind = await Bind(1, stationId, groupStation);
        Assert.Equal(MachoMessage.CallRsp, Envelope(dockedBind).Type);
        var dockedOid = BoundId(dockedBind);
        var noTarget = await Activate(2, dockedOid, PyNone.Instance, new PyInt(1));
        Assert.Equal(MachoMessage.CallRsp, Envelope(noTarget).Type);
        Assert.Equal(new PyInt(1), Result(MarshalCodec.Decode(noTarget.Payload)));
        Assert.Equal((9988400002220L, "targetAttack", 0L, 1), Assert.Single(modules.Activations));

        var target = await Activate(3, dockedOid, new PyLong(9988400002215), new PyInt(5));
        Assert.Equal(MachoMessage.CallRsp, Envelope(target).Type);
        Assert.Equal(new PyInt(1), Result(MarshalCodec.Decode(target.Payload)));
        Assert.Equal((9988400002220L, "targetAttack", 9988400002215L, 5), modules.Activations[1]);

        modules.Answer = ModuleResult.Refusal("MODULE_ALREADY_ACTIVE", 21857, "1MN Civilian Afterburner");
        var refused = await Activate(4, dockedOid, PyNone.Instance, new PyInt(1));
        Assert.Equal(MachoMessage.ErrorResponse, Envelope(refused).Type);
        Assert.Equal(3, modules.Activations.Count);

        await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(Packets.BoundCall(
            open, 5, dockedOid, "Deactivate",
            new PyTuple([new PyLong(9988400002220), new PyStr("targetAttack")]),
            MachoVersion()))));
        var stopped = Assert.IsType<Send>(await front.ReceiveAsync());
        Assert.Equal(MachoMessage.CallRsp, Envelope(stopped).Type);
        Assert.Equal(new PyInt(1), Result(MarshalCodec.Decode(stopped.Payload)));
        Assert.Equal((9988400002220L, "targetAttack"), Assert.Single(modules.Deactivations));

        client!.Set("stationid", PyNone.Instance);
        Assert.Null(client.DockedLocationId);
        var inSpaceBind = await Bind(6, solarSystemId, groupSolarSystem);
        Assert.Equal(MachoMessage.CallRsp, Envelope(inSpaceBind).Type);
        var inSpaceOid = BoundId(inSpaceBind);
        modules.Answer = ModuleResult.Done;
        var inSpace = await Activate(7, inSpaceOid, PyNone.Instance, new PyInt(1));
        Assert.Equal(MachoMessage.CallRsp, Envelope(inSpace).Type);
        Assert.Equal(new PyInt(1), Result(MarshalCodec.Decode(inSpace.Payload)));
        Assert.Equal((9988400002220L, "targetAttack", 0L, 1), modules.Activations[3]);
    }

    [Fact]
    public async Task Item_get_info_uses_the_registered_bound_host_route_and_returns_client_item_info()
    {
        const long characterId = 42;
        const long shipId = 9988400002200;
        var statics = new DogmaStatic([], [], []);
        var character = new Character(characterId, 1, "Pilot", 0, 1, 1, 1, 1, 1373, 1, null, null,
            600, 600, 30000142, 20000002, 10000002, shipId, DateTimeOffset.UnixEpoch, null, null);
        var ship = new Item(shipId, 601, characterId, 600, ItemFlags.Cargo, 1, true, null);
        var snapshot = new ShipSnapshot
        {
            Character = character,
            Ship = ship,
            Fitted = [],
            Skills = [],
            World = new DogmaWorld(statics, characterId, shipId),
        };
        var service = new DogmaIMService(
            statics, null!, () => FixedNowMs,
            (_, _, _, _, _, _) => ValueTask.FromResult<bool?>(null),
            snapshotProvider: (_, _) => ValueTask.FromResult<ShipSnapshot?>(snapshot));
        Start(
            configure: registry => registry.Register(service),
            sessionOpened: session =>
            {
                session.Set("charid", new PyInt(characterId));
                session.Set("stationid", new PyInt(600));
            });

        var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        _ = await front.ReceiveAsync();
        await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(Packets.CallWithKwargs(
            open, 1, "dogmaIM", "MachoBindObject",
            new PyTuple([
                new PyTuple([new PyLong(600), new PyInt(15)]),
                new PyTuple([new PyStr("GetRequiredSkillLevels"), new PyTuple([new PyInt(21857)]), PyNone.Instance]),
            ]),
            MachoVersion()))));
        var bindReply = Assert.IsType<Send>(await front.ReceiveAsync());
        Assert.Equal(MachoMessage.CallRsp, Envelope(bindReply).Type);
        var bindResult = Assert.IsType<PyTuple>(Result(MarshalCodec.Decode(bindReply.Payload)));
        var bound = Assert.IsType<PySubStruct>(bindResult.Items[0]);
        var boundTuple = Assert.IsType<PyTuple>(Assert.IsType<PySubStream>(bound.Value).Decoded);
        var oid = Encoding.UTF8.GetString(Assert.IsType<PyBytes>(boundTuple.Items[0]).Value.Span);

        await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(Packets.BoundCall(
            open, 2, oid, "ItemGetInfo", new PyTuple([new PyLong(shipId)]), MachoVersion()))));
        var reply = Assert.IsType<Send>(await front.ReceiveAsync());
        Assert.Equal(MachoMessage.CallRsp, Envelope(reply).Type);
        var info = Assert.IsType<PyObject>(Result(MarshalCodec.Decode(reply.Payload)));
        Assert.Equal("util.KeyVal", Encoding.UTF8.GetString(Assert.IsType<PyBytes>(info.Name).Value.Span));
        var fields = Assert.IsType<PyDict>(info.Args);
        PyValue Field(string name) => fields.Entries.Single(entry => entry.Key switch
        {
            PyStr key => key.Value == name,
            PyBytes key => Encoding.UTF8.GetString(key.Value.Span) == name,
            _ => false,
        }).Value;
        Assert.Equal(new PyLong(shipId), Field("itemID"));
        Assert.IsType<PyPackedRow>(Field("invItem"));
        Assert.IsType<PyDict>(Field("activeEffects"));
        Assert.IsType<PyLong>(Field("time"));
        Assert.IsType<PyDict>(Field("attributes"));
        Assert.IsType<PyLong>(Field("wallclockTime"));
    }

    [Fact]
    public async Task Refused_module_offline_does_not_call_ship_store_or_emit_a_false_change()
    {
        var modules = new RecordingModules { OfflineAnswer = new ModuleResult(false, "SIM_UNAVAILABLE") };
        var service = new DogmaIMService(
            new DogmaStatic([], [], []), null!, () => FixedNowMs,
            (_, _, _, _, _, _) => ValueTask.FromResult<bool?>(null),
            space: modules, modules: modules);
        Start(
            configure: registry => registry.Register(service),
            sessionOpened: session =>
            {
                session.Set("charid", new PyInt(42));
                session.Set("stationid", new PyInt(600));
            });

        var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        _ = await front.ReceiveAsync();
        await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(Packets.CallWithKwargs(
            open, 1, "dogmaIM", "MachoBindObject",
            new PyTuple([
                new PyTuple([new PyLong(600), new PyInt(15)]),
                new PyTuple([new PyStr("GetRequiredSkillLevels"), new PyTuple([new PyInt(21857)]), PyNone.Instance]),
            ]),
            MachoVersion()))));
        var bindReply = Assert.IsType<Send>(await front.ReceiveAsync());
        var bindResult = Assert.IsType<PyTuple>(Result(MarshalCodec.Decode(bindReply.Payload)));
        var bound = Assert.IsType<PySubStruct>(bindResult.Items[0]);
        var boundTuple = Assert.IsType<PyTuple>(Assert.IsType<PySubStream>(bound.Value).Decoded);
        var oid = Encoding.UTF8.GetString(Assert.IsType<PyBytes>(boundTuple.Items[0]).Value.Span);

        await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(Packets.BoundCall(
            open, 2, oid, "TakeModuleOffline",
            new PyTuple([new PyLong(9988400002220), new PyLong(9988400002220)]), MachoVersion()))));
        var refused = Assert.IsType<Send>(await front.ReceiveAsync());
        Assert.Equal(MachoMessage.ErrorResponse, Envelope(refused).Type);
        Assert.Equal([9988400002220L], modules.OfflineRequests);
        Assert.Empty(modules.Refreshes);
    }

    [Fact]
    public async Task Unload_ammo_returns_the_destination_item_ids_after_its_inventory_notice()
    {
        const long characterId = 42;
        const long shipId = 9988400002200;
        const long moduleId = 9988400002220;
        const long unloadedItemId = 9988400002230;
        var location = 600L;
        var destination = new Item(unloadedItemId, 100, characterId, location, ItemFlags.Hangar, 5, false, null);
        ShipOutcome outcome = new AmmoChanged(shipId, [new ItemChanged(destination, shipId, 27, 5)]);
        (long Character, long Ship, IReadOnlyList<long> Modules, long Location, int? Flag, long? Quantity)? requested = null;
        var service = new DogmaIMService(
            new DogmaStatic([], [], []), null!, () => FixedNowMs,
            (_, _, _, _, _, _) => ValueTask.FromResult<bool?>(null),
            unloadAmmo: (character, ship, modules, destinationLocation, flag, quantity, _) =>
            {
                requested = (character, ship, modules, destinationLocation, flag, quantity);
                return Task.FromResult(outcome);
            });
        Start(
            configure: registry => registry.Register(service),
            sessionOpened: session =>
            {
                session.Set("charid", new PyInt(characterId));
                session.Set("stationid", new PyInt(location));
            });

        var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        _ = await front.ReceiveAsync();
        await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(Packets.CallWithKwargs(
            open, 1, "dogmaIM", "MachoBindObject",
            new PyTuple([
                new PyTuple([new PyLong(location), new PyInt(15)]),
                new PyTuple([new PyStr("GetRequiredSkillLevels"), new PyTuple([new PyInt(21857)]), PyNone.Instance]),
            ]),
            MachoVersion()))));
        var bindReply = Assert.IsType<Send>(await front.ReceiveAsync());
        var bindResult = Assert.IsType<PyTuple>(Result(MarshalCodec.Decode(bindReply.Payload)));
        var bound = Assert.IsType<PySubStruct>(bindResult.Items[0]);
        var boundTuple = Assert.IsType<PyTuple>(Assert.IsType<PySubStream>(bound.Value).Decoded);
        var oid = Encoding.UTF8.GetString(Assert.IsType<PyBytes>(boundTuple.Items[0]).Value.Span);

        await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(Packets.BoundCall(
            open, 2, oid, "UnloadAmmo",
            new PyTuple([new PyLong(shipId), new PyList([new PyLong(moduleId)]), new PyLong(10004), PyNone.Instance]),
            MachoVersion()))));
        var notice = Assert.IsType<Send>(await front.ReceiveAsync());
        Assert.Equal(MachoMessage.Notification, Envelope(notice).Type);
        var reply = Assert.IsType<Send>(await front.ReceiveAsync());
        Assert.Equal(MachoMessage.CallRsp, Envelope(reply).Type);
        Assert.NotNull(requested);
        Assert.Equal(characterId, requested.Value.Character);
        Assert.Equal(shipId, requested.Value.Ship);
        Assert.Equal([moduleId], requested.Value.Modules);
        Assert.Equal(location, requested.Value.Location);
        Assert.Null(requested.Value.Flag);
        Assert.Null(requested.Value.Quantity);
        Assert.Equal(new PyList([new PyLong(unloadedItemId)]), Result(MarshalCodec.Decode(reply.Payload)));

        outcome = new NothingHappened();
        await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(Packets.BoundCall(
            open, 3, oid, "UnloadAmmo",
            new PyTuple([new PyLong(shipId), new PyList([new PyLong(moduleId)]), new PyLong(10004), PyNone.Instance]),
            MachoVersion()))));
        var noUnload = Assert.IsType<Send>(await front.ReceiveAsync());
        Assert.Equal(MachoMessage.CallRsp, Envelope(noUnload).Type);
        Assert.Equal(PyNone.Instance, Result(MarshalCodec.Decode(noUnload.Payload)));
    }

    private static MachoPacket Envelope(Send reply) =>
        Assert.IsType<MachoPacket>(MachoPacket.Decode(MarshalCodec.Decode(reply.Payload)));

    private sealed class RecordingModules : ISpaceModules, ISpaceTargeting
    {
        public ModuleResult? Answer { get; set; } = ModuleResult.Done;
        public List<(long ModuleId, string Effect, long Target, int Repeat)> Activations { get; } = [];
        public List<(long ModuleId, string Effect)> Deactivations { get; } = [];
        public ModuleResult? OfflineAnswer { get; set; } = ModuleResult.Done;
        public List<long> OfflineRequests { get; } = [];
        public List<string> Refreshes { get; } = [];

        public Task<ModuleResult?> ActivateAsync(ClientSession session, long moduleId, string effectName, long targetId, int repeat, CancellationToken cancellationToken, IReadOnlyList<double>? aim = null, IReadOnlyList<double>? aimEnd = null)
        {
            Activations.Add((moduleId, effectName, targetId, repeat));
            return Task.FromResult(Answer);
        }

        public Task<ModuleResult?> DeactivateAsync(ClientSession session, long moduleId, string effectName, CancellationToken cancellationToken)
        {
            Deactivations.Add((moduleId, effectName));
            return Task.FromResult<ModuleResult?>(ModuleResult.Done);
        }

        public Task<TargetingResult?> OrderAsync(ClientSession session, TargetingOrder order, IReadOnlyList<long> targets, CancellationToken cancellationToken) =>
            Task.FromResult<TargetingResult?>(null);

        public Task<ModuleResult?> OverloadAsync(ClientSession session, long moduleId, int effectId, bool active, CancellationToken cancellationToken) =>
            Task.FromResult<ModuleResult?>(ModuleResult.Done);

        public Task<ModuleResult?> OfflineAsync(ClientSession session, long moduleId, CancellationToken cancellationToken)
        {
            OfflineRequests.Add(moduleId);
            return Task.FromResult(OfflineAnswer);
        }

        public Task<ModuleResult?> OverloadRackAsync(ClientSession session, long moduleId, bool active, CancellationToken cancellationToken) =>
            Task.FromResult<ModuleResult?>(ModuleResult.Done);

        public Task RefreshShipAsync(ClientSession session, CancellationToken cancellationToken)
        {
            Refreshes.Add("refresh");
            return Task.CompletedTask;
        }

        public Task<ModuleRepairStart?> StartRepairAsync(ClientSession session, IReadOnlyList<long> moduleIds, CancellationToken cancellationToken) =>
            Task.FromResult<ModuleRepairStart?>(null);

        public Task<ModuleRepairRecord?> EndRepairAsync(ClientSession session, long moduleId, CancellationToken cancellationToken) =>
            Task.FromResult<ModuleRepairRecord?>(null);
    }
}
