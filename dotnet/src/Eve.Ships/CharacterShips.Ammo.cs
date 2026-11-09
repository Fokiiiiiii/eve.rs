using Eve.Dogma;
using Eve.Fitting;
using Eve.Inventory;
using Npgsql;

namespace Eve.Ships;

/// <summary>
/// Ammunition: the rounds a docked ship's modules hold. A charge in a module is an item of the ship whose flag is the flag of the module's slot; the stack it was
/// loaded from stays where it was, smaller (or goes, when the module took it whole). The rules and the order of what the client is told are the reference's
/// (<c>Handle_LoadAmmo</c> and <c>Handle_UnloadAmmo</c> of <c>dogmaService/moduleOperation.js</c>, ADR 0009), checked against the recording
/// <c>oracle/fixtures/scenarios/ammo.json</c>. In space the rounds come from the hold and a load takes the module's reload time (the recording
/// <c>oracle/fixtures/scenarios/reload.json</c>): what is asked is planned (<see cref="PlanReloadsAsync"/>) and done when the time has run (<see cref="CompleteReloadAsync"/>);
/// an unload takes no time.
/// </summary>
public sealed partial class CharacterShips
{
    /// <summary>Where the client is told a stack that went into a module is gone to (the reference's junk location).</summary>
    public const long JunkLocation = 6;

    private const double AttributeTolerance = 1e-9;

    /// <summary>What a row lock finds when a row is not what the work read (the market's custody says the same).</summary>
    public const string ItemChanged = "ITEM_CHANGED";

    /// <summary>How many times a load or an unload starts over when the rows it read were changed under it, before it gives up.</summary>
    private const int AmmoAttempts = 4;

