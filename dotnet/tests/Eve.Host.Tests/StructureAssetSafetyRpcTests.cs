using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [Fact]
    public async Task Structure_asset_safety_remote_calls_cross_the_registered_host_with_client_shapes()
    {
        const long characterId = 900001;
        const long corporationId = 980001;
        const long systemId = 30000142;
        const long sourceStructureId = 7000001;
        const long destinationId = 6000001;
        var callbacks = new WireAssetSafetyReadCallbacks(systemId, destinationId);
        var connected = new ConnectedSessions();
        void NotifyOwner(ClientSession actor, long ownerId, bool corporation, string name, IReadOnlyList<PyValue> arguments)
        {
            foreach (var recipient in connected.Snapshot())
            {
                if ((corporation ? recipient.GetInt("corpid") : recipient.CharacterId) != ownerId)
                    continue;
                var args = arguments.ToArray();
                if (ReferenceEquals(recipient, actor))
                    recipient.AfterResponse(() => recipient.SendNotification(name, "clientID", args));
                else
                    recipient.SendNotification(name, "clientID", args);
            }
        }
        Start(
            configure: registry => registry.Register(new StructureAssetSafetyService(callbacks, NotifyOwner)),
            sessionOpened: session =>
            {
                session.CharacterId = characterId;
                session.Set("corpid", new PyInt(corporationId));
                session.Set("accountRole", new PyLong(GameMasterRoles.Gml));
                session.Set("role", new PyLong(GameMasterRoles.Gml));
                connected.Add(session);
            });
        var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        await front.ReceiveAsync();

        async Task<PyValue?> Call(string method, PyValue[] args, params (string Key, PyValue Value)[] extraKeywords)
        {
            var keywords = new PyDict([
                new KeyValuePair<PyValue, PyValue>(new PyStr("machoVersion"), new PyInt(1)),
                .. extraKeywords.Select(pair => new KeyValuePair<PyValue, PyValue>(new PyStr(pair.Key), pair.Value)),
            ]);
            await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
                Packets.CallWithKwargs(open, callbacks.NextCallId++, "structureAssetSafety", method, new PyTuple(args), keywords))));
            var result = Result(MarshalCodec.Decode(Assert.IsType<Send>(await front.ReceiveAsync()).Payload));
            if (method is "MovePersonalAssetsToSafety" or "MoveCorpAssetsToSafety" or "MoveSafetyWrapToStructure")
            {
                var notice = ReadNotification(Assert.IsType<Send>(await front.ReceiveAsync()));
                Assert.Equal(method == "MoveSafetyWrapToStructure" ? "OnAssetSafetyDelivered" : "OnAssetSafetyCreated", notice.Name);
                Assert.Equal(method == "MoveCorpAssetsToSafety" ? corporationId : characterId, Number(notice.Args[0]));
            }
            return result;
        }

        Assert.Same(PyNone.Instance, await Call("MovePersonalAssetsToSafety",
            [new PyLong(systemId), new PyLong(sourceStructureId)]));
        Assert.Same(PyNone.Instance, await Call("MoveCorpAssetsToSafety",
            [new PyLong(systemId), new PyLong(sourceStructureId)]));

        var names = Assert.IsType<PyDict>(await Call("GetWrapNames", [new PyList([
            new PyLong(callbacks.PersonalWrapId), new PyLong(callbacks.CorporationWrapId)])]));
        Assert.Equal(2, names.Entries.Count);
        Assert.Contains(names.Entries, entry => Number(entry.Key) == callbacks.PersonalWrapId && T(entry.Value) == "Personal safety wrap");
        Assert.Contains(names.Entries, entry => Number(entry.Key) == callbacks.CorporationWrapId && T(entry.Value) == "Corporation safety wrap");

        var targets = Assert.IsType<PyTuple>(await Call("GetStructuresICanDeliverTo", [new PyLong(systemId)]));
        Assert.Equal(2, targets.Items.Count);
        var destination = Assert.IsType<PyDict>(Assert.Single(Assert.IsType<PyList>(targets.Items[0]).Items));
        Assert.Equal(destinationId, Number(Assert.Single(destination.Entries, entry => T(entry.Key) == "itemID").Value));
        Assert.IsType<PyDict>(targets.Items[1]);

        Assert.Same(PyNone.Instance, await Call("MoveEjectTimeGM",
            [new PyLong(callbacks.PersonalWrapId), new PyInt(30)]));
        Assert.Equal(30, callbacks.PersonalWrapEjectDays);

        Assert.Same(PyNone.Instance, await Call("MoveSafetyWrapToStructure",
            [new PyLong(callbacks.PersonalWrapId), new PyLong(systemId)],
            ("destinationID", new PyLong(destinationId))));
        Assert.Equal((callbacks.PersonalWrapId, destinationId), callbacks.DeliveredWrap);
    }

    private static (string Name, PyValue[] Args) ReadNotification(Send send)
    {
        var state = ((PyTuple)((PyObject)MarshalCodec.Decode(send.Payload)).Args).Items;
        Assert.Equal(MachoMessage.Notification, Number(state[0]));
        var destination = ((PyTuple)((PyObject)state[2]).Args).Items;
        var body = (PyBuffer)((PyTuple)((PyTuple)state[4]).Items[0]).Items[1];
        var inner = (PyTuple)MarshalCodec.Decode(body.Value.ToArray());
        return (T(destination[1])!, [.. ((PyTuple)((PyTuple)inner.Items[1]).Items[1]).Items]);
    }

    private sealed class WireAssetSafetyReadCallbacks(long systemId, long destinationId) : IStructureAssetSafetyReadCallbacks
    {
        private readonly List<WireAssetSafetyWrap> _wraps = [];
        public long NextCallId { get; set; } = 1;
        public long PersonalWrapId => 8000001;
        public long CorporationWrapId => 8000002;
        public int PersonalWrapEjectDays { get; private set; } = 5;
        public (long WrapId, long DestinationId) DeliveredWrap { get; private set; }

        private static PyDict Entry(params (string Key, PyValue Value)[] values) => new([
            .. values.Select(value => new KeyValuePair<PyValue, PyValue>(new PyStr(value.Key), value.Value)),
        ]);

        private PyValue WrapRow(WireAssetSafetyWrap wrap) => new PyObject(new PyStr("util.KeyVal"), Entry(
            ("assetWrapID", new PyLong(wrap.Id)),
            ("solarSystemID", new PyLong(wrap.SystemId)),
            ("wrapName", new PyStr(wrap.Name)),            ("ejectTime", new PyLong(wrap.EjectTime)),
            ("daysUntilCanDeliverConst", new PyInt(5)),
            ("daysUntilAutoMoveConst", new PyInt(20)),
            ("nearestNPCStationInfo", Entry(("itemID", new PyLong(destinationId)), ("typeID", new PyInt(1531))))));

        public ValueTask<PyList> GetItemsInSafetyForCharacterAsync(long id, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new PyList([.. _wraps.Where(wrap => !wrap.Delivered && !wrap.Corporation && wrap.OwnerId == id).Select(WrapRow)]));

        public ValueTask<PyList> GetItemsInSafetyForCorpAsync(long characterId, long id, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new PyList([.. _wraps.Where(wrap => !wrap.Delivered && wrap.Corporation && wrap.OwnerId == id).Select(WrapRow)]));

        public ValueTask<PyDict> GetWrapNamesAsync(long characterId, long corporationId, IReadOnlyList<long> wrapIds, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new PyDict([
                .. _wraps.Where(wrap => !wrap.Delivered && wrapIds.Contains(wrap.Id) &&
                    (wrap.OwnerId == characterId || wrap.Corporation && wrap.OwnerId == corporationId))
                    .Select(wrap => new KeyValuePair<PyValue, PyValue>(new PyLong(wrap.Id), new PyStr(wrap.Name))),
            ]));

        public ValueTask<PyTuple> GetStructuresICanDeliverToAsync(long characterId, long corporationId, long requestedSystemId, CancellationToken cancellationToken)
        {
            var targets = requestedSystemId == systemId && _wraps.Any(wrap => !wrap.Delivered && wrap.SystemId == requestedSystemId)
                ? new PyList([Entry(
                    ("itemID", new PyLong(destinationId)),
                    ("typeID", new PyInt(1531)),
                    ("name", new PyStr("Test NPC station")))])
                : new PyList([]);
            var nearest = Entry(("itemID", new PyLong(destinationId)), ("typeID", new PyInt(1531)));
            return ValueTask.FromResult(new PyTuple([targets, nearest]));
        }

        public ValueTask<AssetSafetyActionState> MovePersonalAssetsToSafetyAsync(ServiceCall call, long requestedSystemId, long structureId, CancellationToken cancellationToken)
        {
            if (requestedSystemId != systemId || structureId <= 0)
                return ValueTask.FromResult(AssetSafetyActionState.StructureUnavailable);
            _wraps.Add(new WireAssetSafetyWrap(PersonalWrapId, call.Session!.CharacterId!.Value, false, systemId, "Personal safety wrap"));
            return ValueTask.FromResult(AssetSafetyActionState.PersonalWrapCreated);
        }

        public ValueTask<AssetSafetyActionState> MoveCorpAssetsToSafetyAsync(ServiceCall call, long requestedSystemId, long structureId, CancellationToken cancellationToken)
        {
            if (requestedSystemId != systemId || structureId <= 0)
                return ValueTask.FromResult(AssetSafetyActionState.StructureUnavailable);
            _wraps.Add(new WireAssetSafetyWrap(CorporationWrapId, call.Session!.GetInt("corpid")!.Value, true, systemId, "Corporation safety wrap"));
            return ValueTask.FromResult(AssetSafetyActionState.CorporationWrapCreated);
        }

        public ValueTask<AssetSafetyActionState> MoveSafetyWrapToStructureAsync(ServiceCall call, long wrapId, long requestedSystemId, long targetId, CancellationToken cancellationToken)
        {
            var wrap = _wraps.SingleOrDefault(candidate => candidate.Id == wrapId);
            if (wrap is null || wrap.SystemId != requestedSystemId || targetId != destinationId)
                return ValueTask.FromResult(AssetSafetyActionState.InvalidDestination);
            wrap.Delivered = true;
            DeliveredWrap = (wrapId, targetId);
            return ValueTask.FromResult(AssetSafetyActionState.WrapDelivered);
        }

        public ValueTask<bool> MoveEjectTimeGMAsync(ServiceCall call, long wrapId, int days, CancellationToken cancellationToken)
        {
            if (wrapId != PersonalWrapId || days is not (5 or 30))
                return ValueTask.FromResult(false);
            PersonalWrapEjectDays = days;
            return ValueTask.FromResult(true);
        }

        public ValueTask ProcessDueAutomaticDeliveriesAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

        private sealed class WireAssetSafetyWrap(long id, long ownerId, bool corporation, long systemId, string name)
        {
            public long Id { get; } = id;
            public long OwnerId { get; } = ownerId;
            public bool Corporation { get; } = corporation;
            public long SystemId { get; } = systemId;
            public string Name { get; } = name;
            public long EjectTime { get; } = FixedNowMs;
            public bool Delivered { get; set; }
        }
    }
}
