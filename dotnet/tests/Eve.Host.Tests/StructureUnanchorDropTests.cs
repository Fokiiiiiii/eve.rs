using Eve.Data;
using Eve.Dogma;
using Eve.Host.Persistence.Structures;
using Eve.Inventory;
using Eve.Sde;
using Eve.Ships;
using Eve.Structures;
using Npgsql;
using Xunit;

namespace Eve.Host.Tests;

public sealed class StructureUnanchorDropTests
{
    private const long StructureId = 9_010_011;
    private const int Astrahus = 35832;
    private const int SolarSystemId = 30000142;
    private const long CorporationId = 98_000_011;
    private const long AllianceId = 99_000_011;
    private const int Tritanium = 34;
    private const int NitrogenFuelBlock = 4051;
    private const int SmallShieldBooster = 1103;

    [PgFact]
    public async Task Asset_handoff_is_atomic_and_uses_the_SDE_core_and_container_identity()
    {
        await using var fixture = await Fixture.CreateAsync();
        var assets = await fixture.SeedAsync();
        var record = await fixture.Structures.GetAsync(StructureId);
        Assert.NotNull(record);

        StructureUnanchorDrop rolledBack = new(0, 0);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await using var connection = await fixture.Source.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            rolledBack = await fixture.Authority.CompleteAsync(connection, transaction, record!);
            Assert.True(rolledBack.HullItemId > 0);
            Assert.True(rolledBack.FittingsContainerItemId > 0);
            throw new InvalidOperationException("simulate a later state/outbox failure before commit");
        });

        Assert.Null(await fixture.Items.GetAsync(rolledBack.HullItemId));
        Assert.Null(await fixture.SpaceItems.GetAsync(rolledBack.HullItemId));
        Assert.Equal(StructureId, (await fixture.Items.GetAsync(assets.FittedModule))!.LocationId);
        Assert.Equal(StructureId, (await fixture.Items.GetAsync(assets.Fuel))!.LocationId);
        Assert.Equal(StructureId, (await fixture.Items.GetAsync(assets.Rig))!.LocationId);

        var drop = await fixture.CompleteAsync();
        var hull = Assert.IsType<Item>(await fixture.Items.GetAsync(drop.HullItemId));
        var hullSpace = Assert.IsType<SpaceItem>(await fixture.SpaceItems.GetAsync(drop.HullItemId));
        var fittings = Assert.IsType<Item>(await fixture.Items.GetAsync(drop.FittingsContainerItemId));
        var fittingsSpace = Assert.IsType<SpaceItem>(await fixture.SpaceItems.GetAsync(drop.FittingsContainerItemId));
        var hullType = fixture.Statics.Type(Astrahus)!;
        var coreTypeId = (int)hullType.Attributes[3101];
        var containerType = fixture.Statics.Type(StructureUnanchorDrop.FittingsContainerTypeId)!;

        Assert.Equal((Astrahus, CorporationId, SolarSystemId, 0), (hull.TypeId, hull.OwnerId, hull.LocationId, hull.Flag));
        Assert.Equal(StructureUnanchorDrop.HullMarker(StructureId), hull.CustomInfo);
        Assert.Equal((hullType.GroupId, StructureId, long.MaxValue), (hullSpace.GroupId, hullSpace.LauncherId, hullSpace.ExpiresAtMs));
        Assert.Equal(new[] { 1.25, 2.5, -3.75 }, hullSpace.Position);

        Assert.Equal((StructureUnanchorDrop.FittingsContainerTypeId, CorporationId, SolarSystemId, 0),
            (fittings.TypeId, fittings.OwnerId, fittings.LocationId, fittings.Flag));
        Assert.Equal(StructureUnanchorDrop.FittingsMarker(StructureId), fittings.CustomInfo);
        Assert.Equal((306, containerType.GroupId, StructureId, long.MaxValue),
            (fittingsSpace.GroupId, containerType.GroupId, fittingsSpace.LauncherId, fittingsSpace.ExpiresAtMs));
        Assert.Equal(306, fittingsSpace.GroupId);
        Assert.Equal(new[] { 1.25, 2.5, -3.75 }, fittingsSpace.Position);

        var fitted = Assert.IsType<Item>(await fixture.Items.GetAsync(assets.FittedModule));
        var fuel = Assert.IsType<Item>(await fixture.Items.GetAsync(assets.Fuel));
        Assert.Equal((fittings.Id, ItemFlags.Hangar, CorporationId, SmallShieldBooster),
            (fitted.LocationId, fitted.Flag, fitted.OwnerId, fitted.TypeId));
        Assert.Equal((fittings.Id, ItemFlags.Hangar, CorporationId, NitrogenFuelBlock),
            (fuel.LocationId, fuel.Flag, fuel.OwnerId, fuel.TypeId));
        Assert.Null(await fixture.Items.GetAsync(assets.Rig));

        var children = await fixture.Items.InContainerAsync(fittings.Id);
        var core = Assert.Single(children, item => item.TypeId == coreTypeId);
        Assert.Equal((fittings.Id, ItemFlags.Hangar, CorporationId, true),
            (core.LocationId, core.Flag, core.OwnerId, core.Singleton));
        Assert.Equal(1, core.Quantity);
    }

    private sealed record Seeded(long FittedModule, long Fuel, long Rig);

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string[] _schemas;

        private Fixture(NpgsqlDataSource source, string[] schemas, PgItemStore items, PgSpaceItemStore spaceItems,
            StructureStore structures, DogmaStatic statics, StructureUnanchorDropAuthority authority)
        {
            Source = source;
            _schemas = schemas;
            Items = items;
            SpaceItems = spaceItems;
            Structures = structures;
            Statics = statics;
            Authority = authority;
        }

        public NpgsqlDataSource Source { get; }
        public PgItemStore Items { get; }
        public PgSpaceItemStore SpaceItems { get; }
        public StructureStore Structures { get; }
        public DogmaStatic Statics { get; }
        public StructureUnanchorDropAuthority Authority { get; }

        public static async Task<Fixture> CreateAsync()
        {
            var url = Environment.GetEnvironmentVariable("EVE_PG_URL")!;
            var source = NpgsqlDataSource.Create(PgConnection.ForTest(url));
            var sde = new SdeReader(source);
            Assert.NotNull(await sde.BuildAsync());
            var statics = await DogmaStatic.LoadAsync(sde);
            var suffix = Guid.NewGuid().ToString("N")[..12];
            var schemas = new[] { $"ud_i_{suffix}", $"ud_p_{suffix}", $"ud_s_{suffix}" };
            await PgItemStore.MigrateAsync(source, schemas[0]);
            await PgSpaceItemStore.MigrateAsync(source, schemas[1]);
            await StructureStore.MigrateAsync(source, schemas[2]);
            var items = new PgItemStore(source, schemas[0]);
            var spaceItems = new PgSpaceItemStore(source, schemas[1]);
            var structures = new StructureStore(source, schemas[2]);
            await statics.EnsureTypesAsync([Astrahus, StructureUnanchorDrop.FittingsContainerTypeId, SmallShieldBooster, NitrogenFuelBlock]);
            return new Fixture(source, schemas, items, spaceItems, structures, statics,
                new StructureUnanchorDropAuthority(items, spaceItems, statics));
        }

        public async Task<Seeded> SeedAsync()
        {
            await using var connection = await Source.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            Assert.True(await Structures.CreateAsync(connection, transaction,
                new NewStructure(StructureId, Astrahus, SolarSystemId, 1.25, 2.5, -3.75, 0.5,
                    CorporationId, AllianceId, "Unanchored Astrahus", "")));
            var record = await Structures.GetAsync(connection, transaction, StructureId, true);
            Assert.NotNull(record);
            await Structures.SaveAsync(connection, transaction, record!.Snapshot with { CoreInstalled = true });

            var fitted = await Items.CreateAsync(connection, transaction,
                new NewItem(SmallShieldBooster, CorporationId, StructureId, 11, Quantity: 1, Singleton: true));
            var fuel = await Items.CreateAsync(connection, transaction,
                new NewItem(NitrogenFuelBlock, CorporationId, StructureId, 172, Quantity: 25, Singleton: false));
            var rig = await Items.CreateAsync(connection, transaction,
                new NewItem(Tritanium, CorporationId, StructureId, 92, Quantity: 1, Singleton: true));
            await transaction.CommitAsync();
            return new Seeded(fitted, fuel, rig);
        }

        public async Task<StructureUnanchorDrop> CompleteAsync()
        {
            await using var connection = await Source.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            var record = await Structures.GetAsync(connection, transaction, StructureId, true);
            var drop = await Authority.CompleteAsync(connection, transaction, record!);
            await transaction.CommitAsync();
            return drop;
        }

        public async ValueTask DisposeAsync()
        {
            await using var connection = await Source.OpenConnectionAsync();
            foreach (var schema in _schemas)
            {
                await using var command = new NpgsqlCommand($"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE", connection);
                await command.ExecuteNonQueryAsync();
            }
            await Source.DisposeAsync();
        }
    }
}
