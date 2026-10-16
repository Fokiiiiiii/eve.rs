using Eve.Data;
using Eve.Events;
using Eve.Host.Persistence.Structures;
using Eve.Macho;
using Eve.Marshal;
using Eve.Scheduler;
using Eve.Services;
using Eve.Structures;
using Npgsql;
using Xunit;

namespace Eve.Host.Tests;

/// <summary>
/// E-15 through a real event bus: a change of a system cynosural jammer is told to the pilots in space in that system only, with the state the jammer answers.
/// </summary>
public sealed class StructureCynoJammerNotificationTests
{
    private const long StartMs = 1_700_000_000_000, Structure = 1_000_000_040;
    private const int Jita = 30_000_142, Amarr = 30_002_187;

    private sealed class Sink : IPacketSink
    {
        public List<PyValue> Packets { get; } = [];
        public void Send(PyValue packet) => Packets.Add(packet);
    }

    /// <summary>A pilot in space in the system; its packets are kept in the sink.</summary>
    private static Sink Pilot(CharacterSessions sessions, long characterId, int solarSystemId)
    {
        var sink = new Sink();
        var session = new ClientSession(sink) { SessionId = 700 + (int)characterId };
        session.Set("charid", new PyInt(characterId));
        session.Set("solarsystemid", new PyInt(solarSystemId));
        sessions.Enter(characterId, session);
        return sink;
    }

    /// <summary>The names of the notifications among the packets, in order.</summary>
    private static List<string> Names(Sink sink)
    {
        var names = new List<string>();
        foreach (var packet in sink.Packets.OfType<PyObject>())
        {
            var state = ((PyTuple)packet.Args).Items;
            if (state[0] is not PyInt { Value: MachoMessage.Notification })
                continue;
            var address = ((PyTuple)((PyObject)state[2]).Args).Items;
            names.Add(JsSemantics.StringValue(address[1]) ?? string.Empty);
        }
        return names;
    }

    [PgFact]
    public async Task A_jammer_change_is_told_to_the_pilots_in_space_in_that_system_and_a_beacon_change_is_not()
    {
        var url = Environment.GetEnvironmentVariable("EVE_PG_URL") ?? throw new InvalidOperationException("EVE_PG_URL is not set");
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(url));
        var schema = "cyno_note_" + Guid.NewGuid().ToString("N")[..12];
        await EventBus.MigrateAsync(source, schema);
        await DurableScheduler.MigrateAsync(source, schema);
        var bus = new EventBus(source, new DurableScheduler(source, () => StartMs, null, schema), null, schema);
        var sessions = new CharacterSessions();
        long? state = null;
        new StructureCynoJammerNotifications((_, _, _, _) => Task.FromResult(state), sessions).Subscribe(bus);
        await bus.PumpAsync();
        var inSystem = Pilot(sessions, 1, Jita);
        var elsewhere = Pilot(sessions, 2, Amarr);

        // the jammer goes offline in Jita: the state is null (no jammer)
        await bus.PublishAsync(StructureTimers.ServiceChangedTopic,
            new StructureServiceChanged(Structure, Jita, StructureServiceIds.CynoJammer, ServiceState.Offline, 0));
        await bus.PumpAsync();
        Assert.Equal(new[] { "OnCynoJammerChanged" }, Names(inSystem));
        Assert.Empty(elsewhere.Packets);

        // the jammer runs in Jita: the state is 0
        state = 0;
        await bus.PublishAsync(StructureTimers.ServiceChangedTopic,
            new StructureServiceChanged(Structure, Jita, StructureServiceIds.CynoJammer, ServiceState.Online, StartMs));
        await bus.PumpAsync();
        Assert.Equal(new[] { "OnCynoJammerChanged", "OnCynoJammerChanged" }, Names(inSystem));
        Assert.Empty(elsewhere.Packets);

        // a Cyno Beacon service change is not a jammer change
        await bus.PublishAsync(StructureTimers.ServiceChangedTopic,
            new StructureServiceChanged(Structure, Jita, StructureServiceIds.CynoBeacon, ServiceState.Online, StartMs));
        await bus.PumpAsync();
        Assert.Equal(2, Names(inSystem).Count);
    }
}
