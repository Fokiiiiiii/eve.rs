using Eve.Data;
using Eve.Dogma;
using Eve.Host;
using Eve.Inventory;
using Eve.Sde;
using Eve.Structures;
using Npgsql;
using Xunit;

namespace Eve.Host.Tests;

public sealed class MoonRigReadModelTests
{
    private const long Corporation = 98_000_001;
    private const long ForeignCorporation = 98_000_002;
    private const long SystemId = 30_000_142;
    private const int AthanorTypeId = 35_835;
    private const int TataraTypeId = 35_836;
    private const int MediumEfficiencyRigTypeId = 46_323;
    private const int MediumStabilityRigTypeId = 46_325;
    private const int LargeProficiencyRigTypeId = 46_327;

    private sealed record FittedItem(int TypeId, long OwnerId, int Flag);

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _schema;

        private Fixture(NpgsqlDataSource source, string schema, PgItemStore items, MoonRigReadModel readModel)
        {
            Source = source;
            _schema = schema;
            Items = items;
            ReadModel = readModel;
        }

        public NpgsqlDataSource Source { get; }
        public PgItemStore Items { get; }
        public MoonRigReadModel ReadModel { get; }

        public static async Task<Fixture> CreateAsync()
        {
            var connectionString = Environment.GetEnvironmentVariable("EVE_PG_URL");
            Assert.False(string.IsNullOrWhiteSpace(connectionString));
            var source = NpgsqlDataSource.Create(PgConnection.ForTest(connectionString!));
            var sde = new SdeReader(source);
            Assert.NotNull(await sde.BuildAsync());
            var dogma = await DogmaStatic.LoadAsync(sde);
            var rules = await MoonRulesSource.LoadAsync(sde);
            var schema = "moon_rig_" + Guid.NewGuid().ToString("N")[..12];
            await PgItemStore.MigrateAsync(source, schema);
            var items = new PgItemStore(source, schema);
            return new Fixture(source, schema, items, new MoonRigReadModel(items, dogma, rules));
        }

        public async Task<long> CreateStructureAsync(int structureTypeId = AthanorTypeId, params FittedItem[] fittings)
        {
            await using var connection = await Source.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            var structureId = await Items.CreateAsync(connection, transaction,
                new NewItem(structureTypeId, Corporation, SystemId, 0, 1, true));
            foreach (var fitting in fittings)
                await Items.CreateAsync(connection, transaction,
                    new NewItem(fitting.TypeId, fitting.OwnerId, structureId, fitting.Flag, 1, true));
            await transaction.CommitAsync();
            return structureId;
        }

        public async Task<MoonRules> ReadRulesAsync(long structureId)
        {
            await using var connection = await Source.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            var rules = await ReadModel.ReadAsync(connection, transaction, structureId);
            await transaction.CommitAsync();
            return rules;
        }

        public async ValueTask DisposeAsync()
        {
            await using (var connection = await Source.OpenConnectionAsync())
            await using (var command = new NpgsqlCommand($"DROP SCHEMA IF EXISTS {_schema} CASCADE", connection))
                await command.ExecuteNonQueryAsync();
            await Source.DisposeAsync();
        }
    }

    [Fact]
    public async Task Unrigged_refinery_uses_its_SDE_rules()
    {
        await using var fixture = await Fixture.CreateAsync();
        var structureId = await fixture.CreateStructureAsync();

        var rules = await fixture.ReadRulesAsync(structureId);

        Assert.Equal(1, rules.YieldMultiplier);
        Assert.Equal(10_800_000, rules.AutoFractureDelayMs);
        Assert.Equal(172_800_000, rules.FieldDecayMs);
    }

    [Fact]
    public async Task Fitted_medium_rigs_apply_their_SDE_modifiers_through_Dogma()
    {
        await using var fixture = await Fixture.CreateAsync();
        var structureId = await fixture.CreateStructureAsync(AthanorTypeId,
            new FittedItem(MediumEfficiencyRigTypeId, Corporation, 92),
            new FittedItem(MediumStabilityRigTypeId, Corporation, 93));

        var rules = await fixture.ReadRulesAsync(structureId);

        Assert.Equal(1.02, rules.YieldMultiplier, 8);
        Assert.Equal(12_960_000, rules.AutoFractureDelayMs);
        Assert.Equal(259_200_000, rules.FieldDecayMs);
    }

    [Fact]
    public async Task Tatara_accepts_large_rigs_but_foreign_wrong_size_and_out_of_slot_items_do_not_apply()
    {
        await using var fixture = await Fixture.CreateAsync();
        var structureId = await fixture.CreateStructureAsync(TataraTypeId,
            new FittedItem(LargeProficiencyRigTypeId, Corporation, 92),
            new FittedItem(MediumStabilityRigTypeId, Corporation, 93),
            new FittedItem(LargeProficiencyRigTypeId, ForeignCorporation, 94),
            new FittedItem(LargeProficiencyRigTypeId, Corporation, 95),
            new FittedItem(MediumEfficiencyRigTypeId, Corporation, 11));

        var rules = await fixture.ReadRulesAsync(structureId);

        Assert.Equal(1.02, rules.YieldMultiplier, 8);
        Assert.Equal(12_960_000, rules.AutoFractureDelayMs);
        Assert.Equal(259_200_000, rules.FieldDecayMs);
    }


    [Fact]
    public async Task Unknown_type_in_a_rig_slot_does_not_apply_a_bonus()
    {
        await using var fixture = await Fixture.CreateAsync();
        var structureId = await fixture.CreateStructureAsync(AthanorTypeId,
            new FittedItem(999_999_999, Corporation, 92));

        var rules = await fixture.ReadRulesAsync(structureId);

        Assert.Equal(1, rules.YieldMultiplier);
        Assert.Equal(10_800_000, rules.AutoFractureDelayMs);
        Assert.Equal(172_800_000, rules.FieldDecayMs);
    }

    [Fact]
    public async Task Missing_structure_item_fails_instead_of_returning_fake_rules()
    {
        await using var fixture = await Fixture.CreateAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.ReadRulesAsync(999_999_999));
    }


    [Fact]
    public async Task Non_refinery_structure_type_fails_closed()
    {
        await using var fixture = await Fixture.CreateAsync();
        var shipId = await fixture.CreateStructureAsync(587);

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.ReadRulesAsync(shipId));
    }


    [Fact]
    public async Task Non_refinery_structure_type_fails_instead_of_returning_base_rules()
    {
        await using var fixture = await Fixture.CreateAsync();
        var shipId = await fixture.CreateStructureAsync(587);

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.ReadRulesAsync(shipId));
    }
}
