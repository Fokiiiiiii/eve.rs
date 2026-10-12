using Eve.Data;
using Eve.Events;
using Eve.Host;
using Eve.Macho;
using Eve.Marshal;
using Eve.Scheduler;
using Eve.Services;
using Eve.Structures;
using Npgsql;
using Xunit;

namespace Eve.Host.Tests;

/// <summary>
/// The structure notices (ST-2 d) on the real bus, scheduler and store: the owner corporation hears the state change and the directory change, a pilot docked
/// in the structure hears its current state, and a pilot of another corporation hears nothing. A change that is not a state change sends nothing.
/// </summary>
public sealed class StructureNotificationsTests
{
    private const long Corporation = 98_000_001, OtherCorporation = 98_000_002, Structure = 1_000_000_000_060;
    private const int Astrahus = 35_832, Tanoo = 30_000_001;
    private const long Now = 1_700_006_400_000;

    private sealed class Rules : IStructureRulesSource
    {
        public StructureRules RulesOf(int typeId, int solarSystemId) => StructureRules.Of(StructureSize.Medium, SecurityBand.HighSec);
    }

    private sealed class Sink : IPacketSink
    {
        public List<PyValue> Sent { get; } = [];
        public void Send(PyValue packet) => Sent.Add(packet);
    }

    /// <summary>A decoded client notification, including its address and wire arguments.</summary>
    private sealed record ClientNotice(string Name, string IdType, PyTuple Args);

    private static List<ClientNotice> ClientNotices(Sink sink)
    {
        var notices = new List<ClientNotice>();
        foreach (var packet in sink.Sent.OfType<PyObject>())
        {
            var fields = ((PyTuple)packet.Args).Items;
            if (fields[0] is not PyInt { Value: MachoMessage.Notification })
            {
                continue;
            }
            var address = ((PyTuple)((PyObject)fields[2]).Args).Items;
            var envelope = (PyTuple)((PyTuple)fields[4]).Items[0];
            var call = (PyTuple)MarshalCodec.Decode(((PyBuffer)envelope.Items[1]).Value);
            var args = (PyTuple)((PyTuple)call.Items[1]).Items[1];
            notices.Add(new(
                JsSemantics.StringValue(address[1]) ?? "",
                JsSemantics.StringValue(address[3]) ?? "",
                args));
        }
        return notices;
    }

    private static PyValue HangarField(PyObjectEx state, string name)
    {
        var header = Assert.IsType<PyList>(state.Header);
        var attributes = Assert.IsType<PyDict>(header.Items[1]);
        return attributes.Entries.Single(pair => JsSemantics.StringValue(pair.Key) == name).Value;
    }

    private static long FileTime(long unixMilliseconds) =>
        116_444_736_000_000_000L + unixMilliseconds * 10_000L;

    private static List<string> Notices(Sink sink) => ClientNotices(sink).Select(notice => notice.Name).ToList();

