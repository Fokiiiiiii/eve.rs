using System.Diagnostics.CodeAnalysis;
using Eve.Dogma;
using Eve.Inventory;
using Eve.Inventory.Mutation;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Ships;
using Eve.Ships;

namespace Eve.Host;

/// <summary>
/// <c>dynamicItemService</c>: the crafting window's mutation (<c>CreateDynamicItem</c>, ADR 0016) and the stored record of a mutated item
/// (<c>GetDynamicItemInfo</c>). The mutation is the <see cref="ItemMutationService"/>'s; refusals are the client's <c>CustomNotify</c> errors;
/// the inventory changes are announced as <c>OnItemChange</c> before the reply, in the order the mutation made them.
/// </summary>
public sealed class DynamicItemRpcService : IService
{
    private readonly PgItemStore _items;
    private readonly ItemMutationService _mutations;
    private readonly DogmaStatic _dogma;
    private readonly ItemTypeCatalog _itemTypes;
    private readonly CharacterShips? _ships;

    /// <param name="ships">The ships of the characters: an item fitted or bayed in a ship is read in that ship's world. Null only where no item is in a ship.</param>
    public DynamicItemRpcService(PgItemStore items, ItemMutationService mutations, DogmaStatic dogma, ItemTypeCatalog itemTypes, CharacterShips? ships)
        => (_items, _mutations, _dogma, _itemTypes, _ships) = (items, mutations, dogma, itemTypes, ships);

    public string Name => "dynamicItemService";
    public bool HasMethod(string method) => method is "CreateDynamicItem" or "GetDynamicItemInfo";

    public ValueTask<PyValue> CallAsync(ServiceCall call)
    {
        call.Cancellation.ThrowIfCancellationRequested();
        return call.Method switch
        {
            "CreateDynamicItem" => CreateAsync(call),
            "GetDynamicItemInfo" => GetDynamicItemInfoAsync(call),
            _ => ValueTask.FromResult<PyValue>(PyNone.Instance),
        };
    }

    private async ValueTask<PyValue> CreateAsync(ServiceCall call)
    {
        var characterId = call.Session.CharacterId ?? 0;
        if (characterId <= 0) Refuse("You need an active character to mutate an item.");
        var mutatorId = Long(call.Arg(0));
        var sourceId = Long(call.Arg(1));
        var source = mutatorId > 0 && sourceId > 0 ? await _items.GetAsync(sourceId, call.Cancellation) : null;
        if (source is null) Refuse(Describe(ItemMutationRefusal.SourceMissing));

        // both items are asked for in the place the source is in: a mutaplasmid elsewhere is refused by the authority
        var outcome = await _mutations.MutateAsync(new ItemMutationRequest(characterId, source.LocationId, mutatorId, sourceId), call.Cancellation);
        switch (outcome)
        {
            case ItemMutationRefused refused:
                Refuse(Describe(refused.Reason));
                break;
            case ItemMutationAccepted accepted:
                await AnnounceAsync(call, accepted.Receipt.Changes);
                return new PyInt(accepted.Receipt.ItemId);
        }
        return PyNone.Instance; // not reached: every outcome is accepted or refused above
    }

    /// <summary>The changes of a mutation as the client is told them, in order (the mutaplasmid, the source, the item made).</summary>
    private async Task AnnounceAsync(ServiceCall call, IReadOnlyList<ItemMutationChange> changes)
    {
        var types = await _itemTypes.GetAsync(changes.Select(change => change.After.TypeId).Distinct().ToArray(), call.Cancellation);
        ItemRow Row(Item item) => types.TryGetValue(item.TypeId, out var type)
            ? new ItemRow(item.Id, item.TypeId, item.OwnerId, item.LocationId, item.Flag, item.Quantity, type.GroupId, type.CategoryId)
            : new ItemRow(item.Id, item.TypeId, item.OwnerId, item.LocationId, item.Flag, item.Quantity, 0, 0);
        foreach (var change in changes)
        {
            ShipNotifications.SendItemChange(call.Session,
                new Eve.Ships.ItemChanged(change.After, change.PreviousLocationId, change.PreviousFlag, change.PreviousQuantity), Row);
        }
    }

