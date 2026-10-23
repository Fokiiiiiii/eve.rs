using Eve.Characters;
using Eve.Fitting;
using Eve.Inventory;

namespace Eve.Ships;

/// <summary>A ship that went into space or came out of it: where it is now and where it was.</summary>
using Eve.Dogma;

public sealed record ShipEjection(
    Item Ship,
    Item? Capsule,
    Item? EscapeShip,
    IReadOnlyList<DogmaEvent> Events,
    ShipSnapshot EjectedSnapshot,
    IReadOnlyList<long> ModulesToStop,
    bool HullCollapsed)
{
    public Item PilotShip => EscapeShip ?? Capsule ?? throw new InvalidOperationException("ejection has no pilot hull");
}

public sealed record ShipBoarding(long ShipId, long PreviousOwnerId, long BoarderCharacterId, long BoarderCorporationId);

public sealed record SpaceMove(Item Ship, long PreviousLocationId, int PreviousFlag, Character Character);

/// <summary>What happened to an undock or a dock: the move, or why the ship stayed where it was.</summary>
public sealed record SpaceTransition(SpaceMove? Moved, string? Refusal)
{
    public static SpaceTransition Done(SpaceMove moved) => new(moved, null);

    public static SpaceTransition Refused(string reason) => new(null, reason);
}

/// <summary>The active ship changed while its previous and next ships remain in the same hangar.</summary>
public sealed record ShipTransitionEvent(
    string Kind,
    long CharacterId,
    long ShipId,
    long? RelatedShipId,
    long? FromLocationId,
    int? FromFlag,
    long? ToLocationId,
    int? ToFlag);

public sealed partial class CharacterShips
{
    /// <summary>The flag of an item that is in space, not in any inventory place.</summary>
    public const int InSpaceFlag = 0;

    /// <summary>
    /// The character's active ship leaves the hangar of the station the character is at and is in the solar system, in space. The character's
    /// station stays what it was (it is where the ship docks again if the simulation loses it).
    /// </summary>
public Func<Npgsql.NpgsqlConnection, Npgsql.NpgsqlTransaction, ShipTransitionEvent, CancellationToken, Task>? TransitionEventWriter { get; set; }

    private Task WriteTransitionAsync(
        Npgsql.NpgsqlConnection connection,
        Npgsql.NpgsqlTransaction transaction,
        ShipTransitionEvent transition,
        CancellationToken cancellationToken) =>
        TransitionEventWriter?.Invoke(connection, transaction, transition, cancellationToken) ?? Task.CompletedTask;