    [PgFact]
    public async Task The_owner_and_docked_pilots_receive_persisted_hangar_view_updates_in_order_and_unrelated_sessions_hear_nothing()
    {
        var url = Environment.GetEnvironmentVariable("EVE_PG_URL") ?? throw new InvalidOperationException("EVE_PG_URL is not set");
        var source = NpgsqlDataSource.Create(PgConnection.ForTest(url));
        await using var _ = source;
        var id = Guid.NewGuid().ToString("N")[..12];
        var schemas = new[] { $"ntf_sch_{id}", $"ntf_evt_{id}", $"ntf_str_{id}" };
        await DurableScheduler.MigrateAsync(source, schemas[0]);
        await EventBus.MigrateAsync(source, schemas[1]);
        await StructureStore.MigrateAsync(source, schemas[2]);
        try
        {
            var clock = Now;
            var scheduler = new DurableScheduler(source, () => clock, new SchedulerOptions { Concurrency = 1 }, schemas[0], _ => { });
            var bus = new EventBus(source, scheduler, new EventBusOptions { MaxAttempts = 3 }, schemas[1], _ => { });
            var store = new StructureStore(source, schemas[2]);
            var timers = new StructureTimers(store, scheduler, bus, new Rules(), () => 0.5);
            timers.Register();

            var profiles = new PersistenceStructureProfilesRpcBackend(
                async (table, cancellation) => table == "structures"
                    ? await store.ReadLegacyTableAsync(cancellation)
                    : new System.Text.Json.Nodes.JsonObject(),
                null!, null!);
            var hangarStateReads = 0;
            ValueTask<PyValue?> GetHangarViewStateAsync(long structureId, CancellationToken cancellation)
            {
                hangarStateReads++;
                return profiles.GetMyHangarViewStateAsync(structureId, cancellation);
            }

            var sessions = new CharacterSessions();
            StructureNotifications.Subscribe(bus, store, sessions, GetHangarViewStateAsync);
            await bus.PumpAsync();

            var member = new Sink();
            var memberSession = new ClientSession(member) { SessionId = 1, CharacterId = 1 };
            memberSession.Set("corpid", new PyInt(Corporation));
            memberSession.Set("structureid", new PyLong(Structure));
            sessions.Enter(1, memberSession);

            var docked = new Sink();
            var dockedSession = new ClientSession(docked) { SessionId = 2, CharacterId = 2 };
            dockedSession.Set("corpid", new PyInt(OtherCorporation));
            dockedSession.Set("structureid", new PyLong(Structure));
            sessions.Enter(2, dockedSession);

            var stranger = new Sink();
            var strangerSession = new ClientSession(stranger) { SessionId = 3, CharacterId = 3 };
            strangerSession.Set("corpid", new PyInt(OtherCorporation));
            sessions.Enter(3, strangerSession);

            var elsewhere = new Sink();
            var elsewhereSession = new ClientSession(elsewhere) { SessionId = 4, CharacterId = 4 };
            elsewhereSession.Set("corpid", new PyInt(OtherCorporation));
            elsewhereSession.Set("structureid", new PyLong(Structure + 1));
            sessions.Enter(4, elsewhereSession);

            var controlSeat = new Sink();
            var controlSeatSession = new ClientSession(controlSeat) { SessionId = 5, CharacterId = 5 };
            controlSeatSession.Set("corpid", new PyInt(OtherCorporation));
            controlSeatSession.Set("shipid", new PyLong(Structure));
            controlSeatSession.Set("structureid", new PyLong(Structure));
            sessions.Enter(5, controlSeatSession);

            // The committed anchor transition is visible as the persisted ObjectEx2 hangar state.
            await using (var connection = await source.OpenConnectionAsync())
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                await timers.AnchorAsync(connection, transaction, new NewStructure(Structure, Astrahus, Tanoo, 1, 2, 3, 0, Corporation, 0, "Notice", ""));
                await transaction.CommitAsync();
            }
            await bus.PumpAsync();

            var ownerNotices = ClientNotices(member);
            Assert.Equal(
                new[] { "OnStructureStateChanged", "OnCurrentStructureStateChanged", "OnHangarViewStateUpdated", "OnCorporationStructuresUpdated" },
                ownerNotices.Select(notice => notice.Name));
            Assert.Equal("clientID", ownerNotices[0].IdType);
            Assert.Equal(new PyTuple([new PyLong(Tanoo), new PyLong(Structure), new PyInt(StructureState.AnchorVulnerable)]), ownerNotices[0].Args);
            Assert.Equal("shipid", ownerNotices[1].IdType);
            Assert.Equal(new PyTuple([new PyLong(Tanoo), new PyLong(Structure), new PyInt(StructureState.AnchorVulnerable)]), ownerNotices[1].Args);
            Assert.Equal("*corpid&corprole", ownerNotices[3].IdType);
            Assert.Empty(ownerNotices[3].Args.Items);

            var dockedNotices = ClientNotices(docked);
            Assert.Equal(new[] { "OnCurrentStructureStateChanged", "OnHangarViewStateUpdated" }, dockedNotices.Select(notice => notice.Name));
            Assert.Equal("shipid", dockedNotices[0].IdType);
            Assert.Equal(new PyTuple([new PyLong(Tanoo), new PyLong(Structure), new PyInt(StructureState.AnchorVulnerable)]), dockedNotices[0].Args);

            var hangarNotice = dockedNotices[1];
            Assert.Equal(hangarNotice, ownerNotices[2]);
            Assert.Equal("structureid", hangarNotice.IdType);
            Assert.Equal(2, hangarNotice.Args.Items.Count);
            Assert.Equal(new PyLong(Structure), hangarNotice.Args.Items[0]);
            var anchorViewState = Assert.IsType<PyObjectEx>(hangarNotice.Args.Items[1]);
            Assert.Equal(ObjectExKind.Ex2, anchorViewState.Kind);
            var classHeader = Assert.IsType<PyList>(Assert.IsType<PyList>(anchorViewState.Header).Items[0]);
            Assert.Equal("structures.structureHangarViewState.StructureHangarViewState", JsSemantics.StringValue(classHeader.Items[0]));
            Assert.Equal(new PyInt(StructureState.AnchorVulnerable), HangarField(anchorViewState, "_operatingState"));
            Assert.Equal(new PyInt(UpkeepState.LowPower), HangarField(anchorViewState, "_upkeepState"));
            Assert.Equal(new PyList([new PyReal(0), new PyReal(0), new PyReal(1)]), HangarField(anchorViewState, "_damageState"));
            var anchored = await store.GetAsync(Structure) ?? throw new InvalidOperationException("structure was not persisted");
            Assert.Equal(new PyLong(FileTime(anchored.Snapshot.StateStartedMs)), HangarField(anchorViewState, "_timerStartAt"));
            Assert.Equal(new PyLong(FileTime(anchored.Snapshot.StateEndsMs)), HangarField(anchorViewState, "_timerEndAt"));
            Assert.Equal(PyNone.Instance, HangarField(anchorViewState, "_timerPauseAt"));
            Assert.Equal(MarshalCodec.Decode(MarshalCodec.Encode((await profiles.GetMyHangarViewStateAsync(Structure, CancellationToken.None))!)), anchorViewState);
            Assert.Equal(1, hangarStateReads);

            async Task Apply(StructureEvent structureEvent)
            {
                await using var connection = await source.OpenConnectionAsync();
                await using var transaction = await connection.BeginTransactionAsync();
                Assert.NotNull(await timers.ApplyAsync(connection, transaction, Structure, structureEvent));
                await transaction.CommitAsync();
                await bus.PumpAsync();
            }

            // AnchorVulnerable -> Anchoring -> OnliningVulnerable exercises distinct persisted view states.
            clock = anchored.Snapshot.StateEndsMs;
            await Apply(new TimerDue());
            var anchoring = ClientNotices(docked).Last(notice => notice.Name == "OnHangarViewStateUpdated");
            var anchoringViewState = Assert.IsType<PyObjectEx>(anchoring.Args.Items[1]);
            Assert.Equal(new PyInt(StructureState.Anchoring), HangarField(anchoringViewState, "_operatingState"));
            Assert.NotEqual(anchorViewState, anchoringViewState);
            Assert.Equal(MarshalCodec.Decode(MarshalCodec.Encode((await profiles.GetMyHangarViewStateAsync(Structure, CancellationToken.None))!)), anchoringViewState);

            var anchoringRecord = await store.GetAsync(Structure) ?? throw new InvalidOperationException("structure was not persisted");
            clock = anchoringRecord.Snapshot.StateEndsMs;
            await Apply(new TimerDue());
            var onlining = ClientNotices(docked).Last(notice => notice.Name == "OnHangarViewStateUpdated");
            var onliningViewState = Assert.IsType<PyObjectEx>(onlining.Args.Items[1]);
            Assert.Equal(new PyInt(StructureState.OnliningVulnerable), HangarField(onliningViewState, "_operatingState"));
            Assert.NotEqual(anchoringViewState, onliningViewState);
            Assert.Equal(MarshalCodec.Decode(MarshalCodec.Encode((await profiles.GetMyHangarViewStateAsync(Structure, CancellationToken.None))!)), onliningViewState);

            // Starting unanchoring changes the structure row, so EveJS sends the current hangar view again.
            await Apply(new UnanchorStarted());
            var afterUnanchorStart = ClientNotices(docked);
            Assert.Equal("OnHangarViewStateUpdated", afterUnanchorStart[^1].Name);
            Assert.Equal(onliningViewState, afterUnanchorStart[^1].Args.Items[1]);
            Assert.Equal(4, hangarStateReads);

            // Removal has no next structure row: keep the existing state notices, but send no hangar-view update.
            var unanchoring = await store.GetAsync(Structure) ?? throw new InvalidOperationException("structure was not persisted");
            clock = unanchoring.Snapshot.UnanchorEndsMs;
            await Apply(new TimerDue());
            var afterRemoval = ClientNotices(docked);
            Assert.Equal(
                new[]
                {
                    "OnCurrentStructureStateChanged", "OnHangarViewStateUpdated",
                    "OnCurrentStructureStateChanged", "OnHangarViewStateUpdated",
                    "OnCurrentStructureStateChanged", "OnHangarViewStateUpdated",
                    "OnHangarViewStateUpdated", "OnCurrentStructureStateChanged",
                },
                afterRemoval.Select(notice => notice.Name));
            Assert.Equal(4, afterRemoval.Count(notice => notice.Name == "OnHangarViewStateUpdated"));
            var ownerTransition = new[]
            {
                "OnStructureStateChanged", "OnCurrentStructureStateChanged",
                "OnHangarViewStateUpdated", "OnCorporationStructuresUpdated",
            };
            Assert.Equal(
                Enumerable.Repeat(ownerTransition, 3).SelectMany(notices => notices)
                    .Concat(new[]
                    {
                        "OnHangarViewStateUpdated", "OnStructureStateChanged",
                        "OnCurrentStructureStateChanged", "OnCorporationStructuresUpdated",
                    }),
                ClientNotices(member).Select(notice => notice.Name));
            Assert.Equal(4, hangarStateReads);
            Assert.Empty(Notices(stranger));
            Assert.Empty(Notices(elsewhere));
            Assert.Equal(Enumerable.Repeat("OnCurrentStructureStateChanged", 4), Notices(controlSeat));

            // The subscriber cursor survives a bus restart; acknowledged events are not delivered twice.
            var replayScheduler = new DurableScheduler(source, () => clock, new SchedulerOptions { Concurrency = 1 }, schemas[0], _ => { });
            var replay = new EventBus(source, replayScheduler, new EventBusOptions { MaxAttempts = 3 }, schemas[1], _ => { });
            StructureNotifications.Subscribe(replay, store, sessions, GetHangarViewStateAsync);
            await replay.PumpAsync();
            Assert.Equal(8, ClientNotices(docked).Count);
            Assert.Equal(16, ClientNotices(member).Count);
            Assert.Equal(4, hangarStateReads);
        }
        finally
        {
            await using var connection = await source.OpenConnectionAsync();
            foreach (var schema in schemas)
            {
                await Sql.ExecuteAsync(connection, null, $"DROP SCHEMA IF EXISTS {schema} CASCADE", CancellationToken.None);
            }
        }
    }
}
