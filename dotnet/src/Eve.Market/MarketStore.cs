using System.Data;
using Eve.Data;
using Npgsql;

namespace Eve.Market;

public sealed record MarketNpcSeed(string Key,int TypeId,bool Bid,decimal Price,long Volume,int Range,MarketLocation Location,long IssuedMs,int Duration = 3650);
public sealed record MarketBook(IReadOnlyList<MarketOrder> Asks,IReadOnlyList<MarketOrder> Bids);
public sealed record MarketOwnedOrder(MarketOrder Order,long ActorId,long WalletOwnerId,int AccountKey,decimal Escrow,int State,long LastStateChangeMs);
public sealed record MarketHistory(long UnixDay,decimal Low,decimal High,decimal Average,long Volume,long Orders);

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
        var existing = new Dictionary<string,long>(StringComparer.Ordinal);
        await using (var keys = new NpgsqlCommand($"SELECT seed_key,id FROM {_schema}.orders WHERE is_npc AND seed_key IS NOT NULL",connection,tx))
        await using (var reader = await keys.ExecuteReaderAsync(token))
            while (await reader.ReadAsync(token)) existing.Add(reader.GetString(0),reader.GetInt64(1));
        var rows = new List<MarketOrder>();
        foreach (var seed in seeds)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(seed.Key);
            if (seed.Price != decimal.Round(seed.Price,2)) throw new ArgumentException("Market prices must be whole cents.");
            await using var command = new NpgsqlCommand($"""
                INSERT INTO {_schema}.orders(id,type_id,bid,price,vol_remaining,vol_entered,min_volume,range_value,station_id,solar_system_id,constellation_id,region_id,issued_ms,duration_days,is_npc,seed_key)
                VALUES(COALESCE($13::bigint,nextval('{_schema}.order_ids')),$1,$2,$3,$4,$4,1,$5,$6,$7,$8,$9,$10,$11,true,$12)
                ON CONFLICT(seed_key) DO UPDATE SET type_id=$1,bid=$2,price=$3,
                    vol_remaining=GREATEST(0,$4-(orders.vol_entered-orders.vol_remaining)),vol_entered=$4,
                    range_value=$5,station_id=$6,solar_system_id=$7,constellation_id=$8,region_id=$9,duration_days=$11
                WHERE orders.is_npc AND orders.type_id=$1 AND orders.bid=$2 AND orders.station_id=$6
                RETURNING {BookColumns}
                """,connection,tx)
            {
                Parameters = { new(){Value=seed.TypeId},new(){Value=seed.Bid},new(){Value=seed.Price},new(){Value=seed.Volume},
                    new(){Value=seed.Range},new(){Value=seed.Location.StationId},new(){Value=seed.Location.SolarSystemId},
                    new(){Value=seed.Location.ConstellationId},new(){Value=seed.Location.RegionId},new(){Value=seed.IssuedMs},
                    new(){Value=seed.Duration},new(){Value=seed.Key},new(){Value=existing.TryGetValue(seed.Key,out var id) ? id : DBNull.Value} },
            };
            await using var r = await command.ExecuteReaderAsync(token);
            if (!await r.ReadAsync(token)) throw new InvalidOperationException("MARKET_SEED_IDENTITY_CONFLICT");
            var row = Read(r);
            rows.Add(row);
            existing[seed.Key] = row.Id;
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

    public async Task<IReadOnlyList<MarketOwnedOrder>> OwnOrdersAsync(long actor,long walletOwner,bool includeHistory,CancellationToken token = default)
    {
        await using var command=_source.CreateCommand($"""
            SELECT {BookColumns},actor_id,wallet_owner_id,account_key,escrow_isk,state,last_state_change_ms
            FROM {_schema}.orders WHERE NOT is_npc AND wallet_owner_id=$2
                AND (actor_id=$1 OR $2<>$1) AND ($3 OR state=0)
            ORDER BY issued_ms DESC,id DESC
            """);
        command.Parameters.Add(new NpgsqlParameter { Value=actor });
        command.Parameters.Add(new NpgsqlParameter { Value=walletOwner });
        command.Parameters.Add(new NpgsqlParameter { Value=includeHistory });
        return await ReadOwnersAsync(command,token);
    }

    public async Task<IReadOnlyList<MarketOwnedOrder>> ChangedOrdersAsync(IEnumerable<long> ids,CancellationToken token = default)
    {
        var requested=ids.Distinct().ToArray();
        if(requested.Length==0) return [];
        await using var command=_source.CreateCommand($"""
            SELECT {BookColumns},actor_id,wallet_owner_id,account_key,escrow_isk,state,last_state_change_ms
            FROM {_schema}.orders WHERE id=ANY($1) AND NOT is_npc ORDER BY id
            """);
        command.Parameters.Add(new NpgsqlParameter { Value=requested });
        return await ReadOwnersAsync(command,token);
    }

    private static async Task<IReadOnlyList<MarketOwnedOrder>> ReadOwnersAsync(NpgsqlCommand command,CancellationToken token)
    {
        var rows=new List<MarketOwnedOrder>();
        await using var r=await command.ExecuteReaderAsync(token);
        while(await r.ReadAsync(token))
            rows.Add(new(MarketStore.Read(r),r.GetInt64(15),r.GetInt64(16),r.GetInt32(17),r.GetDecimal(18),r.GetInt32(19),r.GetInt64(20)));
        return rows;
    }

    public async Task<IReadOnlyList<MarketHistory>> HistoryAsync(int region,int type,long fromUnixDay,CancellationToken token = default)
    {
        await using var command=_source.CreateCommand($"""
            SELECT floor(time_ms/86400000.0)::bigint,MIN(price),MAX(price),SUM(gross)/SUM(quantity),
                   SUM(quantity)::bigint,COUNT(*)::bigint
            FROM {_schema}.fills
            WHERE region_id=$1 AND type_id=$2 AND time_ms >= $3::bigint*86400000
            GROUP BY 1 ORDER BY 1
            """);
        command.Parameters.Add(new NpgsqlParameter { Value=region });
        command.Parameters.Add(new NpgsqlParameter { Value=type });
        command.Parameters.Add(new NpgsqlParameter { Value=fromUnixDay });
        var rows=new List<MarketHistory>();
        await using var r=await command.ExecuteReaderAsync(token);
        while(await r.ReadAsync(token)) rows.Add(new(r.GetInt64(0),r.GetDecimal(1),r.GetDecimal(2),r.GetDecimal(3),r.GetInt64(4),r.GetInt64(5)));
        return rows;
    }


    public async Task<IReadOnlyDictionary<int,IReadOnlyList<MarketHistory>>> HistoriesAsync(IEnumerable<int> types,CancellationToken token = default)
    {
        var typeIds=types.Where(id=>id>0).Distinct().ToArray();
        if(typeIds.Length==0) return new Dictionary<int,IReadOnlyList<MarketHistory>>();
        var rows=typeIds.ToDictionary(id=>id,_=>new List<MarketHistory>());
        await using var command=_source.CreateCommand($"""
            SELECT type_id,floor(time_ms/86400000.0)::bigint,MIN(price),MAX(price),SUM(gross)/SUM(quantity),
                   SUM(quantity)::bigint,COUNT(*)::bigint
            FROM {_schema}.fills WHERE type_id=ANY($1)
            GROUP BY type_id,floor(time_ms/86400000.0) ORDER BY type_id,2
            """);
        command.Parameters.Add(new NpgsqlParameter { Value=typeIds });
        await using var r=await command.ExecuteReaderAsync(token);
        while(await r.ReadAsync(token))
            rows[r.GetInt32(0)].Add(new(r.GetInt64(1),r.GetDecimal(2),r.GetDecimal(3),r.GetDecimal(4),r.GetInt64(5),r.GetInt64(6)));
        return rows.ToDictionary(entry=>entry.Key,entry=>(IReadOnlyList<MarketHistory>)entry.Value);
    }

    public async Task<IReadOnlyList<MarketOwnedOrder>> OwnOrdersAsync(long actor,long walletOwner,CancellationToken token = default)
    {
        await using var command=_source.CreateCommand($"""
            SELECT {BookColumns},actor_id,wallet_owner_id,account_key,escrow_isk,state,last_state_change_ms
            FROM {_schema}.orders WHERE NOT is_npc AND state=0
                AND ((actor_id=$1 AND wallet_owner_id=$1) OR (wallet_owner_id=$2 AND $2<>$1))
            ORDER BY issued_ms DESC,id DESC
            """);
        command.Parameters.Add(new NpgsqlParameter { Value=actor });
        command.Parameters.Add(new NpgsqlParameter { Value=walletOwner });
        return await ReadOwnersAsync(command,token);
    }

    public async Task<IReadOnlyList<MarketOwnedOrder>> OrdersForWalletAsync(long actor,long walletOwner,bool includeHistory,CancellationToken token = default)
    {
        await using var command=_source.CreateCommand($"""
            SELECT {BookColumns},actor_id,wallet_owner_id,account_key,escrow_isk,state,last_state_change_ms
            FROM {_schema}.orders WHERE NOT is_npc AND wallet_owner_id=$2 AND (actor_id=$1 OR $2<>$1)
                AND ($3 OR state=0)
            ORDER BY issued_ms DESC,id DESC
            """);
        command.Parameters.Add(new NpgsqlParameter { Value=actor });
        command.Parameters.Add(new NpgsqlParameter { Value=walletOwner });
        command.Parameters.Add(new NpgsqlParameter { Value=includeHistory });
        return await ReadOwnersAsync(command,token);
    }



}