    /// <summary>Runs an operation on the items again, with fresh reads, while it finds the rows changed under it by something else.</summary>
    private static async Task<ShipOutcome> WithFreshRowsAsync(Func<Task<ShipOutcome>> operation)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await operation();
            }
            catch (InvalidOperationException error) when (error.Message == ItemChanged)
            {
                if (attempt >= AmmoAttempts)
                {
                    return new NothingHappened();
                }
            }
        }
    }

    private sealed class AmmoWork(
        PgItemStore items, NpgsqlConnection connection, NpgsqlTransaction transaction, ShipSnapshot snapshot, Dictionary<long, Item> stacks,
        long sourceOwnerId, long? destinationOwnerId, CancellationToken cancellationToken)
    {
        public List<AmmoEvent> Events { get; } = [];

        public bool TouchesFitting { get; set; }

        /// <summary>The stacks of the place the charges come from or go to, as the work has left them.</summary>
        public Dictionary<long, Item> Stacks { get; } = stacks;

        /// <summary>The charges in the ship's slots, by the slot's flag, as the work has left them.</summary>
        public Dictionary<int, Item> Charges { get; } = snapshot.Charges.ToDictionary(c => c.Flag);

        public ShipSnapshot Snapshot => snapshot;

        /// <summary>The ship is in a station (the fitting of a docked ship is told again when a charge changes; a ship in space is told nothing of it).</summary>
        public bool Docked => snapshot.Ship.Flag == ItemFlags.Hangar;

        public DogmaWorld World => snapshot.World;

        /// <summary>The owner of the inventory location the requested stacks came from.</summary>
        public long SourceOwnerId { get; } = sourceOwnerId;

        /// <summary>The owner of a container destination; ordinary unloads retain the loaded charge's owner.</summary>
        public long? DestinationOwnerId { get; } = destinationOwnerId;

        /// <summary>
        /// the market's custody (<c>PgItemStore.LockAndGetAsync</c>, then the expected row against the locked one): a stack that a sale or another load moved or
        /// resized since it was read is <see cref="ItemChanged"/>, and nothing of the work is kept.
        /// </summary>
        public async Task LockAsync(IEnumerable<Item> expected)
        {
            foreach (var item in expected.DistinctBy(i => i.Id).OrderBy(i => i.Id))
            {
                if (await items.LockAndGetAsync(connection, transaction, item.Id, cancellationToken) != item)
                {
                    throw new InvalidOperationException(ItemChanged);
                }
            }
        }

        public async Task SetQuantityAsync(Item item, long quantity) =>
            await items.SetQuantityAsync(connection, transaction, item.Id, quantity, cancellationToken);

        public async Task<long> CreateAsync(NewItem item) => await items.CreateAsync(connection, transaction, item, cancellationToken);

        public async Task MoveAsync(Item item, long ownerId, long locationId, int flag) => await items.MoveToOwnerAsync(connection, transaction, item.Id, ownerId, locationId, flag, cancellationToken);

        public async Task DeleteAsync(Item item) => await items.DeleteAsync(connection, transaction, item.Id, cancellationToken);

        /// <summary>The ship's world now holds this charge in the slot (or none): what dogma reports of the module follows.</summary>
        public void Hold(Item module, Item? charge)
        {
            if (Charges.TryGetValue(module.Flag, out var old))
            {
                World.Remove(old.Id);
                Charges.Remove(module.Flag);
            }
            if (charge is not null)
            {
                Charges[module.Flag] = charge;
                World.Add(ItemDogma.Of(charge, charge.OwnerId, charge.LocationId, charge.Flag));
                World.SetValue(charge.Id, ShipSnapshot.QuantityCharge, charge.Quantity);
            }
        }
    }

    private static (int Type, long Quantity) StateOf(Item? charge) => charge is null ? (0, 0) : (charge.TypeId, charge.Quantity);

    /// <summary>The module's reload time as dogma has it, in whole milliseconds (0 when the module has none).</summary>
    private static int ReloadMs(DogmaWorld world, long moduleId)
    {
        var reload = world.Get(moduleId, AmmoRules.ReloadTimeAttribute);
        return double.IsFinite(reload) && reload > 0 ? (int)Math.Round(reload, MidpointRounding.AwayFromZero) : 0;
    }

    private static IReadOnlyList<AttributeChanged> ModuleChanges(long moduleId, IReadOnlyDictionary<int, double> before, IReadOnlyDictionary<int, double> after) =>
        [.. before.Keys.Union(after.Keys).Order().Select(attribute => (attribute, was: before.GetValueOrDefault(attribute), now: after.GetValueOrDefault(attribute)))
            .Where(c => Math.Abs(c.now - c.was) > AttributeTolerance).Select(c => new AttributeChanged(moduleId, c.attribute, c.was, c.now))];

    /// <summary>What the client is told when the rounds in a module's slot are another number or another kind (<c>_notifyChargeQuantityTransition</c>).</summary>
    private static void TellTransition(AmmoWork work, Item module, (int Type, long Quantity) before, (int Type, long Quantity) after, Item? charge, IReadOnlyDictionary<int, double> moduleBefore)
    {
        var ship = work.Snapshot.Ship.Id;
        if (before != after)
        {
            if (before.Type > 0)
            {
                work.Events.Add(new ChargeQuantityChanged(ship, module.Flag, before.Type, before.Type == after.Type ? after.Quantity : 0, before.Quantity));
            }
            if (after.Type > 0 && after.Type != before.Type && charge is not null)
            {
                var attributes = new Dictionary<int, double>(work.World.Attributes(charge.Id)) { [ShipSnapshot.QuantityCharge] = 0 };
                // every item is told its mass, capacity, volume and radius, with these when its type has none
                foreach (var (attribute, value) in new[] { (4, 0d), (38, 0d), (161, 0d), (162, 1d) })
                {
                    attributes.TryAdd(attribute, value);
                }
                work.Events.Add(new ChargePrimed(ship, module.Flag, after.Type, attributes));
                work.Events.Add(new ChargeQuantityChanged(ship, module.Flag, after.Type, after.Quantity, 0));
            }
        }
        if (ModuleChanges(module.Id, moduleBefore, work.World.Attributes(module.Id)) is { Count: > 0 } changes)
        {
            work.Events.Add(new ModuleAttributesChanged(changes));
        }
    }

    /// <summary>
    /// A loaded charge goes to a place (<c>_moveLoadedChargeToDestination</c>): into the first stack of its kind there, or, when there is none, into a new stack.
    /// <paramref name="quantity"/> rounds go (all of them when it is null).
    /// </summary>
    private static async Task UnloadAsync(AmmoWork work, Item module, Item charge, long locationId, int flag, long? quantity)
    {
        var wanted = quantity is { } asked ? Math.Max(1, Math.Min(charge.Quantity, asked)) : charge.Quantity;
        var target = work.Stacks.Values.Where(s => s.Id != charge.Id && s.TypeId == charge.TypeId && s.LocationId == locationId && s.Flag == flag).OrderBy(s => s.Id).FirstOrDefault();
        if (target is not null)
        {
            var grown = target with { Quantity = target.Quantity + wanted };
            await work.SetQuantityAsync(target, grown.Quantity);
            work.Stacks[target.Id] = grown;
            work.Events.Add(new ItemChanged(grown, target.LocationId, target.Flag, target.Quantity));
        }
        else
        {
            var ownerId = work.DestinationOwnerId ?? charge.OwnerId;
            var created = charge with
            {
                Id = await work.CreateAsync(new NewItem(charge.TypeId, ownerId, locationId, flag, wanted)),
                OwnerId = ownerId,
                LocationId = locationId,
                Flag = flag,
                Quantity = wanted,
            };
            work.Stacks[created.Id] = created;
            work.Events.Add(new ItemChanged(created, 0, 0, created.Quantity));
        }
        if (wanted >= charge.Quantity)
        {
            await work.DeleteAsync(charge);
            work.Hold(module, null);
        }
        else
        {
            var left = charge with { Quantity = charge.Quantity - wanted };
            await work.SetQuantityAsync(charge, left.Quantity);
            work.Hold(module, left);
        }
        work.TouchesFitting = true;
    }

    /// <summary>The hangar or the cargo hold of the ship the charges come from or go to, as the place and the flag, or null for any other.</summary>
    private static (long Location, int Flag)? PlaceOf(ShipSnapshot snapshot, long locationId) =>
        locationId == snapshot.Ship.Id ? (locationId, CargoFlag) : snapshot.Ship.Flag == ItemFlags.Hangar && locationId == snapshot.Ship.LocationId ? (locationId, ItemFlags.Hangar) : null;

    /// <summary>Where the ship is when rounds are loaded or unloaded: in a station, in space, or either (a reload that was asked for in one place and ends in the other).</summary>
    private enum AmmoWhere
    {
        Station,
        Space,
        Either,
    }

    private async Task<AmmoWork?> BeginAmmoAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, long characterId, long shipId, long locationId, AmmoWhere where,
        CancellationToken cancellationToken, int? destinationFlag = null, long? destinationOwnerId = null, bool containerDestination = false,
        long? sourceOwnerId = null, bool containerSource = false)
    {
        await LockAsync(connection, transaction, characterId, cancellationToken);
        if (await LoadAsync(characterId, cancellationToken) is not { } snapshot || snapshot.Ship.Id != shipId
            || (snapshot.Ship.Flag != ItemFlags.Hangar && snapshot.Ship.Flag != InSpaceFlag)
            || (where == AmmoWhere.Station && snapshot.Ship.Flag != ItemFlags.Hangar) || (where == AmmoWhere.Space && snapshot.Ship.Flag != InSpaceFlag))
        {
            return null; // the ship is not where the call is for
        }
        var place = PlaceOf(snapshot, locationId);
        if (place is null && !containerDestination && !containerSource)
        {
            return null;
        }
        if (containerDestination && snapshot.Ship.Flag != ItemFlags.Hangar)
        {
            return null;
        }
        var inventoryOwnerId = containerSource ? sourceOwnerId ?? characterId
            : containerDestination ? destinationOwnerId ?? characterId
            : characterId;
        IReadOnlyList<Item>? sourceContainerContents = null;
        if (containerSource)
        {
            if (sourceOwnerId is not { } ownerId
                || await items.LockAndGetAsync(connection, transaction, locationId, cancellationToken) is not { } container
                || container.OwnerId != ownerId || !container.Singleton || container.Quantity != 1
                || !await IsCargoContainerItemAsync(container, cancellationToken)
                || !await IsAmmoContainerBesideShipAsync(connection, transaction, container, ownerId, snapshot, cancellationToken))
            {
                return null;
            }
            sourceContainerContents = await items.LockContainerAsync(connection, transaction, locationId, cancellationToken);
        }
        var location = place?.Location ?? locationId;
        var flag = containerSource ? ContainerFlag : place?.Flag ?? destinationFlag ?? ContainerFlag;
        var stacks = (sourceContainerContents ?? await items.InLocationAsync(location, inventoryOwnerId, cancellationToken))
            .Where(i => i.OwnerId == inventoryOwnerId && i.Flag == flag && !i.Singleton).ToDictionary(i => i.Id);
        return new AmmoWork(items, connection, transaction, snapshot, stacks, inventoryOwnerId,
            containerDestination || containerSource ? inventoryOwnerId : null, cancellationToken);
    }

    /// <summary>Checks that an ammo container is a reachable personal or corporation container beside the character's ship.</summary>
    public async Task<bool> CanReachAmmoContainerAsync(
        long characterId, long shipId, long containerId, long ownerId, CancellationToken cancellationToken = default)
    {
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockAsync(connection, transaction, characterId, cancellationToken);
        if (await LoadAsync(characterId, cancellationToken) is not { } snapshot || snapshot.Ship.Id != shipId
            || await items.LockAndGetAsync(connection, transaction, containerId, cancellationToken) is not { } container
            || container.OwnerId != ownerId || !container.Singleton || container.Quantity != 1
            || !await IsCargoContainerItemAsync(container, cancellationToken)
            || !await IsAmmoContainerBesideShipAsync(connection, transaction, container, ownerId, snapshot, cancellationToken))
        {
            return false;
        }
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    /// <summary>
    /// Takes up to <paramref name="needed"/> rounds of the kind from the stacks (in order) into the module's slot: into the charge that is there (<paramref name="charge"/>), or into a
    /// new one. A stack that is taken whole is told as gone; the others as smaller. Returns the charge the module holds then, and how many rounds are still missing.
    /// </summary>
    private static async Task<(Item? Charge, long Missing)> TakeRoundsAsync(
        AmmoWork work, Item module, Item? charge, int typeId, long characterId, long shipId, long needed, IReadOnlyList<Item> sources)
    {
        foreach (var stack in sources)
        {
            if (needed <= 0)
            {
                break;
            }
            var have = work.Stacks[stack.Id];
            var take = Math.Min(needed, have.Quantity);
            if (charge is not null)
            {
                charge = charge with { Quantity = charge.Quantity + take };
                await work.SetQuantityAsync(charge, charge.Quantity);
            }
            else if (take == have.Quantity)
            {
                await work.MoveAsync(have, characterId, shipId, module.Flag);
                charge = have with { OwnerId = characterId, LocationId = shipId, Flag = module.Flag };
            }
            else
            {
                charge = have with { Id = await work.CreateAsync(new NewItem(typeId, characterId, shipId, module.Flag, take)), LocationId = shipId, Flag = module.Flag, Quantity = take };
            }
            if (have.Quantity == take)
            {
                work.Stacks.Remove(have.Id);
                if (charge.Id == have.Id)
                {
                    // the stack itself became the module's charge: the client is told it left the hangar
                    work.Events.Add(new ItemChanged(have with { OwnerId = characterId, LocationId = JunkLocation }, have.LocationId, have.Flag, have.Quantity, have.OwnerId));
                }
                else
                {
                    await work.DeleteAsync(have);
                    work.Events.Add(new ItemChanged(have with { LocationId = JunkLocation }, have.LocationId, have.Flag, have.Quantity));
                }
            }
            else
            {
                var left = have with { Quantity = have.Quantity - take };
                await work.SetQuantityAsync(have, left.Quantity);
                work.Stacks[have.Id] = left;
                work.Events.Add(new ItemChanged(left, have.LocationId, have.Flag, have.Quantity));
            }
            needed -= take;
            work.Hold(module, charge);
            work.TouchesFitting = true;
        }
        return (charge, needed);
    }

    /// <summary>
    /// <c>LoadAmmo(shipID, moduleIDs, chargeItemIDs, ammoLocationID)</c> of a docked ship: each module takes rounds of the kind asked for (that of the first stack named
    /// that is the character's, or what it holds already) from the stacks of the hangar or the cargo hold, up to what it holds; another kind goes back first.
    /// </summary>
    public Task<ShipOutcome> LoadAmmoAsync(
        long characterId, long shipId, IReadOnlyList<long> moduleIds, IReadOnlyList<long> chargeItemIds, long ammoLocationId, CancellationToken cancellationToken = default) =>
        WithFreshRowsAsync(() => LoadAmmoOnceAsync(characterId, shipId, moduleIds, chargeItemIds, ammoLocationId, cancellationToken));

    /// <summary>Loads a docked ship's modules from a reachable personal or corporation cargo container.</summary>
    public Task<ShipOutcome> LoadAmmoFromContainerAsync(
        long characterId, long shipId, IReadOnlyList<long> moduleIds, IReadOnlyList<long> chargeItemIds, long containerId, long sourceOwnerId,
        CancellationToken cancellationToken = default) =>
        WithFreshRowsAsync(() => LoadAmmoOnceAsync(characterId, shipId, moduleIds, chargeItemIds, containerId, cancellationToken,
            sourceOwnerId, containerSource: true));

    private async Task<ShipOutcome> LoadAmmoOnceAsync(
        long characterId, long shipId, IReadOnlyList<long> moduleIds, IReadOnlyList<long> chargeItemIds, long ammoLocationId, CancellationToken cancellationToken,
        long? sourceOwnerId = null, bool containerSource = false)
    {
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        if (await BeginAmmoAsync(connection, transaction, characterId, shipId, ammoLocationId, AmmoWhere.Station, cancellationToken,
                sourceOwnerId: sourceOwnerId, containerSource: containerSource) is not { } work)
        {
            return new NothingHappened();
        }
        var place = PlaceOf(work.Snapshot, ammoLocationId) ?? (ammoLocationId, ContainerFlag);
        var requested = new List<Item>();
        foreach (var id in chargeItemIds.Distinct())
        {
            if (await items.GetAsync(id, cancellationToken) is { } item)
            {
                requested.Add(item);
            }
        }
        await statics.EnsureTypesAsync(requested.Select(i => i.TypeId), cancellationToken);

        // every row the loop can read and change is locked and checked first: the stacks of the kinds that can be taken or sent back, and the charges now in the modules
        var asked = moduleIds.Distinct().Select(id => work.Snapshot.Fitted.FirstOrDefault(m => m.Id == id)).OfType<Item>().ToList();
        var inModules = asked.Select(m => work.Charges.GetValueOrDefault(m.Flag)).OfType<Item>().ToList();
        var kinds = requested.Select(i => i.TypeId).Concat(inModules.Select(c => c.TypeId)).ToHashSet();
        await work.LockAsync(work.Stacks.Values.Where(s => kinds.Contains(s.TypeId)).Concat(inModules));

        foreach (var moduleId in moduleIds.Distinct())
        {
            if (work.Snapshot.Fitted.FirstOrDefault(m => m.Id == moduleId) is not { } module || statics.Type(module.TypeId) is not { } moduleType)
            {
                continue;
            }
            work.Charges.TryGetValue(module.Flag, out var charge);
            var before = StateOf(charge);
            var moduleBefore = work.World.Attributes(module.Id);
            try
            {
                var typeId = requested.FirstOrDefault(i => i.OwnerId == work.SourceOwnerId) is { } first ? first.TypeId : charge?.TypeId ?? 0;
                if (typeId <= 0 || statics.Type(typeId) is not { } chargeType || AmmoRules.Capacity(moduleType, chargeType) is not (> 0 and var capacity))
                {
                    continue;
                }
                // the stacks it can take: those named that are of the kind, else every stack of the kind
                var ofKind = work.Stacks.Values.Where(s => s.TypeId == typeId && s.LocationId == place.Location && s.Flag == place.Flag && s.Quantity > 0).OrderBy(s => s.Id).ToList();
                var named = ofKind.Where(s => requested.Any(r => r.Id == s.Id)).ToList();
                var sources = named.Count > 0 ? named : requested.Any(r => r.TypeId != typeId) ? [] : ofKind;
                var reload = ReloadMs(work.World, module.Id);
                if (charge is not null && charge.TypeId != typeId)
                {
                    await UnloadAsync(work, module, charge, place.Location, place.Flag, null);
                    charge = null;
                }
                var needed = capacity - (charge?.Quantity ?? 0);
                if (needed <= 0 || sources.Count == 0)
                {
                    continue;
                }
                // the reload is told before the stack gets smaller
                work.Events.Add(new ChargeBeingLoaded([module.Id], typeId, reload));
                (charge, _) = await TakeRoundsAsync(work, module, charge, typeId, characterId, shipId, needed, sources);
            }
            finally
            {
                TellTransition(work, module, before, StateOf(work.Charges.GetValueOrDefault(module.Flag)), work.Charges.GetValueOrDefault(module.Flag), moduleBefore);
            }
        }
        await transaction.CommitAsync(cancellationToken);
        return Finish(work);
    }

    private static AmmoChanged Finish(AmmoWork work)
    {
        if (work.TouchesFitting && work.Docked)
        {
            work.Events.Add(new FittingRefreshed(work.Snapshot.Fitted));
        }
        return new AmmoChanged(work.Snapshot.Ship.Id, work.Events);
    }

    /// <summary>
    /// <c>UnloadAmmo(shipID, moduleIDs, destination, quantity)</c> of a docked ship: what the modules hold goes to the hangar or the cargo hold (all of it, or
    /// <paramref name="quantity"/> rounds of each).
    /// </summary>
    public Task<ShipOutcome> UnloadAmmoAsync(
        long characterId, long shipId, IReadOnlyList<long> moduleIds, long destinationLocationId, int? destinationFlag, long? quantity, CancellationToken cancellationToken = default) =>
        WithFreshRowsAsync(() => UnloadAmmoOnceAsync(characterId, shipId, moduleIds, destinationLocationId, destinationFlag, quantity, AmmoWhere.Station, null, cancellationToken));

    /// <summary>Unloads a docked ship's charges into a personal or corporation-owned cargo container.</summary>
    public Task<ShipOutcome> UnloadAmmoToContainerAsync(
        long characterId, long shipId, IReadOnlyList<long> moduleIds, long containerId, long destinationOwnerId,
        int? destinationFlag, long? quantity, CancellationToken cancellationToken = default) =>
        WithFreshRowsAsync(() => UnloadAmmoOnceAsync(characterId, shipId, moduleIds, containerId, destinationFlag, quantity,
            AmmoWhere.Station, null, cancellationToken, destinationOwnerId, containerDestination: true));

    /// <summary>Stages whole fitted charge items in a direct trade and returns the matching dogma transition.</summary>
    public async Task<ShipOutcome> UnloadAmmoToTradeAsync(long characterId, long shipId, IReadOnlyList<long> moduleIds,
        Func<IReadOnlyList<(long ModuleId, long ChargeId)>, CancellationToken, Task<IReadOnlyDictionary<long, Item>>> stageCharges,
        CancellationToken cancellationToken = default)
    {
        if (await LoadAsync(characterId, cancellationToken).ConfigureAwait(false) is not { } before || before.Ship.Id != shipId
            || before.Ship.Flag != ItemFlags.Hangar)
            return new NothingHappened();

        var selected = new List<(Item Module, Item Charge)>();
        foreach (var moduleId in moduleIds.Distinct())
        {
            if (before.Fitted.FirstOrDefault(module => module.Id == moduleId) is { } module
                && before.ChargeOf(module) is { } charge)
                selected.Add((module, charge));
        }
        if (selected.Count == 0)
            return new NothingHappened();

        var pairs = selected.Select(entry => (ModuleId: entry.Module.Id, ChargeId: entry.Charge.Id)).ToArray();
        var staged = await stageCharges(pairs, cancellationToken).ConfigureAwait(false);
        if (pairs.Any(pair => !staged.ContainsKey(pair.ChargeId)))
            throw new InvalidOperationException("the trade did not stage every fitted charge");
        var after = await LoadAsync(characterId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("the ship disappeared after fitted charges entered trade");

        var events = new List<AmmoEvent>();
        foreach (var (module, charge) in selected)
        {
            events.Add(new ItemChanged(staged[charge.Id], shipId, charge.Flag, charge.Quantity, charge.OwnerId));
            events.Add(new ChargeQuantityChanged(shipId, module.Flag, charge.TypeId, 0, charge.Quantity));
            if (ModuleChanges(module.Id, before.World.Attributes(module.Id), after.World.Attributes(module.Id)) is { Count: > 0 } changed)
                events.Add(new ModuleAttributesChanged(changed));
        }
        events.Add(new FittingRefreshed(after.Fitted));
        return new AmmoChanged(shipId, events, ItemChangesAnnouncedExternally: true);
    }

    /// <summary>
    /// <c>UnloadAmmo</c> of a ship in space: what the module holds goes to the hold, at once (all of it, or <paramref name="quantity"/> rounds). The gun may be running: the
    /// simulation has its rounds and has taken them out (<paramref name="gun"/>); what is taken out of the charge item is what it took.
    /// </summary>
    public Task<ShipOutcome> UnloadAmmoInSpaceAsync(
        long characterId, long shipId, long moduleId, long destinationLocationId, int? destinationFlag, long? quantity, GunRounds gun, CancellationToken cancellationToken = default) =>
        WithFreshRowsAsync(() => UnloadAmmoOnceAsync(characterId, shipId, [moduleId], destinationLocationId, destinationFlag, quantity, AmmoWhere.Space, gun, cancellationToken));

    /// <summary>Unloads a grouped request after the simulation reports each running gun's actual rounds.</summary>
    public Task<ShipOutcome> UnloadAmmoInSpaceBatchAsync(
        long characterId, long shipId, IReadOnlyList<long> moduleIds, long destinationLocationId, int? destinationFlag, long? quantity,
        IReadOnlyDictionary<long, GunRounds> moduleRounds, CancellationToken cancellationToken = default) =>
        WithFreshRowsAsync(() => UnloadAmmoOnceAsync(characterId, shipId, moduleIds, destinationLocationId, destinationFlag, quantity,
            AmmoWhere.Space, null, cancellationToken, moduleRounds: moduleRounds));

    private async Task<ShipOutcome> UnloadAmmoOnceAsync(
        long characterId, long shipId, IReadOnlyList<long> moduleIds, long destinationLocationId, int? destinationFlag, long? quantity, AmmoWhere where, GunRounds? gun,
        CancellationToken cancellationToken, long? destinationOwnerId = null, bool containerDestination = false,
        IReadOnlyDictionary<long, GunRounds>? moduleRounds = null)
    {
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        if (await BeginAmmoAsync(connection, transaction, characterId, shipId, destinationLocationId, where, cancellationToken,
                destinationFlag, destinationOwnerId, containerDestination) is not { } work)
        {
            return new NothingHappened();
        }
        var place = PlaceOf(work.Snapshot, destinationLocationId);
        Item? container = null;
        if (containerDestination)
        {
            if (destinationOwnerId is not { } ownerId
                || destinationFlag is { } requestedFlag && requestedFlag != ContainerFlag
                || await items.LockAndGetAsync(connection, transaction, destinationLocationId, cancellationToken) is not { } destination
                || destination.OwnerId != ownerId || !destination.Singleton || destination.Quantity != 1
                || !await IsCargoContainerItemAsync(destination, cancellationToken)
                || !await IsOwnedContainerAtStationAsync(connection, transaction, destination, ownerId,
                    work.Snapshot.Ship.LocationId, cancellationToken))
            {
                return new NothingHappened();
            }
            container = destination;
        }
        else if (place is null)
        {
            return new NothingHappened();
        }

        var targetLocation = place?.Location ?? destinationLocationId;
        var flag = containerDestination ? destinationFlag ?? ContainerFlag : destinationFlag ?? place!.Value.Flag;
        if (Slots.IsSlotFlag(flag))
        {
            return new ShipRefused(ShipRefusal.CannotMoveChargesBetweenModules);
        }
        if (container is not null)
        {
            var contents = await items.LockContainerAsync(connection, transaction, container.Id, cancellationToken);
            var charges = moduleIds.Distinct()
                .Select(id => work.Snapshot.Fitted.FirstOrDefault(module => module.Id == id))
                .OfType<Item>()
                .Select(module => work.Charges.GetValueOrDefault(module.Flag))
                .OfType<Item>()
                .DistinctBy(charge => charge.Id)
                .ToArray();
            await statics.EnsureTypesAsync(charges.Select(charge => charge.TypeId), cancellationToken);
            var capacity = statics.AttributeId("capacity") is { } capacityId
                ? statics.Type(container.TypeId)?.Attributes.GetValueOrDefault(capacityId) ?? 0
                : 0;
            var used = await VolumeOfAsync(contents, cancellationToken);
            var required = charges.Sum(charge =>
            {
                var units = quantity is > 0 ? Math.Min(quantity.Value, charge.Quantity) : charge.Quantity;
                return statics.Type(charge.TypeId) is { } type
                    ? ItemVolumes.Of(charge with { Quantity = charge.Singleton ? charge.Quantity : units }, type.GroupId, ItemVolumeOf(type))
                    : 0;
            });
            if (required > Math.Max(0, capacity - used) + RoomTolerance)
            {
                return new ShipRefused(ShipRefusal.NoSpaceForThat, ModuleTypeId: charges.FirstOrDefault()?.TypeId ?? 0,
                    Available: Math.Round(Math.Max(0, capacity - used), 6), Volume: Math.Round(required, 6));
            }
        }

        // the charges that go, and the stacks of their kinds where they go (a charge joins the first of them), are locked and checked first
        var leaving = moduleIds.Distinct().Select(id => work.Snapshot.Fitted.FirstOrDefault(m => m.Id == id)).OfType<Item>()
            .Select(m => work.Charges.GetValueOrDefault(m.Flag)).OfType<Item>().ToList();
        var cargoContents = targetLocation == shipId && flag == CargoFlag
            ? (await items.InLocationAsync(shipId, characterId, cancellationToken)).Where(item => item.Flag == CargoFlag).ToArray()
            : [];
        await work.LockAsync(leaving
            .Concat(work.Stacks.Values.Where(s => s.LocationId == targetLocation && s.Flag == flag && leaving.Any(c => c.TypeId == s.TypeId)))
            .Concat(cargoContents));

        if (targetLocation == shipId && flag == CargoFlag && leaving.Count > 0)
        {
            if (await CargoCapacityRefusalAsync(work.Snapshot, cargoContents,
                    MovingCharges(work.Snapshot, leaving, quantity, gun, moduleRounds), cancellationToken) is { } refusal)
            {
                return refusal;
            }
        }
        foreach (var moduleId in moduleIds.Distinct())
        {
            if (work.Snapshot.Fitted.FirstOrDefault(m => m.Id == moduleId) is not { } module || !work.Charges.TryGetValue(module.Flag, out var charge))
            {
                continue;
            }
            var before = StateOf(charge);
            var moduleBefore = work.World.Attributes(module.Id);
            try
            {
                // a gun that runs has lost the rounds the simulation took: that many leave the charge (none, when it had none to give)
                var moduleGun = moduleRounds?.GetValueOrDefault(moduleId) ?? gun;
                var leaves = moduleGun is { Running: true } ? moduleGun.Before - moduleGun.After : quantity;
                if (leaves is not null and <= 0)
                {
                    continue;
                }
                await UnloadAsync(work, module, charge, targetLocation, flag, leaves);
            }
            finally
            {
                TellTransition(work, module, before, StateOf(work.Charges.GetValueOrDefault(module.Flag)), work.Charges.GetValueOrDefault(module.Flag), moduleBefore);
            }
        }
        await transaction.CommitAsync(cancellationToken);
        return Finish(work);
    }

    private static Item[] MovingCharges(
        ShipSnapshot snapshot, IEnumerable<Item> charges, long? quantity, GunRounds? gun, IReadOnlyDictionary<long, GunRounds>? moduleRounds) => charges.Select(charge =>
    {
        var moduleId = snapshot.Fitted.FirstOrDefault(module => module.Flag == charge.Flag)?.Id;
        var moduleGun = moduleId is { } id ? moduleRounds?.GetValueOrDefault(id) ?? gun : gun;
        var rounds = moduleGun is { Running: true } ? moduleGun.Before - moduleGun.After : quantity;
        if (rounds is <= 0)
            return null;
        var amount = rounds is { } requested ? Math.Min(charge.Quantity, requested) : charge.Quantity;
        return amount > 0 ? charge with { Quantity = charge.Singleton ? charge.Quantity : amount } : null;
    }).OfType<Item>().ToArray();

    private async Task<ShipRefused?> CargoCapacityRefusalAsync(
        ShipSnapshot snapshot, IReadOnlyList<Item> cargoContents, IReadOnlyList<Item> moving, CancellationToken cancellationToken)
    {
        if (moving.Count == 0 || CapacityAttributeOf(CargoFlag) is not { } capacityAttribute)
        {
            return null;
        }
        await statics.EnsureTypesAsync(cargoContents.Select(item => item.TypeId).Concat(moving.Select(item => item.TypeId)), cancellationToken);
        var capacity = snapshot.World.Get(snapshot.Ship.Id, capacityAttribute);
        var used = await VolumeOfAsync(cargoContents, cancellationToken);
        var required = await VolumeOfAsync(moving, cancellationToken);
        var available = Math.Max(0, capacity - used);
        return required > available + RoomTolerance
            ? new ShipRefused(ShipRefusal.NotEnoughCargoSpace, ModuleTypeId: moving[0].TypeId,
                ShipTypeId: snapshot.Ship.TypeId, Available: Math.Round(available, 6), Volume: Math.Round(required, 6))
            : null;
    }

    private async Task<bool> IsCargoContainerItemAsync(Item item, CancellationToken cancellationToken)
    {
        await statics.EnsureTypesAsync([item.TypeId], cancellationToken);
        return statics.Type(item.TypeId) is { } type && IsCargoContainerType(type);
    }

    private async Task<bool> IsOwnedContainerAtStationAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Item container, long ownerId, long stationId,
        CancellationToken cancellationToken)
    {
        var current = container;
        var seen = new HashSet<long>();
        for (var depth = 0; depth < 32; depth++)
        {
            if (!seen.Add(current.Id) || current.OwnerId != ownerId)
                return false;
            if (current.LocationId == stationId)
                return current.Flag == ItemFlags.Hangar || ItemFlags.IsCorporationHangar(current.Flag);
            if (await items.LockAndGetAsync(connection, transaction, current.LocationId, cancellationToken) is not { } parent)
                return false;
            current = parent;
        }
        return false;
    }

    private async Task<bool> IsAmmoContainerBesideShipAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Item container, long ownerId, ShipSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        if (snapshot.Ship.Flag == ItemFlags.Hangar)
            return await IsOwnedContainerAtStationAsync(connection, transaction, container, ownerId,
                snapshot.Ship.LocationId, cancellationToken);

        var current = container;
        var seen = new HashSet<long>();
        for (var depth = 0; depth < 32; depth++)
        {
            if (!seen.Add(current.Id) || current.OwnerId != ownerId)
                return false;
            if (current.LocationId == snapshot.Ship.Id)
                return current.Flag == CargoFlag;
            if (await items.LockAndGetAsync(connection, transaction, current.LocationId, cancellationToken) is not { } parent)
                return false;
            current = parent;
        }
        return false;
    }

    /// <summary>
    /// What a pilot who loads guns in space (<c>LoadAmmo</c>) is to wait for: for each module asked for, the plan of its reload (what the reference queues: <c>Handle_LoadAmmo</c> in
    /// space), in the order asked. A module that is full of the kind, has nothing of it in the hold to take, does not take it or is not the character's is left out; one that is
    /// asked again while it waits is the caller's to refuse. Nothing is changed: the rounds move when the time has run (<see cref="CompleteReloadAsync"/>).
    /// </summary>
    public async Task<IReadOnlyList<ReloadPlan>> PlanReloadsAsync(
        long characterId, long shipId, IReadOnlyList<long> moduleIds, IReadOnlyList<long> chargeItemIds, long ammoLocationId, CancellationToken cancellationToken = default,
        int chargeTypeId = 0, long? sourceOwnerId = null)
    {
        var containerSource = ammoLocationId != shipId;
        if (containerSource && sourceOwnerId is null)
        {
            return [];
        }
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        if (await BeginAmmoAsync(connection, transaction, characterId, shipId, ammoLocationId, AmmoWhere.Space, cancellationToken,
                sourceOwnerId: sourceOwnerId, containerSource: containerSource) is not { } work)
        {
            return [];
        }
        var place = PlaceOf(work.Snapshot, ammoLocationId) ?? (ammoLocationId, ContainerFlag);
        var requested = new List<Item>();
        foreach (var id in chargeItemIds.Distinct())
        {
            if (await items.GetAsync(id, cancellationToken) is { } item)
            {
                requested.Add(item);
            }
        }
        await statics.EnsureTypesAsync(requested.Select(i => i.TypeId), cancellationToken);
        var charges = work.Charges;
        var plans = new List<ReloadPlan>();
        foreach (var moduleId in moduleIds.Distinct())
        {
            if (work.Snapshot.Fitted.FirstOrDefault(m => m.Id == moduleId) is not { } module || statics.Type(module.TypeId) is not { } moduleType)
            {
                continue;
            }
            charges.TryGetValue(module.Flag, out var charge);
            // the kind asked for: the one named, else the one given, else the kind of the charge the module has (a reloading gun whose slot is empty names its kind)
            var typeId = requested.FirstOrDefault(i => i.OwnerId == work.SourceOwnerId) is { } first ? first.TypeId : chargeTypeId > 0 ? chargeTypeId : charge?.TypeId ?? 0;
            if (typeId <= 0 || statics.Type(typeId) is not { } chargeType || AmmoRules.Capacity(moduleType, chargeType) is not (> 0 and var capacity))
            {
                continue;
            }
            var ofKind = work.Stacks.Values
                .Where(s => s.TypeId == typeId && s.LocationId == place.Location && s.Flag == place.Flag && s.Quantity > 0)
                .OrderBy(s => s.Id).ToList();
            var named = ofKind.Where(s => requested.Any(r => r.Id == s.Id)).ToList();
            var sources = named.Count > 0 ? named : requested.Any(r => r.TypeId != typeId) ? [] : ofKind;
            var sameKind = charge is not null && charge.TypeId == typeId;
            var reload = ReloadMs(work.World, module.Id);
            if (sameKind && charge!.Quantity >= capacity)
            {
                continue; // it is full
            }
            if (sources.Count == 0 && !(reload > 0 && sameKind))
            {
                continue; // nothing to take (a module that waits for a reload with rounds in it still waits, as the reference's does)
            }
            plans.Add(new ReloadPlan(shipId, module.Id, module.TypeId, module.Flag, typeId, [.. sources.Select(s => s.Id)], reload,
                ammoLocationId, work.SourceOwnerId, place.Flag));
        }
        await transaction.CommitAsync(cancellationToken);
        return plans;
    }

    /// <summary>
    /// What a reload is asked of the simulation when it ends: how many rounds the module holds when it is full of the kind, and how many the stacks of the plan have in the hold now.
    /// Null when the module is no longer fitted to the ship the character flies or does not take the kind.
    /// </summary>
    public async Task<(long Capacity, long Available)?> ReloadRoomAsync(long characterId, ReloadPlan plan, CancellationToken cancellationToken = default)
    {
        if (await LoadAsync(characterId, cancellationToken) is not { } snapshot || snapshot.Ship.Id != plan.ShipId
            || snapshot.Fitted.FirstOrDefault(m => m.Id == plan.ModuleId && m.Flag == plan.ModuleFlag) is not { } module)
        {
            return null;
        }
        await statics.EnsureTypesAsync([module.TypeId, plan.ChargeTypeId], cancellationToken);
        if (statics.Type(module.TypeId) is not { } moduleType || statics.Type(plan.ChargeTypeId) is not { } chargeType || AmmoRules.Capacity(moduleType, chargeType) is not (> 0 and var capacity))
        {
            return null;
        }
        var named = plan.SourceItemIds.ToHashSet();
        var sourceLocationId = plan.SourceLocationId ?? plan.ShipId;
        var sourceOwnerId = plan.SourceOwnerId ?? characterId;
        var sourceFlag = plan.SourceFlag ?? (sourceLocationId == plan.ShipId ? CargoFlag : ContainerFlag);
        if (sourceLocationId != plan.ShipId
            && !await CanReachAmmoContainerAsync(characterId, plan.ShipId, sourceLocationId, sourceOwnerId, cancellationToken))
        {
            return null;
        }
        var sources = sourceLocationId == plan.ShipId
            ? await items.InLocationAsync(sourceLocationId, sourceOwnerId, cancellationToken)
            : await items.InContainerAsync(sourceLocationId, cancellationToken);
        var available = sources
            .Where(i => i.OwnerId == sourceOwnerId && i.Flag == sourceFlag && !i.Singleton && i.TypeId == plan.ChargeTypeId
                && (named.Count == 0 || named.Contains(i.Id))).Sum(i => i.Quantity);
        return (capacity, available);
    }

    /// <summary>
    /// Ends a reload (<see cref="PlanReloadsAsync"/>): the module takes rounds of the kind from the hold up to what it holds, the stacks and the charge are written, and the client
    /// is told as the reference tells it (the stacks that changed, the charge and what it did to the module). Another kind that is in the module goes back to the hold first. The
    /// module is read again: if it is no longer fitted to the ship the character flies, nothing is done. A gun that runs is given the rounds the simulation added
    /// (<paramref name="gun"/>), which the hold may not have any more: <see cref="Reloaded.Shortfall"/> says how many it was short.
    /// </summary>
    public Task<ShipOutcome> CompleteReloadAsync(long characterId, ReloadPlan plan, GunRounds gun, CancellationToken cancellationToken = default) =>
        WithFreshRowsAsync(() => CompleteReloadOnceAsync(characterId, plan, gun, cancellationToken));

    private async Task<ShipOutcome> CompleteReloadOnceAsync(long characterId, ReloadPlan plan, GunRounds gun, CancellationToken cancellationToken)
    {
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var sourceLocationId = plan.SourceLocationId ?? plan.ShipId;
        var sourceOwnerId = plan.SourceOwnerId ?? characterId;
        var containerSource = sourceLocationId != plan.ShipId;
        if (await BeginAmmoAsync(connection, transaction, characterId, plan.ShipId, sourceLocationId, AmmoWhere.Either, cancellationToken,
                sourceOwnerId: containerSource ? sourceOwnerId : null, containerSource: containerSource) is not { } work)
        {
            return new NothingHappened();
        }
        var place = PlaceOf(work.Snapshot, sourceLocationId) ?? (sourceLocationId, plan.SourceFlag ?? ContainerFlag);
        await statics.EnsureTypesAsync([plan.ChargeTypeId, plan.ModuleTypeId], cancellationToken);
        if (work.Snapshot.Fitted.FirstOrDefault(m => m.Id == plan.ModuleId && m.Flag == plan.ModuleFlag) is not { } module
            || statics.Type(module.TypeId) is not { } moduleType || statics.Type(plan.ChargeTypeId) is not { } chargeType
            || AmmoRules.Capacity(moduleType, chargeType) is not (> 0 and var capacity))
        {
            return new NothingHappened();
        }
        work.Charges.TryGetValue(module.Flag, out var charge);
        var named = plan.SourceItemIds.ToHashSet();
        var sources = work.Stacks.Values.Where(s => s.TypeId == plan.ChargeTypeId && s.LocationId == place.Location && s.Flag == place.Flag && s.Quantity > 0 && (named.Count == 0 || named.Contains(s.Id)))
            .OrderBy(s => s.Id).ToList();
        await work.LockAsync(sources.Concat(charge is null ? [] : [charge]).Concat(work.Stacks.Values.Where(s => charge is not null && s.TypeId == charge.TypeId)));

        var before = StateOf(charge);
        var moduleBefore = work.World.Attributes(module.Id);
        long shortfall = 0;
        try
        {
            if (gun.Running && charge is not null && charge.TypeId != plan.ChargeTypeId)
            {
                return new NothingHappened(); // a gun that runs keeps firing what it began the cycle with: the other kind is not put in it
            }
            if (charge is not null && charge.TypeId != plan.ChargeTypeId)
            {
                await UnloadAsync(work, module, charge, place.Location, place.Flag, null);
                charge = null;
            }
            // a gun that runs is given what the simulation added to its rounds; one that does not, what it lacks
            var needed = gun.Running ? gun.After - gun.Before : capacity - (charge?.Quantity ?? 0);
            if (needed > 0 && sources.Count > 0)
            {
                long missing;
                (charge, missing) = await TakeRoundsAsync(work, module, charge, plan.ChargeTypeId, characterId, plan.ShipId, needed, sources);
                shortfall = gun.Running ? missing : 0;
            }
            else if (gun.Running)
            {
                shortfall = Math.Max(0, needed);
            }
        }
        finally
        {
            TellTransition(work, module, before, StateOf(work.Charges.GetValueOrDefault(module.Flag)), work.Charges.GetValueOrDefault(module.Flag), moduleBefore);
        }
        await transaction.CommitAsync(cancellationToken);
        return new Reloaded(Finish(work), shortfall);
    }

    /// <summary>
    /// Puts items in the character's hangar (what <c>/giveitem</c> does): a stack joins the first stack of its kind there, an item that is a singleton is made
    /// <paramref name="quantity"/> times. Null when the character is not in a station. The client is told each stack that changed or came.
    /// </summary>
    public async Task<IReadOnlyList<ItemChanged>?> GrantAsync(long characterId, int typeId, long quantity, bool singleton, CancellationToken cancellationToken = default)
    {
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockAsync(connection, transaction, characterId, cancellationToken);
        if (await characters.GetAsync(characterId, cancellationToken) is not { StationId: > 0 } character)
        {
            return null;
        }
        var station = character.StationId;
        var changes = new List<ItemChanged>();
        if (singleton)
        {
            for (var made = 0L; made < quantity; made++)
            {
                var item = new NewItem(typeId, characterId, station, ItemFlags.Hangar, 1, Singleton: true);
                changes.Add(new ItemChanged(new Item(await items.CreateAsync(connection, transaction, item, cancellationToken), typeId, characterId, station, ItemFlags.Hangar, 1, true, null), 0, 0, 1));
            }
        }
        else if ((await items.InLocationAsync(station, characterId, cancellationToken)).Where(i => i.Flag == ItemFlags.Hangar && !i.Singleton && i.TypeId == typeId).OrderBy(i => i.Id).FirstOrDefault() is { } read
            && await items.LockAndGetAsync(connection, transaction, read.Id, cancellationToken) is { } stack
            && (stack.TypeId, stack.OwnerId, stack.LocationId, stack.Flag, stack.Singleton) == (typeId, characterId, station, ItemFlags.Hangar, false))
        {
            // the stack is locked and grown from what the lock finds: a sale that took some of it a moment ago is not lost
            var grown = stack with { Quantity = stack.Quantity + quantity };
            await items.SetQuantityAsync(connection, transaction, stack.Id, grown.Quantity, cancellationToken);
            changes.Add(new ItemChanged(grown, stack.LocationId, stack.Flag, stack.Quantity));
        }
        else
        {
            var id = await items.CreateAsync(connection, transaction, new NewItem(typeId, characterId, station, ItemFlags.Hangar, quantity), cancellationToken);
            changes.Add(new ItemChanged(new Item(id, typeId, characterId, station, ItemFlags.Hangar, quantity, false, null), 0, 0, quantity));
        }
        await transaction.CommitAsync(cancellationToken);
        return changes;
    }

    /// <summary>
    /// A turret in space spent rounds: the charge in the module has <paramref name="roundsLeft"/> now (a stack that is empty is gone). The simulation tells the client; this keeps
    /// what the character has. The rounds only go down in space, so a late answer never gives rounds back.
    /// </summary>
    public async Task SpendChargeAsync(long characterId, long moduleId, long roundsLeft, CancellationToken cancellationToken = default)
    {
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockAsync(connection, transaction, characterId, cancellationToken);
        if (await items.GetAsync(moduleId, cancellationToken) is { } module && module.OwnerId == characterId)
        {
            var (_, charges) = await ShipSnapshot.SlotsOfAsync(statics, await items.InLocationAsync(module.LocationId, characterId, cancellationToken), cancellationToken);

            if (charges.FirstOrDefault(c => c.Flag == module.Flag) is { } found
                && await items.LockAndGetAsync(connection, transaction, found.Id, cancellationToken) is { } charge
                && (charge.LocationId, charge.Flag) == (found.LocationId, found.Flag) && roundsLeft < charge.Quantity)
            {
                if (roundsLeft > 0)
                {
                    await items.SetQuantityAsync(connection, transaction, charge.Id, roundsLeft, cancellationToken);
                }
                else
                {
                    await items.DeleteAsync(connection, transaction, charge.Id, cancellationToken);
                }
            }
        }
        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>Takes an item the character owns out of the world (an item made for a fitting that could not be done).</summary>
    public async Task DeleteItemAsync(long characterId, long itemId, CancellationToken cancellationToken = default)
    {
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockAsync(connection, transaction, characterId, cancellationToken);
        if (await items.GetAsync(itemId, cancellationToken) is { } item && item.OwnerId == characterId)
        {
            await items.DeleteAsync(connection, transaction, itemId, cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }
}
