using System.Data;
using System.Text.Json;
using Eve.Characters;
using Eve.Corporations;
using Eve.Inventory;
using Eve.Skills;
using Eve.Universe;
using Eve.Wallet;
using Npgsql;

namespace Eve.Market;

public sealed class MarketError(string code,params (string Name,object Value)[] arguments) : Exception(code)
{
    public string Code { get; } = code;
    /// <summary>The named arguments of a client label (for example actualBrokerFeePerc of MktBrokersFeeUnexpected2); empty for a code without one.</summary>
    public IReadOnlyList<(string Name,object Value)> Arguments { get; } = arguments;
}
public sealed record MarketRequest(long ActorId,int TypeId,bool Buying,decimal Price,long Quantity,long StationId,
    int Duration = 0,int Range = -1,long MinimumVolume = 1,long? ItemId = null,bool UseCorporation = false,double? ExpectedBrokerFee = null);
public sealed record MarketOrderChange(MarketOwnedOrder Order,string Reason,IReadOnlyList<long> RecipientCharacterIds);

public sealed record MarketReceipt(string OperationKey,long? OrderId,long FilledQuantity,decimal GrossAmount,
    IReadOnlyList<long> DeliveredItemIds,IReadOnlyList<WalletAccount> ChangedWallets)
{
    public IReadOnlyList<MarketOrderChange> ChangedOrders { get; init; } = [];
    public MarketOwnedOrder? ChangedOrder { get; init; }
    public string? ChangedOrderReason { get; init; }

    /// <summary>The region and type books whose resting orders or fills this receipt changed (M4): a client's cached book for them is stale.</summary>
    public IReadOnlyList<TouchedBook> TouchedBooks { get; init; } = [];
}

public sealed record TouchedBook(int RegionId,int TypeId);

