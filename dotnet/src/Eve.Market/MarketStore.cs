using System.Data;
using Eve.Data;
using Npgsql;

namespace Eve.Market;

public sealed record MarketNpcSeed(string Key,int TypeId,bool Bid,decimal Price,long Volume,int Range,MarketLocation Location,long IssuedMs,int Duration = 3650);
public sealed record MarketBook(IReadOnlyList<MarketOrder> Asks,IReadOnlyList<MarketOrder> Bids);

/// <summary>Typed authoritative order facts; wallet and custody settlement joins this module's PostgreSQL transaction.</summary>
public sealed partial class MarketStore
{
    public const string DefaultSchema = "market";
    private readonly NpgsqlDataSource _source;
    private readonly string _schema;
    internal const string BookColumns = "id,type_id,bid,price,vol_remaining,vol_entered,min_volume,range_value,station_id,solar_system_id,constellation_id,region_id,issued_ms,duration_days,is_npc";
    public MarketStore(NpgsqlDataSource source,string schema = DefaultSchema)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(schema,"^[a-z_][a-z0-9_]{0,62}$")) throw new ArgumentException("Invalid market schema.",nameof(schema));
        _source=source; _schema=schema;
    }

    internal string Schema => _schema;
    public static IReadOnlyList<Migration> MigrationSet() => Migrations.Embedded(typeof(MarketStore).Assembly,"Migrations");
    public static Task<IReadOnlyList<string>> MigrateAsync(NpgsqlDataSource source,string schema = DefaultSchema,CancellationToken token = default) =>
        Migrations.ApplyAsync(source,schema,MigrationSet(),token);
    internal static MarketOrder Read(NpgsqlDataReader r) => new(r.GetInt64(0),r.GetInt32(1),r.GetBoolean(2),r.GetDecimal(3),
        r.GetInt64(4),r.GetInt64(5),r.GetInt64(6),r.GetInt32(7),new(r.GetInt64(8),r.GetInt32(9),r.GetInt32(10),r.GetInt32(11)),
        r.GetInt64(12),r.GetInt32(13),r.GetBoolean(14));

    /// <summary>NPC supply configuration is idempotent: consumed units do not reappear when the same seed is replayed.</summary>
    public async Task<IReadOnlyList<MarketOrder>> SeedNpcAsync(IEnumerable<MarketNpcSeed> seeds,CancellationToken token = default)
    {
        await using var connection = await _source.OpenConnectionAsync(token);
        await using var tx = await connection.BeginTransactionAsync(token);
        var rows = new List<MarketOrder>();
        foreach (var seed in seeds)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(seed.Key);
            if (seed.Price != decimal.Round(seed.Price,2)) throw new ArgumentException("Market prices must be whole cents.");
            await using var command = new NpgsqlCommand($"""
                INSERT INTO {_schema}.orders(type_id,bid,price,vol_remaining,vol_entered,min_volume,range_value,station_id,solar_system_id,constellation_id,region_id,issued_ms,duration_days,is_npc,seed_key)
                VALUES($1,$2,$3,$4,$4,1,$5,$6,$7,$8,$9,$10,$11,true,$12)
                ON CONFLICT(seed_key) DO UPDATE SET type_id=$1,bid=$2,price=$3,
                    vol_remaining=GREATEST(0,$4-(orders.vol_entered-orders.vol_remaining)),vol_entered=$4,
                    range_value=$5,station_id=$6,solar_system_id=$7,constellation_id=$8,region_id=$9,issued_ms=$10,duration_days=$11
                WHERE orders.is_npc AND orders.type_id=$1 AND orders.bid=$2 AND orders.station_id=$6
                RETURNING {BookColumns}
                """,connection,tx)
            {
                Parameters = { new(){Value=seed.TypeId},new(){Value=seed.Bid},new(){Value=seed.Price},new(){Value=seed.Volume},
                    new(){Value=seed.Range},new(){Value=seed.Location.StationId},new(){Value=seed.Location.SolarSystemId},
                    new(){Value=seed.Location.ConstellationId},new(){Value=seed.Location.RegionId},new(){Value=seed.IssuedMs},
                    new(){Value=seed.Duration},new(){Value=seed.Key} },
            };
            await using var r = await command.ExecuteReaderAsync(token);
            if (!await r.ReadAsync(token)) throw new InvalidOperationException("MARKET_SEED_IDENTITY_CONFLICT");
            rows.Add(Read(r));
        }
        await tx.CommitAsync(token);
        return rows;
    }

    /// <summary>Both halves use one snapshot and the source's price / station / id ordering.</summary>
    public async Task<MarketBook> BookAsync(int region,int type,long nowMs,CancellationToken token = default)
    {
        await using var connection = await _source.OpenConnectionAsync(token);
        await using var tx = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead,token);
        async Task<IReadOnlyList<MarketOrder>> Side(bool bid)
        {
            await using var command = new NpgsqlCommand($"""
                SELECT {BookColumns} FROM {_schema}.orders
                WHERE region_id=$1 AND type_id=$2 AND bid=$3 AND state=0 AND vol_remaining>0
                    AND (is_npc OR issued_ms + duration_days::bigint*86400000 > $4)
                ORDER BY price {(bid ? "DESC" : "ASC")},station_id,id
                """,connection,tx)
            {
                Parameters = { new(){Value=region},new(){Value=type},new(){Value=bid},new(){Value=nowMs} },
            };
            var rows = new List<MarketOrder>();
            await using var r = await command.ExecuteReaderAsync(token);
            while (await r.ReadAsync(token)) rows.Add(Read(r));
            return rows;
        }
        var asks = await Side(false);
        var bids = await Side(true);
        await tx.CommitAsync(token);
        return new(asks,bids);
    }
}
