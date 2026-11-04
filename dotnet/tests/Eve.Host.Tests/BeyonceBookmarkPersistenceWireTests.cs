using System.Reflection;
using Eve.Bookmarks;
using Eve.Data;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;
using Eve.Services.Space;
using Eve.Space;
using Eve.Universe;
using Npgsql;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [PgFact]
    public async Task Beyonce_align_to_resolves_a_persisted_active_bookmark_through_the_bound_host_wire()
    {
        const long characterId = 90_000_013;
        const int systemId = 31_000_007;
        const long targetItemId = 50_000_123;
        const long shipId = 80_000_013;
        var schema = "beyonce_bookmark_" + Guid.NewGuid().ToString("N")[..8];
        var url = Environment.GetEnvironmentVariable("EVE_PG_URL")!;
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(url));
        var nowMs = FixedNowMs;
        try
        {
            await BookmarkStore.MigrateAsync(source, schema);
            var store = new BookmarkStore(source, schema, () => nowMs);
            var folder = await store.AddFolderAsync(characterId, true, "Personal", "");
            await store.SetKnownFolderActiveAsync(characterId, folder.Folder.FolderId, true);
            var saved = await store.CreateBookmarkAsync(characterId, folder.Folder.FolderId,
                new BookmarkTarget(targetItemId, 4, systemId), "Host wire target", "", BookmarkRules.ExpiryNone, null);
            const long useList = 60_000_017;
            const long otherCharacterId = 90_000_014;
            var sharedGroups = new SharedGroups(useList, useList, useList, useList);
            var lists = new AccessLists(new Dictionary<long, int> { [useList] = 2 });
            var sharedFolder = await store.AddFolderAsync(characterId, false, "Shared", "", sharedGroups, lists);
            await store.AddKnownFolderAsync(characterId, sharedFolder.Folder.FolderId, true, lists);
            var sharedBookmark = await store.CreateBookmarkAsync(otherCharacterId, sharedFolder.Folder.FolderId,
                new BookmarkTarget(targetItemId, 4, systemId), "Shared wire target", "", BookmarkRules.ExpiryNone, null, lists);
            var movedSource = await store.CreateBookmarkAsync(characterId, folder.Folder.FolderId,
                new BookmarkTarget(targetItemId, 4, systemId), "Moved into shared", "", BookmarkRules.ExpiryNone, null);
            var moved = Assert.Single(await store.MoveBookmarksAsync(characterId, folder.Folder.FolderId,
                sharedFolder.Folder.FolderId, null, [movedSource.BookmarkId], lists));
            var copied = Assert.Single(await store.CopyBookmarksAsync(characterId, folder.Folder.FolderId,
                sharedFolder.Folder.FolderId, null, [saved.BookmarkId], lists));
            Assert.False(store.IsUsable(sharedBookmark, characterId));
            Assert.True(store.IsUsable(moved, characterId));
            Assert.True(store.IsUsable(copied, characterId));
            var bookmarkMgr = new AccessGroupBookmarkMgrService(
                store,
                (_, _) => ValueTask.FromResult<BookmarkTarget?>(null),
                (_, _) => ValueTask.FromResult(lists),
                null);
            var simulation = DispatchProxy.Create<ISimulation, MovementSimulation>();
            var recorder = (MovementSimulation)(object)simulation;
            var space = new SpaceSessions(simulation, null!, null!, new StationCatalog([]),
                new SolarSystemCatalog([new SolarSystemInfo(systemId, "Test", 20_000_001, 10_000_001, 0.5)]), () => nowMs);
            var beyonce = new BeyonceService(space, () => FixedNowMs)
            {
                BookmarkTargetForCharacter = async (call, bookmarkId, _) =>
                {
                    var bookmark = await bookmarkMgr.ResolveActiveBookmarkAsync(call, bookmarkId);
                    return bookmark is null
                        ? null
                        : new BookmarkWarpTarget(checked((int)bookmark.LocationId), bookmark.ItemId, bookmark.X, bookmark.Y, bookmark.Z);
                },
            };
            Start(
                configure: registry =>
                {
                    registry.Register(beyonce);
                    registry.Register(new BookmarkRefusals(bookmarkMgr));
                },
                sessionOpened: session =>
                {
                    session.CharacterId = characterId;
                    session.Set("charid", new PyLong(characterId));
                    session.Set("solarsystemid", new PyLong(systemId));
                    session.Set("solarsystemid2", new PyLong(systemId));
                    session.Set("locationid", new PyInt(systemId));
                    space.EnterPilotForTest(session, characterId, shipId, 587, systemId);
                });

            var front = await Handshaken(await ConnectAsync());
            var open = Open();
            await front.SendAsync(open);
            _ = await front.ReceiveAsync();
            var transportKwargs = new PyDict([
                new KeyValuePair<PyValue, PyValue>(new PyStr("machoVersion"), new PyInt(1)),
            ]);
            var callKwargs = new PyDict([
                new KeyValuePair<PyValue, PyValue>(new PyStr("dstID"), PyNone.Instance),
                new KeyValuePair<PyValue, PyValue>(new PyStr("bookmarkID"), new PyLong(saved.BookmarkId)),
            ]);
            var nested = Py.Tuple(new PyStr("CmdAlignTo"), Py.Tuple(), callKwargs);
            var boundArgs = Py.Tuple(Py.Tuple(new PyLong(systemId), new PyInt(6)), nested);
            await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
                Packets.CallWithKwargs(open, 1, "beyonce", "MachoBindObject", boundArgs, transportKwargs))));

            var response = Assert.IsType<PyTuple>(Result(MarshalCodec.Decode(Assert.IsType<Send>(await front.ReceiveAsync()).Payload)));
            Assert.Same(PyNone.Instance, response.Items[1]);
            var command = Assert.Single(recorder.Commands);
            Assert.Equal(SimCommands.AlignTo, command.Command);
            Assert.Equal((double)targetItemId, Assert.Single(command.Args));

            var sharedCallKwargs = new PyDict([
                new KeyValuePair<PyValue, PyValue>(new PyStr("dstID"), PyNone.Instance),
                new KeyValuePair<PyValue, PyValue>(new PyStr("bookmarkID"), new PyLong(sharedBookmark.BookmarkId)),
            ]);
            var sharedNested = Py.Tuple(new PyStr("CmdAlignTo"), Py.Tuple(), sharedCallKwargs);
            var sharedBoundArgs = Py.Tuple(Py.Tuple(new PyLong(systemId), new PyInt(6)), sharedNested);
            await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
                Packets.CallWithKwargs(open, 2, "beyonce", "MachoBindObject", sharedBoundArgs, transportKwargs))));
            _ = Result(MarshalCodec.Decode(Assert.IsType<Send>(await front.ReceiveAsync()).Payload));
            Assert.Single(recorder.Commands);

            nowMs += BookmarkRules.SharedBookmarkDelayMs;
            Assert.False(store.IsUsable(sharedBookmark, characterId));
            nowMs++;
            Assert.True(store.IsUsable(sharedBookmark, characterId));
            await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
                Packets.CallWithKwargs(open, 3, "beyonce", "MachoBindObject", sharedBoundArgs, transportKwargs))));
            _ = Result(MarshalCodec.Decode(Assert.IsType<Send>(await front.ReceiveAsync()).Payload));
            Assert.Equal(2, recorder.Commands.Count);
            Assert.Equal(SimCommands.AlignTo, recorder.Commands[1].Command);
            Assert.Equal((double)targetItemId, Assert.Single(recorder.Commands[1].Args));

        }
        finally
        {
            await using var drop = source.CreateCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE");
            await drop.ExecuteNonQueryAsync();
        }
    }
}
