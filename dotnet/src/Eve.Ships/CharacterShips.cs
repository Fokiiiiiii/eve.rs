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
    CharacterTraining? training = null)
{
    /// <summary>The inventory flags a ship's contents can be in besides its slots: the cargo hold.</summary>
    public const int CargoFlag = 5;

    private const int IsOnline = 2;
    private const int ChargeCategory = 8;
    private const double Tolerance = 1e-9;

    /// <summary>How fast the character learns as dogma shows it: its attributes times the server's training speed (the client works its estimates out from them).</summary>
    private Func<int, double>? LearningOf(long characterId) =>
        training is null ? null : attribute => training.Settings.Attribute(characterId, attribute) * training.Settings.Speed;

    /// <summary>The character's active ship, or null when it has none. What the character's training has finished is settled into its skills first.</summary>
    public async Task<ShipSnapshot?> LoadAsync(long characterId, CancellationToken cancellationToken = default)
    {
        if (training is not null && await characters.GetAsync(characterId, cancellationToken) is { } owner)
        {
            await training.SettleAsync(characterId, owner.AccountId, cancellationToken);
        }
        return await ShipSnapshot.LoadAsync(statics, characters, items, skills, modules, characterId, LearningOf(characterId), null, cancellationToken);
    }

    /// <summary>
    /// What changed in the character's ship when its skills went from <paramref name="before"/> to what they are now: the ship's attributes
    /// that differ, in the order of the attributes.
    /// </summary>
    public async Task<IReadOnlyList<AttributeChanged>> SkillChangesAsync(long characterId, IReadOnlyList<CharacterSkill> before, CancellationToken cancellationToken = default)
    {
        var now = await ShipSnapshot.LoadAsync(statics, characters, items, skills, modules, characterId, LearningOf(characterId), null, cancellationToken);
        var was = await ShipSnapshot.LoadAsync(statics, characters, items, skills, modules, characterId, LearningOf(characterId), before, cancellationToken);
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
        long characterId, long itemId, long sourceLocationId, long destinationLocationId, int? flag, long? quantity, CancellationToken cancellationToken = default)
    {
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockAsync(connection, transaction, characterId, cancellationToken);

        if (await LoadAsync(characterId, cancellationToken) is not { } snapshot || await items.GetAsync(itemId, cancellationToken) is not { } item
            || item.OwnerId != characterId || item.Id == snapshot.Ship.Id)
        {
            return new NothingHappened();
        }
        await statics.EnsureTypesAsync([item.TypeId], cancellationToken);
        var ship = snapshot.Ship;
        var station = ship.LocationId;
        var inShip = item.LocationId == ship.Id;
        var inHangar = item.LocationId == station;
        var toShip = destinationLocationId == ship.Id;
        var toHangar = destinationLocationId == station;
        if ((!inShip && !inHangar) || (!toShip && !toHangar) || (sourceLocationId != item.LocationId && !(inShip && sourceLocationId == station)))
        {
            return new NothingHappened();
        }
        if (quantity is { } asked && !item.Singleton && asked != item.Quantity)
        {
            return new NothingHappened(); // splitting a stack is not done yet
        }
        if (inShip && Slots.FamilyOfFlag(item.Flag) == SlotFamily.Rig && !toShip)
        {
            return new NothingHappened(); // a rig cannot be taken out: it has to be destroyed
        }

        var world = snapshot.World;
        var type = statics.Type(item.TypeId);
        var wasInSlot = inShip && Slots.IsSlotFlag(item.Flag);
        var state = wasInSlot ? world.Item(item.Id)?.State ?? ItemState.Offline : ItemState.Offline;
        var shipBefore = world.Attributes(ship.Id);
        var moduleBefore = wasInSlot ? world.Get(item.Id, IsOnline) : 0d;

        // the item is held in the hangar, out of any slot, while it is worked out where it goes
        world.Remove(item.Id);
        var candidate = world.Add(new DogmaItem(item.Id, item.TypeId, characterId, station, ItemFlags.Hangar));

        int destinationFlag;
        if (toHangar)
        {
            if (flag is not null and not 0 && flag != ItemFlags.Hangar)
            {
                return new NothingHappened();
            }
            destinationFlag = ItemFlags.Hangar;
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
        else if (flag is null or 0 || flag == CargoFlag)
        {
            destinationFlag = CargoFlag;
        }
        else
        {
            return new NothingHappened();
        }

        // what the ship is once the item is where it goes
        var landsInSlot = toShip && Slots.IsSlotFlag(destinationFlag);
        var newState = wasInSlot && landsInSlot ? state : ItemState.Offline;
        world.Remove(item.Id);
        if (landsInSlot)
        {
            world.Add(new DogmaItem(item.Id, item.TypeId, characterId, ship.Id, destinationFlag, newState));
        }
        var shipAfter = world.Attributes(ship.Id);

        await items.MoveAsync(connection, transaction, item.Id, destinationLocationId, destinationFlag, cancellationToken);
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
        var moved = new ItemMoved(item with { LocationId = destinationLocationId, Flag = destinationFlag }, item.LocationId, item.Flag);
        return new ShipChanged(item.Id, ship.Id, [moved], events);
    }

    /// <summary>Switches a module fitted to the character's ship on or off.</summary>
    public async Task<ShipOutcome> SetOnlineAsync(long characterId, long shipId, long moduleId, bool online, CancellationToken cancellationToken = default)
    {
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockAsync(connection, transaction, characterId, cancellationToken);

        if (await LoadAsync(characterId, cancellationToken) is not { } snapshot || snapshot.Ship.Id != shipId)
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
