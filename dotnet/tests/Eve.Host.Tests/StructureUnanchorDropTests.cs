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
    public async Task Destruction_loot_moves_fittings_fuel_and_core_into_one_container_and_destroys_the_rigs()
    {
        await using var fixture = await Fixture.CreateAsync();
        var assets = await fixture.SeedAsync();

        var drop = await fixture.CompleteDestructionAsync();

        Assert.NotNull(drop);
        Assert.Equal(0, drop!.HullItemId);
        var container = Assert.IsType<Item>(await fixture.Items.GetAsync(drop.FittingsContainerItemId));
        var space = Assert.IsType<SpaceItem>(await fixture.SpaceItems.GetAsync(drop.FittingsContainerItemId));
        Assert.Equal((StructureDestructionLoot.ContainerTypeId, CorporationId), (container.TypeId, container.OwnerId));
        Assert.Equal(StructureDestructionLoot.ContainerMarker(StructureId), container.CustomInfo);
        Assert.Equal((306, StructureId, long.MaxValue), (space.GroupId, space.LauncherId, space.ExpiresAtMs));
        Assert.Equal(new[] { 1.25, 2.5, -3.75 }, space.Position);

        Assert.Equal(drop.FittingsContainerItemId, (await fixture.Items.GetAsync(assets.FittedModule))!.LocationId);
        Assert.Equal(drop.FittingsContainerItemId, (await fixture.Items.GetAsync(assets.Fuel))!.LocationId);
        Assert.Null(await fixture.Items.GetAsync(assets.Rig));
        var coreTypeId = (int)fixture.Statics.Type(Astrahus)!.Attributes[3101];
        Assert.Contains((await fixture.Items.InContainerAsync(drop.FittingsContainerItemId)), item => item.TypeId == coreTypeId);
        Assert.DoesNotContain(await fixture.Items.InContainerAsync(StructureId), item => item.Flag is >= 11 and <= 172 or 92);
    }

    [PgFact]
    public async Task Destruction_wraps_each_owner_first_and_loots_only_what_the_wrap_leaves()
    {
        await using var fixture = await Fixture.CreateAsync();
        var assets = await fixture.SeedAsync();
        var asked = new List<(long Owner, bool Corporation)>();
        // a wrap that takes the corporation's items at the structure into one marker item
        async Task<Eve.Services.Core.AssetSafetyActionState> Wrap(NpgsqlConnection c, NpgsqlTransaction t, long structureId, long system, long owner, bool corporation, CancellationToken ct)
        {
            asked.Add((owner, corporation));
            var marker = await fixture.Items.CreateAsync(c, t, new NewItem(StructureUnanchorDrop.FittingsContainerTypeId, owner, 0, 0, Quantity: 1, Singleton: true, CustomName: "wrap"), ct);
            foreach (var item in await fixture.Items.InContainerAsync(c, t, structureId, ct))
            {
                if (item.OwnerId == owner)
                    await fixture.Items.MoveAsync(c, t, item.Id, marker, item.Flag, ct);
            }
            return Eve.Services.Core.AssetSafetyActionState.CorporationWrapCreated;
        }

        var drop = await fixture.CompleteDestructionAsync(Wrap);

        Assert.Equal([(CorporationId, true)], asked);
        Assert.NotNull(drop);
        var wrapped = (await fixture.Items.GetAsync(assets.FittedModule))!.LocationId;
        Assert.NotEqual(StructureId, wrapped);
        Assert.NotEqual(drop!.FittingsContainerItemId, wrapped);
        Assert.Equal(wrapped, (await fixture.Items.GetAsync(assets.Fuel))!.LocationId);
        Assert.Null(await fixture.Items.GetAsync(assets.Rig));
        // the wrap took the fitted items and fuel; the loot container holds only the Quantum Core that the installed core makes
        var coreTypeId = (int)fixture.Statics.Type(Astrahus)!.Attributes[3101];
        var inLoot = await fixture.Items.InContainerAsync(drop.FittingsContainerItemId);
        Assert.Equal([coreTypeId], inLoot.Select(item => item.TypeId).ToArray());
    }

    [PgFact]
    public async Task Destruction_loot_is_atomic_with_the_destruction_transaction()
    {
        await using var fixture = await Fixture.CreateAsync();
        var assets = await fixture.SeedAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await using var connection = await fixture.Source.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            var record = await fixture.Structures.GetAsync(connection, transaction, StructureId, true);
            var drop = await new StructureDestructionLootAuthority(fixture.Items, fixture.SpaceItems, fixture.Statics, Fixture.RefusedWrap)
                .CompleteAsync(connection, transaction, record!);
            Assert.NotNull(drop);
            throw new InvalidOperationException("simulate a later failure before the destruction commits");
        });

        Assert.Equal(StructureId, (await fixture.Items.GetAsync(assets.FittedModule))!.LocationId);
        var rig = Assert.IsType<Item>(await fixture.Items.GetAsync(assets.Rig));
        Assert.Equal((StructureId, 92), (rig.LocationId, rig.Flag));
    }

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

        /// <summary>An asset safety wrap that refuses every owner: the destruction loots their items.</summary>
        public static readonly Func<NpgsqlConnection, NpgsqlTransaction, long, long, long, bool, CancellationToken, Task<Eve.Services.Core.AssetSafetyActionState>> RefusedWrap =
            (_, _, _, _, _, _, _) => Task.FromResult(Eve.Services.Core.AssetSafetyActionState.AssetSafetyUnavailable);

        public async Task<StructureUnanchorDrop?> CompleteDestructionAsync(
            Func<NpgsqlConnection, NpgsqlTransaction, long, long, long, bool, CancellationToken, Task<Eve.Services.Core.AssetSafetyActionState>>? wrap = null)
        {
            await using var connection = await Source.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            var record = await Structures.GetAsync(connection, transaction, StructureId, true);
            var drop = await new StructureDestructionLootAuthority(Items, SpaceItems, Statics, wrap ?? RefusedWrap)
                .CompleteAsync(connection, transaction, record!);
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
