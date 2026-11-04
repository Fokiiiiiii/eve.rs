using Eve.Host;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Inventory;
using Eve.Services.Ships;
using Eve.Services.Space;
using Eve.Services.Space.Scanning;
using Eve.Services.Space.Sites;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [Fact]
    public async Task Config_location_name_id_uses_the_client_integer_column_through_host_wire()
    {
        const long structureId = 1_000_000_000_001;
        var service = new Eve.Services.CharacterInfo.ConfigService(
            null!, null!, new Eve.Universe.StationCatalog([]), new Eve.Universe.SolarSystemCatalog([]),
            readStructureNames: (ids, _) => Task.FromResult<IReadOnlyDictionary<long, string>>(
                new Dictionary<long, string> { [structureId] = "Stored Fortizar" }));
        Start(configure: registry => registry.Register(service));

        var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        _ = await front.ReceiveAsync();

        var transportKwargs = new PyDict([
            new KeyValuePair<PyValue, PyValue>(new PyStr("machoVersion"), new PyInt(1)),
        ]);
        await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
            Packets.CallWithKwargs(open, 1, "config", "GetMultiLocationsEx",
                Py.Tuple(new PyList([new PyLong(structureId)])), transportKwargs))));

        var response = Assert.IsType<PyTuple>(Result(MarshalCodec.Decode(
            Assert.IsType<Send>(await front.ReceiveAsync()).Payload)));
        var rows = Assert.IsType<PyTuple>(response.Items[1]);
        var row = Assert.IsType<PyTuple>(Assert.Single(rows.Items));
        Assert.Equal(new PyLong(structureId), row.Items[0]);
        Assert.Equal("Stored Fortizar", System.Text.Encoding.UTF8.GetString(
            Assert.IsType<PyBytes>(row.Items[1]).Value.Span));
        Assert.Same(PyNone.Instance, row.Items[6]);
    }

    [Fact]
    public async Task Beyonce_macho_bind_refusal_keeps_the_bound_object_and_result_tuple_through_host_wire()
    {
        var service = new BeyonceService(null!, () => FixedNowMs);
        Start(
            configure: registry => registry.Register(service),
            sessionOpened: session => session.Set("solarsystemid", new PyLong(31_000_005)));

        var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        _ = await front.ReceiveAsync();

        var transportKwargs = new PyDict([
            new KeyValuePair<PyValue, PyValue>(new PyStr("machoVersion"), new PyInt(1)),
        ]);
        var nested = Py.Tuple(new PyStr("GetFormations"), Py.Tuple([]), new PyDict([]));
        var bindArgs = Py.Tuple(Py.Tuple(new PyLong(31_000_006), new PyInt(6)), nested);
        await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
            Packets.CallWithKwargs(open, 1, "beyonce", "MachoBindObject", bindArgs, transportKwargs))));

        var response = Assert.IsType<PyTuple>(Result(MarshalCodec.Decode(
            Assert.IsType<Send>(await front.ReceiveAsync()).Payload)));
        Assert.IsType<PySubStruct>(response.Items[0]);
        Assert.Same(PyNone.Instance, response.Items[1]);
    }

    [Fact]
    public async Task Beyonce_bookmark_scan_result_resolves_signature_and_returns_client_tuple_through_bound_rpc()
    {
        const long systemId = 31_000_005;
        const long bookmarkId = 41_000_001;
        const long siteId = 51_000_001;
        var target = new ScanTargetSite(siteId, 0, 0, ContentCosmicSites.SignatureScanGroup,
            [12.5, -30.25, 42.75], 1, true, Code: "ABC-123");
        var sites = new FixedCosmicSites(target);
        var saved = new List<(long CharacterId, long SystemId, long FolderId, string Name, string Note, int Expiry, long? SubfolderId)>();
        var service = new BeyonceService(null!, () => FixedNowMs)
        {
            ScanSites = sites,
            SaveBookmarkScanResult = (call, bookmarkTarget, folderId, name, note, expiry, subfolderId) =>
            {
                saved.Add((call.Session.CharacterId!.Value, bookmarkTarget.SolarSystemId, folderId, name!, note!, expiry, subfolderId));
                return ValueTask.FromResult<PyValue>(Py.Tuple(
                    new PyLong(bookmarkId),
                    PyNone.Instance,
                    new PyInt(5),
                    new PyReal(bookmarkTarget.X),
                    new PyReal(bookmarkTarget.Y),
                    new PyReal(bookmarkTarget.Z),
                    new PyLong(bookmarkTarget.SolarSystemId),
                    PyNone.Instance));
            },
        };
        Start(
            configure: registry => registry.Register(service),
            sessionOpened: session =>
            {
                session.CharacterId = 90_000_001;
                session.Set("solarsystemid", new PyLong(systemId));
                session.Set("solarsystemid2", new PyLong(systemId));
            });

        var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        _ = await front.ReceiveAsync();

        var transportKwargs = new PyDict([
            new KeyValuePair<PyValue, PyValue>(new PyStr("machoVersion"), new PyInt(1)),
        ]);
        var nestedKwargs = new PyDict([
            new KeyValuePair<PyValue, PyValue>(new PyStr("subfolderID"), new PyLong(61_000_001)),
        ]);
        var nested = Py.Tuple(
            new PyStr("BookmarkScanResult"),
            Py.Tuple(
                new PyLong(systemId),
                new PyWString("Probe bookmark"),
                new PyWString("Recovered signal"),
                new PyWString("ABC-123"),
                new PyLong(71_000_001),
                new PyInt(2)),
            nestedKwargs);
        var boundArgs = Py.Tuple(Py.Tuple(new PyLong(systemId), new PyInt(6)), nested);
        await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
            Packets.CallWithKwargs(open, 1, "beyonce", "MachoBindObject", boundArgs, transportKwargs))));

        var response = Assert.IsType<PyTuple>(Result(MarshalCodec.Decode(
            Assert.IsType<Send>(await front.ReceiveAsync()).Payload)));
        var reply = Assert.IsType<PyTuple>(response.Items[1]);
        Assert.Equal(8, reply.Items.Count);
        Assert.Equal(new PyLong(bookmarkId), reply.Items[0]);
        Assert.Same(PyNone.Instance, reply.Items[1]);
        Assert.Equal(new PyInt(5), reply.Items[2]);
        Assert.Equal(12.5, Assert.IsType<PyReal>(reply.Items[3]).Value);
        Assert.Equal(-30.25, Assert.IsType<PyReal>(reply.Items[4]).Value);
        Assert.Equal(42.75, Assert.IsType<PyReal>(reply.Items[5]).Value);
        Assert.Equal(new PyLong(systemId), reply.Items[6]);
        Assert.Same(PyNone.Instance, reply.Items[7]);
        Assert.Equal(systemId, sites.RequestedSystemId);
        var bookmark = Assert.Single(saved);
        Assert.True(bookmark.CharacterId > 0);
        Assert.Equal(systemId, bookmark.SystemId);
        Assert.Equal(71_000_001L, bookmark.FolderId);
        Assert.Equal("Probe bookmark", bookmark.Name);
        Assert.Equal("Recovered signal", bookmark.Note);
        Assert.Equal(2, bookmark.Expiry);
        Assert.Equal(61_000_001L, bookmark.SubfolderId);
    }

    [Fact]
    public async Task Invbroker_add_accepts_client_kwargs_through_registered_host_route()
    {
        const long systemId = 31_000_005;
        const long shipId = 71_000_001;
        const long containerId = 72_000_001;
        const long itemId = 81_000_001;
        const long sourceId = 91_000_001;
        var space = new RecordingSpaceContainers((int)systemId, containerId);
        var broker = new InvBrokerService(null!, null!, null!, null!, null!, () => FixedNowMs, space: space);
        Start(
            configure: registry => registry.Register(new IndustryInventoryChangePublisher(broker, new IndustryMonitorRpcService())),
            sessionOpened: session =>
            {
                session.CharacterId = 90_000_002;
                session.Set("shipid", new PyLong(shipId));
                session.Set("solarsystemid", new PyLong(systemId));
                session.Set("solarsystemid2", new PyLong(systemId));
            });

        var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        _ = await front.ReceiveAsync();

        var transportKwargs = new PyDict([
            new KeyValuePair<PyValue, PyValue>(new PyStr("machoVersion"), new PyInt(1)),
        ]);
        var addKwargs = new PyDict([
            new KeyValuePair<PyValue, PyValue>(new PyStr("qty"), PyNone.Instance),
            new KeyValuePair<PyValue, PyValue>(new PyStr("flag"), new PyInt(Eve.Inventory.ItemFlags.Hangar)),
        ]);
        var nested = Py.Tuple(
            new PyStr("Add"),
            Py.Tuple(new PyLong(itemId), new PyLong(sourceId)),
            addKwargs);
        var boundArgs = Py.Tuple(Py.Tuple(new PyLong(containerId), new PyInt(6)), nested);
        await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
            Packets.CallWithKwargs(open, 1, "invbroker", "MachoBindObject", boundArgs, transportKwargs))));

        var response = Assert.IsType<PyTuple>(Result(MarshalCodec.Decode(
            Assert.IsType<Send>(await front.ReceiveAsync()).Payload)));
        Assert.Equal(new PyInt(itemId), response.Items[1]);
        var move = Assert.Single(space.Moves);
        Assert.True(move.CharacterId > 0);
        Assert.Equal(itemId, move.ItemId);
        Assert.Equal(sourceId, move.SourceId);
        Assert.Equal(containerId, move.DestinationId);
        Assert.Equal(Eve.Inventory.ItemFlags.Hangar, move.Flag);
        Assert.Null(move.Quantity);
    }

    private sealed class FixedCosmicSites(ScanTargetSite target) : ICosmicSiteSource
    {
        public int RequestedSystemId { get; private set; }

        public Task<IReadOnlyList<ScanTargetSite>> SitesInAsync(int solarSystemId, CancellationToken cancellationToken = default)
        {
            RequestedSystemId = solarSystemId;
            return Task.FromResult<IReadOnlyList<ScanTargetSite>>([target]);
        }
    }

    private sealed class RecordingSpaceContainers(int systemId, long openContainerId) : ISpaceContainers
    {
        public List<(long CharacterId, long ItemId, long SourceId, long DestinationId, int? Flag, long? Quantity)> Moves { get; } = [];

        public bool IsInSpace(ClientSession session) => true;
        public int? SolarSystemOf(ClientSession session) => systemId;
        public Task<Eve.Inventory.Item?> OpenAsync(ClientSession session, long containerId, CancellationToken cancellationToken) =>
            Task.FromResult<Eve.Inventory.Item?>(containerId == openContainerId
                ? new Eve.Inventory.Item(containerId, 1, 90_000_002, systemId, Eve.Inventory.ItemFlags.Hangar, 1, true, null)
                : null);
        public Task<long?> MoveAsync(ClientSession session, long itemId, long sourceId, long destinationId, long? quantity, CancellationToken cancellationToken) =>
            Task.FromResult<long?>(itemId);
        public Task<long?> MoveAsync(ClientSession session, long itemId, long sourceId, long destinationId, long? quantity, int? destinationFlag, CancellationToken cancellationToken)
        {
            Moves.Add((session.CharacterId!.Value, itemId, sourceId, destinationId, destinationFlag, quantity));
            return Task.FromResult<long?>(itemId);
        }
        public Task<bool?> MoveAllAsync(ClientSession session, IReadOnlyList<long> itemIds, long sourceId, long destinationId, CancellationToken cancellationToken) =>
            Task.FromResult<bool?>(null);
        public Task<IReadOnlyList<long>> JettisonAsync(ClientSession session, IReadOnlyList<long> itemIds, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<long>>([]);
    }
}
