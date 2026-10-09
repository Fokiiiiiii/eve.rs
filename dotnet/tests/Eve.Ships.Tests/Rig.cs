using Eve.Characters;
using Eve.Data;
using Eve.Dogma;
using Eve.Events;
using Eve.Fitting;
using Eve.Inventory;
using Eve.Scheduler;
using Eve.Sde;
using Eve.Skills;
using Eve.Wallet;
using Npgsql;

namespace Eve.Ships.Tests;

/// <summary>The rig's event bus as the fact bus of the cycles.</summary>
internal sealed class BusFacts(EventBus bus) : Eve.Ships.IFactBus
{
    public Task PublishInTransactionAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string topic, object payload, string key, CancellationToken cancellationToken = default) =>
        bus.PublishAsync(connection, transaction, topic, payload, key, cancellationToken);

    public void Wake() => bus.Wake();
}

/// <summary>Skips a test, loudly, when there is no PostgreSQL with the SDE loaded (see tools/dev/pg18.sh).</summary>
public sealed class PgFactAttribute : FactAttribute
{
    public PgFactAttribute()
    {
        if (Rig.Url is null)
        {
            Skip = "EVE_PG_URL is not set (see tools/dev/pg18.sh)";
        }
    }
}

/// <summary>Real stores in throwaway schemas, the real static data, and characters made the way the game makes them.</summary>
internal sealed class Rig : IAsyncDisposable
{
    public static readonly string? Url = Environment.GetEnvironmentVariable("EVE_PG_URL");

    public const long Station = 60015249;

    // module types of the static data
    public const int Afterburner = 21857, Railgun = 3638, Miner = 3651, DamageControlI = 2046, DamageControlII = 2048;

    private readonly string[] _schemas;