/// <summary>
/// Orders, receipts, item custody and money share one transaction. No Node daemon or recovery Saga is required.
/// <para>Lock order (M8; every entry point follows it, MarketLockOrderTests checks the order in the source):</para>
/// <list type="number">
/// <item>the command guard: an advisory lock on the operation key (a replay or a conflicting replay waits here);</item>
/// <item>the character row (LockAndGetAsync), then the corporation row for a corporation order (MarketAuthorityAsync);</item>
/// <item>the book's advisory lock, one per region and type;</item>
/// <item>the order rows, FOR UPDATE, in book order (price, issue time, id);</item>
/// <item>the wallet rows, FOR UPDATE, sorted by owner and account key (LockBalancesAsync);</item>
/// <item>the item rows: the stock or escrow stacks the fills and the custody moves touch.</item>
/// </list>
/// </summary>
public sealed class MarketTrading(NpgsqlDataSource source,MarketStore store,PgCharacterStore characters,CorporationStore corporations,
    PgSkillStore skills,PgItemStore items,WalletTransactions wallets,StationCatalog stations,MarketTopology topology,Func<long> now,IMarketExpiryJobs expiryJobs)
{
    private string Schema => store.Schema;
    private const long EscrowBase = 9200000000;
    /// <summary>The number of resting-order rows the matching has locked over the life of this engine (diagnostics; M7 benchmark).</summary>
    public long CandidateRowsLocked => Interlocked.Read(ref _candidateRowsLocked);
    private long _candidateRowsLocked;
    /// <summary>The rows one candidate read locks at most (M7): the book is read in batches of this size.</summary>
    public const int MatchBatchSize = 50;
    private sealed record HeldOrder(MarketOrder Row,long Actor,WalletAccount Wallet,long? ItemId,decimal Escrow,
        long SourceOwner,long SourceLocation,int SourceFlag);

    private sealed record CommandTerms(MarketRequest Request,WalletAccount Wallet);
    private static NpgsqlCommand Command(NpgsqlConnection c,NpgsqlTransaction tx,string sql,params object?[] values)
    {
        var command = new NpgsqlCommand(sql,c,tx);
        foreach (var value in values) command.Parameters.Add(new NpgsqlParameter { Value=value ?? DBNull.Value });
        return command;
    }
    private async Task<T> Write<T>(Func<NpgsqlConnection,NpgsqlTransaction,Task<T>> action,CancellationToken token)
    {
        for (var attempt=0;;attempt++)
        {
            await using var c = await source.OpenConnectionAsync(token);
            await using var tx = await c.BeginTransactionAsync(IsolationLevel.Serializable,token);
            try { var result=await action(c,tx); await tx.CommitAsync(token); return result; }
            catch (PostgresException error) when (attempt<3 && error.SqlState is "40001" or "40P01")
            {
                await tx.RollbackAsync(CancellationToken.None);
                await Task.Delay(5*(attempt+1),token);
            }
        }
    }
    /// <summary>
    /// The factional warfare upgrade level of a system for a character's orders, read in the caller's transaction (the host supplies it; null: no
    /// factional warfare state, the level is 0).
    /// </summary>
    public Func<NpgsqlConnection,NpgsqlTransaction,long,long,CancellationToken,ValueTask<int>>? FactionWarfareUpgradeLevel { get; init; }

    private async Task<MarketContext> Context(NpgsqlConnection c,NpgsqlTransaction tx,long actor,Station station,CancellationToken token)
    {
        var levels = (await skills.ForCharacterAsync(c,tx,actor,token)).ToDictionary(s => s.TypeId,s => s.Level);
        int Level(int id) => levels.GetValueOrDefault(id);
        var upgrade = FactionWarfareUpgradeLevel is null ? 0 : await FactionWarfareUpgradeLevel(c,tx,actor,station.SolarSystemId,token);
        return MarketRules.Context(new(Level(3443),Level(3444),Level(3446),Level(3447),Level(16595),Level(16596),Level(16597),
            Level(16598),Level(16594),Level(16622),Level(18580)),true,solarSystem:station.SolarSystemId,factionWarfareUpgradeLevel:upgrade);
    }
    /// <summary>
    /// The resting orders a request can meet, and the fills planned over them (M7). The book is read in book order (price, then issue time,
    /// then id) in batches of <see cref="MatchBatchSize"/> rows with FOR UPDATE, and the read stops as soon as the quantity is filled. A batch
    /// locks only the rows it returns, so a call locks at most one batch more than the orders it fills.
    /// </summary>
    private async Task<(List<HeldOrder> Held,IReadOnlyList<MarketFillPlan> Fills)> MatchAsync(NpgsqlConnection c,NpgsqlTransaction tx,
        int typeId,bool buying,decimal priceLimit,long quantity,int range,Station station,long time,CancellationToken token)
    {
        var walk=new MarketFillWalk(topology,typeId,buying,MarketLocation.From(station),priceLimit,quantity,range,time);
        var held=new List<HeldOrder>();
        HeldOrder? cursor=null;
        while (!walk.Filled)
        {
            var batch=await CandidateBatchAsync(c,tx,typeId,buying,priceLimit,station.RegionId,time,cursor,token);
            foreach (var order in batch)
            {
                held.Add(order);
                walk.Offer(order.Row);
            }
            if (batch.Count < MatchBatchSize) break;
            cursor=batch[^1];
        }
        Interlocked.Add(ref _candidateRowsLocked,held.Count);
        return (held,walk.Fills);
    }

    /// <summary>The next batch of candidates after the cursor, in book order, locked FOR UPDATE.</summary>
    private async Task<IReadOnlyList<HeldOrder>> CandidateBatchAsync(NpgsqlConnection c,NpgsqlTransaction tx,int typeId,bool buying,
        decimal priceLimit,int regionId,long time,HeldOrder? after,CancellationToken token)
    {
        var values=new List<object?> { regionId,typeId,!buying,time,priceLimit+(buying ? 0.0001m : -0.0001m) };
        var cursor="";
        if (after is { } last)
        {
            values.AddRange(new object?[] { last.Row.Price,last.Row.IssuedMs,last.Row.Id });
            // the range predicate lets the index start at the cursor; the OR picks up the rows after it in book order
            cursor=$"AND price {(buying ? ">=" : "<=")} $6 AND (price {(buying ? ">" : "<")} $6 OR (price = $6 AND (issued_ms > $7 OR (issued_ms = $7 AND id > $8))))";
        }
        await using var command=Command(c,tx,$"""
            SELECT {MarketStore.BookColumns},actor_id,wallet_owner_id,account_key,escrow_item_id,escrow_isk,source_owner_id,source_location_id,source_flag
            FROM {Schema}.orders WHERE region_id=$1 AND type_id=$2 AND bid=$3 AND state=0 AND vol_remaining>0
                AND (is_npc OR issued_ms+duration_days::bigint*86400000>$4)
                AND price {(buying ? "<=" : ">=")} $5 {cursor}
            ORDER BY price {(buying ? "ASC" : "DESC")},issued_ms,id LIMIT {MatchBatchSize} FOR UPDATE
            """,values.ToArray());
        var result=new List<HeldOrder>();
        await using var reader=await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
            result.Add(new(MarketStore.Read(reader),reader.IsDBNull(15) ? 0 : reader.GetInt64(15),
                new(reader.IsDBNull(16) ? 0 : reader.GetInt64(16),reader.IsDBNull(17) ? 1000 : reader.GetInt32(17)),
                reader.IsDBNull(18) ? null : reader.GetInt64(18),reader.GetDecimal(19),
                reader.IsDBNull(20) ? 0 : reader.GetInt64(20),reader.IsDBNull(21) ? 0 : reader.GetInt64(21),reader.IsDBNull(22) ? 4 : reader.GetInt32(22)));
        return result;
    }
    private async Task Adjust(NpgsqlConnection c,NpgsqlTransaction tx,WalletAccount account,decimal amount,long counterparty,int refType,
        string reason,long time,string key,long station,CancellationToken token)
    {
        if (amount != 0 && (await wallets.AdjustAsync(c,tx,account,amount,counterparty,refType,reason,time,key,"market:"+station,token,referenceId:station)).Replayed)
            throw new MarketError("MARKET_SETTLEMENT_RECONCILIATION_REQUIRED");
    }

    private static bool HasCorporateHangarTakeRole(SessionRoles? roles, long stationId, int flag) =>
        Eve.Corporations.CorporationRoles.HasHangarAccess(roles, stationId, flag, take: true);

    public Task<MarketReceipt> PlaceAsync(MarketRequest request,string operationKey,CancellationToken token = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationKey);
        if (request.ExpectedBrokerFee is { } quote && !double.IsFinite(quote)) request=request with { ExpectedBrokerFee=null };
        if (request.Quantity is <= 0 or > int.MaxValue || request.MinimumVolume <= 0 || request.MinimumVolume > request.Quantity)
            throw new MarketError("MktInvalidMinVolumeCannotExceedQuantity");
        if (request.TypeId <= 0 || request.Price <= 0 || request.Price > (decimal)MarketRules.MaximumOrderPrice || request.Price != decimal.Round(request.Price,2))
            throw new MarketError("INVALID_MARKET_PRICE");
        if (!MarketRules.IsTick(request.Price)) throw new MarketError("MARKET_PRICE_TICK");
        if (!MarketRules.IsAllowedDuration(request.Duration) || !MarketRules.IsAllowedBuyRange(request.Range)) throw new MarketError("INVALID_MARKET_DURATION_OR_RANGE");
        return Write(async (c,tx) =>
        {
            // A command's operation key and participants are stable facts. A collision is never a successful replay.
            await using (var guard = Command(c,tx,"SELECT pg_advisory_xact_lock(hashtextextended($1,0))","market:"+Schema+":command:"+operationKey))
                await guard.ExecuteNonQueryAsync(token);
            var actor = await characters.LockAndGetAsync(c,tx,request.ActorId,token) ?? throw new MarketError("CHARACTER_NOT_FOUND");
            var station = stations.Get(request.StationId) ?? throw new MarketError("MARKET_STATION_NOT_FOUND");
            var account=new WalletAccount(actor.Id);
            SessionRoles? corporationRoles=null;
            if(request.UseCorporation)
            {
                var authority=await corporations.MarketAuthorityAsync(c,tx,actor.Id,null,station.Id,token);
                account=authority.Wallet;
                corporationRoles=authority.Roles;
            }
            var terms = new CommandTerms(request,account);
            var fingerprint = JsonSerializer.Serialize(terms);
            await using (var prior = Command(c,tx,$"SELECT fingerprint,result::text FROM {Schema}.commands WHERE operation_key=$1",operationKey))
            await using (var reader = await prior.ExecuteReaderAsync(token))
            {
                if (await reader.ReadAsync(token))
                {
                    if (JsonSerializer.Deserialize<CommandTerms>(reader.GetString(0)) != terms) throw new MarketError("MARKET_OPERATION_CONFLICT");
                    return JsonSerializer.Deserialize<MarketReceipt>(reader.GetString(1))!;
                }
            }
            var time = now();
            var context = await Context(c,tx,actor.Id,station,token);
            var current = new MarketLocation(actor.StationId,actor.SolarSystemId,actor.ConstellationId,actor.RegionId);
            var location = MarketLocation.From(station);
            // an immediate buy (duration 0) takes what matches on the book and books its unfilled rest like any buy order; the booked order lasts the
            // default buy duration, since an order of duration 0 rests nowhere
            var duration = request.Buying && request.Duration == 0 ? MarketRules.ImmediateBuyBookedDays : request.Duration;
            if (!request.Buying || duration>0)
            {
                if (topology.OrderDistance(current,location) > (request.Buying ? context.Limits.BuyRange : context.Limits.SellRange))
                    throw new MarketError("MARKET_REMOTE_SKILL_REQUIRED");
            }
            if (duration>0)
            {
                if (request.Buying && request.Range > context.Limits.Visibility) throw new MarketError("MARKET_VISIBILITY_REQUIRED");
                if (request.ExpectedBrokerFee is { } quoted && double.IsFinite(quoted) && Math.Abs(quoted-context.BrokerCommissionRate)>0.0001)
                    throw new MarketError("MktBrokersFeeUnexpected2",("actualBrokerFeePerc",100*context.BrokerCommissionRate),("expectedBrokerFeePercentage",quoted));
                await using var count = Command(c,tx,$"SELECT count(*) FROM {Schema}.orders WHERE actor_id=$1 AND state=0",actor.Id);
                if ((long)(await count.ExecuteScalarAsync(token))! >= context.Limits.Count) throw new MarketError("MARKET_ORDER_LIMIT");
            }
            // The lock is per region/type book, not server-wide. Wallet participants use their shared owner/key order.
            await using (var bookLock = Command(c,tx,"SELECT pg_advisory_xact_lock(hashtextextended($1,0))",$"market:{Schema}:book:{station.RegionId}:{request.TypeId}"))
                await bookLock.ExecuteNonQueryAsync(token);
            var (held,plans) = await MatchAsync(c,tx,request.TypeId,request.Buying,request.Price,request.Quantity,request.Range,station,time,token);
            var byId = held.ToDictionary(o => o.Row.Id);
            var makerRemaining = held.ToDictionary(o => o.Row.Id,o => o.Row.VolumeRemaining);
            var makerEscrow = held.ToDictionary(o => o.Row.Id,o => o.Escrow);
            var changedMakerIds = new HashSet<long>();
            var participants = plans.Where(p => !p.Order.IsNpc).Select(p => byId[p.Order.Id].Wallet).Append(account).Distinct().ToArray();
            var locked = await wallets.LockBalancesAsync(c,tx,participants,token);
            Item? stock = null;
            if (!request.Buying)
            {
                stock = request.ItemId is { } itemId ? await items.LockAndGetAsync(c,tx,itemId,token) : null;
                var personalStock=stock is not null && stock.OwnerId==actor.Id && stock.Flag==ItemFlags.Hangar;
                var corpStock=stock is not null && stock.OwnerId==account.OwnerId &&
                    HasCorporateHangarTakeRole(corporationRoles,station.Id,stock.Flag);
                if (stock is null || stock.TypeId != request.TypeId || stock.LocationId != station.Id ||
                    (!personalStock && !corpStock) || stock.Singleton || stock.Quantity < request.Quantity)
                    throw new MarketError("MARKET_ITEM_CUSTODY_INVALID");
            }
            var upper = checked(request.Price*request.Quantity);
            if (request.Buying)
            {
                var required = upper + (duration>0 ? (decimal)MarketRules.BrokerFee(context,null,(double)upper).Amount + (decimal)MarketRules.SccSurcharge(context,null,(double)upper).Amount : 0);
                if (locked[account]<required) throw new InsufficientFundsException(account.OwnerId);
            }
            var delivered = new List<long>();
            var grossTotal = 0m;
            var filled = 0L;
            var index = 0;
            foreach (var plan in plans)
            {
                var maker = byId[plan.Order.Id];
                var makerVolume = makerRemaining[maker.Row.Id];
                if (plan.Quantity <= 0 || plan.Quantity > makerVolume) throw new MarketError("MARKET_ESCROW_INCONSISTENT");
                var makerStation = stations.Get(maker.Row.Location.StationId) ?? throw new MarketError("MARKET_STATION_NOT_FOUND");
                var makerAccount = maker.Row.IsNpc ? new WalletAccount(makerStation.OwnerId) : maker.Wallet;
                var makerActor = maker.Row.IsNpc ? makerStation.OwnerId : maker.Actor;
                var buyer = request.Buying ? actor.Id : makerActor;
                var seller = request.Buying ? makerActor : actor.Id;
                var sellerAccount = request.Buying ? makerAccount : account;
                var buyerAccount = request.Buying ? account : makerAccount;
                var destination = request.Buying ? makerStation : station; // buyer goods stay at the seller's station
                var sellerContext = request.Buying && !maker.Row.IsNpc ? await Context(c,tx,seller,makerStation,token) : context;
                var tax = request.Buying && maker.Row.IsNpc ? 0m : (decimal)MarketRules.SalesTax(sellerContext,(double)plan.GrossAmount);
                var key = $"market:{Schema}:{operationKey}:fill:{index}";
                if (request.Buying)
                {
                    if (maker.Row.IsNpc)
                        await Adjust(c,tx,account,-plan.GrossAmount,makerStation.OwnerId,2,"Market purchase",time,key+":purchase",destination.Id,token);
                    else if (buyerAccount == sellerAccount)
                    {
                        await Adjust(c,tx,account,-plan.GrossAmount,seller,2,"Market purchase",time,key+":purchase",destination.Id,token);
                        await Adjust(c,tx,account,plan.GrossAmount,buyer,2,"Market sale",time,key+":sale",destination.Id,token);
                    }
                    else if ((await wallets.TransferAsync(c,tx,buyerAccount,sellerAccount,plan.GrossAmount,2,"Market transaction",time,key+":transfer","market:"+destination.Id,token,referenceId:destination.Id)).Replayed)
                        throw new MarketError("MARKET_SETTLEMENT_RECONCILIATION_REQUIRED");
                }
                else
                {
                    var makerEscrowRemaining=makerEscrow[maker.Row.Id];
                    if (!request.Buying && !maker.Row.IsNpc && makerEscrowRemaining != checked(maker.Row.Price*makerVolume))
                        throw new MarketError("MARKET_ESCROW_INCONSISTENT");
                    await Adjust(c,tx,account,plan.GrossAmount,maker.Row.IsNpc ? makerStation.OwnerId : buyerAccount.OwnerId,2,"Market sale",time,key+":sale",destination.Id,token);
                }
                if (tax>0) await Adjust(c,tx,sellerAccount,-tax,destination.OwnerId,54,"Market transaction tax",time,key+":tax",destination.Id,token);
                Item moved;
                if (request.Buying && maker.Row.IsNpc)
                {
                    var id = await items.CreateAsync(c,tx,new NewItem(request.TypeId,actor.Id,destination.Id,ItemFlags.Hangar,plan.Quantity),token);
                    moved = (await items.LockAndGetAsync(c,tx,id,token))!;
                }
                else
                {
                    var from = request.Buying
                        ? maker.ItemId is { } escrowId ? await items.LockAndGetAsync(c,tx,escrowId,token) : null
                        : await items.LockAndGetAsync(c,tx,stock!.Id,token);
                    if (from is null || from.TypeId != request.TypeId || from.Quantity < plan.Quantity ||
                        request.Buying && (from.Quantity != makerVolume || from.OwnerId != maker.SourceOwner ||
                            from.LocationId != EscrowBase+maker.Row.Id || from.Flag != ItemFlags.Hangar))
                        throw new MarketError("MARKET_ITEM_CUSTODY_INVALID");
                    moved = await items.MoveStackAsync(c,tx,from,plan.Quantity,
                        !request.Buying && maker.Row.IsNpc ? makerStation.OwnerId : buyer,destination.Id,ItemFlags.Hangar,token);
                    if (!request.Buying && maker.Row.IsNpc) await items.DeleteAsync(c,tx,moved.Id,token);
                }
                if (request.Buying || !maker.Row.IsNpc) delivered.Add(moved.Id);
                var remainingMaker = makerVolume-plan.Quantity;
                var makerEscrowDelta=!request.Buying && !maker.Row.IsNpc ? plan.GrossAmount : 0m;
                await using (var update = Command(c,tx,$"""
                    UPDATE {Schema}.orders SET vol_remaining=vol_remaining-$2,
                        state=CASE WHEN vol_remaining-$2=0 AND NOT is_npc THEN 1 ELSE state END,
                        escrow_isk=escrow_isk-$3,escrow_item_id=CASE WHEN vol_remaining-$2=0 THEN NULL ELSE escrow_item_id END,
                        last_state_change_ms=$4 WHERE id=$1 AND vol_remaining >= $2
                    """,maker.Row.Id,plan.Quantity,makerEscrowDelta,time))
                    if (await update.ExecuteNonQueryAsync(token)!=1) throw new MarketError("MARKET_ORDER_NOT_ACTIVE");
                makerRemaining[maker.Row.Id]=remainingMaker;
                makerEscrow[maker.Row.Id]-=makerEscrowDelta;
                changedMakerIds.Add(maker.Row.Id);
                await using (var history = Command(c,tx,$"""
                    INSERT INTO {Schema}.fills(order_id,type_id,buyer_id,seller_id,buyer_wallet,seller_wallet,buyer_account,seller_account,
                        station_id,region_id,quantity,price,gross,tax,time_ms,operation_key,fill_index)
                    VALUES($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13,$14,$15,$16,$17)
                    """,maker.Row.Id,request.TypeId,buyer,seller,buyerAccount.OwnerId,sellerAccount.OwnerId,buyerAccount.AccountKey,sellerAccount.AccountKey,
                    destination.Id,station.RegionId,plan.Quantity,maker.Row.Price,plan.GrossAmount,tax,time,operationKey,index))
                    await history.ExecuteNonQueryAsync(token);
                filled += plan.Quantity; grossTotal += plan.GrossAmount; index++;
            }
            long? opened = null;
            var remaining = request.Quantity-filled;
            if (duration>0 && remaining>0)
            {
                var value = checked(request.Price*remaining);
                await Adjust(c,tx,account,-(decimal)MarketRules.BrokerFee(context,null,(double)value).Amount,station.OwnerId,46,"Market broker fee",time,
                    $"market:{Schema}:{operationKey}:broker",station.Id,token);
                await Adjust(c,tx,account,-(decimal)MarketRules.SccSurcharge(context,null,(double)value).Amount,station.OwnerId,149,"Market provider tax",time,
                    $"market:{Schema}:{operationKey}:scc",station.Id,token);
                if (request.Buying) await Adjust(c,tx,account,-value,station.OwnerId,42,"Market buy escrow",time,$"market:{Schema}:{operationKey}:escrow",station.Id,token);
                await using (var insert = Command(c,tx,$"""
                    INSERT INTO {Schema}.orders(type_id,bid,price,vol_remaining,vol_entered,min_volume,range_value,station_id,solar_system_id,constellation_id,region_id,
                        issued_ms,duration_days,actor_id,wallet_owner_id,account_key,escrow_isk,source_owner_id,source_location_id,source_flag)
                    VALUES($1,$2,$3,$4,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13,$14,$15,$16,$17,$18,$19) RETURNING id
                    """,request.TypeId,request.Buying,request.Price,remaining,request.Buying ? request.MinimumVolume : 1L,
                    request.Buying ? request.Range : -1,station.Id,station.SolarSystemId,station.ConstellationId,station.RegionId,time,duration,
                    actor.Id,account.OwnerId,account.AccountKey,request.Buying ? value : 0m,
                    stock?.OwnerId ?? actor.Id,stock?.LocationId ?? station.Id,stock?.Flag ?? ItemFlags.Hangar))
                    opened = (long)(await insert.ExecuteScalarAsync(token))!;
                await ScheduleExpiryAsync(c,tx,opened.Value,time,duration,token);
                if (!request.Buying)
                {
                    var rest = await items.LockAndGetAsync(c,tx,stock!.Id,token) ?? throw new MarketError("MARKET_ITEM_CUSTODY_INVALID");
                    var escrow = await items.MoveStackAsync(c,tx,rest,remaining,stock!.OwnerId,EscrowBase+opened.Value,ItemFlags.Hangar,token);
                    await using var bind = Command(c,tx,$"UPDATE {Schema}.orders SET escrow_item_id=$2 WHERE id=$1",opened.Value,escrow.Id);
                    await bind.ExecuteNonQueryAsync(token);
                }
            }
            var changes=new List<MarketOrderChange>();
            foreach (var makerId in plans.Select(plan=>plan.Order.Id).Where(changedMakerIds.Contains).Distinct())
            {
                var maker=byId[makerId];
                if (maker.Row.IsNpc) continue;
                var makerVolume=makerRemaining[makerId];
                var makerState=makerVolume==0 ? 1 : 0;
                var changedMaker=new MarketOwnedOrder(maker.Row with { VolumeRemaining=makerVolume },maker.Actor,
                    maker.Wallet.OwnerId,maker.Wallet.AccountKey,makerEscrow[makerId],makerState,time);
                changes.Add(new(changedMaker,makerState==1 ? "Filled" : "PartialFill",
                    await NoticeRecipientsAsync(c,tx,maker.Wallet.OwnerId,maker.Actor,maker.Row.Location.StationId,[],token)));
            }
            if (opened is { } openedId)
            {
                var openedRow=new MarketOrder(openedId,request.TypeId,request.Buying,request.Price,remaining,remaining,
                    request.Buying ? request.MinimumVolume : 1L,request.Buying ? request.Range : -1,location,time,duration);
                var openedEscrow=request.Buying ? checked(request.Price*remaining) : 0m;
                var created=new MarketOwnedOrder(openedRow,actor.Id,account.OwnerId,account.AccountKey,openedEscrow,0,time);
                changes.Add(new(created,"Created",await NoticeRecipientsAsync(c,tx,account.OwnerId,actor.Id,station.Id,[],token)));
            }
            var receipt = new MarketReceipt(operationKey,opened,filled,grossTotal,delivered,participants) { ChangedOrders=changes,
                TouchedBooks=filled>0 || opened is not null ? [new TouchedBook(station.RegionId,request.TypeId)] : [] };
            await using (var finish = Command(c,tx,$"INSERT INTO {Schema}.commands(operation_key,fingerprint,time_ms,result) VALUES($1,$2,$3,$4::jsonb)",
                operationKey,fingerprint,time,JsonSerializer.Serialize(receipt))) await finish.ExecuteNonQueryAsync(token);
            return receipt;
        },token);
    }

    private sealed record ControlTerms(string Action,long ActorId,long OrderId);
    private async Task<(HeldOrder Order,int State)?> GetOrder(NpgsqlConnection c,NpgsqlTransaction tx,long id,bool locked,CancellationToken token)
    {
        await using var command=Command(c,tx,$"""
            SELECT {MarketStore.BookColumns},actor_id,wallet_owner_id,account_key,escrow_item_id,escrow_isk,source_owner_id,source_location_id,source_flag,state
            FROM {Schema}.orders WHERE id=$1
            """ + (locked ? " FOR UPDATE" : ""),id);
        await using var r=await command.ExecuteReaderAsync(token);
        if (!await r.ReadAsync(token)) return null;
        return (new HeldOrder(MarketStore.Read(r),r.IsDBNull(15) ? 0 : r.GetInt64(15),
            new(r.IsDBNull(16) ? 0 : r.GetInt64(16),r.IsDBNull(17) ? 1000 : r.GetInt32(17)),
            r.IsDBNull(18) ? null : r.GetInt64(18),r.GetDecimal(19),r.IsDBNull(20) ? 0 : r.GetInt64(20),
            r.IsDBNull(21) ? 0 : r.GetInt64(21),r.IsDBNull(22) ? 4 : r.GetInt32(22)),r.GetInt32(23));
    }

    public async Task<MarketReceipt> CancelAsync(long actorId,long orderId,string operationKey,CancellationToken token = default) =>
        (await CloseAsync(actorId,orderId,operationKey,false,token))!;

