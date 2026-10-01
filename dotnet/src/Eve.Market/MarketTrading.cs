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

public sealed class MarketError(string code) : Exception(code) { public string Code { get; } = code; }
public sealed record MarketRequest(long ActorId,int TypeId,bool Buying,decimal Price,long Quantity,long StationId,
    int Duration = 0,int Range = -1,long MinimumVolume = 1,long? ItemId = null,bool UseCorporation = false,double? ExpectedBrokerFee = null);
public sealed record MarketReceipt(string OperationKey,long? OrderId,long FilledQuantity,decimal GrossAmount,
    IReadOnlyList<long> DeliveredItemIds,IReadOnlyList<WalletAccount> ChangedWallets);

/// <summary>Orders, receipts, item custody and money share one transaction. No Node daemon or recovery Saga is required.</summary>
public sealed class MarketTrading(NpgsqlDataSource source,MarketStore store,PgCharacterStore characters,CorporationStore corporations,
    PgSkillStore skills,PgItemStore items,WalletTransactions wallets,StationCatalog stations,MarketTopology topology,Func<long> now)
{
    private string Schema => store.Schema;
    private const long EscrowBase = 9200000000;
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
    private async Task<MarketContext> Context(NpgsqlConnection c,NpgsqlTransaction tx,long actor,Station station,CancellationToken token)
    {
        var levels = (await skills.ForCharacterAsync(c,tx,actor,token)).ToDictionary(s => s.TypeId,s => s.Level);
        int Level(int id) => levels.GetValueOrDefault(id);
        return MarketRules.Context(new(Level(3443),Level(3444),Level(3446),Level(3447),Level(16595),Level(16596),Level(16597),
            Level(16598),Level(16594),Level(16622),Level(18580)),true,solarSystem:station.SolarSystemId);
    }
    private async Task<IReadOnlyList<HeldOrder>> CounterOrders(NpgsqlConnection c,NpgsqlTransaction tx,MarketRequest r,Station station,long time,CancellationToken token)
    {
        await using var command = Command(c,tx,$"""
            SELECT {MarketStore.BookColumns},actor_id,wallet_owner_id,account_key,escrow_item_id,escrow_isk,source_owner_id,source_location_id,source_flag
            FROM {Schema}.orders WHERE region_id=$1 AND type_id=$2 AND bid=$3 AND state=0 AND vol_remaining>0
                AND (is_npc OR issued_ms+duration_days::bigint*86400000>$4)
                AND price {(r.Buying ? "<=" : ">=")} $5
            ORDER BY price {(r.Buying ? "ASC" : "DESC")},station_id,id FOR UPDATE
            """,station.RegionId,r.TypeId,!r.Buying,time,r.Price + (r.Buying ? 0.0001m : -0.0001m));
        var result = new List<HeldOrder>();
        await using var reader = await command.ExecuteReaderAsync(token);
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

    public Task<MarketReceipt> PlaceAsync(MarketRequest request,string operationKey,CancellationToken token = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationKey);
        if (request.ExpectedBrokerFee is { } quote && !double.IsFinite(quote)) request=request with { ExpectedBrokerFee=null };
        if (request.Quantity is <= 0 or > int.MaxValue || request.MinimumVolume <= 0 || request.MinimumVolume > request.Quantity)
            throw new MarketError("MktInvalidMinVolumeCannotExceedQuantity");
        if (request.TypeId <= 0 || request.Price <= 0 || request.Price > (decimal)MarketRules.MaximumOrderPrice || request.Price != decimal.Round(request.Price,2))
            throw new MarketError("INVALID_MARKET_PRICE");
        if (!MarketRules.IsAllowedDuration(request.Duration) || !MarketRules.IsAllowedBuyRange(request.Range)) throw new MarketError("INVALID_MARKET_DURATION_OR_RANGE");
        return Write(async (c,tx) =>
        {
            // A command's operation key and participants are stable facts. A collision is never a successful replay.
            await using (var guard = Command(c,tx,"SELECT pg_advisory_xact_lock(hashtextextended($1,0))","market:"+Schema+":command:"+operationKey))
                await guard.ExecuteNonQueryAsync(token);
            var actor = await characters.LockAndGetAsync(c,tx,request.ActorId,token) ?? throw new MarketError("CHARACTER_NOT_FOUND");
            var station = stations.Get(request.StationId) ?? throw new MarketError("MARKET_STATION_NOT_FOUND");
            var account = request.UseCorporation
                ? (await corporations.MarketAuthorityAsync(c,tx,actor.Id,null,station.Id,token)).Wallet : new WalletAccount(actor.Id);
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
            if (!request.Buying || request.Duration>0)
            {
                if (topology.OrderDistance(current,location) > (request.Buying ? context.Limits.BuyRange : context.Limits.SellRange))
                    throw new MarketError("MARKET_REMOTE_SKILL_REQUIRED");
            }
            if (request.Duration>0)
            {
                if (request.Buying && request.Range > context.Limits.Visibility) throw new MarketError("MARKET_VISIBILITY_REQUIRED");
                if (request.ExpectedBrokerFee is { } quoted && double.IsFinite(quoted) && Math.Abs(quoted-context.BrokerCommissionRate)>0.0001)
                    throw new MarketError("MktBrokersFeeUnexpected2");
                await using var count = Command(c,tx,$"SELECT count(*) FROM {Schema}.orders WHERE actor_id=$1 AND state=0",actor.Id);
                if ((long)(await count.ExecuteScalarAsync(token))! >= context.Limits.Count) throw new MarketError("MARKET_ORDER_LIMIT");
            }
            Item? stock = null;
            if (!request.Buying)
            {
                stock = request.ItemId is { } itemId ? await items.LockAndGetAsync(c,tx,itemId,token) : null;
                if (stock is null || stock.TypeId != request.TypeId || stock.OwnerId != actor.Id || stock.LocationId != station.Id || stock.Flag != ItemFlags.Hangar ||
                    stock.Singleton || stock.Quantity < request.Quantity) throw new MarketError("MARKET_ITEM_CUSTODY_INVALID");
            }
            // The lock is per region/type book, not server-wide. Wallet participants use their shared owner/key order.
            await using (var bookLock = Command(c,tx,"SELECT pg_advisory_xact_lock(hashtextextended($1,0))",$"market:{Schema}:book:{station.RegionId}:{request.TypeId}"))
                await bookLock.ExecuteNonQueryAsync(token);
            var held = await CounterOrders(c,tx,request,station,time,token);
            var plans = MarketMatching.Plan(held.Select(o => o.Row),topology,request.TypeId,request.Buying,location,request.Price,request.Quantity,request.Range,time);
            var byId = held.ToDictionary(o => o.Row.Id);
            var participants = plans.Where(p => !p.Order.IsNpc).Select(p => byId[p.Order.Id].Wallet).Append(account).Distinct().ToArray();
            var locked = await wallets.LockBalancesAsync(c,tx,participants,token);
            var upper = checked(request.Price*request.Quantity);
            if (request.Buying)
            {
                var required = upper + (request.Duration>0 ? (decimal)MarketRules.BrokerFee(context,null,(double)upper).Amount + (decimal)MarketRules.SccSurcharge(context,null,(double)upper).Amount : 0);
                if (locked[account]<required) throw new InsufficientFundsException(account.OwnerId);
            }
            var delivered = new List<long>();
            var grossTotal = 0m;
            var filled = 0L;
            var index = 0;
            foreach (var plan in plans)
            {
                var maker = byId[plan.Order.Id];
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
                    if (!maker.Row.IsNpc && maker.Escrow < plan.GrossAmount) throw new MarketError("MARKET_ESCROW_INCONSISTENT");
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
                        request.Buying && (from.OwnerId != maker.SourceOwner || from.LocationId != EscrowBase+maker.Row.Id || from.Flag != ItemFlags.Hangar))
                        throw new MarketError("MARKET_ITEM_CUSTODY_INVALID");
                    moved = await items.MoveStackAsync(c,tx,from,plan.Quantity,
                        !request.Buying && maker.Row.IsNpc ? makerStation.OwnerId : buyer,destination.Id,ItemFlags.Hangar,token);
                    if (!request.Buying && maker.Row.IsNpc) await items.DeleteAsync(c,tx,moved.Id,token);
                }
                if (request.Buying || !maker.Row.IsNpc) delivered.Add(moved.Id);
                var remainingMaker = maker.Row.VolumeRemaining-plan.Quantity;
                await using (var update = Command(c,tx,$"""
                    UPDATE {Schema}.orders SET vol_remaining=$2,state=CASE WHEN $2=0 AND NOT is_npc THEN 1 ELSE state END,
                        escrow_isk=escrow_isk-$3,escrow_item_id=CASE WHEN $2=0 THEN NULL ELSE escrow_item_id END WHERE id=$1
                    """,maker.Row.Id,remainingMaker,!request.Buying && !maker.Row.IsNpc ? plan.GrossAmount : 0m))
                    await update.ExecuteNonQueryAsync(token);
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
            if (request.Duration>0 && remaining>0)
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
                    VALUES($1,$2,$3,$4,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13,$14,$15,$16,$13,$7,4) RETURNING id
                    """,request.TypeId,request.Buying,request.Price,remaining,request.Buying ? request.MinimumVolume : 1L,
                    request.Buying ? request.Range : -1,station.Id,station.SolarSystemId,station.ConstellationId,station.RegionId,time,request.Duration,
                    actor.Id,account.OwnerId,account.AccountKey,request.Buying ? value : 0m))
                    opened = (long)(await insert.ExecuteScalarAsync(token))!;
                if (!request.Buying)
                {
                    var rest = await items.LockAndGetAsync(c,tx,stock!.Id,token) ?? throw new MarketError("MARKET_ITEM_CUSTODY_INVALID");
                    var escrow = await items.MoveStackAsync(c,tx,rest,remaining,actor.Id,EscrowBase+opened.Value,ItemFlags.Hangar,token);
                    await using var bind = Command(c,tx,$"UPDATE {Schema}.orders SET escrow_item_id=$2 WHERE id=$1",opened.Value,escrow.Id);
                    await bind.ExecuteNonQueryAsync(token);
                }
            }
            var receipt = new MarketReceipt(operationKey,opened,filled,grossTotal,delivered,participants);
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

    public Task<MarketReceipt> CancelAsync(long actorId,long orderId,string operationKey,CancellationToken token = default) =>
        CloseAsync(actorId,orderId,operationKey,false,token);

    private Task<MarketReceipt> CloseAsync(long actorId,long orderId,string operationKey,bool expired,CancellationToken token) =>
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
                    return JsonSerializer.Deserialize<MarketReceipt>(r.GetString(1))!;
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
            if (!expired)
            {
                if (order.Wallet.OwnerId == order.Actor)
                {
                    if (actor!.Id != order.Actor) throw new MarketError("MARKET_ORDER_ACCESS_DENIED");
                }

            }
            else if ((decimal)order.Row.IssuedMs + (decimal)order.Row.Duration*86400000 > now())
                throw new MarketError("MARKET_ORDER_NOT_EXPIRED");
            await wallets.LockBalancesAsync(c,tx,[order.Wallet],token);
            var movedIds=new List<long>();
            if (order.Row.Bid)
            {
                if (order.Escrow != checked(order.Row.Price*order.Row.VolumeRemaining)) throw new MarketError("MARKET_ESCROW_INCONSISTENT");
                await Adjust(c,tx,order.Wallet,order.Escrow,station.OwnerId,42,"Market escrow refund",now(),
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
            await using (var close=Command(c,tx,$"UPDATE {Schema}.orders SET state=$2,escrow_isk=0,escrow_item_id=NULL WHERE id=$1",orderId,expired ? 3 : 2))
                await close.ExecuteNonQueryAsync(token);
            var result=new MarketReceipt(operationKey,orderId,0,0m,movedIds,[order.Wallet]);
            await using (var save=Command(c,tx,$"INSERT INTO {Schema}.commands(operation_key,fingerprint,time_ms,result) VALUES($1,$2,$3,$4::jsonb)",
                operationKey,JsonSerializer.Serialize(terms),now(),JsonSerializer.Serialize(result)))
                await save.ExecuteNonQueryAsync(token);
            return result;
        },token);

    /// <summary>An over-cap refund stays in escrow and can be retried after space is available; no credit is clipped.</summary>
    public async Task<IReadOnlyList<MarketReceipt>> ExpireDueAsync(CancellationToken token = default)
    {
        var ids=new List<(long Id,long Issued)>();
        await using (var command=source.CreateCommand($"SELECT id,issued_ms FROM {Schema}.orders WHERE NOT is_npc AND state=0 AND issued_ms+duration_days::bigint*86400000 <= $1 ORDER BY id"))
        {
            command.Parameters.Add(new NpgsqlParameter { Value=now() });
            await using var r=await command.ExecuteReaderAsync(token);
            while (await r.ReadAsync(token)) ids.Add((r.GetInt64(0),r.GetInt64(1)));
        }
        var receipts=new List<MarketReceipt>();
        foreach (var (id,issued) in ids)
        {
            try { receipts.Add(await CloseAsync(0,id,$"expiry:{id}:{issued}",true,token)); }
            catch (MoneyLimitException) { } // The intact escrow remains the durable pending refund.
            catch (MarketError error) when (error.Code is "MARKET_ORDER_NOT_ACTIVE" or "MARKET_ORDER_NOT_EXPIRED") { }
        }
        return receipts;
    }
}
