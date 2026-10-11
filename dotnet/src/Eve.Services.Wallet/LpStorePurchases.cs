using System.Collections.Concurrent;
using System.Text.Json;
using Eve.Inventory;
using Eve.Wallet;
using Npgsql;

namespace Eve.Services.Wallet;

/// <summary>Who buys an offer: the character, or a corporation's wallets (the character still receives the goods).</summary>
public sealed record LpStorePurchaseRequest(
    long CharacterId,
    long PayerCorporationId,
    int AccountKey,
    long StoreCorporationId,
    long OfferId,
    long NumberOfOffers,
    long LocationId);

/// <summary>An item the purchase changed: the stack after the change, and where and how much it was before it.</summary>
public sealed record LpStoreItemChange(
    long ItemId,
    int TypeId,
    int GroupId,
    int CategoryId,
    long OwnerId,
    long LocationId,
    int Flag,
    long Quantity,
    bool Singleton,
    long PreviousLocationId,
    long PreviousQuantity,
    bool Created,
    bool Removed);

/// <summary>A committed purchase: its id, the loyalty points and ISK it cost, and the items it consumed and created.</summary>
public sealed record LpStorePurchaseReceipt(
    string PurchaseId,
    long LpCost,
    decimal IskCost,
    IReadOnlyList<LpStoreItemChange> ItemChanges);

/// <summary>A purchase refused by name. The code is the reference's error key (EveJS lpStoreMgrService.js ERROR_MESSAGES); nothing was written.</summary>
public sealed class LpStorePurchaseRefusedException(string code) : Exception(code)
{
    public string Code { get; } = code;
}

/// <summary>
/// The loyalty store purchase (<c>TakeOfferForCharacter</c> and <c>TakeOfferForCorporation</c>), settled in one PostgreSQL
/// transaction: the loyalty points, the ISK, the required items of the character's hangar at the station, and the granted item
/// in that hangar. A refusal rolls the transaction back, so a purchase that is refused changes nothing. The purchase row and the
/// journal rows share one id (the purchase id), so each committed purchase is journalled once. Announcements go out after the
/// commit only. The offers are the ones ESI holds (<see cref="LpStoreOfferCache"/>); ESI gives no standing condition, so none
/// is checked (DEVIATIONS "Agents slice AG-3, A9").
/// </summary>
public sealed class LpStorePurchases
{
    /// <summary>The ISK journal reference type of an LP store payment: the client's <c>refPaymentToLPStore</c> (eve-logic eve/common/lib/appConst.py:667).</summary>
    public const int PaymentToLpStoreRefType = 26;

    private const string Reason = "LP store purchase";
    private const long JunkLocation = 6;
    private const long MaximumCount = 1_000_000;

    private readonly NpgsqlDataSource _source;
    private readonly string _schema;
    private readonly WalletTransactions _wallets;
    private readonly LoyaltyPointStore _loyalty;
    private readonly PgItemStore _items;
    private readonly Func<int, CancellationToken, Task<ItemType?>> _itemTypes;
    private readonly LpStoreOfferCache _offers;
    private readonly Func<long> _nowMs;
    private readonly ConcurrentDictionary<long, SemaphoreSlim> _lanes = new();

