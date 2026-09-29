using Eve.Characters;
using Eve.Data;
using Eve.Dogma;
using Eve.Fitting;
using Eve.Inventory;
using Eve.Sde;
using Eve.Skills;
using Eve.Wallet;
using Npgsql;

namespace Eve.Ships.Tests;

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

    private Rig(NpgsqlDataSource source, string suffix, DogmaStatic statics, CharacterCreation creation)
    {
        Source = source;
        _schemas = [$"sc_{suffix}", $"si_{suffix}", $"ss_{suffix}", $"sw_{suffix}", $"sf_{suffix}"];
        Characters = new PgCharacterStore(source, _schemas[0]);
        Items = new PgItemStore(source, _schemas[1]);
        Skills = new PgSkillStore(source, _schemas[2]);
        Modules = new PgModuleStateStore(source, _schemas[4]);
        Statics = statics;
        Creation = creation;
        Ships = new CharacterShips(source, statics, Characters, Items, Skills, Modules);
    }

    public NpgsqlDataSource Source { get; }

    public PgCharacterStore Characters { get; }

    public PgItemStore Items { get; }

    public PgSkillStore Skills { get; }

    public PgModuleStateStore Modules { get; }

    public DogmaStatic Statics { get; }

    public CharacterCreation Creation { get; }

    public CharacterShips Ships { get; }

    public static async Task<Rig> CreateAsync()
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
        var creation = new CharacterCreation(
            source, characters, items, skills, await SkillCatalog.LoadAsync(sde), wallets, new StartingWallet(), await CreationCatalog.LoadAsync(sde, Station), modules);
        return new Rig(source, suffix, await DogmaStatic.LoadAsync(sde), creation);
    }

    /// <summary>A new Caldari character in its rookie ship in the station.</summary>
    public async Task<Created> NewCharacterAsync(string name = "Ship Tester", int account = 1)
    {
        var made = await Creation.CreateAsync(new CreationRequest(account, name, 1, 1, 1, 10, 35, [1], [2]));
        return Assert.IsType<Created>(made);
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