    public async Task<SpaceTransition> UndockAsync(long characterId, CancellationToken cancellationToken = default)
    {
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockAsync(connection, transaction, characterId, cancellationToken);

        if (await characters.GetAsync(characterId, cancellationToken) is not { ShipId: { } shipId } character
            || await items.GetAsync(shipId, cancellationToken) is not { } ship || ship.OwnerId != characterId)
        {
            return SpaceTransition.Refused("NO_SHIP");
        }
        if (ship.LocationId != character.StationId || ship.Flag != ItemFlags.Hangar)
        {
            return SpaceTransition.Refused(ship.LocationId == character.SolarSystemId ? "ALREADY_IN_SPACE" : "NOT_DOCKED");
        }
        await items.MoveAsync(connection, transaction, ship.Id, character.SolarSystemId, InSpaceFlag, cancellationToken);
        await WriteTransitionAsync(connection, transaction,
            new ShipTransitionEvent("undock", characterId, ship.Id, null, ship.LocationId, ship.Flag,
                character.SolarSystemId, InSpaceFlag), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return SpaceTransition.Done(new SpaceMove(ship with { LocationId = character.SolarSystemId, Flag = InSpaceFlag }, ship.LocationId, ship.Flag, character));
    }

    /// <summary>The character's active ship, in space, is put in the hangar of a station of the solar system it is in, and the character is there.</summary>
    public Task<SpaceTransition> DockAsync(long characterId, long stationId, CancellationToken cancellationToken = default) =>
        DockAsync(characterId, stationId, null, cancellationToken);

    public async Task<SpaceTransition> DockAsync(
        long characterId,
        long stationId,
        ShipCondition? condition,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockAsync(connection, transaction, characterId, cancellationToken);

        if (await characters.GetAsync(characterId, cancellationToken) is not { ShipId: { } shipId } character
            || await items.GetAsync(shipId, cancellationToken) is not { } ship || ship.OwnerId != characterId)
        {
            return SpaceTransition.Refused("NO_SHIP");
        }
        if (ship.LocationId != character.SolarSystemId || ship.Flag != InSpaceFlag)
        {
            return SpaceTransition.Refused("NOT_IN_SPACE");
        }
        await items.MoveAsync(connection, transaction, ship.Id, stationId, ItemFlags.Hangar, cancellationToken);
        await characters.SetStationAsync(connection, transaction, characterId, stationId, cancellationToken);
        if (condition is not null)
            await modules.SetShipConditionAsync(connection, transaction, ship.Id, condition, cancellationToken);
        await WriteTransitionAsync(connection, transaction,
            new ShipTransitionEvent("dock", characterId, ship.Id, null, ship.LocationId, ship.Flag,
                stationId, ItemFlags.Hangar), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return SpaceTransition.Done(new SpaceMove(
            ship with { LocationId = stationId, Flag = ItemFlags.Hangar },
            ship.LocationId,
            ship.Flag,
            character with { StationId = stationId }));
    }

    /// <summary>Changes the active ship to another ship in the same station hangar without moving either item.</summary>
    public async Task<bool> BoardAsync(long characterId, long stationId, long currentShipId, long shipId, CancellationToken cancellationToken = default)
    {
        if (currentShipId <= 0 || shipId <= 0 || currentShipId == shipId)
        {
            return false;
        }

        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockAsync(connection, transaction, characterId, cancellationToken);

        if (await characters.GetAsync(characterId, cancellationToken) is not { ShipId: { } activeShipId, StationId: { } currentStationId } character
            || activeShipId != currentShipId || currentStationId != stationId
            || await items.LockAndGetAsync(connection, transaction, currentShipId, cancellationToken) is not { } current
            || await items.LockAndGetAsync(connection, transaction, shipId, cancellationToken) is not { } next
            || current.OwnerId != characterId || next.OwnerId != characterId
            || current.LocationId != stationId || next.LocationId != stationId
            || current.Flag != ItemFlags.Hangar || next.Flag != ItemFlags.Hangar
            || !current.Singleton || !next.Singleton)
        {
            return false;
        }

        await statics.EnsureTypesAsync([current.TypeId, next.TypeId], cancellationToken);
        if (statics.Type(current.TypeId)?.CategoryId != 6 || statics.Type(next.TypeId)?.CategoryId != 6)
        {
            return false;
        }

        await characters.SetShipAsync(connection, transaction, characterId, shipId, cancellationToken);
        await WriteTransitionAsync(connection, transaction,
            new ShipTransitionEvent("board", characterId, shipId, currentShipId, stationId, ItemFlags.Hangar,
                stationId, ItemFlags.Hangar), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    /// <summary>
    /// The character's active ship, in space, has gone through a stargate: it is in space in the other solar system, and so is the character.
    /// </summary>
    public async Task<SpaceTransition> JumpAsync(long characterId, int solarSystemId, int constellationId, int regionId, CancellationToken cancellationToken = default)
    {
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockAsync(connection, transaction, characterId, cancellationToken);

        if (await characters.GetAsync(characterId, cancellationToken) is not { ShipId: { } shipId } character
            || await items.GetAsync(shipId, cancellationToken) is not { } ship || ship.OwnerId != characterId)
        {
            return SpaceTransition.Refused("NO_SHIP");
        }
        if (ship.LocationId != character.SolarSystemId || ship.Flag != InSpaceFlag)
        {
            return SpaceTransition.Refused("NOT_IN_SPACE");
        }
        await items.MoveAsync(connection, transaction, ship.Id, solarSystemId, InSpaceFlag, cancellationToken);
        await characters.SetLocationAsync(connection, transaction, characterId, solarSystemId, constellationId, regionId, cancellationToken);
        await WriteTransitionAsync(connection, transaction,
            new ShipTransitionEvent("gate-jump", characterId, ship.Id, null, ship.LocationId, ship.Flag,
                solarSystemId, InSpaceFlag), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return SpaceTransition.Done(new SpaceMove(
            ship with { LocationId = solarSystemId },
            ship.LocationId,
            ship.Flag,
            character with { SolarSystemId = solarSystemId, ConstellationId = constellationId, RegionId = regionId }));
    }

    /// <summary>Prepares a capsule for leaving the docked ship; the client's following Board call switches to it.</summary>
    /// <summary>Ejects from a docked ship into the character's capsule, keeping the ship in its hangar.</summary>
    public async Task<Item?> LeaveShipAsync(long characterId, long stationId, long shipId, CancellationToken cancellationToken = default)
    {
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockAsync(connection, transaction, characterId, cancellationToken);

        if (await characters.GetAsync(characterId, cancellationToken) is not { ShipId: { } activeShipId } character
            || activeShipId != shipId || character.StationId != stationId)
        {
            return null;
        }
        var ship = await items.LockAndGetAsync(connection, transaction, shipId, cancellationToken);
        if (ship is null || ship.OwnerId != characterId || ship.LocationId != stationId || ship.Flag != ItemFlags.Hangar)
        {
            return null;
        }
        if (ship.TypeId == CapsuleTypeId)
        {
            await transaction.CommitAsync(cancellationToken);
            return ship;
        }

        var inventory = await items.InLocationAsync(stationId, characterId, cancellationToken) ?? [];
        var capsule = inventory.FirstOrDefault(item => item.TypeId == CapsuleTypeId && item.Singleton && item.Flag == ItemFlags.Hangar);
        if (capsule is not null)
        {
            capsule = await items.LockAndGetAsync(connection, transaction, capsule.Id, cancellationToken);
            if (capsule is null || capsule.OwnerId != characterId || capsule.LocationId != stationId || capsule.Flag != ItemFlags.Hangar || !capsule.Singleton)
            {
                return null;
            }
        }
        else
        {
            var capsuleId = await items.CreateAsync(connection, transaction, new NewItem(CapsuleTypeId, characterId, stationId, ItemFlags.Hangar, 1, true), cancellationToken);
            capsule = new Item(capsuleId, CapsuleTypeId, characterId, stationId, ItemFlags.Hangar, 1, true, null);
        }

        await WriteTransitionAsync(connection, transaction,
            new ShipTransitionEvent("leave-ship", characterId, shipId, capsule.Id, stationId, ItemFlags.Hangar,
                stationId, ItemFlags.Hangar), cancellationToken);
        await characters.SetShipAsync(connection, transaction, characterId, capsule.Id, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return capsule;
    }

    /// <summary>
    /// What a host that has just started does before it takes a client: every ship that a host which stopped without closing its sessions left in space is put back
    /// where its character last was (the simulation starts empty with the host's connection, so no ship of a character is in it). Returns how many were put back.
    /// </summary>
    public async Task<int> RecoverAllFromSpaceAsync(
        Func<long, (int SolarSystemId, int ConstellationId, int RegionId)?>? placeOf,
        CancellationToken cancellationToken = default,
        Func<long, CancellationToken, Task<bool>>? shouldRecover = null)
    {
        var recovered = 0;

        foreach (var owner in await items.OwnersWithFlagAsync(InSpaceFlag, cancellationToken))
        {
            if (shouldRecover is not null && !await shouldRecover(owner, cancellationToken))
                continue;
            if (await RecoverFromSpaceAsync(owner, placeOf, cancellationToken))
            {
                recovered++;
            }
        }
        return recovered;
    }

    /// <summary>
    /// A game master's relocation (<c>/tele</c>, <c>/solar</c>): the character's active ship goes into space in the solar system, from the hangar of its station
    /// or from space, and the character is in that system. The station the character last docked at stays (it is where the ship is recovered to if the
    /// simulation loses it). No gate, range or fuel rule applies: the relocation is the game master's.
    /// </summary>
    public async Task<SpaceTransition> RelocateAsync(long characterId, int solarSystemId, int constellationId, int regionId, CancellationToken cancellationToken = default)
    {
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockAsync(connection, transaction, characterId, cancellationToken);

        if (await characters.GetAsync(characterId, cancellationToken) is not { ShipId: { } shipId } character
            || await items.LockAndGetAsync(connection, transaction, shipId, cancellationToken) is not { } ship || ship.OwnerId != characterId)
        {
            return SpaceTransition.Refused("NO_SHIP");
        }
        var docked = ship.Flag == ItemFlags.Hangar && ship.LocationId == character.StationId;
        if (!docked && ship.Flag != InSpaceFlag)
        {
            return SpaceTransition.Refused("NOT_DOCKED_OR_IN_SPACE");
        }
        await items.MoveAsync(connection, transaction, ship.Id, solarSystemId, InSpaceFlag, cancellationToken);
        await characters.SetLocationAsync(connection, transaction, characterId, solarSystemId, constellationId, regionId, cancellationToken);
        await WriteTransitionAsync(connection, transaction,
            new ShipTransitionEvent("gm-relocate", characterId, ship.Id, null, ship.LocationId, ship.Flag,
                solarSystemId, InSpaceFlag), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return SpaceTransition.Done(new SpaceMove(ship with { LocationId = solarSystemId, Flag = InSpaceFlag }, ship.LocationId, ship.Flag,
            character with { SolarSystemId = solarSystemId, ConstellationId = constellationId, RegionId = regionId }));
    }

    /// <summary>
    /// A ship left in space (the simulation was lost, the host restarted) is put back in the hangar of the station the character last was at,
    /// so that a character never wakes up in a ship the simulation does not have. Returns whether anything was moved.
    /// </summary>
    /// <summary>Ejects the pilot into a newly-created capsule while leaving the owned hull in its solar system.</summary>
    public async Task<ShipEjection?> EjectAsync(long characterId, long shipId, CancellationToken cancellationToken = default)
    {
        var fitting = await LoadAsync(characterId, cancellationToken);
        if (fitting is null || fitting.Ship.Id != shipId || fitting.Ship.TypeId == CapsuleTypeId
            || fitting.Ship.LocationId != fitting.Character.SolarSystemId || fitting.Ship.Flag != InSpaceFlag)
            return null;

        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockAsync(connection, transaction, characterId, cancellationToken);
        if (await characters.GetAsync(characterId, cancellationToken) is not { ShipId: { } activeShipId } character
            || activeShipId != shipId
            || await items.LockAndGetAsync(connection, transaction, shipId, cancellationToken) is not { } ship
            || ship.OwnerId != characterId || ship.LocationId != character.SolarSystemId || ship.Flag != InSpaceFlag || ship.TypeId == CapsuleTypeId)
            return null;

        var hullAttribute = statics.AttributeId("hp");
        var armorAttribute = statics.AttributeId("armorHP");
        var shieldAttribute = statics.AttributeId("shieldCapacity");
        if (hullAttribute is not { } hull || armorAttribute is not { } armor || shieldAttribute is not { } shield)
            return null;

        var before = fitting.World.Attributes(ship.Id);
        var oldHull = fitting.World.Get(ship.Id, hull);
        var oldArmor = fitting.World.Get(ship.Id, armor);
        var oldShield = fitting.World.Get(ship.Id, shield);
        var escapeCandidate = await EscapeBayShipAsync(ship, fitting.World, characterId, cancellationToken);
        var moduleChanges = FittingRules.RemovePilotModifiersAndRevalidate(
            fitting.World,
            statics,
            characterId,
            ship.Id,
            fitting.Skills.Select(skill => ShipSnapshot.SkillItemId(characterId, skill.TypeId)),
            fitting.Fitted.Select(module => module.Id));

        var events = new List<DogmaEvent>();
        foreach (var change in moduleChanges)
        {
            var module = fitting.Fitted.FirstOrDefault(item => item.Id == change.ModuleId);
            var wasOnline = change.Before >= ItemState.Online ? 1d : 0d;
            var isOnline = change.After >= ItemState.Online ? 1d : 0d;
            if (Changed(change.ModuleId, IsOnline, wasOnline, isOnline) is { } switched)
                events.Add(switched);
            if (change.After == ItemState.Offline)
                events.Add(new EffectChanged(change.ModuleId, BuiltInEffects.Online, Started: false));
            if (change.Before >= ItemState.Active && module is not null && statics.Type(module.TypeId) is { } type)
            {
                foreach (var effectId in type.EffectIds)
                {
                    if (statics.Effect(effectId)?.Category is EffectDef.Activation or EffectDef.Overload)
                        events.Add(new EffectChanged(change.ModuleId, effectId, Started: false));
                }
            }
            await modules.SetAsync(connection, transaction, change.ModuleId, change.After, cancellationToken);
        }
        var after = fitting.World.Attributes(ship.Id);
        events.AddRange(ShipChanges(ship.Id, before, after));

        var (condition, hullCollapsed) = fitting.Condition.RebaseHitpoints(
            oldHull,
            oldArmor,
            oldShield,
            fitting.World.Get(ship.Id, hull),
            fitting.World.Get(ship.Id, armor),
            fitting.World.Get(ship.Id, shield));
        await modules.SetShipConditionAsync(connection, transaction, ship.Id, condition, cancellationToken);

        Item? escapeShip = escapeCandidate;
        if (escapeShip is not null)
        {
            if (await items.LockAndGetAsync(connection, transaction, escapeShip.Id, cancellationToken) is not { } lockedEscape
                || lockedEscape.OwnerId != characterId || lockedEscape.LocationId != ship.Id
                || lockedEscape.Flag != FrigateEscapeBayFlag || !lockedEscape.Singleton || lockedEscape.Quantity != 1)
                return null;
            escapeShip = lockedEscape;
            await items.MoveAsync(connection, transaction, lockedEscape.Id, ship.LocationId, InSpaceFlag, cancellationToken);
            await characters.SetShipAsync(connection, transaction, characterId, lockedEscape.Id, cancellationToken);
        }

        Item? capsule = null;
        if (escapeShip is null)
        {
            var capsuleId = await items.CreateAsync(connection, transaction,
                new NewItem(CapsuleTypeId, characterId, ship.LocationId, InSpaceFlag, 1, true), cancellationToken);
            capsule = new Item(capsuleId, CapsuleTypeId, characterId, ship.LocationId, InSpaceFlag, 1, true, null);
            await characters.SetShipAsync(connection, transaction, characterId, capsuleId, cancellationToken);
        }
        var nextShipId = escapeShip?.Id ?? capsule?.Id ?? 0;
        await WriteTransitionAsync(connection, transaction,
            new ShipTransitionEvent("eject", characterId, ship.Id, nextShipId, ship.LocationId, ship.Flag,
                ship.LocationId, InSpaceFlag), cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        var ejectedSnapshot = fitting.WithDogma(fitting.World, condition, []);
        return new ShipEjection(ship, capsule, escapeShip, events, ejectedSnapshot,
            [.. moduleChanges.Select(change => change.ModuleId)], hullCollapsed);
    }

    /// <summary>Boards an unpiloted ship in the same solar system and transfers its hull and contents to the pilot.</summary>
    public async Task<ShipBoarding?> BoardInSpaceAsync(long characterId, int solarSystemId, long currentShipId, long shipId, CancellationToken cancellationToken = default)
    {
        if (currentShipId <= 0 || shipId <= 0 || currentShipId == shipId)
            return null;

        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockAsync(connection, transaction, characterId, cancellationToken);
        var character = await characters.GetAsync(characterId, cancellationToken);
        if (character is not { ShipId: { } activeShipId, SolarSystemId: { } currentSystem }
            || activeShipId != currentShipId || currentSystem != solarSystemId
            || await items.LockAndGetAsync(connection, transaction, currentShipId, cancellationToken) is not { } current
            || await items.LockAndGetAsync(connection, transaction, shipId, cancellationToken) is not { } next
            || current.OwnerId != characterId || current.LocationId != solarSystemId || current.Flag != InSpaceFlag || current.TypeId != CapsuleTypeId
            || next.LocationId != solarSystemId || next.Flag != InSpaceFlag || !next.Singleton || next.TypeId == CapsuleTypeId)
            return null;

        await statics.EnsureTypesAsync([current.TypeId, next.TypeId], cancellationToken);
        if (statics.Type(current.TypeId)?.CategoryId != 6 || statics.Type(next.TypeId)?.CategoryId != 6)
            return null;

        var previousOwnerId = next.OwnerId;
        var contents = new Dictionary<long, Item>();
        await TreeAsync(next.Id, contents, cancellationToken);
        foreach (var item in contents.Values)
        {
            await items.MoveToOwnerAsync(connection, transaction, item.Id, characterId, item.LocationId, item.Flag, cancellationToken);
        }
        await items.MoveToOwnerAsync(connection, transaction, next.Id, characterId, solarSystemId, InSpaceFlag, cancellationToken);
        await characters.SetShipAsync(connection, transaction, characterId, next.Id, cancellationToken);
        await WriteTransitionAsync(connection, transaction,
            new ShipTransitionEvent("board-in-space", characterId, next.Id, currentShipId,
                current.LocationId, current.Flag, next.LocationId, next.Flag), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new ShipBoarding(next.Id, previousOwnerId, characterId, character.CorporationId);
    }

    public Task<bool> RecoverFromSpaceAsync(long characterId, CancellationToken cancellationToken = default) => RecoverFromSpaceAsync(characterId, null, cancellationToken);

    /// <summary>
    /// As <see cref="RecoverFromSpaceAsync(long, CancellationToken)"/>, and the character is put in the solar system of that station too when
    /// <paramref name="placeOf"/> says where it is (a ship that has jumped left the system its character docked in last).
    /// </summary>
    public async Task<bool> RecoverFromSpaceAsync(long characterId, Func<long, (int SolarSystemId, int ConstellationId, int RegionId)?>? placeOf, CancellationToken cancellationToken = default)
    {
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockAsync(connection, transaction, characterId, cancellationToken);

        if (await characters.GetAsync(characterId, cancellationToken) is not { ShipId: { } shipId } character
            || await items.GetAsync(shipId, cancellationToken) is not { } ship || ship.OwnerId != characterId
            || ship.LocationId != character.SolarSystemId || ship.Flag != InSpaceFlag)
        {
            return false;
        }
        await RecoverDronesAsync(connection, transaction, characterId, character.SolarSystemId, ship.Id, cancellationToken);
        await items.MoveAsync(connection, transaction, ship.Id, character.StationId, ItemFlags.Hangar, cancellationToken);
        if (placeOf?.Invoke(character.StationId) is { } place && place.SolarSystemId != character.SolarSystemId)
        {
            await characters.SetLocationAsync(connection, transaction, characterId, place.SolarSystemId, place.ConstellationId, place.RegionId, cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return true;
    }
}
