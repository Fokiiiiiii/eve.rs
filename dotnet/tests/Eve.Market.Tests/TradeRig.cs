using Eve.Characters;
using Eve.Corporations;
using Eve.Data;
using Eve.Inventory;
using Eve.Sde;
using Eve.Skills;
using Eve.Universe;
using Eve.Wallet;
using Npgsql;

namespace Eve.Market.Tests;

internal sealed class TradeRig : IAsyncDisposable
{
    private readonly string[] _schemas;
    private TradeRig(NpgsqlDataSource source,StationCatalog stations,MarketTopology topology)
    {
        var id = Guid.NewGuid().ToString("N")[..12];
        _schemas=["mk_"+id,"mc_"+id,"mw_"+id,"mi_"+id,"ms_"+id,"cp_"+id];
        Source=source; Stations=stations; Topology=topology;
        Station=stations.All.First();
        Characters=new(source,_schemas[1]); WalletStore=new(source,_schemas[2]); Wallets=new(source,_schemas[2]);
        Items=new(source,_schemas[3]); Skills=new(source,_schemas[4]);
        Corporations=new(source,Characters,WalletStore,Wallets,Skills,() => Now,_schemas[5]);
        Book=new(source,_schemas[0]); Trading=Reopen();
    }
    public NpgsqlDataSource Source { get; }
    public StationCatalog Stations { get; }
    public MarketTopology Topology { get; }
    public Station Station { get; }
    public PgCharacterStore Characters { get; }
    public PgWalletStore WalletStore { get; }
    public WalletTransactions Wallets { get; }
    public PgItemStore Items { get; }
    public PgSkillStore Skills { get; }
    public CorporationStore Corporations { get; }
    public MarketStore Book { get; }
    public MarketTrading Trading { get; }
    public string MarketSchema => _schemas[0];
    public long Now { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()+10000;
    public MarketTrading Reopen() => new(Source,Book,Characters,Corporations,Skills,Items,Wallets,Stations,Topology,() => Now);
    public static async Task<TradeRig> Create()
    {
        var source=NpgsqlDataSource.Create(PgConnection.ToConnectionString(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        var sde=new SdeReader(source);
        var rig=new TradeRig(source,await StationCatalog.LoadAsync(sde),await MarketTopology.LoadAsync(sde));
        await MarketStore.MigrateAsync(source,rig._schemas[0]);
        await PgCharacterStore.MigrateAsync(source,rig._schemas[1]);
        await PgWalletStore.MigrateAsync(source,rig._schemas[2]);
        await PgItemStore.MigrateAsync(source,rig._schemas[3]);
        await PgSkillStore.MigrateAsync(source,rig._schemas[4]);
        await CorporationStore.MigrateAsync(source,rig._schemas[5]);
        return rig;
    }
    public async Task<long> Actor(string name,decimal isk=10000m)
    {
        await using var c=await Source.OpenConnectionAsync();
        await using var tx=await c.BeginTransactionAsync();
        var id=await Characters.InsertAsync(c,tx,new NewCharacter(1,name,0,1,1,1,11,1376,1000044,null,
            Station.Id,Station.SolarSystemId,Station.ConstellationId,Station.RegionId,null,null));
        await WalletStore.OpenAsync(c,tx,id,isk,0);
        await tx.CommitAsync();
        return id;
    }
    public async Task<long> Stock(long owner,long quantity,int flag = ItemFlags.Hangar)
    {
        await using var c=await Source.OpenConnectionAsync();
        await using var tx=await c.BeginTransactionAsync();
        var id=await Items.CreateAsync(c,tx,new NewItem(34,owner,Station.Id,flag,quantity));
        await tx.CommitAsync();
        return id;
    }
    public MarketRequest Buy(long actor,decimal price,long quantity,int days=0,long min=1,bool corp=false) =>
        new(actor,34,true,price,quantity,Station.Id,days,-1,min,UseCorporation:corp);
    public MarketRequest Sell(long actor,long item,decimal price,long quantity,int days=0,bool corp=false) =>
        new(actor,34,false,price,quantity,Station.Id,days,ItemId:item,UseCorporation:corp);
    public async ValueTask DisposeAsync()
    {
        foreach (var schema in _schemas)
        {
            await using var command=Source.CreateCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE");
            await command.ExecuteNonQueryAsync();
        }
        await Source.DisposeAsync();
    }
}
