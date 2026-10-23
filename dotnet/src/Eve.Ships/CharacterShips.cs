using Eve.Characters;
using Eve.Dogma;
using Eve.Fitting;
using Eve.Inventory;
using Eve.Skills;
using Npgsql;

namespace Eve.Ships;

/// <summary>
/// A character's ship as a whole: what is in it, what is fitted to it, and what its modules are doing. A change to it runs in one
/// transaction across the modules that own the parts (ADR 0003): the item moves, the module state is kept, and the answer says what
/// dogma reports of the change. Only the character's active ship is handled.
/// </summary>
public sealed partial class CharacterShips(
    NpgsqlDataSource source, DogmaStatic statics, PgCharacterStore characters, PgItemStore items, PgSkillStore skills, PgModuleStateStore modules,
    CharacterTraining? training = null, ExpertSystems? expertSystems = null)
{
    /// <summary>The inventory flags a ship's contents can be in besides its slots: the cargo hold.</summary>
    public const int CargoFlag = 5;

    private const int IsOnline = 2;
    private const int ChargeCategory = 8;
    private const double Tolerance = 1e-9;

    /// <summary>How fast the character learns as dogma shows it: its attributes times the server's training speed (the client works its estimates out from them).</summary>
    private Func<int, double>? LearningOf(long characterId) =>
        training is null ? null : attribute => training.Settings.Attribute(characterId, attribute) * training.Settings.Speed;

    /// <summary>The skill levels the character's installed expert systems grant now (none without expert systems in this host).</summary>
    private async Task<IReadOnlyDictionary<int, int>?> GrantsOfAsync(long characterId, CancellationToken cancellationToken) =>
        expertSystems is null ? null : await expertSystems.GrantsAsync(characterId, cancellationToken);

    /// <summary>The character's active ship, or null when it has none. What the character's training has finished is settled into its skills first.</summary>
    public Task<ShipSnapshot?> LoadAsync(long characterId, CancellationToken cancellationToken = default) =>
        LoadSelectedShipAsync(characterId, null, cancellationToken);

    public Task<ShipSnapshot?> LoadShipAsync(long characterId, long shipId, CancellationToken cancellationToken = default) =>
        LoadSelectedShipAsync(characterId, shipId, cancellationToken);

    private async Task<ShipSnapshot?> LoadSelectedShipAsync(long characterId, long? shipId, CancellationToken cancellationToken, long? shipOwnerIdOverride = null)
    {
        if (training is not null && await characters.GetAsync(characterId, cancellationToken) is { } owner)
        {
            await training.SettleAsync(characterId, owner.AccountId, cancellationToken);
        }
        return await ShipSnapshot.LoadAsync(statics, characters, items, skills, modules, characterId, LearningOf(characterId), null, cancellationToken, shipId, shipOwnerIdOverride, await GrantsOfAsync(characterId, cancellationToken));
    }

    /// <summary>
    /// What changed in the character's ship when its skills went from <paramref name="before"/> to what they are now: the ship's attributes
    /// that differ, in the order of the attributes.
    /// </summary>
    public async Task<IReadOnlyList<AttributeChanged>> SkillChangesAsync(long characterId, IReadOnlyList<CharacterSkill> before, IReadOnlyDictionary<int, int>? grantsBefore = null, CancellationToken cancellationToken = default)
    {
        var grants = await GrantsOfAsync(characterId, cancellationToken);
        var now = await ShipSnapshot.LoadAsync(statics, characters, items, skills, modules, characterId, LearningOf(characterId), null, cancellationToken, expertGrants: grants);
        var was = await ShipSnapshot.LoadAsync(statics, characters, items, skills, modules, characterId, LearningOf(characterId), before, cancellationToken, expertGrants: grantsBefore ?? grants);
        return now is null || was is null ? [] : [.. ShipChanges(now.Ship.Id, was.World.Attributes(was.Ship.Id), now.World.Attributes(now.Ship.Id))];
    }

    /// <summary>One character's changes to its ships and items happen one after the other.</summary>
    private static async Task LockAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long characterId, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("SELECT pg_advisory_xact_lock($1)", connection, transaction) { Parameters = { new() { Value = characterId } } };
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>A refusal about a skill names the skill, so its type is made known to the static data.</summary>
    private async Task NameTheSkillAsync(FitFailure failure, CancellationToken cancellationToken)
    {
        if (failure.Error == FitError.SkillRequired && failure.Data.TryGetValue("skillTypeID", out var skill))
        {
            await statics.EnsureTypesAsync([(int)skill], cancellationToken);
        }
    }

    private static bool Differs(double? old, double now) => old is not { } value || Math.Abs(value - now) > Tolerance * Math.Max(1d, Math.Abs(now));

    /// <summary>The ship's attributes that changed between two states, in the order of the attributes.</summary>
    private static IEnumerable<AttributeChanged> ShipChanges(long shipId, IReadOnlyDictionary<int, double> before, IReadOnlyDictionary<int, double> after) =>
        after.Keys.Union(before.Keys).Order()
            .Where(id => after.TryGetValue(id, out var now) && Differs(before.TryGetValue(id, out var old) ? old : null, now))
            .Select(id => new AttributeChanged(shipId, id, before.TryGetValue(id, out var old) ? old : null, after[id]));

    private static AttributeChanged? Changed(long itemId, int attribute, double before, double after) =>
        Math.Abs(before - after) > Tolerance ? new AttributeChanged(itemId, attribute, before, after) : null;

    /// <summary>
    /// Moves an item the character owns to the ship it flies or to the hangar of the station the ship is in: fitting it (in the slot asked
    /// for, or the first that suits), taking it out of its slot, or putting it in the cargo hold.
    /// </summary>
    public async Task<ShipOutcome> MoveAsync(
        long characterId, long itemId, long sourceLocationId, long destinationLocationId, int? flag, long? quantity, CancellationToken cancellationToken = default, long? sourceOwnerId = null, long? destinationOwnerId = null)
    {
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockAsync(connection, transaction, characterId, cancellationToken);

        var activeSnapshot = await LoadAsync(characterId, cancellationToken);
        if (activeSnapshot is null
            || await items.LockAndGetAsync(connection, transaction, itemId, cancellationToken) is not { } item
            || (item.OwnerId != characterId && item.OwnerId != sourceOwnerId) || item.Id == activeSnapshot.Ship.Id)
        {
            return new NothingHappened();
        }

        var activeShip = activeSnapshot.Ship;
        var station = activeShip.LocationId;
        var selectedShipId = destinationLocationId != station && destinationLocationId != activeShip.Id
            ? destinationLocationId
            : sourceLocationId != station && sourceLocationId != activeShip.Id
                ? sourceLocationId
                : activeShip.Id;
        var snapshot = activeSnapshot;
        if (selectedShipId != activeShip.Id)
        {
            if (await items.LockAndGetAsync(connection, transaction, selectedShipId, cancellationToken) is not { } selectedShip
                || (selectedShip.OwnerId != characterId && selectedShip.OwnerId != destinationOwnerId)
                || selectedShip.LocationId != station
                || (selectedShip.Flag != ItemFlags.Hangar && !ItemFlags.IsCorporationHangar(selectedShip.Flag))
                || selectedShip.Quantity != 1)
            {
                return new NothingHappened();
            }
            await statics.EnsureTypesAsync([selectedShip.TypeId], cancellationToken);
            if (statics.Type(selectedShip.TypeId)?.CategoryId != 6)
            {
                return new NothingHappened();
            }
            snapshot = await LoadSelectedShipAsync(characterId, selectedShipId, cancellationToken, selectedShip.OwnerId);
            if (snapshot is null)
            {
                return new NothingHappened();
            }
        }

        var ship = snapshot.Ship;
        if (item.Id == ship.Id)
        {
            return new NothingHappened();
        }
        var inShip = item.LocationId == ship.Id;
        var inHangar = item.LocationId == station;
        var inContainer = !inShip && !inHangar && activeSnapshot.Character.StationId == station
            && await IsOwnedDockedInventoryAsync(item.LocationId, sourceOwnerId ?? characterId, station, cancellationToken);
        var toShip = destinationLocationId == ship.Id;
        var toHangar = destinationLocationId == station;
        if ((!inShip && !inHangar && !inContainer) || (!toShip && !toHangar) || (sourceLocationId != item.LocationId && !(inShip && sourceLocationId == station)))
        {
            return new NothingHappened();
        }
        if (quantity is { } asked && !item.Singleton && (asked <= 0 || asked > item.Quantity))
        {
            return new NothingHappened();
        }
        var splits = quantity is { } some && !item.Singleton && some < item.Quantity;
        if (inShip && !ship.Singleton)
        {
            return new NothingHappened();
        }
        if (inShip && Slots.FamilyOfFlag(item.Flag) == SlotFamily.Rig && !toShip &&
            (statics.Type(ship.TypeId) is not { } shipType || !FittingRules.CanUnfitRigWithoutDestruction(shipType)))
        {
            return new NothingHappened(); // rigs on other hulls remain non-removable here
        }

        var unpack = toShip && !ship.Singleton;
        var provenance = await items.Mutations.GetAsync(item.Id, cancellationToken);
        await statics.EnsureTypesAsync(ItemDogma.TypeIdsOf(item, provenance), cancellationToken);
        var world = snapshot.World;
        var type = statics.Type(item.TypeId);
        var wasInSlot = inShip && Slots.IsSlotFlag(item.Flag);
        var state = wasInSlot ? world.Item(item.Id)?.State ?? ItemState.Offline : ItemState.Offline;
        var shipBefore = world.Attributes(ship.Id);
        var moduleBefore = wasInSlot ? world.Get(item.Id, IsOnline) : 0d;
        var cargoCapacityAttribute = CapacityAttributeOf(CargoFlag);
        var cargoCapacityBefore = cargoCapacityAttribute is { } cargoAttribute ? world.Get(ship.Id, cargoAttribute) : 0d;

        // the item is held in the hangar, out of any slot, while it is worked out where it goes
        world.Remove(item.Id);
        var candidate = world.Add(ItemDogma.Of(item, characterId, station, ItemFlags.Hangar, mutation: provenance));

        int destinationFlag;
        if (toHangar)
        {
            if (flag is { } requestedHangarFlag && ItemFlags.IsCorporationHangar(requestedHangarFlag))
            {
                if (destinationOwnerId is not { } corporationId || corporationId <= 0 || corporationId == characterId)
                    return new NothingHappened();
                destinationFlag = requestedHangarFlag;
            }
            else
            {
                if (flag is not null and not 0 && flag != ItemFlags.Hangar)
                    return new NothingHappened();
                if (destinationOwnerId is { } unexpectedOwner && unexpectedOwner != characterId)
                    return new NothingHappened();
                destinationFlag = ItemFlags.Hangar;
            }
        }
        else if (flag is { } slot and > 0 && Slots.IsSlotFlag(slot))
        {
            if (FittingRules.Check(world, statics, ship.Id, candidate, slot) is { } failure)
            {
                await NameTheSkillAsync(failure, cancellationToken);
                return new ShipRefused(ShipRefusal.CannotFit, failure, item.TypeId, ship.TypeId);
            }
            destinationFlag = slot;
        }
        else if (flag is null or 0 && type is not null && Slots.FamilyOfType(type) is not null && type.CategoryId != ChargeCategory)
        {
            if (FittingRules.FirstFreeSlot(world, statics, ship.Id, candidate) is not { } free)
            {
                return new ShipRefused(ShipRefusal.NoSuitableSlot, null, item.TypeId, ship.TypeId);
            }
            destinationFlag = free;
        }
        else if (flag == DroneBayFlag && toShip)
        {
            if (type?.CategoryId != DroneCategory)
            {
                return new ShipRefused(ShipRefusal.NotADrone, ModuleTypeId: item.TypeId, ShipTypeId: ship.TypeId); // checked before the room, as the reference does
            }
            destinationFlag = DroneBayFlag;
        }
        else if (toShip && flag is { } hold && IsShipHoldFlag(hold))
        {
            if (!CanAddToShipHold(hold, type, item.Singleton, item.Quantity))
                return new ShipRefused(ShipRefusal.NotAllowedInHold, ModuleTypeId: item.TypeId, ShipTypeId: ship.TypeId);
            destinationFlag = hold;
        }
        else if (flag is null or 0 || flag == CargoFlag)
        {
            destinationFlag = CargoFlag;
        }
        else
        {
            return new NothingHappened();
        }

        if (toShip && (destinationFlag is ShipMaintenanceBayFlag or FrigateEscapeBayFlag)
            && !await ShipContentsAllowedInBayAsync(item, destinationFlag, world, characterId, cancellationToken))
            return new ShipRefused(ShipRefusal.NotAllowedInHold, ModuleTypeId: item.TypeId, ShipTypeId: ship.TypeId);

        var destinationItemOwnerId = destinationOwnerId ?? characterId;

        // what the ship is once the item is where it goes
        var landsInSlot = toShip && Slots.IsSlotFlag(destinationFlag);
        if (splits && landsInSlot)
        {
            return new NothingHappened(); // a stack of modules is not split into a slot
        }
        if (toShip && IsShipHoldFlag(destinationFlag) && !(inShip && item.Flag == destinationFlag)
            && await RefuseWhatDoesNotFitAsync(snapshot, item, splits ? quantity!.Value : item.Quantity, destinationFlag, cancellationToken) is { } full)
        {
            return full;
        }
        var newState = wasInSlot && landsInSlot ? state : ItemState.Offline;
        world.Remove(item.Id);
        if (landsInSlot)
        {
            world.Add(ItemDogma.Of(item, characterId, ship.Id, destinationFlag, newState, provenance));
        }
        if (wasInSlot && cargoCapacityAttribute is { } cargoAttributeAfter)
        {
            var addedCargoVolume = landsInSlot && destinationFlag == CargoFlag
                ? await VolumeOfAsync([item], cancellationToken)
                : 0d;
            if (await RefuseCargoCapacityLossAsync(
                    snapshot,
                    cargoCapacityBefore,
                    world.Get(ship.Id, cargoAttributeAfter),
                    addedCargoVolume,
                    cancellationToken) is { } cargoRefusal)
            {
                return cargoRefusal;
            }
        }
        var shipAfter = world.Attributes(ship.Id);

        if (unpack)
        {
            await items.MakeSingletonAsync(connection, transaction, ship.Id, cancellationToken);
        }

        // part of a stack is split off into a stack of its own where it goes; a whole stack goes as it is
        Item? split = null;
        if (splits)
        {
            split = await items.MoveStackAsync(connection, transaction, item, quantity!.Value, destinationItemOwnerId, destinationLocationId, destinationFlag, cancellationToken);
        }
        else
        {
            if (item.OwnerId == destinationItemOwnerId)
                await items.MoveAsync(connection, transaction, item.Id, destinationLocationId, destinationFlag, cancellationToken);
            else
                await items.MoveToOwnerAsync(connection, transaction, item.Id, destinationItemOwnerId, destinationLocationId, destinationFlag, cancellationToken);
        }
        if (wasInSlot || landsInSlot)
        {
            await modules.SetAsync(connection, transaction, item.Id, newState, cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);

        var events = new List<DogmaEvent>(ShipChanges(ship.Id, shipBefore, shipAfter));
        if (wasInSlot)
        {
            var moduleAfter = landsInSlot ? (newState >= ItemState.Online ? 1d : 0d) : 0d;
            if (Changed(item.Id, IsOnline, moduleBefore, moduleAfter) is { } switched)
            {
                events.Add(switched);
            }
            if (!landsInSlot)
            {
                // the reference says the online effect stops whether or not it was running
                events.Add(new EffectChanged(item.Id, BuiltInEffects.Online, Started: false));
            }
        }
        ItemChanged? assembled = unpack
            ? new ItemChanged(ship with { Singleton = true, Quantity = 1 }, ship.LocationId, ship.Flag, ship.Quantity)
            : null;
        if (!wasInSlot && !landsInSlot)
        {
            var stacks = new List<ItemChanged>();
            if (assembled is not null) stacks.Add(assembled);
            if (split is { } created)
                stacks.AddRange([new(item with { Quantity = item.Quantity - created.Quantity }, item.LocationId, item.Flag, item.Quantity), new(created, 0, created.Flag, 0)]);
            else
                stacks.Add(new(item with { OwnerId = destinationItemOwnerId, LocationId = destinationLocationId, Flag = destinationFlag }, item.LocationId, item.Flag, item.Quantity));
            return new ShipChanged(split?.Id ?? item.Id, ship.Id, [], events, stacks);
        }
        var moved = new ItemMoved(item with { OwnerId = destinationItemOwnerId, LocationId = destinationLocationId, Flag = destinationFlag }, item.LocationId, item.Flag);
        return new ShipChanged(item.Id, ship.Id, [moved], events, assembled is null ? null : [assembled]);
    }

    /// <summary>Repackages eligible items in the active station hangar.</summary>
    public async Task<(IReadOnlyList<ShipChanged> Changes, IReadOnlyList<long> ConfirmationItemIds)> RepackageItemsAsync(
        long characterId,
        IReadOnlyList<RepackageItemRequest> requests,
        bool destroyable,
        CancellationToken cancellationToken = default)
    {
        if (characterId <= 0 || requests.Count == 0) return ([], []);
        var active = await LoadAsync(characterId, cancellationToken);
        if (active is null || active.Character.StationId <= 0) return ([], []);
        var unique = requests.Where(request => request.ItemId > 0).DistinctBy(request => request.ItemId).ToArray();

        if (!destroyable)
        {
            var confirm = new List<long>();
            foreach (var request in unique)
                if (await NeedsRigConfirmationAsync(characterId, active, request, cancellationToken))
                    confirm.Add(request.ItemId);
            if (confirm.Count > 0) return ([], confirm);
        }

        var changes = new List<ShipChanged>();
        var confirmations = new List<long>();
        foreach (var request in unique)
        {
            var (change, confirmationRequired) = await RepackageItemAsync(characterId, request, destroyable, cancellationToken);
            if (change is not null) changes.Add(change);
            if (confirmationRequired) confirmations.Add(request.ItemId);
        }
        return (changes, confirmations);
    }

    private async Task<bool> NeedsRigConfirmationAsync(
        long characterId,
        ShipSnapshot active,
        RepackageItemRequest request,
        CancellationToken cancellationToken)
    {
        var stationId = active.Character.StationId;
        if (request.StationId != stationId || request.OwnerId != characterId
            || request.LocationId != stationId || request.Flag != ItemFlags.Hangar
            || await items.GetAsync(request.ItemId, cancellationToken) is not { } item
            || item.OwnerId != characterId || item.LocationId != stationId
            || item.Flag != ItemFlags.Hangar || !item.Singleton || item.Id == active.Ship.Id)
            return false;

        await statics.EnsureTypesAsync([item.TypeId], cancellationToken);
        var type = statics.Type(item.TypeId);
        if (type is null || type.CategoryId != 6 ||
            string.Equals(statics.GroupName(type.GroupId), "Strategic Cruiser", StringComparison.OrdinalIgnoreCase))
            return false;

        var ship = await LoadShipAsync(characterId, item.Id, cancellationToken);
        if (ship is null || ship.Condition.Damage > 0 || ship.Condition.ArmorDamage > 0 || ship.Condition.Incapacitated)
            return false;
        var contents = await items.InContainerAsync(item.Id, cancellationToken);
        return contents.All(content => content.OwnerId == characterId)
            && contents.Any(content => Slots.FamilyOfFlag(content.Flag) == SlotFamily.Rig);
    }

    private async Task<(ShipChanged? Change, bool ConfirmationRequired)> RepackageItemAsync(
        long characterId,
        RepackageItemRequest request,
        bool destroyable,
        CancellationToken cancellationToken)
    {
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockAsync(connection, transaction, characterId, cancellationToken);

        var active = await LoadAsync(characterId, cancellationToken);
        if (active is null || active.Character.StationId <= 0 || request.StationId != active.Character.StationId)
            return (null, false);

        var item = await items.LockAndGetAsync(connection, transaction, request.ItemId, cancellationToken);
        if (item is null || item.OwnerId != request.OwnerId
            || item.LocationId != request.LocationId || item.Flag != request.Flag || !item.Singleton)
            return (null, false);

        var personalHangar = item.OwnerId == characterId
            && item.LocationId == request.StationId && item.Flag == ItemFlags.Hangar;
        var corporationHangar = item.OwnerId != characterId
            && ItemFlags.IsCorporationHangar(item.Flag);
        if (!personalHangar && !corporationHangar)
            return (null, false);

        await statics.EnsureTypesAsync([item.TypeId], cancellationToken);
        var type = statics.Type(item.TypeId);
        if (type is null) return (null, false);
        var category = statics.CategoryName(type.CategoryId);
        var isShip = string.Equals(category, "Ship", StringComparison.OrdinalIgnoreCase);
        var isDrone = string.Equals(category, "Drone", StringComparison.OrdinalIgnoreCase);
        if (corporationHangar &&
            (isShip || string.Equals(category, "Blueprint", StringComparison.OrdinalIgnoreCase)))
            return (null, false);
        if (isShip && (item.OwnerId != characterId || item.Id == active.Ship.Id))
            return (null, false);

        var contents = await items.LockContainerAsync(connection, transaction, item.Id, cancellationToken);
        if (contents.Any(content => content.OwnerId != item.OwnerId))
            return (null, false);

        ShipSnapshot? ship = null;
        var returnRigs = false;
        if (isShip)
        {
            ship = await LoadShipAsync(characterId, item.Id, cancellationToken);
            if (ship is null || ship.Condition.Damage > 0 || ship.Condition.ArmorDamage > 0 || ship.Condition.Incapacitated)
                return (null, false);
            returnRigs = string.Equals(statics.GroupName(type.GroupId), "Strategic Cruiser", StringComparison.OrdinalIgnoreCase);
            if (!destroyable && !returnRigs && contents.Any(content => Slots.FamilyOfFlag(content.Flag) == SlotFamily.Rig))
                return (null, true);
        }
        else
        {
            if (contents.Count != 0)
                return (null, false);
            var conditions = await modules.ModuleConditionsAsync([item.Id], cancellationToken);
            if (conditions.TryGetValue(item.Id, out var condition)
                && (condition.Damage > 0 || (isDrone && condition.ArmorDamage > 0)))
                return (null, false);
        }

        if (!isShip)
        {
            var packaged = await items.RepackageAsync(connection, transaction, item.Id, ship: false, cancellationToken);
            if (packaged is null) return (null, false);
            await transaction.CommitAsync(cancellationToken);
            return (new ShipChanged(item.Id, active.Ship.Id, [], [],
                [new ItemChanged(packaged, item.LocationId, item.Flag, item.Quantity)]), false);
        }

        var world = ship!.World;
        var shipBefore = world.Attributes(item.Id);
        var movedItems = new List<ItemMoved>();
        var stoppedEffects = new List<DogmaEvent>();
        foreach (var content in contents)
        {
            var family = Slots.FamilyOfFlag(content.Flag);
            var slotted = Slots.IsSlotFlag(content.Flag);
            if (world.Item(content.Id) is not null)
                world.Remove(content.Id);
            if (slotted || content.Flag == ItemFlags.HiddenModifiers)
                await modules.SetAsync(connection, transaction, content.Id, ItemState.Offline, cancellationToken);
            if (slotted)
                stoppedEffects.Add(new EffectChanged(content.Id, BuiltInEffects.Online, Started: false));

            var destroy = content.Flag == ItemFlags.HiddenModifiers
                || (!returnRigs && family == SlotFamily.Rig);
            if (destroy)
            {
                await items.DeleteAsync(connection, transaction, content.Id, cancellationToken);
                movedItems.Add(new ItemMoved(
                    content with { LocationId = JunkLocation },
                    content.LocationId,
                    content.Flag));
            }
            else
            {
                await items.MoveAsync(connection, transaction, content.Id,
                    active.Character.StationId, ItemFlags.Hangar, cancellationToken);
                movedItems.Add(new ItemMoved(
                    content with { OwnerId = characterId, LocationId = active.Character.StationId, Flag = ItemFlags.Hangar },
                    content.LocationId,
                    content.Flag));
            }
        }

        var events = new List<DogmaEvent>(ShipChanges(item.Id, shipBefore, world.Attributes(item.Id)));
        events.AddRange(stoppedEffects);
        var packagedShip = await items.RepackageAsync(connection, transaction, item.Id, ship: true, cancellationToken);
        if (packagedShip is null) return (null, false);
        await transaction.CommitAsync(cancellationToken);
        return (new ShipChanged(
            item.Id,
            item.Id,
            movedItems,
            events,
            [new ItemChanged(packagedShip, item.LocationId, item.Flag, item.Quantity)]), false);
    }

    /// <summary>Checks a batch that takes fitted modules out of the active ship before its per-item moves begin.</summary>
    public async Task<ShipRefused?> CheckBatchUnfitCargoCapacityAsync(
        long characterId,
        long shipId,
        IReadOnlyCollection<long> itemIds,
        bool toCargo,
        CancellationToken cancellationToken = default)
    {
        if (itemIds.Count == 0)
            return null;

        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockAsync(connection, transaction, characterId, cancellationToken);
        if (await LoadShipAsync(characterId, shipId, cancellationToken) is not { } snapshot
            || CapacityAttributeOf(CargoFlag) is not { } capacityAttribute)
            return null;

        var leaving = snapshot.Fitted
            .Where(item => itemIds.Contains(item.Id)
                && Slots.IsSlotFlag(item.Flag)
                && statics.Type(item.TypeId)?.CategoryId != ChargeCategory)
            .ToArray();
        if (leaving.Length == 0)
            return null;

        var world = snapshot.World;
        var capacityBefore = world.Get(shipId, capacityAttribute);
        foreach (var item in leaving)
            world.Remove(item.Id);
        var addedCargoVolume = toCargo ? await VolumeOfAsync(leaving, cancellationToken) : 0d;
        return await RefuseCargoCapacityLossAsync(
            snapshot,
            capacityBefore,
            world.Get(shipId, capacityAttribute),
            addedCargoVolume,
            cancellationToken);
    }

    /// <summary>The attribute that caps how many modules of one group may be online at once (the SDE's <c>maxGroupOnline</c>).</summary>
    private const int MaxGroupOnlineAttribute = 978;

    /// <summary>
    /// A module that would be the one too many of its group online: the group's limit is the lowest <c>maxGroupOnline</c> of the modules fitted in it (a module without
    /// one does not limit), and the modules of the group that are online already count against it. The reference refuses the online with
    /// <c>CannotOnlineReachedMaxGroupOnline</c> after its processor and power checks (<c>locationDogmaItem.py</c> CheckCanOnlineItem).
    /// </summary>
    internal static ShipRefused? MaxGroupOnlineRefusal(IReadOnlyList<Eve.Inventory.Item> fitted, int shipTypeId, DogmaStatic statics, DogmaWorld world, Eve.Inventory.Item module)
    {
        var group = statics.Type(module.TypeId)?.GroupId ?? 0;
        if (group <= 0)
        {
            return null;
        }
        var members = fitted.Where(m => (statics.Type(m.TypeId)?.GroupId ?? 0) == group).ToArray();
        var limits = members.Select(m => world.Get(m.Id, MaxGroupOnlineAttribute)).Where(limit => limit > 0).ToArray();
        if (limits.Length == 0)
        {
            return null;
        }
        var limit = (int)Math.Floor(limits.Min());
        var online = members.Count(m => m.Id != module.Id && world.Item(m.Id) is { State: >= ItemState.Online });
        return online >= limit
            ? new ShipRefused(ShipRefusal.MaxGroupOnline, null, module.TypeId, shipTypeId, GroupId: group, MaxGroupOnline: limit)
            : null;
    }

    /// <summary>Switches a module fitted to the character's ship on or off.</summary>
    public async Task<ShipOutcome> SetOnlineAsync(long characterId, long shipId, long moduleId, bool online, CancellationToken cancellationToken = default)
    {
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockAsync(connection, transaction, characterId, cancellationToken);

        if (await LoadShipAsync(characterId, shipId, cancellationToken) is not { } snapshot)
        {
            return new ShipRefused(ShipRefusal.NotFitted);
        }
        var world = snapshot.World;
        if (snapshot.Fitted.FirstOrDefault(i => i.Id == moduleId) is not { } module || world.Item(moduleId) is not { } fitted)
        {
            return new ShipRefused(ShipRefusal.NotFitted);
        }
        var events = new List<DogmaEvent>();
        var wasOnline = fitted.State >= ItemState.Online;
        var cargoCapacityAttribute = CapacityAttributeOf(CargoFlag);
        var cargoCapacityBefore = cargoCapacityAttribute is { } capacityAttribute ? world.Get(shipId, capacityAttribute) : 0d;
        if (online && !wasOnline)
        {
            if (Slots.FamilyOfFlag(module.Flag) == SlotFamily.Rig)
            {
                return new ShipRefused(ShipRefusal.NotFitted, ModuleTypeId: module.TypeId, ShipTypeId: snapshot.Ship.TypeId); // rigs have nothing to switch
            }
            if (statics.Type(module.TypeId) is { } type && FittingRules.MissingSkill(world, characterId, type) is { } missing)
            {
                await NameTheSkillAsync(missing, cancellationToken);
                return new ShipRefused(ShipRefusal.SkillRequired, missing, module.TypeId, snapshot.Ship.TypeId);
            }
            if (FittingRules.OnlineShortage(world, shipId, moduleId) is { } shortage)
            {
                return new ShipRefused(shortage == FitError.NotEnoughCpu ? ShipRefusal.NotEnoughCpu : ShipRefusal.NotEnoughPower, null, module.TypeId, snapshot.Ship.TypeId);
            }
            if (MaxGroupOnlineRefusal(snapshot.Fitted, snapshot.Ship.TypeId, statics, world, module) is { } limited)
            {
                return limited;
            }
        }

        if (online == wasOnline)
        {
            // asking for what already is: the client is told the effect runs, nothing else changes
            if (online)
            {
                events.Add(new EffectChanged(moduleId, BuiltInEffects.Online, Started: true));
            }
            return new ShipChanged(moduleId, shipId, [], events);
        }

        var shipBefore = world.Attributes(shipId);
        var moduleBefore = world.Get(moduleId, IsOnline);
        world.SetState(moduleId, online ? ItemState.Online : ItemState.Offline);
        if (!online && cargoCapacityAttribute is { } capacityAttributeAfter
            && await RefuseCargoCapacityLossAsync(
                snapshot,
                cargoCapacityBefore,
                world.Get(shipId, capacityAttributeAfter),
                addedCargoVolume: 0,
                cancellationToken) is { } cargoRefusal)
        {
            return cargoRefusal;
        }
        var shipAfter = world.Attributes(shipId);
        await modules.SetAsync(connection, transaction, moduleId, online ? ItemState.Online : ItemState.Offline, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        if (Changed(moduleId, IsOnline, moduleBefore, world.Get(moduleId, IsOnline)) is { } switched)
        {
            events.Add(switched);
        }
        events.AddRange(ShipChanges(shipId, shipBefore, shipAfter));
        events.Add(new EffectChanged(moduleId, BuiltInEffects.Online, Started: online));
        return new ShipChanged(moduleId, shipId, [], events);
    }
}
