using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Eve.GameStore;
using Eve.Host.PublicGateway;
using Eve.Identity;
using Eve.Inventory;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;
using Eve.Services.Skills;
using Eve.Wallet;
using Npgsql;

namespace Eve.Host;

public sealed partial class PersistenceNewEdenStoreRuntime
{
    private const string StorePurchaseTable = "newEdenStorePurchases";
    private const long InGameStoreId = 4;

    private sealed record StorePurchaseTerms(
        long OfferId,
        string StoreOfferId,
        long PlexPrice,
        string Kind,
        long DurationDays,
        long SlotCount,
        int TypeId,
        long ItemQuantity);

    public bool SupportsStoreOfferPurchase(long storeId, JsonObject offer) =>
        storeId == InGameStoreId && TryReadStorePurchaseTerms(offer, out _);

    public async ValueTask<PyValue> PurchaseStoreOfferAsync(
        StoreOfferPurchaseRequest request,
        CancellationToken cancellationToken)
    {
        if (request.AuthenticatedUserId <= 0 || request.AuthenticatedCharacterId <= 0 ||
            request.CentsPerPlex <= 0 || !SupportsStoreOfferPurchase(StoreId(request.Store), request.OfferData))
            return PyNone.Instance;

        var currency = Eve.Macho.JsSemantics.Truthy(request.Currency)
            ? CurrencyText(request.Currency).Trim().ToUpperInvariant()
            : "PLX";
        if (currency.Length == 0)
            currency = "PLX";
        if (currency != "PLX")
            return PyNone.Instance;

        var terms = ReadStorePurchaseTerms(request.OfferData);
        var quantity = PurchaseQuantity(request.Qty);
        var callId = PositiveInteger(request.OperationId);
        var payerId = PositiveInteger(request.Sender);
        var targetId = PositiveInteger(request.Recipient);
        payerId = payerId > 0 ? payerId : request.AuthenticatedCharacterId;
        targetId = targetId > 0 ? targetId : payerId;
        if (quantity <= 0 || callId <= 0 || payerId <= 0 || targetId <= 0)
            return PyNone.Instance;

        long totalCost;
        long itemQuantity;
        long mctSlotCount;
        TimeSpan subscriptionExtension;
        try
        {
            totalCost = checked(terms.PlexPrice * quantity);
            itemQuantity = terms.Kind == "item" ? checked(terms.ItemQuantity * quantity) : 0;
            mctSlotCount = terms.Kind == "mct" ? checked(terms.SlotCount * quantity) : 0;
            subscriptionExtension = terms.Kind == "omega"
                ? TimeSpan.FromDays(checked(terms.DurationDays * quantity))
                : TimeSpan.Zero;
            _ = checked(totalCost * request.CentsPerPlex);
        }
        catch (OverflowException)
        {
            return PyNone.Instance;
        }

        var accountId = request.AuthenticatedUserId;
        var payer = await characters.GetAsync(payerId, cancellationToken).ConfigureAwait(false);
        var target = await characters.GetAsync(targetId, cancellationToken).ConfigureAwait(false);
        if (payer is null || target is null || payer.AccountId != accountId || target.AccountId != accountId)
            return PyNone.Instance;

        Eve.Inventory.ItemType? itemType = null;
        var deliveryLocationId = 0L;
        if (terms.Kind == "item")
        {
            itemType = await itemTypes.GetAsync(terms.TypeId, cancellationToken).ConfigureAwait(false);
            deliveryLocationId = DeliveryStationId(target, targetId);
            if (itemType is null || deliveryLocationId <= 0 || itemQuantity <= 0)
                return PyNone.Instance;
        }

        var fingerprint = RequestFingerprint(
            request, terms, quantity, payerId, targetId, request.CentsPerPlex);
        var receiptKey = $"{accountId.ToString(CultureInfo.InvariantCulture)}:{callId.ToString(CultureInfo.InvariantCulture)}";
        var now = DateTimeOffset.FromUnixTimeMilliseconds(nowUnixMs());
        var balance = 0L;
        var balanceCents = 0L;
        DateTimeOffset? previousSubscriptionEnd = null;
        DateTimeOffset? subscriptionEnd = null;
        ItemRow? deliveredItem = null;

        try
        {
            await using var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await LockPurchaseReceiptAsync(connection, transaction, receiptKey, cancellationToken).ConfigureAwait(false);
            var existing = await store.ReadRowAsync(
                connection, transaction, StorePurchaseTable, receiptKey, cancellationToken).ConfigureAwait(false);
            if (existing is not null)
            {
                var receipt = JsonNode.Parse(existing) as JsonObject;
                if (receipt?["fingerprint"]?.GetValue<string>() != fingerprint || receipt["response"] is not JsonObject savedResponse)
                    return PyNone.Instance;
                return ToPyValue(savedResponse);
            }

            if (totalCost == 0)
            {
                var balances = await plexWallets.ReadPlexBalancesAsync(
                    connection, transaction, [payerId], cancellationToken).ConfigureAwait(false);
                if (!balances.TryGetValue(payerId, out balance))
                    return PyNone.Instance;
            }
            else
            {
                balance = await plexWallets.AdjustPlexAsync(
                    connection, transaction, payerId, -totalCost, cancellationToken).ConfigureAwait(false);
            }
            balanceCents = checked(balance * request.CentsPerPlex);

            switch (terms.Kind)
            {
                case "omega":
                    previousSubscriptionEnd = await ReadSubscriptionEndForUpdateAsync(
                        connection, transaction, checked((uint)accountId), cancellationToken).ConfigureAwait(false);
                    if (previousSubscriptionEnd is null)
                        return PyNone.Instance;
                    subscriptionEnd = await accountDirectory.ExtendSubscriptionAsync(
                        connection, transaction, checked((uint)accountId), subscriptionExtension, now, cancellationToken).ConfigureAwait(false);
                    break;
                case "mct":
                    previousSubscriptionEnd = await ReadSubscriptionEndForUpdateAsync(
                        connection, transaction, checked((uint)accountId), cancellationToken).ConfigureAwait(false);
                    if (previousSubscriptionEnd is not { } mctSubscriptionEnd || mctSubscriptionEnd <= now)
                        return PyNone.Instance;
                    for (var unit = 0L; unit < mctSlotCount; unit++)
                        await GrantTrainingSlotAsync(
                            connection, transaction, accountId, null, TimeSpan.FromDays(terms.DurationDays), now, cancellationToken).ConfigureAwait(false);
                    break;
                case "item":
                {
                    var itemId = await items.CreateAsync(
                        connection, transaction,
                        new NewItem(terms.TypeId, targetId, deliveryLocationId, ItemFlags.Hangar, itemQuantity),
                        cancellationToken).ConfigureAwait(false);
                    deliveredItem = new ItemRow(
                        itemId, terms.TypeId, targetId, deliveryLocationId, ItemFlags.Hangar,
                        itemQuantity, itemType!.GroupId, itemType.CategoryId);
                    break;
                }
            }

            var response = PurchaseResponse(terms, quantity, totalCost, balance, payerId, targetId);
            var receiptRow = new JsonObject
            {
                ["fingerprint"] = fingerprint,
                ["response"] = response,
            };
            await store.ApplyChangesAsync(
                connection, transaction, StorePurchaseTable,
                [new RowUpsert(receiptKey, receiptRow.ToJsonString())], [], ownerFence, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is InsufficientFundsException or WalletNotFoundException or MoneyLimitException)
        {
            return PyNone.Instance;
        }
        catch (OverflowException)
        {
            return PyNone.Instance;
        }

        var transactionAt = DateTimeOffset.FromUnixTimeMilliseconds(nowUnixMs());
        var plexDeltaCents = checked(-totalCost * request.CentsPerPlex);
        if (sessions.Of(payerId) is { } payerSession)
        {
            payerSession.Set("plexBalance", Py.Number(balance));
            payerSession.Set("balanceChange", Py.Number(-totalCost));
        }
        publishNotice(
            "eve_public.plex.vault.api.BalanceChangedNotice",
            EncodeBalanceChangedNotice(transactionAt, plexDeltaCents, balanceCents),
            payerId);

        if (subscriptionEnd is { } newEnd && previousSubscriptionEnd is { } oldEnd)
        {
            var oldGrade = oldEnd > now ? 1L : 0L;
            var newGrade = newEnd > now ? 1L : 0L;
            var endFileTime = Py.FileTimeFromUnixMs(newEnd.ToUnixTimeMilliseconds());
            foreach (var session in sessions.OfUser(accountId))
                SubscriptionMgrService.AnnouncePurchasedSubscription(session, oldGrade, newGrade, endFileTime);
        }
        else if (terms.Kind == "mct")
        {
            foreach (var session in sessions.OfUser(accountId))
                SkillNotifications.MultipleCharactersTrainingUpdated(session);
        }
        else if (deliveredItem is { } item && sessions.Of(targetId) is { } targetSession)
        {
            targetSession.SendNotification(
                "OnItemChange", "clientID", InventoryWire.PackedRow(item), new PyDict([]), PyNone.Instance);
        }

        return ToPyValue(PurchaseResponse(terms, quantity, totalCost, balance, payerId, targetId));
    }

    private static bool TryReadStorePurchaseTerms(JsonObject offer, out StorePurchaseTerms terms)
    {
        terms = null!;
        if (offer["canPurchase"] is not JsonValue flag || !flag.TryGetValue<bool>(out var canPurchase) || !canPurchase ||
            offer["products"] is not JsonArray { Count: > 0 } products ||
            products.Any(product => product is not JsonObject p || PositiveJson(p["id"]) <= 0 || PositiveJson(p["typeId"]) <= 0 || PositiveJson(p["quantity"]) <= 0))
            return false;

        var price = (offer["offerPricings"] as JsonArray)?.OfType<JsonObject>()
            .FirstOrDefault(value => string.Equals(JsonText(value["currency"]), "PLX", StringComparison.OrdinalIgnoreCase));
        var fulfillment = offer["fulfillment"] as JsonObject;
        var offerId = PositiveJson(offer["id"]);
        var storeOfferId = JsonText(offer["storeOfferID"]);
        var plexPrice = JsonLong(price?["price"], -1);
        var kind = JsonText(fulfillment?["kind"]);
        if (offerId <= 0 || storeOfferId.Length == 0 || plexPrice < 0)
            return false;

        switch (kind)
        {
            case "omega":
            case "mct":
            {
                var durationDays = PositiveJson(fulfillment?["durationDays"]);
                var slotCount = kind == "mct" ? Math.Max(1, PositiveJson(fulfillment?["slotCount"], 1)) : 0;
                if (durationDays <= 0)
                    return false;
                terms = new StorePurchaseTerms(offerId, storeOfferId, plexPrice, kind, durationDays, slotCount, 0, 0);
                return true;
            }
            case "item":
            {
                var typeId = PositiveJson(fulfillment?["typeID"]);
                var itemQuantity = Math.Max(1, PositiveJson(fulfillment?["quantity"], 1));
                if (typeId <= 0 || products.Count != 1 || PositiveJson((products[0] as JsonObject)?["typeId"]) != typeId)
                    return false;
                terms = new StorePurchaseTerms(offerId, storeOfferId, plexPrice, kind, 0, 0, checked((int)typeId), itemQuantity);
                return true;
            }
            default:
                return false;
        }
    }

    private static StorePurchaseTerms ReadStorePurchaseTerms(JsonObject offer) =>
        TryReadStorePurchaseTerms(offer, out var terms)
            ? terms
            : throw new InvalidOperationException("The trusted store offer has unsupported fulfillment data.");

    private static long PurchaseQuantity(PyValue value)
    {
        var number = Eve.Macho.JsSemantics.ToNumber(value);
        if (double.IsNaN(number) || number == 0)
            return 1;
        if (double.IsPositiveInfinity(number))
            return 0;
        var quantity = Math.Max(1, Math.Truncate(number));
        return quantity < long.MaxValue ? (long)quantity : 0;
    }

    private static long PositiveInteger(PyValue value)
    {
        var number = Eve.Macho.JsSemantics.ToNumber(value);
        return double.IsFinite(number) && number > 0 && number == Math.Truncate(number) && number < long.MaxValue
            ? (long)number
            : 0;
    }

    private static string CurrencyText(PyValue value) => value switch
    {
        PyStr text => text.Value,
        PyBytes bytes => Encoding.UTF8.GetString(bytes.Value.Span),
        PyBuffer buffer => Encoding.UTF8.GetString(buffer.Value.Span),
        PyToken token => token.Value,
        PyInt integer => integer.Value.ToString(CultureInfo.InvariantCulture),
        PyLong integer => integer.Value.ToString(CultureInfo.InvariantCulture),
        PyReal real => real.Value.ToString("G", CultureInfo.InvariantCulture),
        PyBool boolean => boolean.Value ? "true" : "false",
        _ => value.ToString(),
    };

    private static long StoreId(PyValue value)
    {
        var id = PositiveInteger(value);
        return id > 0 ? id : InGameStoreId;
    }

    private static string RequestFingerprint(
        StoreOfferPurchaseRequest request,
        StorePurchaseTerms terms,
        long quantity,
        long payerId,
        long targetId,
        long centsPerPlex)
    {
        var semanticRequest = string.Join('|',
            request.AuthenticatedUserId.ToString(CultureInfo.InvariantCulture),
            StoreId(request.Store).ToString(CultureInfo.InvariantCulture),
            terms.OfferId.ToString(CultureInfo.InvariantCulture),
            terms.StoreOfferId,
            "PLX",
            quantity.ToString(CultureInfo.InvariantCulture),
            payerId.ToString(CultureInfo.InvariantCulture),
            targetId.ToString(CultureInfo.InvariantCulture),
            centsPerPlex.ToString(CultureInfo.InvariantCulture),
            request.GameTime.ToString());
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(semanticRequest)));
    }

    private static async Task LockPurchaseReceiptAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string receiptKey,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT pg_advisory_xact_lock(hashtextextended($1, 0))", connection, transaction);
        command.Parameters.Add(new NpgsqlParameter { Value = receiptKey });
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<DateTimeOffset?> ReadSubscriptionEndForUpdateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        uint accountId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"SELECT subscription_end_at FROM {accountDirectory.Schema}.accounts WHERE id = $1 FOR UPDATE", connection, transaction);
        command.Parameters.Add(new NpgsqlParameter { Value = checked((int)accountId) });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) && !reader.IsDBNull(0)
            ? reader.GetFieldValue<DateTimeOffset>(0)
            : null;
    }

    private static JsonObject PurchaseResponse(
        StorePurchaseTerms terms,
        long quantity,
        long spent,
        long balance,
        long payerId,
        long targetId) => new()
    {
        ["success"] = true,
        ["offer_id"] = terms.OfferId,
        ["store_offer_id"] = terms.StoreOfferId,
        ["quantity"] = quantity,
        ["currency"] = "PLX",
        ["spent"] = spent,
        ["balance"] = balance,
        ["payer_character_id"] = payerId,
        ["character_id"] = targetId,
    };

    private static byte[] EncodeBalanceChangedNotice(DateTimeOffset at, long amountCents, long balanceCents)
    {
        var timestamp = new ProtoWriter();
        timestamp.Uint(1, checked((ulong)at.ToUnixTimeSeconds()));
        timestamp.Uint(2, checked((ulong)(at.UtcDateTime.Ticks % TimeSpan.TicksPerSecond * 100)));
        var amount = new ProtoWriter();
        amount.Uint(1, unchecked((ulong)amountCents));
        var balance = new ProtoWriter();
        balance.Uint(1, checked((ulong)balanceCents));
        var attributes = new ProtoWriter();
        attributes.Message(1, timestamp.ToArray());
        attributes.Message(2, amount.ToArray());
        attributes.Message(3, balance.ToArray());
        var identifier = new ProtoWriter();
        identifier.Uint(1, checked((ulong)at.ToUnixTimeMilliseconds()));
        var notice = new ProtoWriter();
        notice.Message(1, identifier.ToArray());
        notice.Message(2, attributes.ToArray());
        return notice.ToArray();
    }

    private static long PositiveJson(JsonNode? value, long fallback = 0)
    {
        var parsed = JsonLong(value, fallback);
        return parsed > 0 ? parsed : fallback;
    }

    private static long JsonLong(JsonNode? value, long fallback = 0) =>
        long.TryParse(value?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : fallback;

    private static string JsonText(JsonNode? value) =>
        value is JsonValue scalar && scalar.TryGetValue<string>(out var text) ? text : string.Empty;

    private static PyValue ToPyValue(JsonNode? node)
    {
        if (node is JsonObject obj)
            return new PyDict([.. obj.Select(entry => new KeyValuePair<PyValue, PyValue>(new PyStr(entry.Key), ToPyValue(entry.Value)))]);
        if (node is JsonArray array)
            return new PyList([.. array.Select(ToPyValue)]);
        if (node is not JsonValue value)
            return PyNone.Instance;
        if (value.TryGetValue<string>(out var text))
            return new PyStr(text);
        if (value.TryGetValue<bool>(out var boolean))
            return boolean ? PyBool.True : PyBool.False;
        if (value.TryGetValue<int>(out var integer))
            return Py.Number(integer);
        if (value.TryGetValue<long>(out var longInteger))
            return Py.Number(longInteger);
        if (value.TryGetValue<double>(out var real) && double.IsFinite(real))
            return Py.Number(real);
        return PyNone.Instance;
    }
}
