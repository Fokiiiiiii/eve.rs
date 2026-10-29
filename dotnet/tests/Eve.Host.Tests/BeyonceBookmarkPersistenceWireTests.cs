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
    private class MovementSimulation : DispatchProxy
    {
        public List<(ulong SessionId, byte Command, double[] Args)> Commands { get; } = [];

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method?.Name == "Command" && args is [ulong sessionId, byte command, double[] values])
                Commands.Add((sessionId, command, values));
            return method?.ReturnType == typeof(bool) ? true : null;
        }
    }

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
        try
        {
            await BookmarkStore.MigrateAsync(source, schema);
            var store = new BookmarkStore(source, schema, () => FixedNowMs);
            var folder = await store.AddFolderAsync(characterId, true, "Personal", "");
            var saved = await store.CreateBookmarkAsync(characterId, folder.Folder.FolderId,
                new BookmarkTarget(targetItemId, 4, systemId), "Host wire target", "", BookmarkRules.ExpiryNone, null);
            var bookmarkMgr = new AccessGroupBookmarkMgrService(
                store,
                (_, _) => ValueTask.FromResult<BookmarkTarget?>(null),
                (_, _) => ValueTask.FromResult(AccessLists.Empty),
                null);
            var simulation = DispatchProxy.Create<ISimulation, MovementSimulation>();
            var recorder = (MovementSimulation)(object)simulation;
            var space = new SpaceSessions(simulation, null!, null!, new StationCatalog([]),
                new SolarSystemCatalog([new SolarSystemInfo(systemId, "Test", 20_000_001, 10_000_001, 0.5)]), () => FixedNowMs);
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

            Assert.Same(PyNone.Instance, Result(MarshalCodec.Decode(Assert.IsType<Send>(await front.ReceiveAsync()).Payload)));
            var command = Assert.Single(recorder.Commands);
            Assert.Equal(SimCommands.AlignTo, command.Command);
            Assert.Equal((double)targetItemId, Assert.Single(command.Args));
        }
        finally
        {
            await using var drop = source.CreateCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE");
            await drop.ExecuteNonQueryAsync();
        }
    }
}
