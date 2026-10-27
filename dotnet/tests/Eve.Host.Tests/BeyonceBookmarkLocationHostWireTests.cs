using System.Text;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Space;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [Fact]
    public async Task Beyonce_bookmark_location_resolves_live_target_and_returns_client_tuple_through_bound_rpc()
    {
        const long systemId = 31_000_005;
        const long itemId = 71_000_001;
        const long bookmarkId = 41_000_001;
        var target = new ScanBookmarkTarget(systemId, 12.5, -30.25, 42.75);
        var resolvedItems = new List<long>();
        var saved = new List<(long CharacterId, long SystemId, long FolderId, string Name, string Note, int Expiry, long? SubfolderId)>();
        var service = new BeyonceService(null!, () => FixedNowMs)
        {
            ResolveBookmarkLocationTarget = (call, requestedItemId, _) =>
            {
                resolvedItems.Add(requestedItemId);
                return Task.FromResult<ScanBookmarkTarget?>(requestedItemId == itemId ? target : null);
            },
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
            new KeyValuePair<PyValue, PyValue>(new PyStr("subfolderID"), new PyInt(61_000_001)),
        ]);
        Packet Request(int callId, long requestedItemId) =>
            new(open.SessionId, MarshalCodec.Encode(Packets.CallWithKwargs(
                open,
                callId,
                "beyonce",
                "MachoBindObject",
                Py.Tuple(
                    Py.Tuple(new PyLong(systemId), new PyInt(6)),
                    Py.Tuple(
                        new PyStr("BookmarkLocation"),
                        Py.Tuple(
                            new PyLong(requestedItemId),
                            new PyLong(71_000_001),
                            new PyBytes("Ship bookmark"u8.ToArray()),
                            new PyWString("Current position"),
                            new PyInt(2)),
                        nestedKwargs)),
                transportKwargs)));

        await front.SendAsync(Request(1, itemId));
        var success = Assert.IsType<PyTuple>(Result(MarshalCodec.Decode(
            Assert.IsType<Send>(await front.ReceiveAsync()).Payload)));
        var reply = Assert.IsType<PyTuple>(success.Items[1]);
        Assert.Equal(8, reply.Items.Count);
        Assert.Equal(new PyLong(bookmarkId), reply.Items[0]);
        Assert.Same(PyNone.Instance, reply.Items[1]);
        Assert.Equal(new PyInt(5), reply.Items[2]);
        Assert.Equal(12.5, Assert.IsType<PyReal>(reply.Items[3]).Value);
        Assert.Equal(-30.25, Assert.IsType<PyReal>(reply.Items[4]).Value);
        Assert.Equal(42.75, Assert.IsType<PyReal>(reply.Items[5]).Value);
        Assert.Equal(new PyLong(systemId), reply.Items[6]);
        Assert.Same(PyNone.Instance, reply.Items[7]);

        var savedBookmark = Assert.Single(saved);
        Assert.Equal(90_000_001, savedBookmark.CharacterId);
        Assert.Equal(systemId, savedBookmark.SystemId);
        Assert.Equal(71_000_001, savedBookmark.FolderId);
        Assert.Equal("Ship bookmark", savedBookmark.Name);
        Assert.Equal("Current position", savedBookmark.Note);
        Assert.Equal(2, savedBookmark.Expiry);
        Assert.Equal(61_000_001, savedBookmark.SubfolderId);
        Assert.Equal([itemId], resolvedItems);

        await front.SendAsync(Request(2, itemId + 1));
        var failure = Assert.IsType<Send>(await front.ReceiveAsync()).Payload;
        var error = Assert.IsType<MachoPacket>(MachoPacket.Decode(MarshalCodec.Decode(failure)));
        Assert.Equal(MachoMessage.ErrorResponse, error.Type);
        Assert.Contains("BookmarkNotAvailable", Encoding.ASCII.GetString(failure.Span));
        Assert.Equal([itemId, itemId + 1], resolvedItems);
        Assert.Single(saved);
    }
}
