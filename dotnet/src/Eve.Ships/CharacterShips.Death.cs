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
public sealed record AbandonedShipDeath(Item Ship, Item? Wreck, IReadOnlyList<DeathChange> Changes);

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
    public async Task<AbandonedShipDeath?> DestroyAbandonedShipAsync(
        long ownerCharacterId, int solarSystemId, long shipId, int? wreckTypeId, string seed,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockAsync(connection, transaction, ownerCharacterId, cancellationToken);

        if (await characters.GetAsync(ownerCharacterId, cancellationToken) is not { } character
            || character.ShipId == shipId
            || await items.LockAndGetAsync(connection, transaction, shipId, cancellationToken) is not { } ship
            || ship.OwnerId != ownerCharacterId
            || ship.LocationId != solarSystemId
            || ship.Flag != InSpaceFlag)
            return null;

        Item? wreck = null;
        if (wreckTypeId is { } type)
        {
            var id = await items.CreateAsync(connection, transaction,
                new NewItem(type, ownerCharacterId, ship.LocationId, InSpaceFlag, 1, true), cancellationToken);
            wreck = new Item(id, type, ownerCharacterId, ship.LocationId, InSpaceFlag, 1, true, null);
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
                        snapshot[item.Id] = item with { Quantity = item.Quantity - move.Quantity };
                        var stack = await items.CreateAsync(connection, transaction,
                            new NewItem(item.TypeId, ownerCharacterId, wreck.Id, ItemFlags.Hangar, move.Quantity, false, item.CustomName), cancellationToken);
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
        await transaction.CommitAsync(cancellationToken);
        await modules.SetShipConditionAsync(ship.Id, ShipCondition.Full, cancellationToken);
        await modules.SetModuleConditionsAsync(
            changes.Where(change => change.Destroyed).ToDictionary(change => change.Item.Id, _ => ModuleCondition.Untouched),
            cancellationToken);
        return new AbandonedShipDeath(ship, wreck, changes);
    }

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

    /// <summary>The character, as the stores have it.</summary>
    public Task<Character?> CharacterAsync(long characterId, CancellationToken cancellationToken = default) => characters.GetAsync(characterId, cancellationToken);

    /// <summary>
    /// The character's capsule, in space, is destroyed: the pilot wakes in the clone at the home station in a rookie ship (with the starter modules of its kind),
    /// and what is left in space is the pilot's corpse (a space item of <paramref name="corpseTypeId"/> named <paramref name="corpseName"/>) where the capsule was.
    /// Null when the ship is not the character's capsule in space. A port of what <c>destroySessionCapsuleToHomeStation</c> of EVE.js does to the items.
    /// </summary>
    public async Task<PodDeath?> DestroyCapsuleAsync(
        long characterId, long capsuleId, int? corpseTypeId, string corpseName, int rookieShipTypeId, HomePlace home, CancellationToken cancellationToken = default)
    {
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockAsync(connection, transaction, characterId, cancellationToken);

        if (await characters.GetAsync(characterId, cancellationToken) is not { } character || character.ShipId != capsuleId
            || await items.GetAsync(capsuleId, cancellationToken) is not { } capsule || capsule.OwnerId != characterId || capsule.TypeId != CapsuleTypeId
            || capsule.LocationId != character.SolarSystemId || capsule.Flag != InSpaceFlag)
        {
            return null;
        }

        Item? corpse = null;
        if (corpseTypeId is { } type)
        {
            var id = await items.CreateAsync(connection, transaction, new NewItem(type, characterId, capsule.LocationId, InSpaceFlag, 1, true, corpseName), cancellationToken);
            corpse = new Item(id, type, characterId, capsule.LocationId, InSpaceFlag, 1, true, corpseName);
        }
        // a pod holds nothing, and what somehow is in it is destroyed rather than left where it is about to vanish
        var held = new List<Item>();
        foreach (var item in await items.InContainerAsync(capsule.Id, cancellationToken))
        {
            await items.DeleteAsync(connection, transaction, item.Id, cancellationToken);
            await modules.SetAsync(connection, transaction, item.Id, ItemState.Offline, cancellationToken);
            held.Add(item);
        }
        await items.DeleteAsync(connection, transaction, capsule.Id, cancellationToken);

        var shipId = await items.CreateAsync(connection, transaction, new NewItem(rookieShipTypeId, characterId, home.StationId, ItemFlags.Hangar, 1, true), cancellationToken);
        var ship = new Item(shipId, rookieShipTypeId, characterId, home.StationId, ItemFlags.Hangar, 1, true, null);
        var fitted = new List<Item>();
        foreach (var module in StarterKits.For(rookieShipTypeId))
        {
            var moduleId = await items.CreateAsync(connection, transaction, new NewItem(module.TypeId, characterId, shipId, module.Flag, 1, true), cancellationToken);
            await modules.SetAsync(connection, transaction, moduleId, ItemState.Online, cancellationToken);
            fitted.Add(new Item(moduleId, module.TypeId, characterId, shipId, module.Flag, 1, true, null));
        }
        await characters.SetShipAsync(connection, transaction, characterId, shipId, cancellationToken);
        await characters.SetStationAsync(connection, transaction, characterId, home.StationId, cancellationToken);
        if (home.SolarSystemId != character.SolarSystemId)
        {
            await characters.SetLocationAsync(connection, transaction, characterId, home.SolarSystemId, home.ConstellationId, home.RegionId, cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);

        await modules.SetShipConditionAsync(capsule.Id, ShipCondition.Full, cancellationToken);
        var woke = character with
        {
            ShipId = shipId, StationId = home.StationId, SolarSystemId = home.SolarSystemId, ConstellationId = home.ConstellationId, RegionId = home.RegionId,
        };
        return new PodDeath(capsule, corpse, woke, ship, fitted, held);
    }
}

/// <summary>Where a pilot wakes up: the station of its clone and where that is.</summary>
public sealed record HomePlace(long StationId, int SolarSystemId, int ConstellationId, int RegionId);

/// <summary>What became of a capsule that was destroyed: the corpse it left in space, and the character in a rookie ship at the home station.</summary>
/// <param name="Capsule">The capsule as it was, in space; it is gone.</param>
/// <param name="Corpse">The pilot's corpse, in space where the capsule was; null when no kind of corpse was given.</param>
/// <param name="Character">The pilot, at the home station, in <paramref name="Ship"/>.</param>
/// <param name="Ship">The rookie ship in the hangar of the home station.</param>
/// <param name="Modules">The modules fitted to the rookie ship.</param>
/// <param name="Held">What was in the capsule, which is destroyed with it.</param>
public sealed record PodDeath(Item Capsule, Item? Corpse, Character Character, Item Ship, IReadOnlyList<Item> Modules, IReadOnlyList<Item> Held);

