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
public sealed record AbandonedShipDeath(Item Ship, Item? Wreck, IReadOnlyList<DeathChange> Changes, Item? Capsule = null);

public sealed record ShipDeath(
    Item Ship,
    Character Character,
    Item? Capsule,
    Item? EscapeShip,
    Item? Wreck,
    IReadOnlyList<DeathChange> Changes,
    IReadOnlyList<DogmaEvent> Events)
{
    public Item PilotShip => EscapeShip ?? Capsule ?? throw new InvalidOperationException("ship death has no surviving pilot hull");

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
    public async Task<ShipDeath?> DestroyShipAsync(
        long characterId,
        long shipId,
        int? wreckTypeId,
        string seed,
        CancellationToken cancellationToken = default,
        RunSpaceItemScope? runSpaceScope = null)
    {
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockAsync(connection, transaction, characterId, cancellationToken);

        var character = await characters.GetAsync(characterId, cancellationToken);
        var ship = await items.LockAndGetAsync(connection, transaction, shipId, cancellationToken);
        if (character is null || character.ShipId != shipId
            || ship is null || ship.OwnerId != characterId
            || ship.LocationId != character.SolarSystemId || ship.Flag != InSpaceFlag || ship.TypeId == CapsuleTypeId)
        {
            return null;
        }
        if (runSpaceScope is not null)
        {
            if (wreckTypeId is null)
                throw new ArgumentException("A run-space wreck scope requires a wreck type.", nameof(runSpaceScope));
            ValidateRunSpaceScope(runSpaceScope, characterId, ship.LocationId, requireSettlementKey: true);
        }

        var events = new List<DogmaEvent>();
        var fitting = await LoadAsync(characterId, cancellationToken);
        if (fitting is not null && fitting.Ship.Id == ship.Id)
        {
            var world = fitting.World;
            var shipBefore = world.Attributes(ship.Id);
            var switchedOff = fitting.Fitted.Select(module => (module, Online: world.Get(module.Id, IsOnline))).ToList();
            foreach (var (module, _) in switchedOff)
                world.Remove(module.Id);
            events.AddRange(ShipChanges(ship.Id, shipBefore, world.Attributes(ship.Id)));
            foreach (var (module, online) in switchedOff)
            {
                if (Changed(module.Id, IsOnline, online, 0) is { } switched)
                    events.Add(switched);
                events.Add(new EffectChanged(module.Id, BuiltInEffects.Online, Started: false));
            }
        }

        Item? escapeShip = fitting is not null && fitting.Ship.Id == ship.Id
            ? await EscapeBayShipAsync(ship, fitting.World, characterId, cancellationToken)
            : null;
        if (escapeShip is not null)
        {
            if (await items.LockAndGetAsync(connection, transaction, escapeShip.Id, cancellationToken) is not { } lockedEscape
                || lockedEscape.OwnerId != characterId || lockedEscape.LocationId != ship.Id
                || lockedEscape.Flag != FrigateEscapeBayFlag || !lockedEscape.Singleton || lockedEscape.Quantity != 1)
                escapeShip = null;
            else
                escapeShip = lockedEscape;
        }

        Item? wreck = null;
        if (wreckTypeId is { } type)
        {
            var provenance = runSpaceScope is null
                ? null
                : new RunSpaceItemProvenance(runSpaceScope, RunSpaceItemRole.Wreck, characterId, ship.Id, ship.Id);
            var id = await items.CreateAsync(
                connection,
                transaction,
                new NewItem(type, characterId, ship.LocationId, InSpaceFlag, 1, true)
                {
                    RunSpaceProvenance = provenance,
                },
                cancellationToken);
            wreck = new Item(id, type, characterId, ship.LocationId, InSpaceFlag, 1, true, null)
            {
                RunSpaceProvenance = provenance,
            };
        }

        var snapshot = new Dictionary<long, Item>();
        var tree = await TreeAsync(ship.Id, snapshot, cancellationToken);
        if (escapeShip is not null)
        {
            var escapeContents = new Dictionary<long, Item>();
            await TreeAsync(escapeShip.Id, escapeContents, cancellationToken);
            if (escapeContents.Values.Any(item => item.OwnerId != characterId))
                escapeShip = null;
            else
            {
                foreach (var id in escapeContents.Keys)
                    snapshot.Remove(id);
                snapshot.Remove(escapeShip.Id);
                tree = [.. tree.Where(node => node.ItemId != escapeShip.Id)];
            }
        }

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
        Item? capsule = null;
        var pilotShipId = escapeShip?.Id ?? 0;
        if (escapeShip is not null)
        {
            await items.MoveAsync(connection, transaction, escapeShip.Id, ship.LocationId, InSpaceFlag, cancellationToken);
        }
        else
        {
            var capsuleId = await items.CreateAsync(connection, transaction, new NewItem(CapsuleTypeId, characterId, ship.LocationId, InSpaceFlag, 1, true), cancellationToken);
            capsule = new Item(capsuleId, CapsuleTypeId, characterId, ship.LocationId, InSpaceFlag, 1, true, null);
            pilotShipId = capsuleId;
        }
        await characters.SetShipAsync(connection, transaction, characterId, pilotShipId, cancellationToken);
        await WriteTransitionAsync(connection, transaction,
            new ShipTransitionEvent("ship-destroyed", characterId, ship.Id, pilotShipId,
                ship.LocationId, InSpaceFlag, ship.LocationId, InSpaceFlag), cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        await modules.SetShipConditionAsync(ship.Id, ShipCondition.Full, cancellationToken);
        await modules.SetModuleConditionsAsync(changes.Where(c => c.Destroyed).ToDictionary(c => c.Item.Id, _ => ModuleCondition.Untouched), cancellationToken);
        return new ShipDeath(ship, character with { ShipId = pilotShipId }, capsule, escapeShip, wreck, changes, events);
    }

    /// <summary>What is in a place and in what is in it, as the death rules look at it (the ids of the snapshot are those of the items).</summary>
    public async Task<AbandonedShipDeath?> DestroyAbandonedShipAsync(
        long ownerCharacterId, int solarSystemId, long shipId, int? wreckTypeId, string seed,
        CancellationToken cancellationToken = default, bool replaceActiveShip = false)
    {
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockAsync(connection, transaction, ownerCharacterId, cancellationToken);

        if (await characters.GetAsync(ownerCharacterId, cancellationToken) is not { } character
            || await items.LockAndGetAsync(connection, transaction, shipId, cancellationToken) is not { } ship
            || ship.OwnerId != ownerCharacterId
            || ship.LocationId != solarSystemId
            || ship.Flag != InSpaceFlag)
            return null;
        var wasActiveShip = character.ShipId == shipId;
        if (wasActiveShip && (!replaceActiveShip || ship.TypeId == CapsuleTypeId || character.SolarSystemId != solarSystemId))
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
        Item? capsule = null;
        if (wasActiveShip)
        {
            var capsuleId = await items.CreateAsync(connection, transaction,
                new NewItem(CapsuleTypeId, ownerCharacterId, ship.LocationId, InSpaceFlag, 1, true), cancellationToken);
            capsule = new Item(capsuleId, CapsuleTypeId, ownerCharacterId, ship.LocationId, InSpaceFlag, 1, true, null);
            await characters.SetShipAsync(connection, transaction, ownerCharacterId, capsuleId, cancellationToken);
            await WriteTransitionAsync(connection, transaction,
                new ShipTransitionEvent("ship-destroyed", ownerCharacterId, ship.Id, capsuleId,
                    ship.LocationId, InSpaceFlag, ship.LocationId, InSpaceFlag), cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        await modules.SetShipConditionAsync(ship.Id, ShipCondition.Full, cancellationToken);
        await modules.SetModuleConditionsAsync(
            changes.Where(change => change.Destroyed).ToDictionary(change => change.Item.Id, _ => ModuleCondition.Untouched),
            cancellationToken);
        return new AbandonedShipDeath(ship, wreck, changes, capsule);
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
        long characterId,
        long capsuleId,
        int? corpseTypeId,
        string corpseName,
        int rookieShipTypeId,
        HomePlace home,
        CancellationToken cancellationToken = default,
        RunSpaceItemScope? runSpaceScope = null)
    {
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockAsync(connection, transaction, characterId, cancellationToken);

        var character = await characters.GetAsync(characterId, cancellationToken);
        var capsule = await items.LockAndGetAsync(connection, transaction, capsuleId, cancellationToken);
        if (character is null || character.ShipId != capsuleId
            || capsule is null || capsule.OwnerId != characterId || capsule.TypeId != CapsuleTypeId
            || capsule.LocationId != character.SolarSystemId || capsule.Flag != InSpaceFlag)
        {
            return null;
        }
        if (runSpaceScope is not null)
        {
            if (corpseTypeId is null)
                throw new ArgumentException("A run-space corpse scope requires a corpse type.", nameof(runSpaceScope));
            ValidateRunSpaceScope(runSpaceScope, characterId, capsule.LocationId, requireSettlementKey: true);
        }

        Item? corpse = null;
        if (corpseTypeId is { } type)
        {
            var provenance = runSpaceScope is null
                ? null
                : new RunSpaceItemProvenance(runSpaceScope, RunSpaceItemRole.Corpse, characterId, capsule.Id, capsule.Id);
            var id = await items.CreateAsync(
                connection,
                transaction,
                new NewItem(type, characterId, capsule.LocationId, InSpaceFlag, 1, true, corpseName)
                {
                    RunSpaceProvenance = provenance,
                },
                cancellationToken);
            corpse = new Item(id, type, characterId, capsule.LocationId, InSpaceFlag, 1, true, corpseName)
            {
                RunSpaceProvenance = provenance,
            };
        }
        var implantsInCharacter = await items.InContainerAsync(connection, transaction, characterId, cancellationToken);
        var implantCandidates = implantsInCharacter
            .Where(item => item.OwnerId == characterId && item.LocationId == characterId && item.Flag == PilotAugmentations.ImplantFlag)
            .ToArray();
        var destroyedImplants = new List<Item>();
        if (implantCandidates.Length > 0)
        {
            var nonDestructibleAttribute = statics.AttributeId("nonDestructible")
                ?? throw new InvalidOperationException("SDE attribute nonDestructible is missing");
            await statics.EnsureTypesAsync(implantCandidates.Select(item => item.TypeId).Distinct(), cancellationToken);
            foreach (var candidate in implantCandidates)
            {
                var implant = await items.LockAndGetAsync(connection, transaction, candidate.Id, cancellationToken);
                if (implant is null || implant.OwnerId != characterId || implant.LocationId != characterId
                    || implant.Flag != PilotAugmentations.ImplantFlag)
                    continue;
                var implantType = statics.Type(implant.TypeId)
                    ?? throw new InvalidOperationException($"SDE type {implant.TypeId} is missing");
                if (!PilotAugmentations.IsImplantType(implantType) || implantType.Attributes.GetValueOrDefault(nonDestructibleAttribute) > 0)
                    continue;
                await items.DeleteAsync(connection, transaction, implant.Id, cancellationToken);
                await modules.SetAsync(connection, transaction, implant.Id, ItemState.Offline, cancellationToken);
                destroyedImplants.Add(implant);
            }
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
        await WriteTransitionAsync(connection, transaction,
            new ShipTransitionEvent("capsule-destroyed", characterId, capsule.Id, shipId,
                capsule.LocationId, InSpaceFlag, home.StationId, ItemFlags.Hangar), cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        await modules.SetShipConditionAsync(capsule.Id, ShipCondition.Full, cancellationToken);
        var woke = character with
        {
            ShipId = shipId, StationId = home.StationId, SolarSystemId = home.SolarSystemId, ConstellationId = home.ConstellationId, RegionId = home.RegionId,
        };
        return new PodDeath(capsule, corpse, woke, ship, fitted, held, destroyedImplants);
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
/// <param name="Implants">The active implants destroyed with the capsule; implants marked non-destructible survive.</param>
public sealed record PodDeath(Item Capsule, Item? Corpse, Character Character, Item Ship, IReadOnlyList<Item> Modules, IReadOnlyList<Item> Held, IReadOnlyList<Item> Implants);