public Task<MarketReceipt> ModifyAsync(long actorId,long orderId,decimal newPrice,string operationKey,CancellationToken token = default,
        decimal? expectedPrice = null,long? expectedIssuedMs = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationKey);
        if (newPrice <= 0 || newPrice > (decimal)MarketRules.MaximumOrderPrice)
            throw new MarketError("INVALID_MARKET_PRICE");
        if (!MarketRules.IsTick(newPrice)) throw new MarketError("MARKET_PRICE_TICK");
        return Write(async (c,tx) =>
        {
            await using (var guard=Command(c,tx,"SELECT pg_advisory_xact_lock(hashtextextended($1,0))","market:"+Schema+":command:"+operationKey))
                await guard.ExecuteNonQueryAsync(token);
            var terms=new ControlTerms("modify:"+newPrice.ToString("0.00",System.Globalization.CultureInfo.InvariantCulture),actorId,orderId);
            await using (var prior=Command(c,tx,$"SELECT fingerprint,result::text FROM {Schema}.commands WHERE operation_key=$1",operationKey))
            await using (var r=await prior.ExecuteReaderAsync(token))
            {
                if (await r.ReadAsync(token))
                {
                    if (JsonSerializer.Deserialize<ControlTerms>(r.GetString(0)) != terms) throw new MarketError("MARKET_OPERATION_CONFLICT");
                    return JsonSerializer.Deserialize<MarketReceipt>(r.GetString(1))!;
                }
            }
            var actor=await characters.LockAndGetAsync(c,tx,actorId,token) ?? throw new MarketError("CHARACTER_NOT_FOUND");
            var initial=await GetOrder(c,tx,orderId,false,token) ?? throw new MarketError("MARKET_ORDER_NOT_FOUND");
            var first=initial.Order;
            if (first.Row.IsNpc) throw new MarketError("MARKET_ORDER_ACCESS_DENIED");
            if (first.Wallet.OwnerId != first.Actor &&
                (await corporations.MarketAuthorityAsync(c,tx,actorId,first.Wallet.AccountKey,first.Row.Location.StationId,token)).Wallet != first.Wallet)
                throw new MarketError("MARKET_ORDER_ACCESS_DENIED");
            if (first.Wallet.OwnerId == first.Actor && actor.Id != first.Actor) throw new MarketError("MARKET_ORDER_ACCESS_DENIED");
            await using (var bookLock=Command(c,tx,"SELECT pg_advisory_xact_lock(hashtextextended($1,0))",$"market:{Schema}:book:{first.Row.Location.RegionId}:{first.Row.TypeId}"))
                await bookLock.ExecuteNonQueryAsync(token);
            var current=await GetOrder(c,tx,orderId,true,token) ?? throw new MarketError("MARKET_ORDER_NOT_FOUND");
            var order=current.Order;
            if (order.Row.IsNpc) throw new MarketError("MARKET_ORDER_ACCESS_DENIED");
            var station=stations.Get(order.Row.Location.StationId) ?? throw new MarketError("MARKET_STATION_NOT_FOUND");
            var time=now();
            async Task<MarketReceipt> Save(MarketReceipt receipt)
            {
                await using var save=Command(c,tx,$"INSERT INTO {Schema}.commands(operation_key,fingerprint,time_ms,result) VALUES($1,$2,$3,$4::jsonb)",
                    operationKey,JsonSerializer.Serialize(terms),time,JsonSerializer.Serialize(receipt));
                await save.ExecuteNonQueryAsync(token);
                return receipt;
            }
            if (current.State != 0 || order.Row.VolumeRemaining <= 0)
                return await Save(new(operationKey,orderId,0,0m,[],[]));
            // Q15: the client's view of the price and the issue time must still be the order's; a stale view is refused, not applied
            if (expectedPrice is { } shownPrice && shownPrice != order.Row.Price ||
                expectedIssuedMs is { } shownIssued && shownIssued != order.Row.IssuedMs)
                throw new MarketError("MARKET_ORDER_STALE");
            // Q3: a modification waits five minutes after the later of the issue and the last modification
            var readyAt = Math.Max(order.Row.IssuedMs,await LastModifiedAsync(c,tx,orderId,token)) + MarketRules.ModificationDelayMs;
            if (time < readyAt) throw new MarketError("MARKET_MODIFY_TOO_SOON",("remainingSeconds",(readyAt-time+999)/1000));
            var context=await Context(c,tx,actor.Id,station,token);
            var location=new MarketLocation(actor.StationId,actor.SolarSystemId,actor.ConstellationId,actor.RegionId);
            if (topology.OrderDistance(location,MarketLocation.From(station)) > context.Limits.ModifyRange)
                throw new MarketError("MARKET_REMOTE_SKILL_REQUIRED");
            if (order.Row.Price == newPrice) return await Save(new(operationKey,orderId,0,0m,[],[]));

            var oldVolume=order.Row.VolumeRemaining;
            var oldValue=checked(order.Row.Price*oldVolume);
            var newValue=checked(newPrice*oldVolume);
            if (order.Row.Bid && order.Escrow != oldValue || !order.Row.Bid && order.Escrow != 0m)
                throw new MarketError("MARKET_ESCROW_INCONSISTENT");
            var (opposing,fills)=await MatchAsync(c,tx,order.Row.TypeId,order.Row.Bid,newPrice,oldVolume,order.Row.Range,station,time,token);
            var byId=opposing.ToDictionary(o=>o.Row.Id);
            var makerRemaining=opposing.ToDictionary(o=>o.Row.Id,o=>o.Row.VolumeRemaining);
            var makerEscrow=opposing.ToDictionary(o=>o.Row.Id,o=>o.Escrow);
            var changedWallets=new HashSet<WalletAccount>{order.Wallet};
            if (order.Row.Bid)
                foreach (var maker in fills.Where(fill=>!fill.Order.IsNpc))
                    changedWallets.Add(byId[maker.Order.Id].Wallet);
            await wallets.LockBalancesAsync(c,tx,changedWallets.OrderBy(wallet=>wallet.OwnerId).ThenBy(wallet=>wallet.AccountKey).ToArray(),token);
            Item? ownSellItem=null;
            if (!order.Row.Bid)
            {
                ownSellItem=order.ItemId is { } itemId ? await items.LockAndGetAsync(c,tx,itemId,token) : null;
                if (ownSellItem is null || ownSellItem.TypeId != order.Row.TypeId || ownSellItem.OwnerId != order.SourceOwner ||
                    ownSellItem.LocationId != EscrowBase+orderId || ownSellItem.Flag != ItemFlags.Hangar ||
                    ownSellItem.Quantity != oldVolume) throw new MarketError("MARKET_ITEM_CUSTODY_INVALID");
            }


            var reserveDelta=order.Row.Bid ? newValue-oldValue : 0m;
            var brokerFee=(decimal)MarketRules.BrokerFee(context,(double)oldValue,(double)newValue).Amount;
            var sccFee=(decimal)MarketRules.SccSurcharge(context,(double)oldValue,(double)newValue).Amount;
            await Adjust(c,tx,order.Wallet,-reserveDelta,station.OwnerId,42,
                reserveDelta > 0 ? "Market buy escrow increase" : "Market buy escrow refund",time,
                $"market:{Schema}:{operationKey}:reserve",station.Id,token);
            await Adjust(c,tx,order.Wallet,-brokerFee,station.OwnerId,46,"Market broker fee for modified order",time,
                $"market:{Schema}:{operationKey}:broker",station.Id,token);
            await Adjust(c,tx,order.Wallet,-sccFee,station.OwnerId,149,"Market provider tax for modified order",time,
                $"market:{Schema}:{operationKey}:scc",station.Id,token);

            var delivered=new List<long>();
            var grossTotal=0m;
            var filled=0L;
            var remaining=oldVolume;
            var escrow=order.Row.Bid ? newValue : 0m;
            var sellerContexts=new Dictionary<(long CharacterId,long StationId),MarketContext>();
            async Task<MarketContext> SellerContext(long characterId,Station sellerStation)
            {
                var key=(characterId,sellerStation.Id);
                if (!sellerContexts.TryGetValue(key,out var sellerContext))
                    sellerContexts[key]=sellerContext=await Context(c,tx,characterId,sellerStation,token);
                return sellerContext;
            }
            var index=0;
            foreach (var plan in fills)
            {
                var maker=byId[plan.Order.Id];
                var makerVolume=makerRemaining[maker.Row.Id];
                if (plan.Quantity <= 0 || plan.Quantity > makerVolume)
                    throw new MarketError("MARKET_ESCROW_INCONSISTENT");
                var makerStation=stations.Get(maker.Row.Location.StationId) ?? throw new MarketError("MARKET_STATION_NOT_FOUND");
                var makerActor=maker.Row.IsNpc ? makerStation.OwnerId : maker.Actor;
                var makerAccount=maker.Row.IsNpc ? new WalletAccount(makerStation.OwnerId) : maker.Wallet;
                var destination=order.Row.Bid ? makerStation : station;
                var buyer=order.Row.Bid ? order.Actor : makerActor;
                var seller=order.Row.Bid ? makerActor : order.Actor;
                var buyerAccount=order.Row.Bid ? order.Wallet : makerAccount;
                var sellerAccount=order.Row.Bid ? makerAccount : order.Wallet;
                var sellerContext=order.Row.Bid
                    ? (maker.Row.IsNpc ? context : await SellerContext(seller,makerStation))
                    : context;
                var tax=order.Row.Bid && maker.Row.IsNpc
                    ? 0m
                    : (decimal)MarketRules.SalesTax(sellerContext,(double)plan.GrossAmount);
                var key=$"market:{Schema}:{operationKey}:fill:{index}";

                if (order.Row.Bid)
                {
                    var limitCost=checked(newPrice*plan.Quantity);
                    if (limitCost < plan.GrossAmount) throw new MarketError("MARKET_ESCROW_INCONSISTENT");
                    if (!maker.Row.IsNpc)
                        await Adjust(c,tx,sellerAccount,plan.GrossAmount,buyerAccount.OwnerId,2,"Market sale",time,
                            key+":sale",destination.Id,token);
                    var improvement=limitCost-plan.GrossAmount;
                    if (improvement>0)
                        await Adjust(c,tx,order.Wallet,improvement,makerStation.OwnerId,42,"Market price improvement refund",time,
                            key+":improvement",makerStation.Id,token);
                    if (tax>0)
                        await Adjust(c,tx,sellerAccount,-tax,destination.OwnerId,54,"Market transaction tax",time,
                            key+":tax",destination.Id,token);

                    Item moved;
                    if (maker.Row.IsNpc)
                    {
                        var id=await items.CreateAsync(c,tx,new NewItem(order.Row.TypeId,order.Actor,destination.Id,ItemFlags.Hangar,plan.Quantity),token);
                        moved=await items.LockAndGetAsync(c,tx,id,token) ?? throw new MarketError("MARKET_ITEM_CUSTODY_INVALID");
                    }
                    else
                    {
                        var source=maker.ItemId is { } escrowId ? await items.LockAndGetAsync(c,tx,escrowId,token) : null;
                        if (source is null || source.TypeId != order.Row.TypeId || source.OwnerId != maker.SourceOwner ||
                            source.LocationId != EscrowBase+maker.Row.Id || source.Flag != ItemFlags.Hangar ||
                            source.Quantity != makerVolume) throw new MarketError("MARKET_ITEM_CUSTODY_INVALID");
                        moved=await items.MoveStackAsync(c,tx,source,plan.Quantity,order.Actor,destination.Id,ItemFlags.Hangar,token);
                    }
                    delivered.Add(moved.Id);
                }
                else
                {
                    var makerEscrowRemaining=makerEscrow[maker.Row.Id];
                    if (!maker.Row.IsNpc && makerEscrowRemaining != checked(maker.Row.Price*makerVolume))
                        throw new MarketError("MARKET_ESCROW_INCONSISTENT");
                    await Adjust(c,tx,order.Wallet,plan.GrossAmount,makerAccount.OwnerId,2,"Market sale",time,
                        key+":sale",destination.Id,token);
                    if (tax>0)
                        await Adjust(c,tx,order.Wallet,-tax,destination.OwnerId,54,"Market transaction tax",time,
                            key+":tax",destination.Id,token);

                    var source=order.ItemId is { } ownItemId ? await items.LockAndGetAsync(c,tx,ownItemId,token) : null;
                    if (source is null || source.TypeId != order.Row.TypeId || source.OwnerId != order.SourceOwner ||
                        source.LocationId != EscrowBase+order.Row.Id || source.Flag != ItemFlags.Hangar ||
                        source.Quantity != remaining) throw new MarketError("MARKET_ITEM_CUSTODY_INVALID");
                    var moved=await items.MoveStackAsync(c,tx,source,plan.Quantity,makerActor,destination.Id,ItemFlags.Hangar,token);
                    if (maker.Row.IsNpc) await items.DeleteAsync(c,tx,moved.Id,token);
                    else delivered.Add(moved.Id);
                }

                var remainingMaker=makerVolume-plan.Quantity;
                var makerEscrowDelta=!order.Row.Bid && !maker.Row.IsNpc ? plan.GrossAmount : 0m;
                await using (var update=Command(c,tx,$"""
                    UPDATE {Schema}.orders SET vol_remaining=vol_remaining-$2,
                        state=CASE WHEN vol_remaining-$2=0 AND NOT is_npc THEN 1 ELSE state END,
                        escrow_isk=escrow_isk-$3,escrow_item_id=CASE WHEN vol_remaining-$2=0 THEN NULL ELSE escrow_item_id END,
                        last_state_change_ms=$4 WHERE id=$1 AND vol_remaining >= $2
                    """,maker.Row.Id,plan.Quantity,makerEscrowDelta,time))
                    if (await update.ExecuteNonQueryAsync(token)!=1) throw new MarketError("MARKET_ORDER_NOT_ACTIVE");
                makerRemaining[maker.Row.Id]=remainingMaker;
                makerEscrow[maker.Row.Id]-=makerEscrowDelta;
                await using (var history=Command(c,tx,$"""
                    INSERT INTO {Schema}.fills(order_id,type_id,buyer_id,seller_id,buyer_wallet,seller_wallet,buyer_account,seller_account,
                        station_id,region_id,quantity,price,gross,tax,time_ms,operation_key,fill_index)
                    VALUES($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13,$14,$15,$16,$17)
                    """,maker.Row.Id,order.Row.TypeId,buyer,seller,buyerAccount.OwnerId,sellerAccount.OwnerId,buyerAccount.AccountKey,sellerAccount.AccountKey,
                    destination.Id,station.RegionId,plan.Quantity,maker.Row.Price,plan.GrossAmount,tax,time,operationKey,index))
                    await history.ExecuteNonQueryAsync(token);
                remaining-=plan.Quantity;
                filled+=plan.Quantity;
                grossTotal+=plan.GrossAmount;
                if (order.Row.Bid) escrow-=checked(newPrice*plan.Quantity);
                index++;
            }

            var finalEscrow=order.Row.Bid ? newValue-checked(newPrice*filled) : 0m;
            if (escrow != finalEscrow || finalEscrow < 0) throw new MarketError("MARKET_ESCROW_INCONSISTENT");
            var state=remaining==0 ? 1 : 0;
            var changedRow=order.Row with { Price=newPrice,VolumeRemaining=remaining,IssuedMs=time };
            await using (var update=Command(c,tx,$"""
                UPDATE {Schema}.orders SET price=$2,vol_remaining=$3,state=CASE WHEN $3=0 THEN 1 ELSE state END,
                    escrow_isk=$4,escrow_item_id=CASE WHEN $3=0 THEN NULL ELSE escrow_item_id END,last_state_change_ms=$5,
                    issued_ms=$5,last_modified_ms=$5
                WHERE id=$1
                """,orderId,newPrice,remaining,finalEscrow,time))
                await update.ExecuteNonQueryAsync(token);
            await ScheduleExpiryAsync(c,tx,orderId,time,order.Row.Duration,token);
            var changedOrder=new MarketOwnedOrder(changedRow,order.Actor,order.Wallet.OwnerId,order.Wallet.AccountKey,
                finalEscrow,state,time);
            var reason=filled==0 ? "Modified" : remaining==0 ? "Filled" : "PartialFill";
            var changes=new List<MarketOrderChange>();
            foreach (var makerId in fills.Select(fill=>fill.Order.Id).Distinct())
            {
                var maker=byId[makerId];
                if (maker.Row.IsNpc) continue;
                var makerVolume=makerRemaining[makerId];
                var makerState=makerVolume==0 ? 1 : 0;
                var changedMaker=new MarketOwnedOrder(maker.Row with { VolumeRemaining=makerVolume },maker.Actor,
                    maker.Wallet.OwnerId,maker.Wallet.AccountKey,makerEscrow[makerId],makerState,time);
                changes.Add(new(changedMaker,makerState==1 ? "Filled" : "PartialFill",
                    await NoticeRecipientsAsync(c,tx,maker.Wallet.OwnerId,maker.Actor,maker.Row.Location.StationId,[],token)));
            }
            var recipients=await NoticeRecipientsAsync(c,tx,order.Wallet.OwnerId,order.Actor,station.Id,[actorId],token);
            changes.Add(new(changedOrder,reason,recipients));
            var receipt=new MarketReceipt(operationKey,orderId,filled,grossTotal,delivered,
                changedWallets.OrderBy(wallet=>wallet.OwnerId).ThenBy(wallet=>wallet.AccountKey).ToArray())
            {
                ChangedOrders=changes,
                ChangedOrder=changedOrder,
                ChangedOrderReason=reason,
                TouchedBooks=[new TouchedBook(station.RegionId,order.Row.TypeId)],
            };
            return await Save(receipt);
        },token);
    }

    /// <summary>The time of the order's last modification (0 when never modified); a modification restarts the duration (decision Q4).</summary>
    private async Task<long> LastModifiedAsync(NpgsqlConnection c,NpgsqlTransaction tx,long orderId,CancellationToken token)
    {
        await using var command = Command(c,tx,$"SELECT last_modified_ms FROM {Schema}.orders WHERE id=$1",orderId);
        return (long)(await command.ExecuteScalarAsync(token))!;
    }

    public async Task<IReadOnlyList<MarketOwnedOrder>> OwnerOrdersAsync(long actorId,bool corporation,bool includeHistory,CancellationToken token = default)
    {
        await using var c=await source.OpenConnectionAsync(token);
        await using var tx=await c.BeginTransactionAsync(IsolationLevel.RepeatableRead,token);
        var character=await characters.LockAndGetAsync(c,tx,actorId,token) ?? throw new MarketError("CHARACTER_NOT_FOUND");
        var walletOwner=corporation ? character.CorporationId : character.Id;
        if (walletOwner <= 0)
        {
            await tx.CommitAsync(token);
            return [];
        }
        var orders=await store.OrdersForWalletAsync(actorId,walletOwner,includeHistory,token);
        await tx.CommitAsync(token);
        return orders;
    }

    private Task<MarketReceipt?> CloseAsync(long actorId,long orderId,string operationKey,bool expired,CancellationToken token) =>
        Write(async (c,tx) =>
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(operationKey);
            await using (var guard=Command(c,tx,"SELECT pg_advisory_xact_lock(hashtextextended($1,0))","market:"+Schema+":command:"+operationKey))
                await guard.ExecuteNonQueryAsync(token);
            var terms=new ControlTerms(expired ? "expire" : "cancel",actorId,orderId);
            await using (var prior=Command(c,tx,$"SELECT fingerprint,result::text FROM {Schema}.commands WHERE operation_key=$1",operationKey))
            await using (var r=await prior.ExecuteReaderAsync(token))
            {
                if (await r.ReadAsync(token))
                {
                    if (JsonSerializer.Deserialize<ControlTerms>(r.GetString(0)) != terms) throw new MarketError("MARKET_OPERATION_CONFLICT");
                    // an expiry is announced by the call that performed it; a replay has nothing new to announce (the sweep and the job may both reach it)
                    return expired ? null : JsonSerializer.Deserialize<MarketReceipt>(r.GetString(1))!;
                }
            }
            var actor = !expired ? await characters.LockAndGetAsync(c,tx,actorId,token) ?? throw new MarketError("CHARACTER_NOT_FOUND") : null;
            var initial=await GetOrder(c,tx,orderId,false,token) ?? throw new MarketError("MARKET_ORDER_NOT_FOUND");
            if (!expired && initial.Order.Wallet.OwnerId != initial.Order.Actor &&
                (await corporations.MarketAuthorityAsync(c,tx,actorId,initial.Order.Wallet.AccountKey,initial.Order.Row.Location.StationId,token)).Wallet != initial.Order.Wallet)
                throw new MarketError("MARKET_ORDER_ACCESS_DENIED");
            await using (var bookLock=Command(c,tx,"SELECT pg_advisory_xact_lock(hashtextextended($1,0))",$"market:{Schema}:book:{initial.Order.Row.Location.RegionId}:{initial.Order.Row.TypeId}"))
                await bookLock.ExecuteNonQueryAsync(token);
            var current=await GetOrder(c,tx,orderId,true,token) ?? throw new MarketError("MARKET_ORDER_NOT_FOUND");
            var order=current.Order;
            if (order.Row.IsNpc || current.State != 0) throw new MarketError("MARKET_ORDER_NOT_ACTIVE");
            var station=stations.Get(order.Row.Location.StationId) ?? throw new MarketError("MARKET_STATION_NOT_FOUND");
            if (!expired && order.Wallet.OwnerId == order.Actor && actor!.Id != order.Actor)
                throw new MarketError("MARKET_ORDER_ACCESS_DENIED");
            var nowMs=now();
            if (expired && (decimal)order.Row.IssuedMs + (decimal)order.Row.Duration*86400000 > nowMs)
                throw new MarketError("MARKET_ORDER_NOT_EXPIRED");
            await wallets.LockBalancesAsync(c,tx,[order.Wallet],token);
            var movedIds=new List<long>();
            if (order.Row.Bid)
            {
                if (order.Escrow != checked(order.Row.Price*order.Row.VolumeRemaining)) throw new MarketError("MARKET_ESCROW_INCONSISTENT");
                await Adjust(c,tx,order.Wallet,order.Escrow,station.OwnerId,42,"Market escrow refund",nowMs,
                    $"market:{Schema}:{operationKey}:refund",station.Id,token);
            }
            else
            {
                var item=order.ItemId is { } id ? await items.LockAndGetAsync(c,tx,id,token) : null;
                if (item is null || item.OwnerId != order.SourceOwner || item.LocationId != EscrowBase+order.Row.Id ||
                    item.Flag != ItemFlags.Hangar || item.Quantity != order.Row.VolumeRemaining) throw new MarketError("MARKET_ITEM_CUSTODY_INVALID");
                var moved=await items.MoveStackAsync(c,tx,item,item.Quantity,order.SourceOwner,order.SourceLocation,order.SourceFlag,token);
                movedIds.Add(moved.Id);
            }
            await using (var close=Command(c,tx,$"UPDATE {Schema}.orders SET state=$2,escrow_isk=0,escrow_item_id=NULL,last_state_change_ms=$3 WHERE id=$1",
                orderId,expired ? 2 : 3,nowMs))
                await close.ExecuteNonQueryAsync(token);
            await CancelExpiryAsync(c,tx,orderId,token);
            var state=expired ? 2 : 3;
            var reason=expired ? "Expired" : "Cancelled";
            var closedOrder=new MarketOwnedOrder(order.Row,order.Actor,order.Wallet.OwnerId,order.Wallet.AccountKey,0m,state,nowMs);
            var recipients=await NoticeRecipientsAsync(c,tx,order.Wallet.OwnerId,order.Actor,station.Id,[actorId],token);
            var result=new MarketReceipt(operationKey,orderId,0,0m,movedIds,[order.Wallet])
            {
                ChangedOrders=[new(closedOrder,reason,recipients)],
                ChangedOrder=closedOrder,
                ChangedOrderReason=reason,
                TouchedBooks=[new TouchedBook(station.RegionId,order.Row.TypeId)],
            };
            await using (var save=Command(c,tx,$"INSERT INTO {Schema}.commands(operation_key,fingerprint,time_ms,result) VALUES($1,$2,$3,$4::jsonb)",
                operationKey,JsonSerializer.Serialize(terms),nowMs,JsonSerializer.Serialize(result)))
                await save.ExecuteNonQueryAsync(token);
            return result;
        },token);

    // ---- expiry jobs (M3; market-decisions Q7) ---------------------------------------------------------------------------------------------------

    /// <summary>Schedules the expiry job of a resting order in the transaction that places or moves it; an immediate order rests nowhere.</summary>
    private async Task ScheduleExpiryAsync(NpgsqlConnection c,NpgsqlTransaction tx,long orderId,long issuedMs,int durationDays,CancellationToken token)
    {
        if (durationDays <= 0) return;
        await expiryJobs.ScheduleAsync(c,tx,orderId,issuedMs+durationDays*86400000L+MarketExpiry.GraceMs,token);
    }

    /// <summary>Cancels the expiry job of an order that is closed in the same transaction.</summary>
    private async Task CancelExpiryAsync(NpgsqlConnection c,NpgsqlTransaction tx,long orderId,CancellationToken token)
    {
        await expiryJobs.CancelAsync(c,tx,orderId,token);
    }

    /// <summary>The issue time, expiry instant and state (0 active) of an order, or null when there is no such order.</summary>
    public async Task<(long IssuedMs,long ExpiresMs,int State)?> ExpiryStateAsync(long orderId,CancellationToken token = default)
    {
        await using var command=source.CreateCommand($"SELECT issued_ms,issued_ms+duration_days::bigint*86400000,state FROM {Schema}.orders WHERE id=$1");
        command.Parameters.Add(new NpgsqlParameter { Value=orderId });
        await using var r=await command.ExecuteReaderAsync(token);
        if (!await r.ReadAsync(token)) return null;
        return (r.GetInt64(0),r.GetInt64(1),Convert.ToInt32(r.GetValue(2)));
    }

    /// <summary>Expires one resting order whose duration has run out (operation key expiry:{id}:{issued}); null when it is no longer active.</summary>
    public async Task<MarketReceipt?> ExpireOrderAsync(long orderId,CancellationToken token = default)
    {
        var state=await ExpiryStateAsync(orderId,token) ?? throw new MarketError("MARKET_ORDER_NOT_FOUND");
        try { return await CloseAsync(0,orderId,$"expiry:{orderId}:{state.IssuedMs}",true,token); }
        catch (MarketError error) when (error.Code is "MARKET_ORDER_NOT_ACTIVE" or "MARKET_ORDER_NOT_EXPIRED") { return null; }
    }

    /// <summary>The resting orders still open (not NPC) with their expiry instants: a restart gives each one its job.</summary>
    public async Task<IReadOnlyList<(long OrderId,long ExpiresMs)>> OpenRestingExpiriesAsync(CancellationToken token = default)
    {
        var result=new List<(long OrderId,long ExpiresMs)>();
        await using var command=source.CreateCommand($"SELECT id,issued_ms+duration_days::bigint*86400000 FROM {Schema}.orders WHERE NOT is_npc AND state=0 AND duration_days>0 ORDER BY id");
        await using var r=await command.ExecuteReaderAsync(token);
        while (await r.ReadAsync(token)) result.Add((r.GetInt64(0),r.GetInt64(1)));
        return result;
    }

    /// <summary>
    /// Who hears of a change to an order: its owner, the character acting on it, and for a corporation order every member with the
    /// corporation's market rights at the station (G11; the test the client uses, buyThisTypeWindow.py:324). Zero ids are dropped.
    /// </summary>
    private async Task<long[]> NoticeRecipientsAsync(NpgsqlConnection c,NpgsqlTransaction tx,long walletOwnerId,long orderActor,long stationId,long[] acting,CancellationToken token)
    {
        var ids=new List<long> { orderActor };
        ids.AddRange(acting);
        if (walletOwnerId != orderActor)
            ids.AddRange(await corporations.MarketRightsHoldersAsync(c,tx,walletOwnerId,stationId,token));
        return [.. ids.Where(id => id > 0).Distinct()];
    }

    /// <summary>An over-cap refund stays in escrow and can be retried after space is available; no credit is clipped.</summary>
    public async Task<IReadOnlyList<MarketReceipt>> ExpireDueAsync(CancellationToken token = default)
    {
        var ids=new List<(long Id,long Issued)>();
        await using (var command=source.CreateCommand($"SELECT id,issued_ms FROM {Schema}.orders WHERE NOT is_npc AND state=0 AND issued_ms+duration_days::bigint*86400000+{MarketExpiry.GraceMs} <= $1 ORDER BY id"))
        {
            command.Parameters.Add(new NpgsqlParameter { Value=now() });
            await using var r=await command.ExecuteReaderAsync(token);
            while (await r.ReadAsync(token)) ids.Add((r.GetInt64(0),r.GetInt64(1)));
        }
        var receipts=new List<MarketReceipt>();
        foreach (var (id,issued) in ids)
        {
            try { if (await CloseAsync(0,id,$"expiry:{id}:{issued}",true,token) is { } closed) receipts.Add(closed); }
            catch (MoneyLimitException) { } // The intact escrow remains the durable pending refund.
            catch (MarketError error) when (error.Code is "MARKET_ORDER_NOT_ACTIVE" or "MARKET_ORDER_NOT_EXPIRED") { }
        }
        return receipts;
    }
}
