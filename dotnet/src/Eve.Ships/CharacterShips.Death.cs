using Eve.Characters;
using Eve.Dogma;
using Eve.Fitting;
using Eve.Inventory;

namespace Eve.Ships;

/// <summary>An item of a destroyed ship: it is in the wreck now (<see cref="Moved"/>: as it is there, and where it was) or it is gone (as it was).</summary>
public sealed record DeathChange(Item Item, long PreviousLocationId, int PreviousFlag, bool Destroyed);

/// <summary>What became of a ship that was destroyed: the capsule its pilot flies now, the wreck (if the ship left one) and where its contents went.</summary>
/// <param name="Ship">The ship as it was, in space; it is gone.</param>
/// <param name="Character">The pilot, whose ship is the capsule now.</param>
/// <param name="Capsule">The capsule, in space where the ship was.</param>
/// <param name="Wreck">The wreck, in space where the ship was; null when the ship left none.</param>
/// <param name="Changes">What happened to what the ship held, in the order the rules did it (a stack that dropped in part shows as the new stack that is in the wreck, and the rest as destroyed).</param>
/// <param name="Events">What dogma reports of the ship's modules leaving it: the ship's attributes that changed, and each module's, in the order of the slots.</param>
public sealed record ShipDeath(Item Ship, Character Character, Item Capsule, Item? Wreck, IReadOnlyList<DeathChange> Changes, IReadOnlyList<DogmaEvent> Events)
{
    /// <summary>What is in the wreck now.</summary>
    public IReadOnlyList<ItemMoved> Moved => [.. Changes.Where(c => !c.Destroyed).Select(c => new ItemMoved(c.Item, c.PreviousLocationId, c.PreviousFlag))];

    /// <summary>What is gone, as it was.</summary>
    public IReadOnlyList<Item> Destroyed => [.. Changes.Where(c => c.Destroyed).Select(c => c.Item)];
}

public sealed partial class CharacterShips
{
    /// <summary>The type of the capsule (<c>Capsule</c>) a pilot is left in when the ship is destroyed.</summary>
    public const int CapsuleTypeId = 670;

