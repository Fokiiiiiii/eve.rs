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

    /// <summary>The names of the notifications a session was sent, in order.</summary>
    private static List<string> Notices(Sink sink)
    {
        var names = new List<string>();
        foreach (var packet in sink.Sent.OfType<PyObject>())
        {
            var state = ((PyTuple)packet.Args).Items;
            if (state[0] is not PyInt { Value: MachoMessage.Notification })
            {
                continue;
            }
            var address = ((PyTuple)((PyObject)state[2]).Args).Items;
            names.Add(JsSemantics.StringValue(address[1]) ?? "");
        }
        return names;
    }

    [PgFact]
    public async Task The_owner_corporation_and_the_pilots_docked_in_the_structure_hear_its_state_and_a_stranger_hears_nothing()
    {
        var url = Environment.GetEnvironmentVariable("EVE_PG_URL") ?? throw new InvalidOperationException("EVE_PG_URL is not set");
        var source = NpgsqlDataSource.Create(PgConnection.ToConnectionString(url));
        await using var _ = source;
        var id = Guid.NewGuid().ToString("N")[..12];
        var schemas = new[] { $"ntf_sch_{id}", $"ntf_evt_{id}", $"ntf_str_{id}" };
        await DurableScheduler.MigrateAsync(source, schemas[0]);
        await EventBus.MigrateAsync(source, schemas[1]);
        await StructureStore.MigrateAsync(source, schemas[2]);
        try
        {
            var scheduler = new DurableScheduler(source, () => Now, new SchedulerOptions { Concurrency = 1 }, schemas[0], _ => { });
            var bus = new EventBus(source, scheduler, new EventBusOptions { MaxAttempts = 3 }, schemas[1], _ => { });
            var store = new StructureStore(source, schemas[2]);
            var timers = new StructureTimers(store, scheduler, bus, new Rules(), () => 0.5);
            timers.Register();
            var sessions = new CharacterSessions();
            StructureNotifications.Subscribe(bus, store, sessions);
            await bus.PumpAsync();

            var member = new Sink();
            var memberSession = new ClientSession(member) { SessionId = 1, CharacterId = 1 };
            memberSession.Set("corpid", new PyInt(Corporation));
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

            // the anchoring: Unanchored to AnchorVulnerable, the owner's directory changes and the docked pilot sees the new state
            await using (var connection = await source.OpenConnectionAsync())
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                await timers.AnchorAsync(connection, transaction, new NewStructure(Structure, Astrahus, Tanoo, 1, 2, 3, 0, Corporation, 0, "Notice", ""));
                await transaction.CommitAsync();
            }
            await bus.PumpAsync();
            Assert.Equal(new[] { "OnStructureStateChanged", "OnCorporationStructuresUpdated" }, Notices(member));
            Assert.Equal(new[] { "OnCurrentStructureStateChanged" }, Notices(docked));
            Assert.Empty(Notices(stranger));

            // the unanchoring starts: the state stays, nothing that the client shows changes, so nothing is sent
            await using (var connection = await source.OpenConnectionAsync())
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                await timers.ApplyAsync(connection, transaction, Structure, new UnanchorStarted());
                await transaction.CommitAsync();
            }
            await bus.PumpAsync();
            Assert.Equal(2, Notices(member).Count);
            Assert.Single(Notices(docked));
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
