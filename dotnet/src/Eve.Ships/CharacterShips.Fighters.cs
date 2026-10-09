using Eve.Dogma;
using Eve.Inventory;
using Eve.Inventory.Mutation;

namespace Eve.Ships;

/// <summary>Why a squadron did not move between the fighter bay and a launch tube.</summary>
public enum FighterMoveRefusal
{
    NoShip,
    NotInBay,
    NotInSpace,
    NotASquadron,
    NoSuchTube,
    TubeOccupied,
    TubeEmpty,
    NoSlotForClass,
    ClassSlotsFull,
    TubesFull,
    BayFull,
}

/// <summary>
/// The answer to a squadron moved between the fighter bay and a tube: <paramref name="Change"/> is the item as the client is told it changed when it moved;
/// <paramref name="Refusal"/> says why it did not, with the room the bay had and the volume asked for when it was the bay that was full.
/// </summary>
public sealed record FighterMove(FighterMoveRefusal? Refusal, ItemChanged? Change = null, double Available = 0, double Volume = 0)
{
    public bool Accepted => Refusal is null;
}

/// <summary>
/// The fighter bay and launch tubes of a ship (EVE.js <c>fighterMgrService</c>, loading and unloading). A squadron is an item in the bay (flag 158) or in a
/// tube (flags 159-163) of the ship; the tubes do not count against the bay's volume, so a squadron that leaves a tube must find room in the bay.
/// Launching, recalling and abandoning move a squadron out of the ship into space and belong to the simulation (<c>ISpaceFighters</c>).
/// </summary>
public sealed partial class CharacterShips
{
    /// <summary>The fighter category of the static data: a squadron is an item of it.</summary>
    public const int FighterCategory = 87;

