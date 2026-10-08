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
        PgItemStore items, NpgsqlConnection connection, NpgsqlTransaction transaction, ShipSnapshot snapshot, Dictionary<long, Item> stacks, CancellationToken cancellationToken)
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

        /// <summary>
        /// Locks the rows the work is about to change, in the order of their ids, and checks that each is what the decisions were made from. This is the convention of
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

        public async Task MoveAsync(Item item, long locationId, int flag) => await items.MoveAsync(connection, transaction, item.Id, locationId, flag, cancellationToken);

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
            var created = charge with { Id = await work.CreateAsync(new NewItem(charge.TypeId, charge.OwnerId, locationId, flag, wanted)), LocationId = locationId, Flag = flag, Quantity = wanted };
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
        NpgsqlConnection connection, NpgsqlTransaction transaction, long characterId, long shipId, long locationId, AmmoWhere where, CancellationToken cancellationToken)
    {
        await LockAsync(connection, transaction, characterId, cancellationToken);
        if (await LoadAsync(characterId, cancellationToken) is not { } snapshot || snapshot.Ship.Id != shipId
            || (snapshot.Ship.Flag != ItemFlags.Hangar && snapshot.Ship.Flag != InSpaceFlag)
            || (where == AmmoWhere.Station && snapshot.Ship.Flag != ItemFlags.Hangar) || (where == AmmoWhere.Space && snapshot.Ship.Flag != InSpaceFlag))
        {
            return null; // the ship is not where the call is for
        }
        if (PlaceOf(snapshot, locationId) is not { } place)
        {
            return null;
        }
        var stacks = (await items.InLocationAsync(place.Location, characterId, cancellationToken)).Where(i => i.Flag == place.Flag && !i.Singleton).ToDictionary(i => i.Id);
        return new AmmoWork(items, connection, transaction, snapshot, stacks, cancellationToken);
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
                await work.MoveAsync(have, shipId, module.Flag);
                charge = have with { LocationId = shipId, Flag = module.Flag };
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
                    work.Events.Add(new ItemChanged(have with { LocationId = JunkLocation }, have.LocationId, have.Flag, have.Quantity));
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

    private async Task<ShipOutcome> LoadAmmoOnceAsync(
        long characterId, long shipId, IReadOnlyList<long> moduleIds, IReadOnlyList<long> chargeItemIds, long ammoLocationId, CancellationToken cancellationToken)
    {
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        if (await BeginAmmoAsync(connection, transaction, characterId, shipId, ammoLocationId, AmmoWhere.Station, cancellationToken) is not { } work)
        {
            return new NothingHappened();
        }
        var place = PlaceOf(work.Snapshot, ammoLocationId)!.Value;
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
                var typeId = requested.FirstOrDefault(i => i.OwnerId == characterId) is { } first ? first.TypeId : charge?.TypeId ?? 0;
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

    /// <summary>
    /// <c>UnloadAmmo</c> of a ship in space: what the module holds goes to the hold, at once (all of it, or <paramref name="quantity"/> rounds). The gun may be running: the
    /// simulation has its rounds and has taken them out (<paramref name="gun"/>); what is taken out of the charge item is what it took.
    /// </summary>
    public Task<ShipOutcome> UnloadAmmoInSpaceAsync(
        long characterId, long shipId, long moduleId, long destinationLocationId, int? destinationFlag, long? quantity, GunRounds gun, CancellationToken cancellationToken = default) =>
        WithFreshRowsAsync(() => UnloadAmmoOnceAsync(characterId, shipId, [moduleId], destinationLocationId, destinationFlag, quantity, AmmoWhere.Space, gun, cancellationToken));

    private async Task<ShipOutcome> UnloadAmmoOnceAsync(
        long characterId, long shipId, IReadOnlyList<long> moduleIds, long destinationLocationId, int? destinationFlag, long? quantity, AmmoWhere where, GunRounds? gun,
        CancellationToken cancellationToken)
    {
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        if (await BeginAmmoAsync(connection, transaction, characterId, shipId, destinationLocationId, where, cancellationToken) is not { } work)
        {
            return new NothingHappened();
        }
        var place = PlaceOf(work.Snapshot, destinationLocationId)!.Value;
        var flag = destinationFlag ?? place.Flag;
        // the charges that go, and the stacks of their kinds where they go (a charge joins the first of them), are locked and checked first
        var leaving = moduleIds.Distinct().Select(id => work.Snapshot.Fitted.FirstOrDefault(m => m.Id == id)).OfType<Item>()
            .Select(m => work.Charges.GetValueOrDefault(m.Flag)).OfType<Item>().ToList();
        await work.LockAsync(leaving.Concat(work.Stacks.Values.Where(s => s.LocationId == place.Location && s.Flag == flag && leaving.Any(c => c.TypeId == s.TypeId))));
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
                var leaves = gun is { Running: true } ? gun.Before - gun.After : quantity;
                if (leaves is not null and <= 0)
                {
                    continue;
                }
                await UnloadAsync(work, module, charge, place.Location, flag, leaves);
            }
            finally
            {
                TellTransition(work, module, before, StateOf(work.Charges.GetValueOrDefault(module.Flag)), work.Charges.GetValueOrDefault(module.Flag), moduleBefore);
            }
        }
        await transaction.CommitAsync(cancellationToken);
        return Finish(work);
    }

    /// <summary>
    /// What a pilot who loads guns in space (<c>LoadAmmo</c>) is to wait for: for each module asked for, the plan of its reload (what the reference queues: <c>Handle_LoadAmmo</c> in
    /// space), in the order asked. A module that is full of the kind, has nothing of it in the hold to take, does not take it or is not the character's is left out; one that is
    /// asked again while it waits is the caller's to refuse. Nothing is changed: the rounds move when the time has run (<see cref="CompleteReloadAsync"/>).
    /// </summary>
    public async Task<IReadOnlyList<ReloadPlan>> PlanReloadsAsync(
        long characterId, long shipId, IReadOnlyList<long> moduleIds, IReadOnlyList<long> chargeItemIds, long ammoLocationId, CancellationToken cancellationToken = default)
    {
        if (await LoadAsync(characterId, cancellationToken) is not { } snapshot || snapshot.Ship.Id != shipId || snapshot.Ship.Flag != InSpaceFlag || ammoLocationId != shipId)
        {
            return [];
        }
        var requested = new List<Item>();
        foreach (var id in chargeItemIds.Distinct())
        {
            if (await items.GetAsync(id, cancellationToken) is { } item)
            {
                requested.Add(item);
            }
        }
        await statics.EnsureTypesAsync(requested.Select(i => i.TypeId), cancellationToken);
        var hold = (await items.InLocationAsync(shipId, characterId, cancellationToken)).Where(i => i.Flag == CargoFlag && !i.Singleton && i.Quantity > 0).ToList();
        var charges = snapshot.Charges.ToDictionary(c => c.Flag);
        var plans = new List<ReloadPlan>();
        foreach (var moduleId in moduleIds.Distinct())
        {
            if (snapshot.Fitted.FirstOrDefault(m => m.Id == moduleId) is not { } module || statics.Type(module.TypeId) is not { } moduleType)
            {
                continue;
            }
            charges.TryGetValue(module.Flag, out var charge);
            var typeId = requested.FirstOrDefault(i => i.OwnerId == characterId) is { } first ? first.TypeId : charge?.TypeId ?? 0;
            if (typeId <= 0 || statics.Type(typeId) is not { } chargeType || AmmoRules.Capacity(moduleType, chargeType) is not (> 0 and var capacity))
            {
                continue;
            }
            var ofKind = hold.Where(s => s.TypeId == typeId).OrderBy(s => s.Id).ToList();
            var named = ofKind.Where(s => requested.Any(r => r.Id == s.Id)).ToList();
            var sources = named.Count > 0 ? named : requested.Any(r => r.TypeId != typeId) ? [] : ofKind;
            var sameKind = charge is not null && charge.TypeId == typeId;
            var reload = ReloadMs(snapshot.World, module.Id);
            if (sameKind && charge!.Quantity >= capacity)
            {
                continue; // it is full
            }
            if (sources.Count == 0 && !(reload > 0 && sameKind))
            {
                continue; // nothing to take (a module that waits for a reload with rounds in it still waits, as the reference's does)
            }
            plans.Add(new ReloadPlan(shipId, module.Id, module.TypeId, module.Flag, typeId, [.. sources.Select(s => s.Id)], reload));
        }
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
        var available = (await items.InLocationAsync(plan.ShipId, characterId, cancellationToken))
            .Where(i => i.Flag == CargoFlag && !i.Singleton && i.TypeId == plan.ChargeTypeId && (named.Count == 0 || named.Contains(i.Id))).Sum(i => i.Quantity);
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
        if (await BeginAmmoAsync(connection, transaction, characterId, plan.ShipId, plan.ShipId, AmmoWhere.Either, cancellationToken) is not { } work)
        {
            return new NothingHappened();
        }
        var place = PlaceOf(work.Snapshot, plan.ShipId)!.Value;
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
            // the row is locked and the decision made on what the lock finds (the rounds only go down, and a stack that was taken away is not made again)
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
