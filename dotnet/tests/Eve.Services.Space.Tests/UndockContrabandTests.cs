using Eve.Data;
using Eve.Events;
using Eve.Inventory;
using Eve.Relations;
using Eve.Scheduler;
using Eve.Sde;
using Eve.Universe;
using Npgsql;

namespace Eve.Services.Space.Tests;

public sealed class UndockContrabandTests
{
    private const long CharacterId = 91_000_001;
    private const long OtherCharacterId = 91_000_002;
    private const int ContrabandA = 92_000_001;
    private const int ContrabandB = 92_000_002;
    private const int FittedType = 92_000_003;
    private const int OtherShipType = 92_000_004;
    private const int ForeignOwnerType = 92_000_005;
    private const int UnlistedType = 92_000_006;
    private const int FactionA = 93_000_001;
    private const int FactionB = 93_000_002;
    private const int OtherFaction = 93_000_003;
    private const int SystemAHigh = 94_000_001;
    private const int SystemALow = 94_000_002;
    private const int SystemBMid = 94_000_003;
    private const int SystemBHigh = 94_000_004;
    private const int SystemNoFaction = 94_000_005;
    private const int SystemOtherFaction = 94_000_006;
    private const int ShipType = 95_000_001;
    private const long Epoch = 1_700_000_000_000;

    [PgFact]
    public async Task Inspection_uses_SDE_faction_thresholds_and_current_owned_ship_cargo()
    {
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(Rig.Url!));
        var itemSchema = "undock_items_" + Guid.NewGuid().ToString("N");
        var sdeSchema = "undock_sde_" + Guid.NewGuid().ToString("N");