    private Rig(NpgsqlDataSource source, string suffix, DogmaStatic statics, CharacterCreation creation, IShipInsuranceInvalidator? shipInsuranceInvalidator)
    {
        Source = source;
        _schemas = [$"sc_{suffix}", $"si_{suffix}", $"ss_{suffix}", $"sw_{suffix}", $"sf_{suffix}", $"pd_{suffix}", $"pe_{suffix}"];
        Characters = new PgCharacterStore(source, _schemas[0]);
        Items = new PgItemStore(source, _schemas[1], shipInsuranceInvalidator);
        Skills = new PgSkillStore(source, _schemas[2]);
        Modules = new PgModuleStateStore(source, _schemas[4]);
        Statics = statics;
        Creation = creation;
        Ships = new CharacterShips(source, statics, Characters, Items, Skills, Modules);
        Events = new EventBus(source, new DurableScheduler(source, () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), schema: _schemas[5]), schema: _schemas[6]);
        Facts = new BusFacts(Events);
    }

    public NpgsqlDataSource Source { get; }

    /// <summary>The schema the module states and conditions are kept in.</summary>
    public string FittingSchema => _schemas[4];

    public PgCharacterStore Characters { get; }

    public PgItemStore Items { get; }

    public PgSkillStore Skills { get; }

    public PgModuleStateStore Modules { get; }

    public DogmaStatic Statics { get; }

    public CharacterCreation Creation { get; }

    public CharacterShips Ships { get; }

    /// <summary>The event bus the mining cycles publish on (its outbox is kept in this rig's schema).</summary>
    public EventBus Events { get; }

    /// <summary>The bus as the cycles see it: the facts go to <see cref="Events"/> in the grant's transaction.</summary>
    public Eve.Ships.IFactBus Facts { get; }

    public const int Cockroach = 11019;
    private const int CaldariRookieShip = 601;
    public const int FixtureBattleship = 900_001_001;
    public const int FixtureCarrier = 900_001_002;
    public const int FixtureFrigate = 900_001_003;
    public const int FixturePrismaticite = 900_001_004;
    public const int FixtureAsteroidOre = 900_001_005;
    public const int FixtureDrone = 900_001_006;
    public const int FixtureFighterCarrier = 900_001_007;

    /// <summary>The fighter bay of <see cref="FixtureFighterCarrier"/> (a squadron of the static data is far larger than a few cubic metres).</summary>
    public const double FighterCarrierBayM3 = 1_000_000;

    public static DogmaStatic WithShipFixtures(DogmaStatic statics)
    {
        const int battleshipGroup = 900_002_001;
        const int carrierGroup = 900_002_002;
        const int frigateGroup = 900_002_003;
        const int prismaticiteGroup = 900_002_004;
        const int asteroidOreGroup = 900_002_005;
        int Attribute(string name) => statics.AttributeId(name)
            ?? throw new InvalidOperationException($"SDE attribute {name} is missing");

        var common = new Dictionary<int, double>
        {
            [Attribute("mass")] = 1_000_000,
            [Attribute("capacity")] = 1_000,
            [Attribute("volume")] = 100,
            [Attribute("radius")] = 100,
            [Attribute("hp")] = 1_000,
        };
        TypeDef Ship(int typeId, int groupId, string name, params (string Attribute, double Value)[] capacities)
        {
            var attributes = new Dictionary<int, double>(common);
            foreach (var (attribute, value) in capacities)
                attributes[Attribute(attribute)] = value;
            return new TypeDef(typeId, groupId, statics.CategoryId("Ship") ?? 6, attributes, [], name);
        }

        var bayAttributes = new (string Attribute, double Value)[]
        {
            ("droneCapacity", 100),
            ("fighterCapacity", 1_000_000),
            ("frigateEscapeBayCapacity", 1),
            ("shipMaintenanceBayCapacity", 1_000_000),
            ("fleetHangarCapacity", 1_000_000),
            ("specialFuelBayCapacity", 1_000_000),
            ("generalMiningHoldCapacity", 1_000_000),
            ("specialAsteroidHoldCapacity", 1_000_000),
            ("specialIceHoldCapacity", 1_000_000),
            ("specialGasHoldCapacity", 1_000_000),
            ("specialMineralHoldCapacity", 1_000_000),
            ("specialSalvageHoldCapacity", 1_000_000),
            ("specialShipHoldCapacity", 1_000_000),
            ("specialSmallShipHoldCapacity", 1_000_000),
            ("specialMediumShipHoldCapacity", 1_000_000),
            ("specialLargeShipHoldCapacity", 1_000_000),
            ("specialIndustrialShipHoldCapacity", 1_000_000),
            ("specialAmmoHoldCapacity", 1_000_000),
            ("specialCommandCenterHoldCapacity", 1_000_000),
            ("specialPlanetaryCommoditiesHoldCapacity", 1_000_000),
            ("specialMaterialBayCapacity", 1_000_000),
            ("specialQuafeHoldCapacity", 1_000_000),
            ("specialCorpseHoldCapacity", 1_000_000),
            ("specialMobileDepotHoldCapacity", 1_000_000),
            ("specialBoosterHoldCapacity", 1_000_000),
            ("specialSubsystemHoldCapacity", 1_000_000),
            ("specialColonyResourcesHoldCapacity", 1_000_000),
            ("outputMoonMaterialBayCapacity", 1_000_000),
            ("specialExpeditionHoldCapacity", 1_000_000),
        };
        var asteroidBattleship = Ship(FixtureBattleship, battleshipGroup, "Test Battleship", bayAttributes);
        var carrierAttributes = new Dictionary<int, double>(common)
        {
            [Attribute("shipMaintenanceBayCapacity")] = 500_000,
            [Attribute("fleetHangarCapacity")] = 50_000,
        };
        var carrier = new TypeDef(FixtureCarrier, carrierGroup, statics.CategoryId("Ship") ?? 6, carrierAttributes, [], "Test Carrier");
        var frigateAttributes = new Dictionary<int, double>(common)
        {
            [TypeDef.RequiredSkillAttributes[0]] = 3327,
        };
        var frigate = new TypeDef(FixtureFrigate, frigateGroup, statics.CategoryId("Ship") ?? 6, frigateAttributes, [], "Test Frigate");
        var prismaticite = new TypeDef(
            FixturePrismaticite,
            prismaticiteGroup,
            statics.CategoryId("Asteroid") ?? 25,
            new Dictionary<int, double> { [Attribute("volume")] = 1 },
            [],
            "Test Prismaticite");
        var asteroidOre = new TypeDef(
            FixtureAsteroidOre,
            asteroidOreGroup,
            statics.CategoryId("Asteroid") ?? 25,
            new Dictionary<int, double> { [Attribute("volume")] = 1 },
            [],
            "Test Asteroid Ore");
        var drone = new TypeDef(
            FixtureDrone,
            900_002_006,
            statics.CategoryId("Drone") ?? 18,
            new Dictionary<int, double> { [Attribute("volume")] = 25 },
            [],
            "Test Drone");
        // a carrier with five launch tubes, three light and two heavy squadron slots, and a fighter bay of FighterCarrierBayM3
        var fighterCarrier = new TypeDef(
            FixtureFighterCarrier,
            carrierGroup,
            statics.CategoryId("Ship") ?? 6,
            new Dictionary<int, double>(common)
            {
                [Attribute("fighterTubes")] = 5,
                [Attribute("fighterLightSlots")] = 3,
                [Attribute("fighterHeavySlots")] = 2,
                [Attribute("fighterCapacity")] = Rig.FighterCarrierBayM3,
            },
            [],
            "Test Fighter Carrier");
        return statics.WithFixtures(
            [asteroidBattleship, carrier, frigate, prismaticite, asteroidOre, drone, fighterCarrier],
            new Dictionary<int, int>
            {
                [battleshipGroup] = statics.CategoryId("Ship") ?? 6,
                [carrierGroup] = statics.CategoryId("Ship") ?? 6,
                [frigateGroup] = statics.CategoryId("Ship") ?? 6,
                [prismaticiteGroup] = statics.CategoryId("Asteroid") ?? 25,
                [asteroidOreGroup] = statics.CategoryId("Asteroid") ?? 25,
            },
            new Dictionary<int, string>
            {
                [battleshipGroup] = "Battleship",
                [carrierGroup] = "Carrier",
                [frigateGroup] = "Frigate",
                [prismaticiteGroup] = "Prismaticite",
                [asteroidOreGroup] = "Asteroid Ore",
            });
    }

    private static CreationCatalog CockroachCatalog(CreationCatalog source) =>
        new(
            source.Races.Select(r => r.Id == 1 ? r with { ShipTypeId = Cockroach, ShipName = "Cockroach" } : r),
            source.Bloodlines,
            CreationCatalog.Schools.Select(s => source.StarterFor(s.CorporationId)!));

    public static async Task<Rig> CreateAsync(
        IShipInsuranceInvalidator? shipInsuranceInvalidator = null,
        Func<DogmaStatic, DogmaStatic>? staticFixtures = null)
    {
        CharacterNames.EnsureUnicodeSupport();
        var source = NpgsqlDataSource.Create(PgConnection.ToConnectionString(Url!));
        var sde = new SdeReader(source);
        Assert.True(await sde.BuildAsync() is not null, "no SDE is loaded: run `cargo run -p eve-sde -- ensure --pg $EVE_PG_URL`");
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var characters = new PgCharacterStore(source, $"sc_{suffix}");
        var items = new PgItemStore(source, $"si_{suffix}");
        var skills = new PgSkillStore(source, $"ss_{suffix}");
        var wallets = new PgWalletStore(source, $"sw_{suffix}");
        var modules = new PgModuleStateStore(source, $"sf_{suffix}");
        await PgCharacterStore.MigrateAsync(source, $"sc_{suffix}");
        await PgItemStore.MigrateAsync(source, $"si_{suffix}");
        await PgSkillStore.MigrateAsync(source, $"ss_{suffix}");
        await PgWalletStore.MigrateAsync(source, $"sw_{suffix}");
        await PgModuleStateStore.MigrateAsync(source, $"sf_{suffix}");
        await DurableScheduler.MigrateAsync(source, $"pd_{suffix}");
        await EventBus.MigrateAsync(source, $"pe_{suffix}");
        var creation = new CharacterCreation(
            source, characters, items, skills, await SkillCatalog.LoadAsync(sde), wallets, new StartingWallet(), CockroachCatalog(await CreationCatalog.LoadAsync(sde, Station)), modules);
        var statics = await DogmaStatic.LoadAsync(sde);
        if (staticFixtures is not null)
            statics = staticFixtures(statics);
        return new Rig(source, suffix, statics, creation, shipInsuranceInvalidator);
    }

    /// <summary>A new test character on the Cockroach hull with the starter modules fitted.</summary>
    public async Task<Created> NewCharacterAsync(string name = "Ship Tester", int account = 1, int shipTypeId = Cockroach)
    {
        await Statics.EnsureTypesAsync([Cockroach, Afterburner, Railgun, Miner]);
        var made = await Creation.CreateAsync(new CreationRequest(account, name, 1, 1, 1, 10, 35, [1], [2]));
        var character = Assert.IsType<Created>(made);
        if (shipTypeId != Cockroach)
        {
            var replacement = (await Ships.GrantAsync(character.CharacterId, shipTypeId, 1, singleton: true))!.Single().After;
            await using var connection = await Source.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            await Characters.SetShipAsync(connection, transaction, character.CharacterId, replacement.Id);
            await transaction.CommitAsync();
            character = character with { ShipId = replacement.Id };
        }
        var kit = StarterKits.For(shipTypeId == Cockroach ? CaldariRookieShip : shipTypeId);
        if (kit.Count != 0)
        {
            await using var connection = await Source.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            foreach (var module in kit)
            {
                var moduleId = await Items.CreateAsync(connection, transaction, new NewItem(module.TypeId, character.CharacterId, character.ShipId, module.Flag, 1, true));
                await Modules.SetAsync(connection, transaction, moduleId, ItemState.Online);
            }
            await transaction.CommitAsync();
        }
        return character;
    }

    /// <summary>An item of a type in the character's hangar (a module, packaged goods).</summary>
    public async Task<long> GiveAsync(long owner, int type, bool singleton = true, long quantity = 1, long location = Station, int flag = ItemFlags.Hangar)
    {
        await using var connection = await Source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var id = await Items.CreateAsync(connection, transaction, new NewItem(type, owner, location, flag, quantity, singleton));
        await transaction.CommitAsync();
        return id;
    }

    /// <summary>Sets a skill of the character to a level.</summary>
    public async Task SetSkillAsync(long owner, int type, int level)
    {
        await using var connection = await Source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await Skills.GrantAsync(connection, transaction, owner, [new CharacterSkill(type, level, 0)]);
        await transaction.CommitAsync();
    }

    /// <summary>What is in a ship, by flag: (type, flag, state of the module).</summary>
    public async Task<IReadOnlyList<(int Type, int Flag, ItemState State)>> InShipAsync(long owner, long ship)
    {
        var contents = await Items.InLocationAsync(ship, owner);
        var states = await Modules.StatesAsync(contents.Select(i => i.Id));
        return [.. contents.OrderBy(i => i.Flag).Select(i => (i.TypeId, i.Flag, states[i.Id]))];
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var schema in _schemas)
        {
            await using var command = Source.CreateCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE");
            await command.ExecuteNonQueryAsync();
        }
        await Source.DisposeAsync();
    }
}
