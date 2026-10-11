using System.Data;
using Eve.Data;
using Npgsql;

namespace Eve.Market;

public sealed record MarketNpcSeed(string Key,int TypeId,bool Bid,decimal Price,long Volume,int Range,MarketLocation Location,long IssuedMs,int Duration = 3650,long MinimumVolume = 1,long? VolumeRemaining = null);
public sealed record MarketBook(IReadOnlyList<MarketOrder> Asks,IReadOnlyList<MarketOrder> Bids);
public sealed record MarketOwnedOrder(MarketOrder Order,long ActorId,long WalletOwnerId,int AccountKey,decimal Escrow,int State,long LastStateChangeMs);
/// <summary>One fill (a trade) as the transaction history reads it. Prices are per unit.</summary>
public sealed record MarketFill(long Id,int TypeId,long BuyerId,long SellerId,long BuyerWallet,int BuyerAccount,
    long SellerWallet,int SellerAccount,long StationId,long Quantity,decimal Price,long TimeMs);
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
    public static IReadOnlyList<Migration> MigrationSet() =>
    [
        .. Migrations.Embedded(typeof(MarketStore).Assembly,"Migrations"),
        new Migration("005_market_seed_v4", """
            ALTER TABLE orders ADD COLUMN seed_volume_remaining bigint
                CHECK(seed_volume_remaining IS NULL OR (seed_volume_remaining >= 0 AND seed_volume_remaining <= vol_entered));
            CREATE TABLE market_seed_state (
                version integer PRIMARY KEY CHECK(version=4),
                fingerprint text NOT NULL,
                sde_build integer NOT NULL CHECK(sde_build > 0),
                captured_at_ms bigint NOT NULL CHECK(captured_at_ms > 0),
                imported_at_ms bigint NOT NULL CHECK(imported_at_ms > 0),
                order_count bigint NOT NULL CHECK(order_count > 0)
            );
            """),
    ];
    public static Task<IReadOnlyList<string>> MigrateAsync(NpgsqlDataSource source,string schema = DefaultSchema,CancellationToken token = default) =>
        Migrations.ApplyAsync(source,schema,MigrationSet(),token);


    public async Task<bool> HasSeedV4Async(long sdeBuild,CancellationToken token = default)
    {
        if (sdeBuild<=0) throw new ArgumentOutOfRangeException(nameof(sdeBuild));
        await using var connection=await _source.OpenConnectionAsync(token);
        await using var command=new NpgsqlCommand($"SELECT 1 FROM {_schema}.market_seed_state WHERE version=4 AND sde_build=$1",connection);
        command.Parameters.Add(new(){Value=sdeBuild});
        return await command.ExecuteScalarAsync(token) is not null;
    }
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
            var seedRemaining=seed.VolumeRemaining ?? seed.Volume;
            if (seed.Volume<=0 || seedRemaining<0 || seedRemaining>seed.Volume || seed.MinimumVolume<=0 || seed.MinimumVolume>seed.Volume)
                throw new ArgumentOutOfRangeException(nameof(seeds),"Invalid seeded market volumes.");
            await using var command = new NpgsqlCommand($"""
                INSERT INTO {_schema}.orders(id,type_id,bid,price,vol_remaining,vol_entered,min_volume,range_value,station_id,solar_system_id,constellation_id,region_id,issued_ms,duration_days,is_npc,seed_key,seed_volume_remaining)
                VALUES(COALESCE($13::bigint,nextval('{_schema}.order_ids')),$1,$2,$3,$15,$4,$14,$5,$6,$7,$8,$9,$10,$11,true,$12,$15)
                ON CONFLICT(seed_key) DO UPDATE SET type_id=$1,bid=$2,price=$3,
                    vol_remaining=GREATEST(0,$15-(COALESCE(orders.seed_volume_remaining,orders.vol_entered)-orders.vol_remaining)),
                    vol_entered=$4,min_volume=$14,range_value=$5,station_id=$6,solar_system_id=$7,
                    constellation_id=$8,region_id=$9,duration_days=$11,seed_volume_remaining=$15
                WHERE orders.is_npc AND orders.type_id=$1 AND orders.bid=$2 AND orders.station_id=$6
                RETURNING {BookColumns}
                """,connection,tx)
            {
                Parameters =
                {
                    new(){Value=seed.TypeId},new(){Value=seed.Bid},new(){Value=seed.Price},new(){Value=seed.Volume},
                    new(){Value=seed.Range},new(){Value=seed.Location.StationId},new(){Value=seed.Location.SolarSystemId},
                    new(){Value=seed.Location.ConstellationId},new(){Value=seed.Location.RegionId},new(){Value=seed.IssuedMs},
                    new(){Value=seed.Duration},new(){Value=seed.Key},new(){Value=existing.TryGetValue(seed.Key,out var id) ? id : DBNull.Value},
                    new(){Value=seed.MinimumVolume},new(){Value=seedRemaining},
                },
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


    public async Task<long> SeedNpcV4Async(
        IAsyncEnumerable<MarketNpcSeed> seeds,string fingerprint,int sdeBuild,long capturedAtMs,Func<long> now,CancellationToken token = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fingerprint);
        ArgumentNullException.ThrowIfNull(now);
        if (sdeBuild<=0) throw new ArgumentOutOfRangeException(nameof(sdeBuild));
        if (capturedAtMs<=0) throw new ArgumentOutOfRangeException(nameof(capturedAtMs));

        await using var connection=await _source.OpenConnectionAsync(token);
        await using var tx=await connection.BeginTransactionAsync(token);
        await using (var gate=new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended($1,0))",connection,tx))
        {
            gate.Parameters.Add(new(){Value=$"market:{_schema}:seed:v4"});
            await gate.ExecuteNonQueryAsync(token);
        }
        await using (var prior=new NpgsqlCommand($"SELECT fingerprint,sde_build FROM {_schema}.market_seed_state WHERE version=4",connection,tx))
        await using (var reader=await prior.ExecuteReaderAsync(token))
        {
            if (await reader.ReadAsync(token) && reader.GetString(0)==fingerprint && reader.GetInt32(1)==sdeBuild)
            {
                await reader.DisposeAsync();
                await tx.CommitAsync(token);
                return 0;
            }
        }

        await using (var create=new NpgsqlCommand("""
            CREATE TEMP TABLE market_seed_v4_stage(
                row_no bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                seed_key text NOT NULL UNIQUE,
                type_id integer NOT NULL,
                bid boolean NOT NULL,
                price numeric(20,2) NOT NULL,
                vol_entered bigint NOT NULL,
                min_volume bigint NOT NULL,
                vol_remaining bigint NOT NULL,
                range_value integer NOT NULL,
                station_id bigint NOT NULL,
                solar_system_id integer NOT NULL,
                constellation_id integer NOT NULL,
                region_id integer NOT NULL,
                issued_ms bigint NOT NULL,
                duration_days integer NOT NULL
            ) ON COMMIT DROP
            """,connection,tx))
            await create.ExecuteNonQueryAsync(token);

        long count=0;
        await using (var importer=await connection.BeginBinaryImportAsync("""
            COPY market_seed_v4_stage(
                seed_key,type_id,bid,price,vol_entered,min_volume,vol_remaining,range_value,
                station_id,solar_system_id,constellation_id,region_id,issued_ms,duration_days
            ) FROM STDIN (FORMAT BINARY)
            """,token))
        {
            await foreach (var seed in seeds.WithCancellation(token))
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(seed.Key);
                var remaining=seed.VolumeRemaining ?? seed.Volume;
                var invalidField = seed.TypeId<=0 ? "type_id" :
                    seed.Price<=0 || seed.Price>(decimal)MarketRules.MaximumOrderPrice || seed.Price!=decimal.Round(seed.Price,2) ? "price" :
                    seed.Volume<=0 ? "volume_total" : remaining<0 || remaining>seed.Volume ? "volume_remain" :
                    seed.MinimumVolume<=0 || seed.MinimumVolume>seed.Volume ? "min_volume" :
                    seed.Location.StationId<=0 ? "location_id" : seed.Location.SolarSystemId<=0 ? "system_id" :
                    seed.Location.ConstellationId<=0 ? "constellation_id" : seed.Location.RegionId<=0 ? "region_id" :
                    seed.Duration<=0 ? "duration" : null;
                if (invalidField is not null)
                    throw new ArgumentException($"Invalid V4 market order seed '{seed.Key}': {invalidField}.");
                await importer.StartRowAsync(token);
                await importer.WriteAsync(seed.Key,NpgsqlTypes.NpgsqlDbType.Text,token);
                await importer.WriteAsync(seed.TypeId,NpgsqlTypes.NpgsqlDbType.Integer,token);
                await importer.WriteAsync(seed.Bid,NpgsqlTypes.NpgsqlDbType.Boolean,token);
                await importer.WriteAsync(seed.Price,NpgsqlTypes.NpgsqlDbType.Numeric,token);
                await importer.WriteAsync(seed.Volume,NpgsqlTypes.NpgsqlDbType.Bigint,token);
                await importer.WriteAsync(seed.MinimumVolume,NpgsqlTypes.NpgsqlDbType.Bigint,token);
                await importer.WriteAsync(remaining,NpgsqlTypes.NpgsqlDbType.Bigint,token);
                await importer.WriteAsync(seed.Range,NpgsqlTypes.NpgsqlDbType.Integer,token);
                await importer.WriteAsync(seed.Location.StationId,NpgsqlTypes.NpgsqlDbType.Bigint,token);
                await importer.WriteAsync(seed.Location.SolarSystemId,NpgsqlTypes.NpgsqlDbType.Integer,token);
                await importer.WriteAsync(seed.Location.ConstellationId,NpgsqlTypes.NpgsqlDbType.Integer,token);
                await importer.WriteAsync(seed.Location.RegionId,NpgsqlTypes.NpgsqlDbType.Integer,token);
                await importer.WriteAsync(seed.IssuedMs,NpgsqlTypes.NpgsqlDbType.Bigint,token);
                await importer.WriteAsync(seed.Duration,NpgsqlTypes.NpgsqlDbType.Integer,token);
                count++;
            }
            await importer.CompleteAsync(token);
        }
        if (count==0) throw new InvalidDataException("Market seed v4 is empty; refusing to replace the current seed.");
        var importedAtMs=now();
        if (importedAtMs<=0) throw new ArgumentOutOfRangeException(nameof(now));

        await using (var retire=new NpgsqlCommand($"""
            UPDATE {_schema}.orders AS old SET state=2,vol_remaining=0,
                seed_volume_remaining=COALESCE(old.seed_volume_remaining,old.vol_entered),
                last_state_change_ms=$1
            WHERE old.is_npc AND old.seed_key IS NOT NULL AND old.state=0
                AND NOT EXISTS(SELECT 1 FROM market_seed_v4_stage AS seed WHERE seed.seed_key=old.seed_key)
            """,connection,tx))
        {
            retire.Parameters.Add(new(){Value=importedAtMs});
            await retire.ExecuteNonQueryAsync(token);
        }

        await using (var upsert=new NpgsqlCommand($"""
            WITH rows AS (
                INSERT INTO {_schema}.orders AS current_orders(
                    id,type_id,bid,price,vol_remaining,vol_entered,min_volume,range_value,station_id,
                    solar_system_id,constellation_id,region_id,issued_ms,duration_days,is_npc,seed_key,seed_volume_remaining
                )
                SELECT COALESCE(old.id,nextval('{_schema}.order_ids')),seed.type_id,seed.bid,seed.price,
                    GREATEST(0,seed.vol_remaining-GREATEST(0,COALESCE(old.seed_volume_remaining,old.vol_entered,0)-COALESCE(old.vol_remaining,0))),
                    seed.vol_entered,seed.min_volume,seed.range_value,seed.station_id,seed.solar_system_id,
                    seed.constellation_id,seed.region_id,seed.issued_ms,seed.duration_days,true,seed.seed_key,seed.vol_remaining
                FROM market_seed_v4_stage AS seed
                LEFT JOIN {_schema}.orders AS old ON old.seed_key=seed.seed_key AND old.is_npc
                WHERE true
                ORDER BY seed.row_no
                ON CONFLICT(seed_key) DO UPDATE SET
                    price=EXCLUDED.price,
                    vol_remaining=GREATEST(0,EXCLUDED.seed_volume_remaining-
                        (COALESCE(current_orders.seed_volume_remaining,current_orders.vol_entered)-current_orders.vol_remaining)),
                    vol_entered=EXCLUDED.vol_entered,min_volume=EXCLUDED.min_volume,range_value=EXCLUDED.range_value,
                    station_id=EXCLUDED.station_id,solar_system_id=EXCLUDED.solar_system_id,
                    constellation_id=EXCLUDED.constellation_id,region_id=EXCLUDED.region_id,
                    issued_ms=EXCLUDED.issued_ms,duration_days=EXCLUDED.duration_days,
                    seed_volume_remaining=EXCLUDED.seed_volume_remaining
                WHERE current_orders.is_npc AND current_orders.type_id=EXCLUDED.type_id
                    AND current_orders.bid=EXCLUDED.bid AND current_orders.station_id=EXCLUDED.station_id
                RETURNING id
            )
            SELECT COUNT(*)::bigint FROM rows
            """,connection,tx))
        {
            var imported=(long)(await upsert.ExecuteScalarAsync(token))!;
            if (imported!=count) throw new InvalidOperationException("MARKET_SEED_IDENTITY_CONFLICT");
        }

        await using (var state=new NpgsqlCommand($"""
            INSERT INTO {_schema}.market_seed_state(version,fingerprint,sde_build,captured_at_ms,imported_at_ms,order_count)
            VALUES(4,$1,$2,$3,$4,$5)
            ON CONFLICT(version) DO UPDATE SET fingerprint=$1,sde_build=$2,captured_at_ms=$3,
                imported_at_ms=$4,order_count=$5
            """,connection,tx))
        {
            state.Parameters.Add(new(){Value=fingerprint});
            state.Parameters.Add(new(){Value=sdeBuild});
            state.Parameters.Add(new(){Value=capturedAtMs});
            state.Parameters.Add(new(){Value=importedAtMs});
            state.Parameters.Add(new(){Value=count});
            await state.ExecuteNonQueryAsync(token);
        }
        await tx.CommitAsync(token);
        return count;
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
                ORDER BY price {(bid ? "DESC" : "ASC")},issued_ms,id
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


    public Task<IReadOnlyList<(MarketOrder Order,long Quantity)>> StationAsksAsync(
        int region,long stationId,IReadOnlySet<long> npcStationIds,long nowMs,CancellationToken token = default) =>
        AskSummariesAsync(region,null,stationId,npcStationIds,nowMs,token);

    public Task<IReadOnlyList<(MarketOrder Order,long Quantity)>> SystemAsksAsync(
        int region,int solarSystemId,IReadOnlySet<long> npcStationIds,long nowMs,CancellationToken token = default) =>
        AskSummariesAsync(region,solarSystemId,null,npcStationIds,nowMs,token);

    public Task<IReadOnlyList<(MarketOrder Order,long Quantity)>> RegionBestAsync(
        int region,IReadOnlySet<long> npcStationIds,long nowMs,CancellationToken token = default) =>
        AskSummariesAsync(region,null,null,npcStationIds,nowMs,token);

    private async Task<IReadOnlyList<(MarketOrder Order,long Quantity)>> AskSummariesAsync(
        int region,int? solarSystemId,long? stationId,IReadOnlySet<long> npcStationIds,long nowMs,CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(npcStationIds);
        if(region<=0 || npcStationIds.Count==0 || solarSystemId is <= 0 || stationId is <= 0 ||
            stationId is { } selectedStation && !npcStationIds.Contains(selectedStation))
            return [];

        await using var connection=await _source.OpenConnectionAsync(token);
        await using var command=new NpgsqlCommand($"""
            WITH scoped AS (
                SELECT {BookColumns} FROM {_schema}.orders
                WHERE region_id=$1 AND bid=false AND state=0 AND vol_remaining>0
                    AND (is_npc OR issued_ms + duration_days::bigint*86400000 > $2)
                    AND station_id=ANY($3)
                    AND ($4::integer IS NULL OR solar_system_id=$4)
                    AND ($5::bigint IS NULL OR station_id=$5)
            ),
            summaries AS (
                SELECT DISTINCT ON (type_id) *,
                    LEAST(SUM(vol_remaining) OVER (PARTITION BY type_id),9223372036854775807)::bigint AS total_ask_quantity
                FROM scoped
                ORDER BY type_id,price,issued_ms,id
            )
            SELECT {BookColumns},total_ask_quantity FROM summaries ORDER BY type_id
            """,connection)
        {
            Parameters =
            {
                new(){Value=region},
                new(){Value=nowMs},
                new(){Value=npcStationIds.ToArray()},
                new(){Value=solarSystemId is { } system ? system : DBNull.Value},
                new(){Value=stationId is { } station ? station : DBNull.Value},
            },
        };
        var rows=new List<(MarketOrder Order,long Quantity)>();
        await using var reader=await command.ExecuteReaderAsync(token);
        while(await reader.ReadAsync(token))
            rows.Add((Read(reader),reader.GetInt64(15)));
        return rows;
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

    /// <summary>The fills a wallet took part in, as buyer or seller, in one division (null: every division), from a Unix millisecond on, newest first.</summary>
    public async Task<IReadOnlyList<MarketFill>> WalletFillsAsync(long walletOwnerId,int? accountKey,long fromMs,CancellationToken token = default)
    {
        await using var command=_source.CreateCommand($"""
            SELECT id,type_id,buyer_id,seller_id,buyer_wallet,buyer_account,seller_wallet,seller_account,station_id,quantity,price,time_ms
            FROM {_schema}.fills
            WHERE time_ms>=$2 AND (
                (buyer_wallet=$1 AND ($3::integer IS NULL OR buyer_account=$3))
                OR (seller_wallet=$1 AND ($3::integer IS NULL OR seller_account=$3)))
            ORDER BY time_ms DESC,id DESC
            """);
        command.Parameters.Add(new NpgsqlParameter { Value=walletOwnerId });
        command.Parameters.Add(new NpgsqlParameter { Value=fromMs });
        command.Parameters.Add(new NpgsqlParameter { Value=accountKey is { } key ? key : DBNull.Value });
        var rows=new List<MarketFill>();
        await using var r=await command.ExecuteReaderAsync(token);
        while(await r.ReadAsync(token))
            rows.Add(new(r.GetInt64(0),r.GetInt32(1),r.GetInt64(2),r.GetInt64(3),r.GetInt64(4),r.GetInt32(5),r.GetInt64(6),r.GetInt32(7),
                r.GetInt64(8),r.GetInt64(9),r.GetDecimal(10),r.GetInt64(11)));
        return rows;
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

    /// <summary>
    /// The average sale price of each type that has a sale: the newest day with sales, the volume-weighted average of its fills
    /// over every region (<c>SUM(gross)/SUM(quantity)</c>, the figure <see cref="HistoryAsync"/> reports per region). A type with
    /// no sale has no entry.
    /// </summary>
    public async Task<IReadOnlyDictionary<long,decimal>> LatestAverageSalesAsync(CancellationToken token = default)
    {
        await using var command=_source.CreateCommand($"""
            WITH daily AS (
                SELECT type_id,floor(time_ms/86400000.0)::bigint AS day,
                       SUM(gross)/NULLIF(SUM(quantity),0) AS average,SUM(quantity) AS volume
                FROM {_schema}.fills
                GROUP BY type_id,day)
            SELECT DISTINCT ON (type_id) type_id,average
            FROM daily WHERE volume>0
            ORDER BY type_id,day DESC
            """);
        var sales=new Dictionary<long,decimal>();
        await using var r=await command.ExecuteReaderAsync(token);
        while(await r.ReadAsync(token)) sales[r.GetInt32(0)]=r.GetDecimal(1);
        return sales;
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
