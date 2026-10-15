using Eve.Characters;
using Eve.Data;
using Eve.Dogma;
using Eve.Events;
using Eve.Host.Persistence.Structures;
using Eve.Inventory;
using Eve.Ships;
using Eve.Marshal;
using Eve.Scheduler;
using Eve.Services;
using Eve.Sde;
using Eve.Structures;
using Npgsql;
using System.Text.Json.Nodes;
using Xunit;

namespace Eve.Host.Tests;

/// <summary>
/// The owner corporation hears of a structure fuel burn (EveJS createStructureFuelAlertNotifications), through a real event bus on PostgreSQL: an owner
/// member online outside the structure gets OnItemChange for the burnt fuel; when no member is online the fuel alert is written with the fuel left in the bay.
/// </summary>
public sealed class StructureFuelOwnerNotificationTests
{
    private const long Corporation = 98_000_001, Structure = 1_000_000_020, ItemId = 1_000_000_021, StartMs = 1_700_000_000_000;
    private const int Astrahus = 35_832, HydrogenFuelBlock = 4_051, FuelBayFlag = 172, SolarSystemId = 30_000_142;

    private sealed class Sink : IPacketSink
    {
        public List<PyValue> Packets { get; } = [];
        public void Send(PyValue packet) => Packets.Add(packet);
    }

    private sealed record Alert(long Corporation, long[] Recipients, string Key, JsonObject Data);

    private sealed class Rig : IAsyncDisposable
    {
        private readonly NpgsqlDataSource _source;
        private readonly EventBus _bus;
        private readonly CharacterSessions _sessions = new();
        private readonly Dictionary<long, Sink> _sinks = [];
        private readonly PgCharacterStore _characters;

        public List<Alert> Alerts { get; } = [];

        private Rig(NpgsqlDataSource source, EventBus bus, PgCharacterStore characters)
        {
            _source = source;
            _bus = bus;
            _characters = characters;
        }

        public static async Task<Rig> CreateAsync()
        {
            var url = Environment.GetEnvironmentVariable("EVE_PG_URL") ?? throw new InvalidOperationException("EVE_PG_URL is not set");
            var source = NpgsqlDataSource.Create(PgConnection.ForTest(url));
            var suffix = Guid.NewGuid().ToString("N")[..12];
            var structureSchema = $"fuel_note_s_{suffix}";
            var characterSchema = $"fuel_note_c_{suffix}";
            var eventSchema = $"fuel_note_e_{suffix}";
            await StructureStore.MigrateAsync(source, structureSchema);
            await PgCharacterStore.MigrateAsync(source, characterSchema);
            await EventBus.MigrateAsync(source, eventSchema);
            await DurableScheduler.MigrateAsync(source, eventSchema);
            var sde = new SdeReader(source);
            Assert.NotNull(await sde.BuildAsync());
            var dogma = await DogmaStatic.LoadAsync(sde);

            var structures = new StructureStore(source, structureSchema);
            var characters = new PgCharacterStore(source, characterSchema);
            await using (var connection = await source.OpenConnectionAsync())
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                await structures.CreateAsync(connection, transaction, new NewStructure(
                    Structure, Astrahus, SolarSystemId, 0, 0, 0, 0, Corporation, 0, "Fuel Test", ""));
                await transaction.CommitAsync();
            }

            var scheduler = new DurableScheduler(source, () => StartMs, null, eventSchema);
            var bus = new EventBus(source, scheduler, null, eventSchema);
            var rig = new Rig(source, bus, characters);
            new StructureFuelOwnerNotifications(structures, characters, rig._sessions, dogma,
                async (corporationId, recipients, key, data, context, token) =>
                {
                    rig.Alerts.Add(new Alert(corporationId, recipients, key, data));
                    await Task.CompletedTask;
                }).Subscribe(bus);
            // the first pump registers the subscriber, so the events published after it are delivered
            await bus.PumpAsync();
            return rig;
        }

        /// <summary>A member of the owner corporation; with online it has a session (outside the structure).</summary>
        public async Task<long> MemberAsync(string name, bool online)
        {
            await using var connection = await _source.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            var id = await _characters.InsertAsync(connection, transaction, new NewCharacter(
                1, name, 0, 1, 1, 1, 1, 1373, Corporation, null, 0, SolarSystemId, 20_000_020, 10_000_002, null, null));
            await transaction.CommitAsync();
            if (online)
            {
                var sink = new Sink();
                var session = new ClientSession(sink) { SessionId = 600 + _sinks.Count };
                session.Set("charid", new PyInt(id));
                session.Set("corpid", new PyInt(Corporation));
                _sessions.Enter(id, session);
                _sinks[id] = sink;
            }
            return id;
        }

        public List<PyValue> PacketsOf(long characterId) => _sinks.TryGetValue(characterId, out var sink) ? sink.Packets : [];

        /// <summary>Burns fuel out of the structure bay (the remaining stacks given) and lets the bus deliver it.</summary>
        public async Task BurnAsync(params StructureFuelRemaining[] remaining)
        {
            var after = new Item(ItemId, HydrogenFuelBlock, Corporation, Structure, FuelBayFlag, 500, false, null);
            await _bus.PublishAsync(StructureFuelBayNotifications.Topic,
                new StructureFuelBayItemsChanged(Structure, [new ItemChanged(after, Structure, FuelBayFlag, 1_000)], remaining));
            await _bus.PumpAsync();
        }

        public async ValueTask DisposeAsync() => await _source.DisposeAsync();
    }

    [PgFact]
    public async Task An_owner_member_online_outside_the_structure_gets_the_burn_as_an_item_change_and_no_alert()
    {
        await using var rig = await Rig.CreateAsync();
        var member = await rig.MemberAsync("Online Member", online: true);
        await rig.MemberAsync("Offline Member", online: false);

        await rig.BurnAsync(new StructureFuelRemaining(HydrogenFuelBlock, 500));

        Assert.NotEmpty(rig.PacketsOf(member));
        Assert.Empty(rig.Alerts);
    }

    [PgFact]
    public async Task With_no_owner_member_online_the_alert_goes_to_every_member_with_the_fuel_left_and_no_item_change()
    {
        await using var rig = await Rig.CreateAsync();
        var first = await rig.MemberAsync("Offline Member", online: false);
        var second = await rig.MemberAsync("Second Offline Member", online: false);

        await rig.BurnAsync(new StructureFuelRemaining(HydrogenFuelBlock, 500));

        var alert = Assert.Single(rig.Alerts);
        Assert.Equal(Corporation, alert.Corporation);
        Assert.Equal(new[] { first, second }.Order().ToArray(), alert.Recipients.Order().ToArray());
        Assert.StartsWith("structure-fuel:", alert.Key);
        Assert.Equal(Structure, alert.Data["structureID"]!.GetValue<long>());
        Assert.Equal(SolarSystemId, alert.Data["solarsystemID"]!.GetValue<int>());
        var pair = Assert.IsType<JsonArray>(Assert.Single(alert.Data["listOfTypesAndQty"]!.AsArray()));
        Assert.Equal((500L, HydrogenFuelBlock), (pair[0]!.GetValue<long>(), pair[1]!.GetValue<int>()));
    }

    [PgFact]
    public async Task A_burn_that_leaves_no_fuel_writes_no_alert()
    {
        await using var rig = await Rig.CreateAsync();
        await rig.MemberAsync("Offline Member", online: false);

        await rig.BurnAsync();

        Assert.Empty(rig.Alerts);
    }
}
