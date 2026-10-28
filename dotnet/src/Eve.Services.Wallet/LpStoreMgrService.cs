using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Wallet;

namespace Eve.Services.Wallet;

public sealed class LpStoreMgrService(
    LpStoreOfferCache offers,
    LpStorePurchases purchases,
    Func<long, long, CancellationToken, ValueTask<bool>> isDirector,
    Func<long, CancellationToken, Task<IReadOnlyList<LpStoreOffer>>>? readOffers = null,
    Func<LpStorePurchaseRequest, CancellationToken, Task<LpStorePurchaseReceipt>>? takePurchase = null,
    Func<long, long, CancellationToken, ValueTask<bool>>? hasLoyaltyStoreService = null) : IService
{

    private static readonly Dictionary<string, string> Messages = new()
    {
        ["ACCESS_DENIED"] = "You do not have permission to spend your corporation's funds.",
        ["AK_UNSUPPORTED"] = "That offer requires unsupported currency.",
        ["CHARACTER_NOT_FOUND"] = "Character data is unavailable for this purchase.",
        ["CORPORATION_NOT_FOUND"] = "Corporation data is unavailable for this purchase.",
        ["INSUFFICIENT_ISK"] = "You do not have enough ISK.",
        ["INSUFFICIENT_LP"] = "You do not have enough loyalty points.",
        ["INSUFFICIENT_REQUIRED_ITEMS"] = "You do not have the required items for that offer.",
        ["INVALID_CHARACTER"] = "Character data is unavailable for this purchase.",
        ["INVALID_QUANTITY"] = "That offer quantity is not valid.",
        ["INVALID_STORE"] = "That LP store is not available.",
        ["LOYALTY_STORE_UNAVAILABLE"] = "That loyalty store service is not available.",
        ["ITEM_GRANT_FAILED"] = "LP store purchase delivery failed.",
        ["OFFER_NOT_FOUND"] = "That offer is no longer available.",
        ["REQ_ITEMS_UNSUPPORTED"] = "That offer requires unsupported extra items.",
    };

    private const long DefaultAccountKey = 1000;

    public string Name => "LPStoreMgr";

    public bool HasMethod(string method) => method is
        "GetAvailableOffersFromCorp" or
        "TakeOfferForCharacter" or
        "TakeOfferForCorporation";

    public ValueTask<PyValue> CallAsync(ServiceCall call) => call.Method switch
    {
        "GetAvailableOffersFromCorp" => OffersAsync(call),
        "TakeOfferForCharacter" => TakeForCharacterAsync(call),
        "TakeOfferForCorporation" => TakeForCorporationAsync(call),
        _ => throw new InvalidOperationException($"LPStoreMgr has no method {call.Method}"),
    };

    private async ValueTask<PyValue> OffersAsync(ServiceCall call)
    {
        await RequireStructureLoyaltyStoreAccessAsync(call);
        var corporationId = Positive(call.Args.Count > 0 ? call.Args[0] : null);
        if (corporationId == 0) return new PyList([]);
        var rows = readOffers is null
            ? await offers.GetAsync(corporationId, call.Cancellation)
            : await readOffers(corporationId, call.Cancellation);
        return new PyList([.. rows.Select(Row)]);
    }

    private async ValueTask<PyValue> TakeForCharacterAsync(ServiceCall call)
    {
        await RequireStructureLoyaltyStoreAccessAsync(call);
        var characterId = call.Session.CharacterId ?? 0;
        var request = new LpStorePurchaseRequest(characterId, PayerCorporationId: 0, AccountKey: (int)DefaultAccountKey,
            StoreCorporationId: Positive(Arg(call, 0)), OfferId: Positive(Arg(call, 1)), NumberOfOffers: Quantity(call),
            LocationId: LocationOf(call));
        return await TakeAsync(call, request);
    }

    private async ValueTask<PyValue> TakeForCorporationAsync(ServiceCall call)
    {
        await RequireStructureLoyaltyStoreAccessAsync(call);
        var storeCorporationId = Positive(Arg(call, 0));
        if (storeCorporationId == 0) throw Refusal("INVALID_STORE");
        var characterId = call.Session.CharacterId ?? 0;
        var corporationId = call.Session.GetInt("corporationID") is > 0 and var corporation
            ? corporation
            : Math.Max(0, call.Session.GetInt("corpid") ?? 0);

        // Authorize from the member's stored roles, not the session copy.
        if (corporationId <= 0 || characterId <= 0 || !await isDirector(characterId, corporationId, call.Cancellation))
            throw Refusal("ACCESS_DENIED");
        var accountKey = call.Session.GetInt("corpAccountKey") ?? call.Session.GetInt("corpaccountkey") ?? DefaultAccountKey;
        var request = new LpStorePurchaseRequest(characterId, corporationId, (int)accountKey, storeCorporationId,
            Positive(Arg(call, 1)), Quantity(call), LocationOf(call));
        return await TakeAsync(call, request);
    }

    private async ValueTask<PyValue> TakeAsync(ServiceCall call, LpStorePurchaseRequest request)
    {
        LpStorePurchaseReceipt receipt;
        try
        {
            receipt = takePurchase is null
                ? await purchases.TakeAsync(request, call.Cancellation)
                : await takePurchase(request, call.Cancellation);
        }
        catch (LpStorePurchaseRefusedException refusal)
        {
            throw Refusal(refusal.Code);
        }
        foreach (var change in receipt.ItemChanges)
        {
            call.Session.SendNotification("OnItemChange", "clientID", InventoryWire.PackedRow(RowOf(change)), Differences(change), PyNone.Instance);
        }
        return PyBool.True;
    }

    private static Exception Refusal(string code) => MachoErrors.ThrowUserError("CustomNotify",
        ("notify", new PyStr(Messages.TryGetValue(code, out var text) ? text : "LP store purchase failed.")));

    private static PyValue? Arg(ServiceCall call, int index) => index < call.Args.Count ? call.Args[index] : null;

    private static long Quantity(ServiceCall call) => call.Args.Count > 2 ? Positive(call.Args[2]) : 1;

    private async ValueTask RequireStructureLoyaltyStoreAccessAsync(ServiceCall call)
    {
        var structureId = StructureOf(call.Session);
        if (structureId <= 0) return;

        var characterId = call.Session.CharacterId ?? Positive(call.Session.Get("charid"));
        if (characterId <= 0 || hasLoyaltyStoreService is null ||
            !await hasLoyaltyStoreService(characterId, structureId, call.Cancellation))
            throw Refusal("LOYALTY_STORE_UNAVAILABLE");
    }

    private static long StructureOf(ClientSession session) =>
        Positive(session.Get("structureid") ?? session.Get("structureID"));

    private static long LocationOf(ServiceCall call)
    {
        var structureId = StructureOf(call.Session);
        return structureId > 0
            ? structureId
            : Positive(call.Session.Get("stationid") ?? call.Session.Get("stationID"));
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

    /// <summary>The row of a changed item as the client reads it: a singleton's quantity is -1 (as the ship and module rows are sent).</summary>
    private static ItemRow RowOf(LpStoreItemChange change) => new(
        change.ItemId, change.TypeId, change.OwnerId, change.LocationId, change.Flag,
        change.Singleton ? -1 : change.Quantity, change.GroupId, change.CategoryId);

    /// <summary>What the item was before the change, as the reference sends it: the place it left (3) and the stack size (9), each only if it differs.</summary>
    private static PyDict Differences(LpStoreItemChange change)
    {
        var differences = new List<KeyValuePair<PyValue, PyValue>>();
        if (!change.Created && change.LocationId != change.PreviousLocationId)
            differences.Add(new(new PyInt(3), InventoryWire.Id(change.PreviousLocationId)));
        if (!change.Created && change.Quantity != change.PreviousQuantity)
            differences.Add(new(new PyInt(9), InventoryWire.Id(change.PreviousQuantity)));
        return new PyDict(differences);
    }

    private static long Positive(PyValue? value) => value switch
    {
        PyInt integer when integer.Value > 0 => integer.Value,
        PyLong integer when integer.Value > 0 => integer.Value,
        _ => 0,
    };
}