        try
        {
            await ExecuteAsync(source, $"CREATE SCHEMA {itemSchema}");
            await ExecuteAsync(source, $"CREATE SCHEMA {sdeSchema}");
            await ExecuteAsync(source, $"CREATE TABLE {sdeSchema}.\"contrabandTypes\" (\"key\" text NOT NULL, doc jsonb NOT NULL, ord integer NOT NULL)");
            await PgItemStore.MigrateAsync(source, itemSchema);
            await SeedSdeAsync(source, sdeSchema);

            var systems = Systems();
            var items = new PgItemStore(source, itemSchema);
            var ships = await CreateItemsAsync(
                source,
                items,
                new NewItem(ShipType, CharacterId, SystemAHigh, 0, Singleton: true),
                new NewItem(ShipType, CharacterId, SystemAHigh, 0, Singleton: true),
                new NewItem(ShipType, OtherCharacterId, SystemAHigh, 0, Singleton: true));

            var shipId = ships[0];
            var otherShipId = ships[1];
            var foreignShipId = ships[2];
            var inspector = await UndockContraband.LoadAsync(new SdeReader(source, sdeSchema), items, systems);

            Assert.Empty(await inspector.InspectAsync(CharacterId, shipId, SystemAHigh));

            await CreateItemsAsync(
                source,
                items,
                new NewItem(ContrabandA, CharacterId, shipId, ItemFlags.Cargo),
                new NewItem(ContrabandA, CharacterId, shipId, ItemFlags.Cargo),
                new NewItem(ContrabandB, CharacterId, shipId, ItemFlags.Cargo),
                new NewItem(FittedType, CharacterId, shipId, ItemFlags.HiSlot0),
                new NewItem(ForeignOwnerType, OtherCharacterId, shipId, ItemFlags.Cargo),
                new NewItem(OtherShipType, CharacterId, otherShipId, ItemFlags.Cargo),
                new NewItem(ContrabandA, CharacterId, foreignShipId, ItemFlags.Cargo),
                new NewItem(UnlistedType, CharacterId, shipId, ItemFlags.Cargo));

            Assert.Equal(new[] { ContrabandA }, await inspector.InspectAsync(CharacterId, shipId, SystemAHigh));
            Assert.Empty(await inspector.InspectAsync(CharacterId, shipId, SystemALow));
            Assert.Equal(new[] { ContrabandB }, await inspector.InspectAsync(CharacterId, shipId, SystemBMid));
            Assert.Equal(new[] { ContrabandA, ContrabandB }, await inspector.InspectAsync(CharacterId, shipId, SystemBHigh));
            Assert.Empty(await inspector.InspectAsync(CharacterId, shipId, SystemNoFaction));
            Assert.Empty(await inspector.InspectAsync(CharacterId, shipId, SystemOtherFaction));
            Assert.Empty(await inspector.InspectAsync(CharacterId, foreignShipId, SystemAHigh));

            var reloadedItems = new PgItemStore(source, itemSchema);
            var cargoBeforeInspection = await reloadedItems.InLocationAsync(shipId, CharacterId);
            var reloadedInspector = await UndockContraband.LoadAsync(new SdeReader(source, sdeSchema), reloadedItems, systems);
            Assert.Equal(new[] { ContrabandA }, await reloadedInspector.InspectAsync(CharacterId, shipId, SystemAHigh));
            Assert.Equal(cargoBeforeInspection, await reloadedItems.InLocationAsync(shipId, CharacterId));
        }
        finally
        {
            await ExecuteAsync(source, $"DROP SCHEMA IF EXISTS {sdeSchema} CASCADE");
            await ExecuteAsync(source, $"DROP SCHEMA IF EXISTS {itemSchema} CASCADE");
        }
    }

    [PgFact]
    public async Task Penalty_is_settled_by_the_durable_job_once_and_charges_the_fine_loses_the_standing_and_journals_it()
    {
        await using var rig = await PenaltyRig.CreateAsync(new PenaltyOptions(Balance: 1000m));

        // two units of ContrabandA: 2 x 100 ISK base x 0.1 = 20 ISK; ContrabandB has no rule for this faction and stays
        var cargo = await rig.CreateCargoAsync(new NewItem(ContrabandA, CharacterId, rig.ShipId, ItemFlags.Cargo, Quantity: 2, Singleton: false),
            new NewItem(ContrabandB, CharacterId, rig.ShipId, ItemFlags.Cargo));
        Assert.Null(await rig.Inspector.ApplyAsync(CharacterId, rig.ShipId, SystemNoFaction));

        var penalty = await rig.Inspector.ApplyAsync(CharacterId, rig.ShipId, SystemAHigh);
        Assert.NotNull(penalty);
        Assert.Equal((20m, 20m), (penalty!.Fine, penalty.Charged));
        Assert.Equal(new[] { cargo[0] }, penalty.Confiscated.Select(item => item.Id).ToArray());
        Assert.Equal(980m, await rig.Balance());
        Assert.Null(await rig.Items.GetAsync(cargo[0]));
        Assert.NotNull(await rig.Items.GetAsync(cargo[1]));

        // the standing toward the faction falls by the SDE standingLoss (0.02): 10 x -0.02 from a neutral standing
        Assert.Equal(-0.2, await rig.Relations.ValueAsync("standing", FactionA, CharacterId));
        // the fine is journaled with the client's ref type (contraband fine, 51) against the faction
        Assert.Equal((51, (long)FactionA, -20m), await rig.JournalFineAsync());
        Assert.Empty(await rig.Scheduler.ListAsync(UndockContraband.SettleJobKind));

        // the same contraband is not fined twice
        Assert.Null(await rig.Inspector.ApplyAsync(CharacterId, rig.ShipId, SystemAHigh));
        Assert.Equal(980m, await rig.Balance());
        Assert.Equal(-0.2, await rig.Relations.ValueAsync("standing", FactionA, CharacterId));
    }

    [PgFact]
    public async Task A_scheduled_penalty_survives_a_restart_and_is_settled_by_the_runner_exactly_once()
    {
        await using var rig = await PenaltyRig.CreateAsync(new PenaltyOptions(Balance: 1000m));
        await rig.CreateCargoAsync(new NewItem(ContrabandA, CharacterId, rig.ShipId, ItemFlags.Cargo));

        // the inspection schedules the job and nothing else: the process stops before the runner gets to it
        var operation = await rig.Inspector.ScheduleSettlementAsync(CharacterId, rig.ShipId, SystemAHigh);
        Assert.NotNull(operation);
        Assert.Equal(1000m, await rig.Balance());
        Assert.Single(await rig.Scheduler.ListAsync(UndockContraband.SettleJobKind));

        // a new process loads the same schemas: the runner finds the waiting job and settles it
        await using var restarted = await rig.Restart();
        Assert.Equal(1, await restarted.Scheduler.RunDueAsync());
        Assert.Equal(990m, await restarted.Balance());
        Assert.Null(await restarted.Items.GetAsync(restarted.Cargo[0]));
        Assert.Equal(0, await restarted.Scheduler.RunDueAsync());
        Assert.Equal(990m, await restarted.Balance());
        Assert.Equal(-0.2, await restarted.Relations.ValueAsync("standing", FactionA, CharacterId));
    }

    [PgFact]
    public async Task An_inspection_that_rolls_below_the_smuggling_chance_evades_and_leaves_the_cargo_and_the_wallet_alone()
    {
        await using var rig = await PenaltyRig.CreateAsync(new PenaltyOptions(Balance: 1000m, SmugglingChance: 1d));
        var cargo = await rig.CreateCargoAsync(new NewItem(ContrabandA, CharacterId, rig.ShipId, ItemFlags.Cargo));

        Assert.Null(await rig.Inspector.ApplyAsync(CharacterId, rig.ShipId, SystemAHigh));
        Assert.NotNull(await rig.Items.GetAsync(cargo[0]));
        Assert.Equal(1000m, await rig.Balance());
        Assert.Empty(await rig.Scheduler.ListAsync(UndockContraband.SettleJobKind));
        Assert.Equal(0d, await rig.Relations.ValueAsync("standing", FactionA, CharacterId));
    }

    [PgFact]
    public async Task The_inspection_roll_is_seeded_by_the_inspection_so_a_chance_of_one_half_splits_the_outcomes_deterministically()
    {
        await using var rig = await PenaltyRig.CreateAsync(new PenaltyOptions(Balance: 1000m, SmugglingChance: 0.5d));
        // EVE.js: the smuggling chance is the chance to get through, so a roll below it evades and a roll at or above it is caught
        var evades = Enumerable.Range(0, 200).First(t => UndockContraband.InspectionRoll(CharacterId, rig.ShipId, SystemAHigh, Epoch + t) < 0.5d) + Epoch;
        var caught = Enumerable.Range(0, 200).First(t => UndockContraband.InspectionRoll(CharacterId, rig.ShipId, SystemAHigh, Epoch + t) >= 0.5d) + Epoch;
        Assert.Equal(UndockContraband.InspectionRoll(CharacterId, rig.ShipId, SystemAHigh, caught),
            UndockContraband.InspectionRoll(CharacterId, rig.ShipId, SystemAHigh, caught));

        await rig.CreateCargoAsync(new NewItem(ContrabandA, CharacterId, rig.ShipId, ItemFlags.Cargo));
        rig.Clock = evades;
        Assert.Null(await rig.Inspector.ApplyAsync(CharacterId, rig.ShipId, SystemAHigh));
        Assert.Equal(1000m, await rig.Balance());

        rig.Clock = caught;
        Assert.Equal(10m, (await rig.Inspector.ApplyAsync(CharacterId, rig.ShipId, SystemAHigh))?.Fine);
        Assert.Equal(990m, await rig.Balance());
    }

    [Fact]
    public void The_standing_moves_by_the_raw_change_toward_minus_ten_and_stays_at_the_floor()
    {
        Assert.Equal(-0.2, UndockContraband.StandingAfter(0, -0.02));
        Assert.Equal(3.5, UndockContraband.StandingAfter(5, -0.1));
        Assert.Equal(-10, UndockContraband.StandingAfter(-10, -0.5));
    }

    /// <summary>A penalty rig: its own schemas, the item store, the wallet, the scheduler (with the event bus), the relations and the loaded inspector.</summary>
    private sealed class PenaltyRig : IAsyncDisposable
    {
        private readonly string[] _schemas;
        private readonly PenaltyOptions _options;
        private readonly string _itemSchema, _sdeSchema, _walletSchema, _schedulerSchema, _eventsSchema, _relationsSchema, _contrabandSchema;

        private PenaltyRig(NpgsqlDataSource source, PenaltyOptions options, string[] schemas, long shipId)
        {
            Source = source;
            _options = options;
            _schemas = schemas;
            (_itemSchema, _sdeSchema, _walletSchema, _schedulerSchema, _eventsSchema, _relationsSchema, _contrabandSchema) =
                (schemas[0], schemas[1], schemas[2], schemas[3], schemas[4], schemas[5], schemas[6]);
            ShipId = shipId;
        }

        public NpgsqlDataSource Source { get; }
        public long ShipId { get; }
        public PgItemStore Items { get; private set; } = null!;
        public DurableScheduler Scheduler { get; private set; } = null!;
        public RelationStore Relations { get; private set; } = null!;
        public UndockContraband Inspector { get; private set; } = null!;
        public Eve.Wallet.WalletTransactions Wallets { get; private set; } = null!;
        public long Clock { get; set; } = Epoch;
        public long[] Cargo { get; private set; } = [];
        private bool _owns = true;

        public static async Task<PenaltyRig> CreateAsync(PenaltyOptions options)
        {
            var source = NpgsqlDataSource.Create(PgConnection.ForTest(Rig.Url!));
            var schemas = Enumerable.Range(0, 7).Select(_ => "undock_pen_" + Guid.NewGuid().ToString("N")).ToArray();
            try
            {
                foreach (var schema in schemas)
                {
                    await ExecuteAsync(source, $"CREATE SCHEMA {schema}");
                }
                await ExecuteAsync(source, $"CREATE TABLE {schemas[1]}.\"contrabandTypes\" (\"key\" text NOT NULL, doc jsonb NOT NULL, ord integer NOT NULL)");
                await ExecuteAsync(source, $"CREATE TABLE {schemas[1]}.\"types\" (\"key\" text NOT NULL, doc jsonb NOT NULL, ord integer NOT NULL)");
                await ExecuteAsync(source,
                    $"INSERT INTO {schemas[1]}.\"types\" (\"key\", doc, ord) VALUES ('{ContrabandA}', '{{\"basePrice\": 100}}'::jsonb, 0), ('{ContrabandB}', '{{\"basePrice\": 50}}'::jsonb, 1)");
                await PgItemStore.MigrateAsync(source, schemas[0]);
                await Eve.Wallet.PgWalletStore.MigrateAsync(source, schemas[2]);
                await DurableScheduler.MigrateAsync(source, schemas[3]);
                await EventBus.MigrateAsync(source, schemas[4]);
                await RelationStore.MigrateAsync(source, schemas[5]);
                await UndockContraband.MigrateAsync(source, schemas[6]);
                await SeedSdeAsync(source, schemas[1]);

                var shipId = (await CreateItemsAsync(source, new PgItemStore(source, schemas[0]), new NewItem(ShipType, CharacterId, SystemAHigh, 0, Singleton: true)))[0];
                var rig = new PenaltyRig(source, options, schemas, shipId);
                await rig.LoadAsync();
                await using (var connection = await source.OpenConnectionAsync())
                await using (var transaction = await connection.BeginTransactionAsync())
                {
                    await new Eve.Wallet.PgWalletStore(source, schemas[2]).OpenAsync(connection, transaction, CharacterId, options.Balance, 0);
                    await transaction.CommitAsync();
                }
                return rig;
            }
            catch
            {
                await DropAsync(source, schemas);
                await source.DisposeAsync();
                throw;
            }
        }

        /// <summary>A new process over the same schemas: its own scheduler, bus, relations and inspector, all registered as the Host registers them.</summary>
        public async Task<PenaltyRig> Restart()
        {
            var restarted = new PenaltyRig(Source, _options, _schemas, ShipId) { Clock = Clock, Cargo = Cargo, _owns = false };
            await restarted.LoadAsync();
            return restarted;
        }

        private async Task LoadAsync()
        {
            var systems = Systems();
            Items = new PgItemStore(Source, _itemSchema);
            Wallets = new Eve.Wallet.WalletTransactions(Source, _walletSchema);
            Scheduler = new DurableScheduler(Source, () => Clock, new SchedulerOptions { Concurrency = 1 }, _schedulerSchema);
            var bus = new EventBus(Source, Scheduler, null, _eventsSchema);
            Relations = new RelationStore(Source, Scheduler, bus, _relationsSchema);
            var chance = _options.SmugglingChance;
            Inspector = await UndockContraband.LoadAsync(new SdeReader(Source, _sdeSchema), Items, systems, Source, Wallets, Scheduler, Relations,
                (_, _) => Task.FromResult(chance), () => Clock, _contrabandSchema);
        }

        public async Task<long[]> CreateCargoAsync(params NewItem[] newItems)
        {
            Cargo = await CreateItemsAsync(Source, Items, newItems);
            return Cargo;
        }

        public async Task<decimal> Balance() => await Wallets.AccountBalanceAsync(new Eve.Wallet.WalletAccount(CharacterId)) ?? 0m;

        /// <summary>The journal line of the fine: its ref type, its counterparty and its amount.</summary>
        public async Task<(int RefType, long Counterparty, decimal Amount)> JournalFineAsync()
        {
            await using var command = Source.CreateCommand(
                $"SELECT ref_type_id, counterparty_id, amount FROM {_walletSchema}.journal WHERE owner_id = {CharacterId} AND ref_type_id = {UndockContraband.FineRefType}");
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            return (reader.GetInt32(0), reader.GetInt64(1), reader.GetDecimal(2));
        }

        public async ValueTask DisposeAsync()
        {
            // a restarted process shares the schemas and the source of the one it restarts
            if (!_owns)
            {
                return;
            }
            await DropAsync(Source, _schemas);
            await Source.DisposeAsync();
        }

        private static async Task DropAsync(NpgsqlDataSource source, string[] schemas)
        {
            foreach (var schema in schemas)
            {
                await ExecuteAsync(source, $"DROP SCHEMA IF EXISTS {schema} CASCADE");
            }
        }
    }

    private sealed record PenaltyOptions(decimal Balance, double SmugglingChance = 0d);

    private static SolarSystemCatalog Systems() => new(
    [
        new SolarSystemInfo(SystemAHigh, "a-high", 1, 1, 0.6, FactionA),
        new SolarSystemInfo(SystemALow, "a-low", 1, 1, 0.3, FactionA),
        new SolarSystemInfo(SystemBMid, "b-mid", 1, 1, 0.75, FactionB),
        new SolarSystemInfo(SystemBHigh, "b-high", 1, 1, 0.85, FactionB),
        new SolarSystemInfo(SystemNoFaction, "no-faction", 1, 1, 0.95),
        new SolarSystemInfo(SystemOtherFaction, "other-faction", 1, 1, 0.95, OtherFaction),
    ]);

    private static async Task SeedSdeAsync(NpgsqlDataSource source, string schema)
    {
        var rows = new (int TypeId, string Document)[]
        {
            (ContrabandA, $$"""{"factions":[{"_key":{{FactionA}},"confiscateMinSec":0.4,"fineByValue":0.1,"standingLoss":0.02,"attackMinSec":0.2},{"_key":{{FactionB}},"confiscateMinSec":0.8,"fineByValue":0.3,"standingLoss":0.04,"attackMinSec":0.6}]}"""),
            (ContrabandB, $$"""{"factions":[{"_key":{{FactionB}},"confiscateMinSec":0.7,"fineByValue":0.2,"standingLoss":0.03,"attackMinSec":0.5}]}"""),
            (FittedType, $$"""{"factions":[{"_key":{{FactionA}},"confiscateMinSec":0.4,"fineByValue":0.1,"standingLoss":0.02,"attackMinSec":0.2}]}"""),
            (OtherShipType, $$"""{"factions":[{"_key":{{FactionB}},"confiscateMinSec":0.7,"fineByValue":0.2,"standingLoss":0.03,"attackMinSec":0.5}]}"""),
            (ForeignOwnerType, $$"""{"factions":[{"_key":{{FactionA}},"confiscateMinSec":0.4,"fineByValue":0.1,"standingLoss":0.02,"attackMinSec":0.2}]}"""),
            (UnlistedType, $$"""{"factions":[{"_key":"bad","confiscateMinSec":0.4},{"_key":{{FactionA}},"confiscateMinSec":"bad"}]}"""),
        };

        await using var connection = await source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        for (var index = 0; index < rows.Length; index++)
        {
            await using var command = new NpgsqlCommand(
                $$"""INSERT INTO {{schema}}."contrabandTypes" ("key", doc, ord) VALUES ($1, $2::jsonb, $3)""",
                connection,
                transaction);
            command.Parameters.AddWithValue(rows[index].TypeId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue(rows[index].Document);
            command.Parameters.AddWithValue(index);
            await command.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
    }

    private static async Task<long[]> CreateItemsAsync(
        NpgsqlDataSource source,
        PgItemStore items,
        params NewItem[] newItems)
    {
        await using var connection = await source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var ids = new long[newItems.Length];
        for (var index = 0; index < newItems.Length; index++)
        {
            ids[index] = await items.CreateAsync(connection, transaction, newItems[index]);
        }

        await transaction.CommitAsync();
        return ids;
    }

    private static async Task ExecuteAsync(NpgsqlDataSource source, string sql)
    {
        await using var command = source.CreateCommand(sql);
        await command.ExecuteNonQueryAsync();
    }
}
