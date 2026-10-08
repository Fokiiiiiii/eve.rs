using Eve.Characters;
using Eve.Data;
using Eve.Skills;
using Eve.Wallet;
using Npgsql;

namespace Eve.Corporations.Tests;

public sealed class PgFactAttribute : FactAttribute
{
    public PgFactAttribute() { if (Rig.Url is null) Skip = "EVE_PG_URL is not set"; }
}
internal sealed class Rig : IAsyncDisposable
{
    internal static readonly string? Url = Environment.GetEnvironmentVariable("EVE_PG_URL");
    private readonly string _id = Guid.NewGuid().ToString("N")[..12];
    private readonly string[] _schemas;
    private Rig(NpgsqlDataSource source)
    {
        Source = source;
        _schemas = ["cp_" + _id,"cc_" + _id,"cw_" + _id,"cs_" + _id,"ci_" + _id];
        Characters = new PgCharacterStore(source,_schemas[1]);
        Wallets = new PgWalletStore(source,_schemas[2]);
        Skills = new PgSkillStore(source,_schemas[3]);
        Transactions = new WalletTransactions(source,_schemas[2]);
        Store = new CorporationStore(source,Characters,Wallets,Transactions,Skills,() => Now,_schemas[0]);
        OfficeStore = new Eve.Corporations.CorporationOfficeStore(source,_schemas[0]);
        Items = new Eve.Inventory.PgItemStore(source,_schemas[4]);
    }
    public NpgsqlDataSource Source { get; }
    public PgCharacterStore Characters { get; }
    public PgWalletStore Wallets { get; }
    public WalletTransactions Transactions { get; }
    public PgSkillStore Skills { get; }
    public CorporationStore Store { get; }
    public Eve.Corporations.CorporationOfficeStore OfficeStore { get; }
    public Eve.Inventory.PgItemStore Items { get; }
    public long Now { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 10000;
    public string CorporationSchema => _schemas[0];
    public string CharacterSchema => _schemas[1];

    public static async Task<Rig> CreateAsync()
    {
        var rig = new Rig(NpgsqlDataSource.Create(PgConnection.ToConnectionString(Url!)));
        await PgCharacterStore.MigrateAsync(rig.Source,rig._schemas[1]);
        await PgWalletStore.MigrateAsync(rig.Source,rig._schemas[2]);
        await PgSkillStore.MigrateAsync(rig.Source,rig._schemas[3]);
        await CorporationStore.MigrateAsync(rig.Source,rig._schemas[0]);
        await Eve.Corporations.CorporationOfficeStore.MigrateAsync(rig.Source,rig._schemas[0]);
        await Eve.Inventory.PgItemStore.MigrateAsync(rig.Source,rig._schemas[4]);
        return rig;
    }

    public CorporationStore ReopenStore() => new(Source,Characters,Wallets,new WalletTransactions(Source,_schemas[2]),Skills,() => Now,_schemas[0]);
    public async Task<long> CharacterAsync(string name,decimal isk = 5000000m)
    {
        await using var connection = await Source.OpenConnectionAsync();
        await using var tx = await connection.BeginTransactionAsync();
        var id = await Characters.InsertAsync(connection,tx,new NewCharacter(1,name,0,1,1,1,11,1376,
            1000044,null,60000001,30000001,20000001,10000001,null,null));
        await Wallets.OpenAsync(connection,tx,id,isk,10);
        await tx.CommitAsync();
        return id;
    }


    public async Task SeedNpcCorporationAsync(long corporationId)
    {
        await using var connection = await Source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var owner = new NpgsqlCommand($"INSERT INTO {_schemas[0]}.owner_names(owner_id,name_key,ticker_key,is_npc) VALUES($1,$2,$3,true)", connection, transaction))
        {
            owner.Parameters.AddWithValue(corporationId);
            owner.Parameters.AddWithValue($"test npc {corporationId}");
            owner.Parameters.AddWithValue($"N{corporationId}");
            await owner.ExecuteNonQueryAsync();
        }
        await using (var corporation = new NpgsqlCommand($"""
            INSERT INTO {_schemas[0]}.corporations
                (id,name,ticker,ceo_id,creator_id,station_id,solar_system_id,tax_rate,lp_tax_rate,description,url,member_limit,is_npc,created_ms)
            VALUES($1,$2,$3,0,0,0,0,0,0,'','',-1,true,0)
            """, connection, transaction))
        {
            corporation.Parameters.AddWithValue(corporationId);
            corporation.Parameters.AddWithValue($"Test NPC {corporationId}");
            corporation.Parameters.AddWithValue($"N{corporationId}");
            await corporation.ExecuteNonQueryAsync();
        }
        await transaction.CommitAsync();
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
