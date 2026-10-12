using System.Globalization;
using System.Text.Json.Nodes;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;

namespace Eve.Services.Core;

/// <summary>The fast-checkout purchase the host grants: no charge, the offer's fulfillment granted to the character.</summary>
public interface IFastCheckoutCallbacks
{
    ValueTask GrantFastCheckoutOfferAsync(
        long authenticatedUserId,
        long characterId,
        string storeOfferId,
        string currency,
        string operationId,
        CancellationToken cancellationToken);
}

/// <summary>
/// The in-client PLEX checkout that the client calls (eve-logic fastcheckout/client/fastCheckoutClientService.py): the flags and
/// offers it reads, and <c>BuyOffer</c>. The answer's <c>Message</c> is "OK" (fastCheckoutClientService.py:230-232), which the
/// client takes as a confirmed payment. Nothing is charged; the pack is granted through the store's fulfillment.
/// </summary>
[Service("FastCheckoutService")]
public sealed partial class FastCheckoutService(NewEdenStoreCatalog catalog, IFastCheckoutCallbacks purchases)
{
    private readonly NewEdenStoreCatalog _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
    private readonly IFastCheckoutCallbacks _purchases = purchases ?? throw new ArgumentNullException(nameof(purchases));

    [Method("IsFastCheckoutEnabledForUser")]
    public PyValue IsFastCheckoutEnabledForUser(ServiceCall call) => PyValue.Of(_catalog.FastCheckoutEnabled);

    /// <summary>The server keeps no per-user fast-checkout cache, so there is nothing to clear: the answer is the client's acknowledgement.</summary>
    [Method("ClearFastCheckoutCacheForUser")]
    public PyValue ClearFastCheckoutCacheForUser(ServiceCall call) => PyBool.True;

    [Method("GetTestingConfiguration")]
    public PyValue GetTestingConfiguration(ServiceCall call) => new PyTuple([
        PyValue.Of(_catalog.ChinaFunnelEnabled),
        new PyStr(_catalog.FakeBuyPlexOfferUrl),
        PyValue.Of(_catalog.UseShellExecuteToBuyPlexOffer),
    ]);

    [Method("GetOffersForUser")]
    public PyValue GetOffersForUser(ServiceCall call) =>
        Py.Dict(("plex", new PyList([.. _catalog.FastCheckoutOffers.OfType<JsonObject>().Select(OfferKeyVal)])));

    [Method("BuyOffer")]
    public async ValueTask<PyValue> BuyOffer(ServiceCall call)
    {
        call.Cancellation.ThrowIfCancellationRequested();
        if (call.Session.UserId is not { } userId || userId <= 0 ||
            call.Session.CharacterId is not { } characterId || characterId <= 0)
            throw MachoErrors.ThrowUserError("PurchaseFailed");
        if (!_catalog.StoreEnabled)
            throw MachoErrors.ThrowUserError("STORE_DISABLED");
        if (!_catalog.FastCheckoutEnabled)
            throw MachoErrors.ThrowUserError("FAST_CHECKOUT_DISABLED");

        var offerId = OfferIdOf(call.Kwarg("offer")) ?? OfferIdOf(call.Kwarg("offerID"))
            ?? throw MachoErrors.ThrowUserError("OfferNotAvailable");
        var offer = _catalog.FindFastCheckoutOffer(offerId) ?? throw MachoErrors.ThrowUserError("OfferNotAvailable");
        var storeOfferId = offer["storeOfferID"]?.GetValue<string>() ?? string.Empty;
        var currency = offer["currency"]?.GetValue<string>() ?? "USD";
        var operationId = CallIdText(call.CallId);
        if (string.IsNullOrEmpty(operationId))
            throw MachoErrors.ThrowUserError("PurchaseFailed");
        await _purchases.GrantFastCheckoutOfferAsync(
            userId, characterId, storeOfferId, currency, operationId, call.Cancellation).ConfigureAwait(false);

        return Py.Dict(
            ("Message", new PyStr(_catalog.FakeFastCheckoutResponse)),
            ("offerID", new PyInt(offerId)),
            ("storeOfferID", new PyStr(storeOfferId)));
    }

    /// <summary>The call id as text: the operation id of the purchase (a number, or a string), empty when there is none.</summary>
    private static string CallIdText(PyValue? callId) => callId switch
    {
        PyInt number => number.Value.ToString(CultureInfo.InvariantCulture),
        PyLong number => number.Value.ToString(CultureInfo.InvariantCulture),
        var text when Eve.Macho.JsSemantics.StringValue(text) is not null => Eve.Macho.JsSemantics.StringValue(text)!,
        _ => string.Empty,
    };

    /// <summary>The offer as <c>util.KeyVal</c> carries it (EVE.js FastCheckoutService.js:87-123).</summary>
    private static PyValue OfferKeyVal(JsonObject offer)
    {
        var id = offer["id"]?.GetValue<long>() ?? 0;
        var quantity = offer["quantity"]?.GetValue<long>() ?? 0;
        var entries = new List<(string Name, PyValue Value)>
        {
            ("id", new PyInt(id)),
            ("offerID", new PyInt(id)),
            ("storeOfferID", new PyStr(offer["storeOfferID"]?.GetValue<string>() ?? string.Empty)),
            ("name", new PyStr(offer["name"]?.GetValue<string>() ?? string.Empty)),
            ("price", new PyReal(offer["price"]?.GetValue<double>() ?? 0)),
            ("currency", new PyStr(offer["currency"]?.GetValue<string>() ?? "USD")),
            ("quantity", new PyInt(quantity)),
            ("baseQuantity", new PyInt(offer["baseQuantity"]?.GetValue<long>() ?? quantity)),
            ("tags", new PyList([.. (offer["tags"] as JsonArray ?? []).Select(tag => (PyValue)new PyStr(tag?.GetValue<string>() ?? string.Empty))])),
            ("imageUrl", offer["imageUrl"] is JsonValue image ? new PyStr(image.GetValue<string>()) : PyNone.Instance),
        };
        return Py.KeyVal(entries.ToArray());
    }

    /// <summary>The offer id of a <c>util.KeyVal</c> or dict argument (its <c>id</c>, else <c>offerID</c>), or null.</summary>
    private static long? OfferIdOf(PyValue? offer)
    {
        IEnumerable<KeyValuePair<PyValue, PyValue>> entries = offer switch
        {
            PyDict dict => dict.Entries,
            PyObject { Args: PyDict dict } => dict.Entries,
            PyObjectEx objectEx => objectEx.Dict,
            PyInt or PyLong => [new KeyValuePair<PyValue, PyValue>(new PyStr("id"), offer)],
            _ => [],
        };
        foreach (var name in new[] { "id", "offerID" })
        {
            foreach (var entry in entries)
            {
                if (JsSemantics.StringValue(entry.Key) != name)
                    continue;
                var id = JsSemantics.ToPositiveInteger(entry.Value);
                if (id > 0)
                    return id;
            }
        }
        return null;
    }
}
