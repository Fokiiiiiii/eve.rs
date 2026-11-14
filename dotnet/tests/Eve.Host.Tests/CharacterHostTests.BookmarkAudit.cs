using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Npgsql;

namespace Eve.Host.Tests;

public sealed partial class CharacterHostTests
{
    private static long _nextBookmarkAuditCallId = 94_000;

    [PgFact]
    public async Task Bookmark_expiry_mode_four_hours_persists_four_hours_through_registered_host_and_restart()
    {
        await using var rig = await CharacterHostRig.CreateAsync();
        var characterId = await rig.CreateCharacterAsync();
        try
        {
            var sink = new RecordingPacketSink();
            var session = new ClientSession(sink)
            {
                SessionId = 94_001,
                ClientId = 94_001,
                UserId = 1,
                CharacterId = characterId,
            };
            session.Set("charid", new PyLong(characterId));
            rig.Sessions.Enter(characterId, session);
            var dispatcher = new PacketDispatcher(rig.Registry, () => rig.NowUnixMs);

            var active = Assert.IsType<PyList>(await DispatchBookmarkAuditAsync(
                dispatcher, session, "GetMyActiveBookmarks"));
            var folders = Assert.IsType<PyList>(active.Items[0]);
            var folder = Assert.IsType<PyObject>(Assert.Single(folders.Items));
            var folderId = Integer(Field(folder, "folderID"));

            var created = Assert.IsType<PyTuple>(await DispatchBookmarkAuditAsync(
                dispatcher, session, "BookmarkStaticLocation",
                new PyLong(rig.Station), new PyLong(folderId), new PyWString("Expiry regression"), new PyWString(""),
                new PyInt(Eve.Bookmarks.BookmarkRules.Expiry4Hours)));
            var bookmarkId = Integer(created.Items[0]);
            Assert.True(bookmarkId > 0);
            Assert.Equal(8, created.Items.Count);

            var (createdMs, expiryMs) = await ReadBookmarkTimesAsync(rig, bookmarkId);
            const long fourHoursMs = 4L * 60 * 60 * 1000;
            Assert.Equal(fourHoursMs, expiryMs - createdMs);
            Assert.Equal(new PyLong(Py.FileTimeFromUnixMs(expiryMs)), created.Items[7]);

            await rig.RestartAsync();
            rig.Sessions.Enter(characterId, session);
            dispatcher = new PacketDispatcher(rig.Registry, () => rig.NowUnixMs);
            active = Assert.IsType<PyList>(await DispatchBookmarkAuditAsync(
                dispatcher, session, "GetMyActiveBookmarks"));
            var bookmarks = Assert.IsType<PyList>(active.Items[1]);
            var persisted = Assert.Single(bookmarks.Items, bookmark => Integer(Field(bookmark, "bookmarkID")) == bookmarkId);
            Assert.Equal(new PyLong(Py.FileTimeFromUnixMs(expiryMs)), Field(persisted, "expiry"));
            Assert.Equal((createdMs, expiryMs), await ReadBookmarkTimesAsync(rig, bookmarkId));
        }
        finally
        {
            await CleanupBookmarkRowsAsync(rig, characterId);
        }
    }

    private static async Task<(long CreatedMs, long ExpiryMs)> ReadBookmarkTimesAsync(CharacterHostRig rig, long bookmarkId)
    {
        await using var connection = await rig.Source.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            $"SELECT created_ms, expiry_ms FROM {Eve.Bookmarks.BookmarkStore.DefaultSchema}.bookmarks WHERE bookmark_id = $1", connection);
        command.Parameters.Add(new NpgsqlParameter { Value = bookmarkId });
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return (reader.GetInt64(0), reader.GetInt64(1));
    }

    private static async Task CleanupBookmarkRowsAsync(CharacterHostRig rig, long characterId)
    {
        await using var connection = await rig.Source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var schema = Eve.Bookmarks.BookmarkStore.DefaultSchema;
        foreach (var table in new[] { "bookmarks", "subfolders", "connections", "folders" })
        {
            var predicate = table switch
            {
                "bookmarks" => $"folder_id IN (SELECT folder_id FROM {schema}.folders WHERE owner_id = $1)",
                "subfolders" => $"folder_id IN (SELECT folder_id FROM {schema}.folders WHERE owner_id = $1)",
                "connections" => "character_id = $1",
                _ => "owner_id = $1",
            };
            await using var command = new NpgsqlCommand($"DELETE FROM {schema}.{table} WHERE {predicate}", connection, transaction);
            command.Parameters.Add(new NpgsqlParameter { Value = characterId });
            await command.ExecuteNonQueryAsync();
        }
        await transaction.CommitAsync();
    }

    private static async Task<PyValue> DispatchBookmarkAuditAsync(
        PacketDispatcher dispatcher,
        ClientSession session,
        string method,
        params PyValue[] arguments)
    {
        var callId = Interlocked.Increment(ref _nextBookmarkAuditCallId);
        var source = new MachoAddress(AddressKind.Client, CallId: new PyLong(callId), ClientId: new PyInt(session.ClientId));
        var requestBody = new PyTuple([PyNone.Instance, new PyStr(method), new PyTuple(arguments), new PyDict([])]);
        var payload = new PyTuple([new PyTuple([PyBool.True, new PySubStream(requestBody)])]);
        var request = new PyObject(new PyStr("carbon.common.script.net.machoNetPacket.CallReq"), new PyTuple([
            new PyInt(MachoMessage.CallReq), source.Encode(), MachoAddress.Any("accessGroupBookmarkMgr").Encode(), new PyInt(7),
            payload, new PyDict([]), PyNone.Instance, PyNone.Instance, PyNone.Instance, PyNone.Instance,
            PyNone.Instance, PyNone.Instance, PyNone.Instance, PyNone.Instance]));
        var sink = Assert.IsType<RecordingPacketSink>(session.Sink);
        var before = sink.Sent.Count;
        Assert.True(await dispatcher.DispatchAsync(MarshalCodec.Decode(MarshalCodec.Encode(request)), session));
        var packet = sink.Sent.Skip(before).Last(item => BookmarkAuditPacketType(item) is MachoMessage.CallRsp or MachoMessage.ErrorResponse);
        Assert.Equal(MachoMessage.CallRsp, BookmarkAuditPacketType(packet));
        var fields = Assert.IsType<PyTuple>(Assert.IsType<PyObject>(packet).Args).Items;
        return Assert.IsType<PySubStream>(Assert.IsType<PyTuple>(fields[4]).Items[0]).Decoded!;
    }

    private static long BookmarkAuditPacketType(PyValue packet) =>
        Assert.IsType<PyInt>(Assert.IsType<PyTuple>(Assert.IsType<PyObject>(packet).Args).Items[0]).Value;
}