    public LpStorePurchases(
        NpgsqlDataSource source,
        string schema,
        WalletTransactions wallets,
        LoyaltyPointStore loyalty,
        PgItemStore items,
        Func<int, CancellationToken, Task<ItemType?>> itemTypes,
        LpStoreOfferCache offers,
        Func<long>? nowMs = null)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(schema, "^[a-z_][a-z0-9_]{0,62}$"))
        {
            throw new ArgumentException($"not a usable schema name: {schema}", nameof(schema));
        }
        _source = source;
        _schema = schema;
        _wallets = wallets;
        _loyalty = loyalty;
        _items = items;
        _itemTypes = itemTypes;
        _offers = offers;
        _nowMs = nowMs ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    /// <summary>Buys an offer. Throws <see cref="LpStorePurchaseRefusedException"/> for every refusal, with nothing written.</summary>
    public async Task<LpStorePurchaseReceipt> TakeAsync(LpStorePurchaseRequest request, CancellationToken cancellationToken = default)
    {
        if (request.CharacterId <= 0) throw Refused("INVALID_CHARACTER");
        if (request.StoreCorporationId <= 0 || request.LocationId <= 0) throw Refused("INVALID_STORE");
        if (request.NumberOfOffers <= 0 || request.NumberOfOffers > MaximumCount) throw Refused("INVALID_QUANTITY");

        var offer = (await _offers.GetAsync(request.StoreCorporationId, cancellationToken))
            .FirstOrDefault(candidate => candidate.OfferId == request.OfferId)
            ?? throw Refused("OFFER_NOT_FOUND");
        if (offer.AkCost > 0) throw Refused("AK_UNSUPPORTED");
        if (offer.TypeId is <= 0 or > int.MaxValue) throw Refused("ITEM_GRANT_FAILED");
        var rewardType = await _itemTypes((int)offer.TypeId, cancellationToken) ?? throw Refused("ITEM_GRANT_FAILED");

        long lpCost, grantedQuantity;
        decimal iskCost;
        var requiredByType = new Dictionary<int, long>();
        try
        {
            lpCost = checked(offer.LpCost * request.NumberOfOffers);
            iskCost = checked(offer.IskCost * request.NumberOfOffers);
            grantedQuantity = checked(offer.Quantity * request.NumberOfOffers);
            foreach (var requirement in offer.RequiredItems)
            {
                if (requirement.TypeId is <= 0 or > int.MaxValue) throw Refused("REQ_ITEMS_UNSUPPORTED");
                var typeId = (int)requirement.TypeId;
                requiredByType[typeId] = checked((requiredByType.GetValueOrDefault(typeId)) + requirement.Quantity * request.NumberOfOffers);
            }
        }
        catch (OverflowException)
        {
            throw Refused("INVALID_QUANTITY");
        }

        var payerCorporation = request.PayerCorporationId > 0;
        var holder = payerCorporation
            ? new LoyaltyHolder(LoyaltyHolderKind.Corporation, request.PayerCorporationId)
            : new LoyaltyHolder(LoyaltyHolderKind.Character, request.CharacterId);
        var account = payerCorporation
            ? new WalletAccount(request.PayerCorporationId, request.AccountKey)
            : new WalletAccount(request.CharacterId);
        var purchaseId = Guid.NewGuid().ToString("N");
        var now = _nowMs();

        var lane = _lanes.GetOrAdd(request.CharacterId, _ => new SemaphoreSlim(1, 1));
        await lane.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await _source.OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            LoyaltyPointChange? lpChange = null;
            IReadOnlyList<JournalEntry> iskEntries = [];
            var changes = new List<LpStoreItemChange>();

            if (lpCost > 0)
            {
                try
                {
                    lpChange = await _loyalty.ApplyAsync(connection, transaction, holder, offer.CorporationId, -lpCost,
                        Reason, $"lp-store:{purchaseId}:lp", cancellationToken);
                }
                catch (InsufficientLoyaltyPointsException)
                {
                    throw Refused("INSUFFICIENT_LP");
                }
            }

            if (iskCost > 0)
            {
                try
                {
                    var settlement = await _wallets.AdjustAsync(connection, transaction, account, -iskCost,
                        offer.CorporationId, PaymentToLpStoreRefType, Reason, now, $"lp-store:{purchaseId}:isk",
                        $"lp-store:{purchaseId}", cancellationToken);
                    iskEntries = settlement.Entries;
                }
                catch (InsufficientFundsException)
                {
                    throw Refused("INSUFFICIENT_ISK");
                }
                catch (WalletNotFoundException)
                {
                    throw Refused(payerCorporation ? "CORPORATION_NOT_FOUND" : "CHARACTER_NOT_FOUND");
                }
            }

            if (requiredByType.Count > 0)
            {
                var stacks = await _items.LockInLocationAsync(connection, transaction, request.LocationId,
                    request.CharacterId, ItemFlags.Hangar, requiredByType.Keys.ToArray(), cancellationToken);
                foreach (var (typeId, quantityNeeded) in requiredByType.OrderBy(pair => pair.Key))
                {
                    var remaining = quantityNeeded;
                    foreach (var stack in stacks.Where(stack => stack.TypeId == typeId).OrderBy(stack => stack.Id))
                    {
                        if (remaining == 0) break;
                        var available = stack.Quantity > 0 ? stack.Quantity : stack.Singleton ? 1 : 0;
                        if (available <= 0) continue;
                        var used = Math.Min(remaining, available);
                        var type = await _itemTypes(stack.TypeId, cancellationToken);
                        if (used == available)
                        {
                            await _items.DeleteAsync(connection, transaction, stack.Id, cancellationToken);
                            changes.Add(Change(stack.Id, stack.TypeId, type, stack.OwnerId, JunkLocation, stack.Flag, available,
                                stack.Singleton, stack.LocationId, available, removed: true));
                        }
                        else
                        {
                            await _items.SetQuantityAsync(connection, transaction, stack.Id, available - used, cancellationToken);
                            changes.Add(Change(stack.Id, stack.TypeId, type, stack.OwnerId, stack.LocationId, stack.Flag, available - used,
                                stack.Singleton, stack.LocationId, available));
                        }
                        remaining -= used;
                    }
                    if (remaining > 0) throw Refused("INSUFFICIENT_REQUIRED_ITEMS");
                }
            }

            // The granted item follows the New Eden Store's rule (PersistenceNewEdenStoreRuntime.GrantAsync): ship and blueprint
            // categories (6 and 9) are one item per unit, every other type is one stack.
            var singleton = rewardType.CategoryId is 6 or 9;
            var itemCount = singleton ? grantedQuantity : 1;
            var stackQuantity = singleton ? 1 : grantedQuantity;
            if (itemCount > MaximumCount) throw Refused("INVALID_QUANTITY");
            for (var index = 0L; index < itemCount; index++)
            {
                var itemId = await _items.CreateAsync(connection, transaction,
                    new NewItem(rewardType.TypeId, request.CharacterId, request.LocationId, ItemFlags.Hangar, stackQuantity, singleton),
                    cancellationToken);
                changes.Add(new LpStoreItemChange(itemId, rewardType.TypeId, rewardType.GroupId, rewardType.CategoryId,
                    request.CharacterId, request.LocationId, ItemFlags.Hangar, stackQuantity, singleton, 0, 0, Created: true, Removed: false));
            }

            var required = requiredByType.OrderBy(pair => pair.Key).Select(pair => new { typeId = pair.Key, quantity = pair.Value }).ToArray();
            var granted = new { itemTypeId = rewardType.TypeId, quantity = grantedQuantity, count = itemCount };
            await using (var insert = new NpgsqlCommand($"""
                INSERT INTO {_schema}.lp_store_purchases (purchase_id, character_id, payer_corporation_id, account_key, store_corporation_id,
                    offer_id, number_of_offers, lp_cost, isk_cost, location_id, required_items, granted_items, created_at_ms)
                VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11::jsonb, $12::jsonb, $13)
                """, connection, transaction))
            {
                insert.Parameters.Add(new NpgsqlParameter { Value = purchaseId });
                insert.Parameters.Add(new NpgsqlParameter { Value = request.CharacterId });
                insert.Parameters.Add(new NpgsqlParameter { Value = payerCorporation ? (object)request.PayerCorporationId : DBNull.Value });
                insert.Parameters.Add(new NpgsqlParameter { Value = request.AccountKey });
                insert.Parameters.Add(new NpgsqlParameter { Value = offer.CorporationId });
                insert.Parameters.Add(new NpgsqlParameter { Value = offer.OfferId });
                insert.Parameters.Add(new NpgsqlParameter { Value = request.NumberOfOffers });
                insert.Parameters.Add(new NpgsqlParameter { Value = lpCost });
                insert.Parameters.Add(new NpgsqlParameter { Value = iskCost });
                insert.Parameters.Add(new NpgsqlParameter { Value = request.LocationId });
                insert.Parameters.Add(new NpgsqlParameter { Value = JsonSerializer.Serialize(required) });
                insert.Parameters.Add(new NpgsqlParameter { Value = JsonSerializer.Serialize(granted) });
                insert.Parameters.Add(new NpgsqlParameter { Value = now });
                await insert.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);

            if (lpChange is not null) _loyalty.AnnounceCommitted(lpChange);
            foreach (var entry in iskEntries)
            {
                _wallets.AnnounceCommitted(new WalletBalanceChange(entry.OwnerId, entry.AccountKey, entry.Balance));
            }
            return new LpStorePurchaseReceipt(purchaseId, lpCost, iskCost, changes);
        }
        finally
        {
            lane.Release();
        }
    }

    private static LpStoreItemChange Change(long itemId, int typeId, ItemType? type, long ownerId, long locationId, int flag, long quantity,
        bool singleton, long previousLocationId, long previousQuantity, bool removed = false) =>
        new(itemId, typeId, type?.GroupId ?? 0, type?.CategoryId ?? 0, ownerId, locationId, flag, quantity, singleton,
            previousLocationId, previousQuantity, Created: false, Removed: removed);

    private static LpStorePurchaseRefusedException Refused(string code) => new(code);
}
