using System.Globalization;
using System.Text.Json.Nodes;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;

namespace Eve.Services.Core;

/// <summary>
/// storeManager catalog reads and buy_offer dispatch to an optional settlement provider. The reader returns the existing GameStore
/// newEdenStore authority snapshot; absent stores use the empty catalog from EVE.js v0.12.9.
/// </summary>
public sealed class StoreOffersService(
    Func<CancellationToken, ValueTask<JsonObject?>> readStoreState,
    Func<StoreOfferPurchaseRequest, CancellationToken, ValueTask<PyValue>>? purchaseOffer = null) : IService
{
    private const long DefaultStoreId = 4;
    private readonly Func<CancellationToken, ValueTask<JsonObject?>> readStoreState =
        readStoreState ?? throw new ArgumentNullException(nameof(readStoreState));

    public string Name => "storeManager";

    public bool HasMethod(string method) => method is
        "get_offers" or "GetOffers" or
        "get_categories" or "GetCategories" or
        "get_products" or "GetProducts" or
        "can_purchase_offer" or "CanPurchaseOffer" or
        "buy_offer";

    public async ValueTask<PyValue> CallAsync(ServiceCall call)
    {
        if (!HasMethod(call.Method))
            return PyNone.Instance;

        call.Cancellation.ThrowIfCancellationRequested();
        if (call.Method == "buy_offer")
            return await PurchaseAsync(call).ConfigureAwait(false);

        var state = await readStoreState(call.Cancellation).ConfigureAwait(false);
        var storeId = call.Method is "can_purchase_offer" or "CanPurchaseOffer"
            ? PositiveId(call.Kwarg("store_id"), DefaultStoreId)
            : PositiveId(call.Arg(0), DefaultStoreId);
        var store = (state?["stores"] as JsonObject)?[storeId.ToString(CultureInfo.InvariantCulture)] as JsonObject;

        return call.Method switch
        {
            "get_offers" or "GetOffers" => List(store?["offers"]),
            "get_categories" or "GetCategories" => List(store?["categories"]),
            "get_products" or "GetProducts" => List(store?["products"]),
            "can_purchase_offer" or "CanPurchaseOffer" => PurchaseAvailability(state, store, call.Arg(0)),
            _ => PyNone.Instance,
        };
    }

    private async ValueTask<PyValue> PurchaseAsync(ServiceCall call)
    {
        if (call.Args.Count != 8)
            throw new ArgumentException("buy_offer requires exactly eight positional arguments.", nameof(call));
        if (call.Session.CharacterId is not { } authenticatedCharacterId || authenticatedCharacterId <= 0)
            throw new UnauthorizedAccessException("An authenticated character is required to buy a store offer.");
        if (purchaseOffer is null)
            throw new InvalidOperationException("Store offer purchase settlement provider is unavailable.");

        var request = new StoreOfferPurchaseRequest(
            authenticatedCharacterId,
            call.Arg(0),
            call.Arg(1),
            call.Arg(2),
            call.Arg(3),
            call.Arg(4),
            call.Arg(5),
            call.Arg(6),
            call.Arg(7));
        var result = await purchaseOffer(request, call.Cancellation).ConfigureAwait(false);
        if (!JsSemantics.Truthy(result))
            throw new InvalidOperationException("Store offer purchase was not settled.");

        return result;
    }

    private static PyValue List(JsonNode? node) => node is JsonArray values
        ? new PyList([.. values.Select(ToPyValue)])
        : new PyList([]);

    private static PyValue PurchaseAvailability(JsonObject? state, JsonObject? store, PyValue requestedValue)
    {
        var storeDisabled = (state?["config"] as JsonObject)?["enabled"] is JsonValue enabledValue &&
            enabledValue.TryGetValue<bool>(out var enabled) && !enabled;
        var offer = storeDisabled ? null : FindOffer(store?["offers"] as JsonArray, requestedValue);
        var canPurchase = offer is not null;
        var offerId = offer is null ? 0 : PositiveId(offer["id"], 0);
        var storeOfferValue = offer?["storeOfferID"];
        var storeOfferId = JsTruthy(storeOfferValue) ? JsString(storeOfferValue) : null;

        return Py.Dict(
            ("canPurchase", canPurchase ? PyBool.True : PyBool.False),
            ("offerID", canPurchase ? new PyInt(offerId) : PyNone.Instance),
            ("storeOfferID", canPurchase && storeOfferId is not null ? new PyStr(storeOfferId) : PyNone.Instance),
            ("purchaseLimitedReason", canPurchase ? PyNone.Instance : new PyStr(storeDisabled ? "store_disabled" : "offer_unavailable")));
    }

    private static JsonObject? FindOffer(JsonArray? offers, PyValue requestedValue)
    {
        var raw = JsString(requestedValue).Trim();
        if (raw.Length == 0)
            return null;

        var lookup = raw.StartsWith("VEVC-", StringComparison.OrdinalIgnoreCase) ? raw[5..] : raw;
        var numericId = PositiveId(new PyStr(lookup), 0);
        foreach (var node in offers ?? [])
        {
            if (node is not JsonObject offer)
                continue;

            var offerId = PositiveId(offer["id"], 0);
            var storeOfferValue = offer["storeOfferID"];
            var storeOfferId = JsTruthy(storeOfferValue) ? JsString(storeOfferValue).Trim() : string.Empty;
            if ((numericId > 0 && offerId == numericId) ||
                (storeOfferId.Length > 0 && storeOfferId.Equals(lookup, StringComparison.OrdinalIgnoreCase)) ||
                (offerId > 0 && $"VEVC-{offerId}".Equals(raw, StringComparison.OrdinalIgnoreCase)) ||
                (storeOfferId.Length > 0 && $"VEVC-{storeOfferId}".Equals(raw, StringComparison.OrdinalIgnoreCase)))
                return offer;
        }

        return null;
    }

    private static long PositiveId(PyValue? value, long fallback)
    {
        var number = JsSemantics.ToNumber(value);
        return double.IsFinite(number) && number > 0 && number == Math.Truncate(number) && number < long.MaxValue
            ? (long)number
            : fallback;
    }

    private static long PositiveId(JsonNode? node, long fallback) =>
        PositiveId(node is null ? PyNone.Instance : ToPyValue(node), fallback);

    private static string JsString(PyValue value) => value switch
    {
        PyStr text => text.Value,
        PyInt number => number.Value.ToString(CultureInfo.InvariantCulture),
        PyLong number => number.Value.ToString(CultureInfo.InvariantCulture),
        PyReal number => number.Value.ToString("G", CultureInfo.InvariantCulture),
        PyBool boolean => boolean.Value ? "true" : "false",
        _ => string.Empty,
    };

    private static string JsString(JsonNode? node) => ToPyValue(node) is PyStr text
        ? text.Value
        : ToPyValue(node) switch
        {
            PyInt number => number.Value.ToString(CultureInfo.InvariantCulture),
            PyLong number => number.Value.ToString(CultureInfo.InvariantCulture),
            PyReal number => number.Value.ToString("G", CultureInfo.InvariantCulture),
            PyBool boolean => boolean.Value ? "true" : "false",
            _ => string.Empty,
        };

private static bool JsTruthy(JsonNode? node)
    {
        if (node is null)
            return false;
        if (node is JsonObject or JsonArray)
            return true;
        if (node is not JsonValue value)
            return false;
        if (value.TryGetValue<bool>(out var boolean))
            return boolean;
        if (value.TryGetValue<string>(out var text))
            return text.Length > 0;
        if (value.TryGetValue<int>(out var integer))
            return integer != 0;
        if (value.TryGetValue<long>(out var longInteger))
            return longInteger != 0;
        if (value.TryGetValue<double>(out var real))
            return real != 0 && !double.IsNaN(real);
        if (value.TryGetValue<decimal>(out var decimalValue))
            return decimalValue != 0;
        return true;
    }

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
        if (value.TryGetValue<double>(out var real))
            return Py.Number(real);
        if (value.TryGetValue<decimal>(out var decimalValue))
            return Py.Number((double)decimalValue);
        return PyNone.Instance;
    }
}

/// <summary>The authenticated caller and exact positional fields for one storeManager.buy_offer settlement.</summary>
/// <remarks>
/// The Host provider must use the full request, including GameTime, as its durable idempotency identity;
/// validate Sender against AuthenticatedCharacterId; and return the provider response only after atomic
/// currency debit and product fulfillment have committed.
/// </remarks>
public sealed record StoreOfferPurchaseRequest(
    long AuthenticatedCharacterId,
    PyValue Offer,
    PyValue Currency,
    PyValue Qty,
    PyValue Sender,
    PyValue Recipient,
    PyValue Message,
    PyValue GameTime,
    PyValue Store);