    /// <summary>
    /// The character's active ship, in space, is destroyed: what it held is dropped into a wreck or destroyed by the rules of the reference
    /// (<see cref="DeathOutcome"/>, decided by <paramref name="seed"/>), the ship is gone, and the character is in a capsule where the ship was.
    /// Null when the ship is not the character's active ship, or not in space, or is a capsule. Without a <paramref name="wreckTypeId"/> nothing is left and everything is destroyed.
    /// </summary>
    public async Task<ShipDeath?> DestroyShipAsync(long characterId, long shipId, int? wreckTypeId, string seed, CancellationToken cancellationToken = default)
    {
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockAsync(connection, transaction, characterId, cancellationToken);

        if (await characters.GetAsync(characterId, cancellationToken) is not { } character || character.ShipId != shipId
            || await items.GetAsync(shipId, cancellationToken) is not { } ship || ship.OwnerId != characterId
            || ship.LocationId != character.SolarSystemId || ship.Flag != InSpaceFlag || ship.TypeId == CapsuleTypeId)
        {
            return null; // a capsule is not left in a capsule: what becomes of a pilot whose capsule is destroyed is not done here
        }

        // what dogma says of the modules leaving: the ship before and after the fitted modules are taken off it
        var events = new List<DogmaEvent>();
        if (await LoadAsync(characterId, cancellationToken) is { } fitting && fitting.Ship.Id == ship.Id)
        {
            var world = fitting.World;
            var shipBefore = world.Attributes(ship.Id);
            var switchedOff = fitting.Fitted.Select(module => (module, Online: world.Get(module.Id, IsOnline))).ToList();
            foreach (var (module, _) in switchedOff)
            {
                world.Remove(module.Id);
            }
            events.AddRange(ShipChanges(ship.Id, shipBefore, world.Attributes(ship.Id)));
            foreach (var (module, online) in switchedOff)
            {
                if (Changed(module.Id, IsOnline, online, 0) is { } switched)
                {
                    events.Add(switched);
                }
                events.Add(new EffectChanged(module.Id, BuiltInEffects.Online, Started: false));
            }
        }

        Item? wreck = null;
        if (wreckTypeId is { } type)
        {
            var id = await items.CreateAsync(connection, transaction, new NewItem(type, characterId, ship.LocationId, InSpaceFlag, 1, true), cancellationToken);
            wreck = new Item(id, type, characterId, ship.LocationId, InSpaceFlag, 1, true, null);
        }

        var snapshot = new Dictionary<long, Item>();
        var tree = await TreeAsync(ship.Id, snapshot, cancellationToken);
        var actions = DeathOutcome.Resolve(tree, seed, new DeathOptions(ForceAllDestroyed: wreck is null));
        var changes = new List<DeathChange>();
        foreach (var action in actions)
        {
            var item = snapshot[action.ItemId];
            switch (action)
            {
                case DeathMove move when wreck is not null:
                    if (move.Quantity >= item.Quantity)
                    {
                        await items.MoveAsync(connection, transaction, item.Id, wreck.Id, ItemFlags.Hangar, cancellationToken);
                        changes.Add(new DeathChange(item with { LocationId = wreck.Id, Flag = ItemFlags.Hangar }, item.LocationId, item.Flag, false));
                        await modules.SetAsync(connection, transaction, item.Id, ItemState.Offline, cancellationToken);
                    }
                    else
                    {
                        // a stack that drops in part: the units that drop are a stack of their own in the wreck, what is left of the stack is destroyed by the next action
                        snapshot[item.Id] = item with { Quantity = item.Quantity - move.Quantity };
                        var stack = await items.CreateAsync(connection, transaction, new NewItem(item.TypeId, characterId, wreck.Id, ItemFlags.Hangar, move.Quantity, false, item.CustomName), cancellationToken);
                        changes.Add(new DeathChange(item with { Id = stack, LocationId = wreck.Id, Flag = ItemFlags.Hangar, Quantity = move.Quantity }, item.LocationId, item.Flag, false));
                    }
                    break;
                default:
                    await items.DeleteAsync(connection, transaction, item.Id, cancellationToken);
                    await modules.SetAsync(connection, transaction, item.Id, ItemState.Offline, cancellationToken);
                    changes.Add(new DeathChange(item, item.LocationId, item.Flag, true));
                    break;
            }
        }

        await items.DeleteAsync(connection, transaction, ship.Id, cancellationToken);
        var capsuleId = await items.CreateAsync(connection, transaction, new NewItem(CapsuleTypeId, characterId, ship.LocationId, InSpaceFlag, 1, true), cancellationToken);
        var capsule = new Item(capsuleId, CapsuleTypeId, characterId, ship.LocationId, InSpaceFlag, 1, true, null);
        await characters.SetShipAsync(connection, transaction, characterId, capsuleId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        // what the dead ship and what is destroyed had taken is not kept
        await modules.SetShipConditionAsync(ship.Id, ShipCondition.Full, cancellationToken);
        await modules.SetModuleConditionsAsync(changes.Where(c => c.Destroyed).ToDictionary(c => c.Item.Id, _ => ModuleCondition.Untouched), cancellationToken);
        return new ShipDeath(ship, character with { ShipId = capsuleId }, capsule, wreck, changes, events);
    }

    /// <summary>What is in a place and in what is in it, as the death rules look at it (the ids of the snapshot are those of the items).</summary>
    private async Task<IReadOnlyList<DeathNode>> TreeAsync(long locationId, Dictionary<long, Item> snapshot, CancellationToken cancellationToken, int depth = 0)
    {
        if (depth > 32)
        {
            throw new InvalidOperationException("DEATH_OUTCOME_CONTAINMENT_INVALID");
        }
        var nodes = new List<DeathNode>();
        foreach (var item in await items.InContainerAsync(locationId, cancellationToken))
        {
            if (!snapshot.TryAdd(item.Id, item))
            {
                throw new InvalidOperationException("DEATH_OUTCOME_CONTAINMENT_INVALID");
            }
            nodes.Add(new DeathNode(item.Id, item.TypeId, item.Flag, item.Singleton, Math.Max(1, item.Quantity), await TreeAsync(item.Id, snapshot, cancellationToken, depth + 1)));
        }
        return nodes;
    }
}
