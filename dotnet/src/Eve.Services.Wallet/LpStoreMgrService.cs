using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Wallet;

namespace Eve.Services.Wallet;

/// <summary>
/// <c>LPStoreMgr</c>: the loyalty store of a corporation. <c>GetAvailableOffersFromCorp(corpID)</c> answers the offers the store holds
/// (ESI's offers, through <see cref="LpStoreOfferCache"/>) as the reference's KeyVal rows (eve-logic lpStoreSvc.py:464-530; EveJS
/// lpStoreOfferCatalog.js:133-147). The offer carries no <c>requiredStandings</c>: ESI does not return one, so none is shown
/// (DEVIATIONS "Agents slice AG-2, A9"). Taking an offer is not served yet and is refused by name.
/// </summary>
public sealed class LpStoreMgrService(LpStoreOfferCache offers) : IService
{
    private const string TakeNotAvailable = "This loyalty point store purchase is not available on this server yet.";

    public string Name => "LPStoreMgr";

    public bool HasMethod(string method) => method is
        "GetAvailableOffersFromCorp" or
        "TakeOfferForCharacter" or
        "TakeOfferForCorporation";

    public ValueTask<PyValue> CallAsync(ServiceCall call) => call.Method switch
    {
        "GetAvailableOffersFromCorp" => OffersAsync(call),
        "TakeOfferForCharacter" or "TakeOfferForCorporation" =>
            throw MachoErrors.ThrowUserError("CustomInfo", ("info", new PyStr(TakeNotAvailable))),
        _ => throw new InvalidOperationException($"LPStoreMgr has no method {call.Method}"),
    };

    private async ValueTask<PyValue> OffersAsync(ServiceCall call)
    {
        var corporationId = Positive(call.Args.Count > 0 ? call.Args[0] : null);
        if (corporationId == 0) return new PyList([]);
        var rows = await offers.GetAsync(corporationId, call.Cancellation);
        return new PyList([.. rows.Select(Row)]);
    }

    private static PyValue Row(LpStoreOffer offer) => Py.KeyVal(
        ("typeID", new PyInt(offer.TypeId)),
        ("iskCost", new PyInt(offer.IskCost)),
        ("akCost", new PyInt(offer.AkCost)),
        ("reqItems", new PyList([.. offer.RequiredItems.Select(item => (PyValue)new PyTuple([new PyInt(item.TypeId), new PyInt(item.Quantity)]))])),
        ("offerID", new PyInt(offer.OfferId)),
        ("qty", new PyInt(offer.Quantity)),
        ("requiredStandings", PyNone.Instance),
        ("corpID", new PyInt(offer.CorporationId)),
        ("lootItems", new PyList([])),
        ("lpCost", new PyInt(offer.LpCost)));

    private static long Positive(PyValue? value) => value switch
    {
        PyInt integer when integer.Value > 0 => integer.Value,
        PyLong integer when integer.Value > 0 => integer.Value,
        _ => 0,
    };
}