    /// <summary>A squadron in the fighter bay goes into a launch tube (the tube must be empty and fit the squadron's class).</summary>
    public async Task<FighterMove> LoadFighterToTubeAsync(long characterId, long shipId, long fighterId, int tubeFlag, CancellationToken cancellationToken = default)
    {
        if (!FighterTubes.IsTube(tubeFlag))
        {
            return new(FighterMoveRefusal.NoSuchTube);
        }
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockAsync(connection, transaction, characterId, cancellationToken);

        if (await LoadAsync(characterId, cancellationToken) is not { } snapshot || snapshot.Ship.Id != shipId)
        {
            return new(FighterMoveRefusal.NoShip);
        }
        if (await items.LockAndGetAsync(connection, transaction, fighterId, cancellationToken) is not { } fighter
            || fighter.OwnerId != characterId || fighter.LocationId != shipId || fighter.Flag != FighterBayFlag)
        {
            return new(FighterMoveRefusal.NotInBay);
        }
        if (await SquadronTypeAsync(fighter.TypeId, cancellationToken) is not { } squadron)
        {
            return new(FighterMoveRefusal.NotASquadron);
        }

        var tubes = (await items.InContainerAsync(shipId, cancellationToken)).Where(i => FighterTubes.IsTube(i.Flag)).ToList();
        if (tubes.Any(i => i.Flag == tubeFlag))
        {
            return new(FighterMoveRefusal.TubeOccupied);
        }
        var occupied = await BroadClassesAsync(tubes, cancellationToken);
        double HostAttribute(string name) => statics.AttributeId(name) is { } id ? snapshot.World.Get(shipId, id) : 0;
        double SquadronAttribute(string name) => TypeAttribute(squadron, name);
        if (FighterTubes.RefusalOf(HostAttribute, SquadronAttribute, tubeFlag, occupied) is { } tubeRefusal)
        {
            return new(FighterMoveRefusalOf(tubeRefusal));
        }

        await items.MoveAsync(connection, transaction, fighterId, shipId, tubeFlag, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(null, new ItemChanged(fighter with { Flag = tubeFlag }, shipId, FighterBayFlag, fighter.Quantity));
    }

    /// <summary>The squadron in a tube goes back into the fighter bay, when the bay has the volume for it.</summary>
    public async Task<FighterMove> UnloadTubeToBayAsync(long characterId, long shipId, int tubeFlag, CancellationToken cancellationToken = default)
    {
        if (!FighterTubes.IsTube(tubeFlag))
        {
            return new(FighterMoveRefusal.NoSuchTube);
        }
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockAsync(connection, transaction, characterId, cancellationToken);

        if (await LoadAsync(characterId, cancellationToken) is not { } snapshot || snapshot.Ship.Id != shipId)
        {
            return new(FighterMoveRefusal.NoShip);
        }
        var inTube = (await items.InContainerAsync(shipId, cancellationToken)).FirstOrDefault(i => i.Flag == tubeFlag);
        if (inTube is null || await items.LockAndGetAsync(connection, transaction, inTube.Id, cancellationToken) is not { } fighter
            || fighter.LocationId != shipId || fighter.Flag != tubeFlag)
        {
            return new(FighterMoveRefusal.TubeEmpty);
        }
        if (await SquadronTypeAsync(fighter.TypeId, cancellationToken) is not { } squadron)
        {
            return new(FighterMoveRefusal.NotASquadron);
        }

        var room = await RoomAsync(characterId, shipId, FighterBayFlag, cancellationToken);
        var free = room?.Free ?? 0;
        var required = ItemVolumes.Of(fighter, squadron.GroupId, ItemVolumeOf(squadron));
        if (required > free + RoomTolerance)
        {
            return new(FighterMoveRefusal.BayFull, null, Math.Round(free, 6), Math.Round(required, 6));
        }

        await items.MoveAsync(connection, transaction, fighter.Id, shipId, FighterBayFlag, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(null, new ItemChanged(fighter with { Flag = FighterBayFlag }, shipId, tubeFlag, fighter.Quantity));
    }

    /// <summary>
    /// The squadron in a launch tube goes out of the ship into its solar system, flagged as in space: the simulation then has it (a launch that the
    /// simulation refuses is taken back with <see cref="TakeFighterIntoTubeAsync"/>).
    /// </summary>
    public async Task<FighterMove> LaunchFighterFromTubeAsync(long characterId, long shipId, int tubeFlag, int solarSystemId, CancellationToken cancellationToken = default)
    {
        if (!FighterTubes.IsTube(tubeFlag))
        {
            return new(FighterMoveRefusal.NoSuchTube);
        }
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockAsync(connection, transaction, characterId, cancellationToken);

        if (await LoadAsync(characterId, cancellationToken) is not { } snapshot || snapshot.Ship.Id != shipId)
        {
            return new(FighterMoveRefusal.NoShip);
        }
        var inTube = (await items.InContainerAsync(shipId, cancellationToken)).FirstOrDefault(i => i.Flag == tubeFlag);
        if (inTube is null || await items.LockAndGetAsync(connection, transaction, inTube.Id, cancellationToken) is not { } fighter
            || fighter.OwnerId != characterId || fighter.LocationId != shipId || fighter.Flag != tubeFlag)
        {
            return new(FighterMoveRefusal.TubeEmpty);
        }
        if (await SquadronTypeAsync(fighter.TypeId, cancellationToken) is null)
        {
            return new(FighterMoveRefusal.NotASquadron);
        }

        await items.MoveAsync(connection, transaction, fighter.Id, solarSystemId, InSpaceFlag, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(null, new ItemChanged(fighter with { LocationId = solarSystemId, Flag = InSpaceFlag }, shipId, tubeFlag, fighter.Quantity));
    }

    /// <summary>A squadron that is out in the solar system goes back into a launch tube of its ship: a recall that has landed, or a launch the simulation refused.</summary>
    public async Task<FighterMove> TakeFighterIntoTubeAsync(long characterId, long shipId, long fighterId, int tubeFlag, int solarSystemId, CancellationToken cancellationToken = default)
    {
        if (!FighterTubes.IsTube(tubeFlag))
        {
            return new(FighterMoveRefusal.NoSuchTube);
        }
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockAsync(connection, transaction, characterId, cancellationToken);

        if (await LoadAsync(characterId, cancellationToken) is not { } snapshot || snapshot.Ship.Id != shipId)
        {
            return new(FighterMoveRefusal.NoShip);
        }
        if (await items.LockAndGetAsync(connection, transaction, fighterId, cancellationToken) is not { } fighter
            || fighter.OwnerId != characterId || fighter.LocationId != solarSystemId || fighter.Flag != InSpaceFlag)
        {
            return new(FighterMoveRefusal.NotInSpace);
        }
        if (await SquadronTypeAsync(fighter.TypeId, cancellationToken) is null)
        {
            return new(FighterMoveRefusal.NotASquadron);
        }
        var tubes = (await items.InContainerAsync(shipId, cancellationToken)).Where(i => FighterTubes.IsTube(i.Flag)).ToList();
        if (tubes.Any(i => i.Flag == tubeFlag))
        {
            return new(FighterMoveRefusal.TubeOccupied);
        }

        await items.MoveAsync(connection, transaction, fighter.Id, shipId, tubeFlag, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(null, new ItemChanged(fighter with { LocationId = shipId, Flag = tubeFlag }, solarSystemId, InSpaceFlag, fighter.Quantity));
    }

    /// <summary>
    /// A squadron let go in space is scooped back into its ship: into the fighter bay (when the bay has the volume for it) or into a launch tube (as a
    /// load does: the tube must be free and take the squadron's class). The simulation is asked afterwards; <see cref="ReleaseScoopedFighterAsync"/> puts it back
    /// when the simulation refuses.
    /// </summary>
    public async Task<FighterMove> ScoopFighterAsync(long characterId, long shipId, long fighterId, int solarSystemId, int flag, CancellationToken cancellationToken = default)
    {
        if (flag != FighterBayFlag && !FighterTubes.IsTube(flag))
        {
            return new(FighterMoveRefusal.NoSuchTube);
        }
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockAsync(connection, transaction, characterId, cancellationToken);

        if (await LoadAsync(characterId, cancellationToken) is not { } snapshot || snapshot.Ship.Id != shipId)
        {
            return new(FighterMoveRefusal.NoShip);
        }
        if (await items.LockAndGetAsync(connection, transaction, fighterId, cancellationToken) is not { } fighter
            || fighter.OwnerId != characterId || fighter.LocationId != solarSystemId || fighter.Flag != InSpaceFlag)
        {
            return new(FighterMoveRefusal.NotInSpace);
        }
        if (await SquadronTypeAsync(fighter.TypeId, cancellationToken) is not { } squadron)
        {
            return new(FighterMoveRefusal.NotASquadron);
        }
        if (FighterTubes.IsTube(flag))
        {
            var tubes = (await items.InContainerAsync(shipId, cancellationToken)).Where(i => FighterTubes.IsTube(i.Flag)).ToList();
            if (tubes.Any(i => i.Flag == flag))
            {
                return new(FighterMoveRefusal.TubeOccupied);
            }
            var occupied = await BroadClassesAsync(tubes, cancellationToken);
            double HostAttribute(string name) => statics.AttributeId(name) is { } id ? snapshot.World.Get(shipId, id) : 0;
            double SquadronAttribute(string name) => TypeAttribute(squadron, name);
            if (FighterTubes.RefusalOf(HostAttribute, SquadronAttribute, flag, occupied) is { } tubeRefusal)
            {
                return new(FighterMoveRefusalOf(tubeRefusal));
            }
        }
        else
        {
            var room = await RoomAsync(characterId, shipId, FighterBayFlag, cancellationToken);
            var free = room?.Free ?? 0;
            var required = ItemVolumes.Of(fighter, squadron.GroupId, ItemVolumeOf(squadron));
            if (required > free + RoomTolerance)
            {
                return new(FighterMoveRefusal.BayFull, null, Math.Round(free, 6), Math.Round(required, 6));
            }
        }

        await items.MoveAsync(connection, transaction, fighterId, shipId, flag, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(null, new ItemChanged(fighter with { LocationId = shipId, Flag = flag }, solarSystemId, InSpaceFlag, fighter.Quantity));
    }

    /// <summary>A squadron that was scooped into the ship goes back into space where it was, when the simulation refused the scoop.</summary>
    public async Task ReleaseScoopedFighterAsync(long characterId, long shipId, long fighterId, int solarSystemId, CancellationToken cancellationToken = default)
    {
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockAsync(connection, transaction, characterId, cancellationToken);
        if (await items.LockAndGetAsync(connection, transaction, fighterId, cancellationToken) is not { } fighter
            || fighter.OwnerId != characterId || fighter.LocationId != shipId)
        {
            return;
        }
        await items.MoveAsync(connection, transaction, fighterId, solarSystemId, InSpaceFlag, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>A squadron out in space that was destroyed: its item is deleted. Null when it is not the character's squadron out in space.</summary>
    public async Task<Item?> RemoveFighterAsync(long characterId, long fighterId, CancellationToken cancellationToken = default)
    {
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockAsync(connection, transaction, characterId, cancellationToken);
        if (await items.LockAndGetAsync(connection, transaction, fighterId, cancellationToken) is not { } fighter
            || fighter.OwnerId != characterId || fighter.Flag != InSpaceFlag)
        {
            return null;
        }
        await items.DeleteAsync(connection, transaction, fighter.Id, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return fighter;
    }

    /// <summary>
    /// A squadron out in space lost members: the item is the members it has left (a stack's quantity), so the database keeps the size the pilot was told.
    /// Returns false when the squadron is not the character's squadron out in space or is a single one (which has no members to lose).
    /// </summary>
    public async Task<bool> SetSquadronSizeAsync(long characterId, long fighterId, int size, CancellationToken cancellationToken = default)
    {
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockAsync(connection, transaction, characterId, cancellationToken);
        if (await items.LockAndGetAsync(connection, transaction, fighterId, cancellationToken) is not { } fighter
            || fighter.OwnerId != characterId || fighter.Flag != InSpaceFlag || fighter.Singleton || size < 1)
        {
            return false;
        }
        await items.SetQuantityAsync(connection, transaction, fighterId, size, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    /// <summary>The squadrons of a ship: the ones in its tubes and the ones in its fighter bay, as the database has them.</summary>
    public async Task<IReadOnlyList<Item>> FightersOfShipAsync(long shipId, CancellationToken cancellationToken = default)
    {
        var contents = (await items.InContainerAsync(shipId, cancellationToken))
            .Where(i => i.Flag == FighterBayFlag || FighterTubes.IsTube(i.Flag))
            .ToList();
        await statics.EnsureTypesAsync(contents.Select(i => i.TypeId), cancellationToken);
        return [.. contents.Where(i => statics.Type(i.TypeId)?.CategoryId == FighterCategory)];
    }

    /// <summary>The squadrons of a character that are out in a solar system (the ones launched and not yet taken back).</summary>
    public async Task<IReadOnlyList<Item>> FightersInSpaceAsync(long characterId, int solarSystemId, CancellationToken cancellationToken = default)
    {
        var contents = (await items.InLocationAsync(solarSystemId, characterId, cancellationToken))
            .Where(i => i.Flag == InSpaceFlag)
            .ToList();
        await statics.EnsureTypesAsync(contents.Select(i => i.TypeId), cancellationToken);
        return [.. contents.Where(i => statics.Type(i.TypeId)?.CategoryId == FighterCategory)];
    }

    private async Task<TypeDef?> SquadronTypeAsync(int typeId, CancellationToken cancellationToken)
    {
        await statics.EnsureTypesAsync([typeId], cancellationToken);
        return statics.Type(typeId) is { CategoryId: FighterCategory } type ? type : null;
    }

    private async Task<List<FighterBroadClass>> BroadClassesAsync(IReadOnlyCollection<Item> squadrons, CancellationToken cancellationToken)
    {
        await statics.EnsureTypesAsync(squadrons.Select(i => i.TypeId), cancellationToken);
        var classes = new List<FighterBroadClass>(squadrons.Count);
        foreach (var squadron in squadrons)
        {
            if (statics.Type(squadron.TypeId) is { } type && FighterTubes.ClassOf(name => TypeAttribute(type, name)) is { } definition)
            {
                classes.Add(definition.Broad);
            }
        }
        return classes;
    }

    private double TypeAttribute(TypeDef type, string name) =>
        statics.AttributeId(name) is { } id ? type.Attributes.GetValueOrDefault(id, 0) : 0;

    private static FighterMoveRefusal FighterMoveRefusalOf(FighterTubeRefusal refusal) => refusal switch
    {
        FighterTubeRefusal.NoSuchTube => FighterMoveRefusal.NoSuchTube,
        FighterTubeRefusal.NotASquadron => FighterMoveRefusal.NotASquadron,
        FighterTubeRefusal.NoSlotForClass => FighterMoveRefusal.NoSlotForClass,
        FighterTubeRefusal.ClassSlotsFull => FighterMoveRefusal.ClassSlotsFull,
        FighterTubeRefusal.TubesFull => FighterMoveRefusal.TubesFull,
        _ => throw new ArgumentOutOfRangeException(nameof(refusal)),
    };
}