    private async ValueTask<PyValue> GetDynamicItemInfoAsync(ServiceCall call)
    {
        var itemId = Long(call.Arg(0));
        if (itemId <= 0 || await _items.GetAsync(itemId, call.Cancellation) is not { } item
            || await _items.Mutations.GetAsync(item.Id, call.Cancellation) is not { } record)
            return PyNone.Instance; // only a mutated item has dynamic information: nothing is made up for any other

        // the effective attributes are the server's own dogma for the item: what the client reads as the item's attributes
        await _dogma.EnsureTypesAsync([record.SourceTypeId, item.TypeId], call.Cancellation);
        var world = await WorldOfAsync(item, record, call.Cancellation);

        return Py.KeyVal(
            ("itemID", new PyInt(item.Id)),
            ("typeID", new PyInt(item.TypeId)),
            ("sourceTypeID", new PyInt(record.SourceTypeId)),
            ("sourceItemID", new PyInt(record.SourceItemId)),
            ("mutatorTypeID", new PyInt(record.MutatorTypeId)),
            ("mutatorItemID", new PyInt(record.MutatorItemId)),
            ("characterID", new PyInt(record.CharacterId)),
            ("createdAtMs", new PyInt(record.CreatedAtMs)),
            ("attributes", NumericDict(world.Attributes(item.Id))),
            ("modifiers", NumericDict(record.Rolls)),
            ("baseAttributes", NumericDict(record.BaseValues)));
    }

    /// <summary>
    /// The world the item's attributes are read in. An item fitted to a ship, or in its bays and tubes, is read in that ship's world, so the ship's modifiers
    /// apply to it as they do to the fit and to a launch (DEVIATIONS 143). Any other item is read in its own world, where no ship acts on it.
    /// </summary>
    private async Task<DogmaWorld> WorldOfAsync(Item item, ItemMutationRecord record, CancellationToken cancellationToken)
    {
        if (_ships is not null && await _items.GetAsync(item.LocationId, cancellationToken) is { } container && container.OwnerId == item.OwnerId)
        {
            await _dogma.EnsureTypesAsync([container.TypeId], cancellationToken);
            if (_dogma.Type(container.TypeId) is { } containerType && containerType.CategoryId == (_dogma.CategoryId("Ship") ?? 6)
                && await _ships.LoadShipAsync(item.OwnerId, container.Id, cancellationToken) is { } ship)
            {
                if (ship.World.Item(item.Id) is null)
                {
                    ship.World.Add(ItemDogma.Of(item, item.OwnerId, item.LocationId, item.Flag, mutation: record));
                }
                return ship.World;
            }
        }
        var world = new DogmaWorld(_dogma);
        world.Add(ItemDogma.Of(item, item.OwnerId, item.LocationId, item.Flag, mutation: record));
        return world;
    }

    /// <summary>The client's reason for a refusal: the text its own mutation uses where the reference has one (dynamicItemService.js:393-416).</summary>
    private static string Describe(ItemMutationRefusal reason) => reason switch
    {
        ItemMutationRefusal.MutatorMissing or ItemMutationRefusal.SourceMissing => "The selected mutaplasmid or source item could not be found.",
        ItemMutationRefusal.SameItem => "The mutaplasmid and source item must be different inventory items.",
        ItemMutationRefusal.NotOwner => "You can only mutate items owned by the active character.",
        ItemMutationRefusal.NotInReach => "The mutaplasmid and the source item must be in the same station or ship.",
        ItemMutationRefusal.NotStowed => "The source item must be unfitted and kept in a hangar or cargo hold.",
        ItemMutationRefusal.NotSingleton => "Only a single assembled module can be mutated, not a stack.",
        ItemMutationRefusal.ItemLocked => "A blueprint-locked item cannot be mutated.",
        ItemMutationRefusal.NotAMutaplasmid => "That item is not a supported mutaplasmid.",
        ItemMutationRefusal.InputNotMappable => "That mutaplasmid cannot mutate the selected source item.",
        ItemMutationRefusal.AttributeMissingInSource => "The selected source item lacks an attribute that this mutaplasmid changes.",
        _ => "The selected item could not be mutated.",
    };

    private static PyDict NumericDict(IReadOnlyDictionary<int, double> values) => new(values
        .OrderBy(pair => pair.Key)
        .Select(pair => new KeyValuePair<PyValue, PyValue>(new PyInt(pair.Key), Py.Number(pair.Value)))
        .ToArray());

    private static long Long(PyValue value) => value switch { PyInt i => i.Value, PyLong l => l.Value, PyReal r => (long)r.Value, _ => 0 };

    [DoesNotReturn]
    private static void Refuse(string message) => throw MachoErrors.ThrowUserError("CustomNotify", ("notify", new PyStr(message)));
}
