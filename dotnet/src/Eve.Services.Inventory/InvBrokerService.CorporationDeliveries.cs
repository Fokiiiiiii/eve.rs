using Eve.Inventory;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;

namespace Eve.Services.Inventory;

public sealed partial class InvBrokerService
{
    [Method("DeliverToCorpHangar")]
    public async ValueTask<PyValue> DeliverToCorpHangar(ServiceCall call)
    {
        if (call.Session.CharacterId is not { } characterId
            || DockedLocationOf(call.Session) is not { } dockedStation
            || IdOf(call.Arg(0)) is not { } sourceLocation
            || IdOf(call.Arg(1)) != dockedStation
            || IdOf(call.Arg(4)) is not { } ownerId
            || IdOf(call.Arg(5)) is not { } destinationFlag
            || destinationFlag is < ItemFlags.CorpSAG1 or > ItemFlags.CorpSAG7)
            return None;

        var corporationId = CorporationIdOf(call.Session);
        if (corporationId <= 0 || ownerId != characterId && ownerId != corporationId
            || hasCorporationOffice is null
            || !await hasCorporationOffice(corporationId, dockedStation, call.Cancellation)
            || !await CorporationAccessAsync(characterId, corporationId, dockedStation,
                (int)destinationFlag, CorporationHangarOperation.Deposit, call.Cancellation))
            return None;

        var rawIds = call.Arg(2) switch
        {
            PyList list => list.Items,
            PyTuple tuple => tuple.Items,
            _ => [],
        };
        if (rawIds.Count is 0 or > MaxGetItemsIds)
            return None;
        var itemIds = rawIds.Select(IdOf).ToArray();
        if (itemIds.Any(id => id is null or <= 0))
            return None;

        var selected = new List<Item>(itemIds.Length);
        foreach (var itemId in itemIds.Select(id => id!.Value).Distinct())
        {
            if (await items.GetAsync(itemId, call.Cancellation) is not { } item
                || item.LocationId != sourceLocation || item.OwnerId != ownerId
                || ownerId == corporationId &&
                    !await CanAccessCorporationItemAsync(call.Session, item, CorporationHangarOperation.Take, call.Cancellation))
                return None;
            selected.Add(item);
        }

        var quantity = IdOf(call.Arg(3));
        if (quantity is <= 0 || quantity is { } count &&
                (selected.Count != 1 || count > selected[0].Quantity))
            return None;

        var boundId = $"N={MachoMessage.ProxyNodeId}:{BoundObjectIds.Next()}";
        _bound[boundId] = new Context(Kind.Hangar, dockedStation, call.Session.SessionId);
        try
        {
            var moved = 0;
            foreach (var item in selected)
            {
                var kwargs = new PyDict([
                    new(new PyStr("flag"), new PyInt((int)destinationFlag)),
                    new(new PyStr("qty"), quantity is { } requestedQty ? new PyLong(requestedQty) : PyNone.Instance),
                ]);
                var result = await Add(new ServiceCall
                {
                    Method = "Add",
                    Args = [InventoryWire.Id(item.Id), InventoryWire.Id(sourceLocation)],
                    Kwargs = kwargs,
                    Session = call.Session,
                    CallId = call.CallId,
                    BoundObjectId = boundId,
                    Cancellation = call.Cancellation,
                });
                if (result is not PyNone)
                    moved++;
            }
            return moved > 0 ? PyBool.True : None;
        }
        finally
        {
            _bound.TryRemove(boundId, out _);
        }
    }
}
