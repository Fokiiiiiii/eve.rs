using Eve.Dogma;
using Eve.Fitting;
using Eve.Inventory;

namespace Eve.Ships;

/// <summary>
/// <c>AssembleShip</c>: a packaged ship in a station's hangar becomes an assembled one, and a Strategic Cruiser takes the subsystems that complete it
/// (EveJS <c>shipService.js</c> <c>Handle_AssembleShip</c> with its <c>_buildStrategicCruiserAssemblyPlan</c>; the client's
/// <c>invItemFunctions.AssembleShip</c>). The subsystems and the assembly are one transaction per ship.
/// </summary>
public sealed partial class CharacterShips
{
    private const int ShipCategory = 6, SubsystemCategory = 32;

    /// <summary>The longest name an assembled ship is given (EveJS <c>SHIP_NAME_MAX_LENGTH</c>).</summary>
    public const int ShipNameMaxLength = 32;

    /// <summary>
    /// Assembles the ships of the character that are packaged in its hangar at <paramref name="stationId"/>. Ships that cannot be assembled are left out (the
    /// reference leaves them out, and logs why). Answers what changed of each ship that was assembled, in the order asked.
    /// </summary>
    public async Task<IReadOnlyList<ShipChanged>> AssembleAsync(
        long characterId, long stationId, IReadOnlyList<long> shipIds, IReadOnlyList<long> subsystemIds, string? name, CancellationToken cancellationToken = default)
    {
        var changes = new List<ShipChanged>();
        foreach (var shipId in shipIds.Distinct())
        {
            if (await AssembleOneAsync(characterId, stationId, shipId, subsystemIds, name, cancellationToken) is { } changed)
            {
                changes.Add(changed);
            }
        }
        return changes;
    }

    private async Task<ShipChanged?> AssembleOneAsync(
        long characterId, long stationId, long shipId, IReadOnlyList<long> subsystemIds, string? name, CancellationToken cancellationToken)
    {
        var before = await LoadShipAsync(characterId, shipId, cancellationToken);
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockAsync(connection, transaction, characterId, cancellationToken);
        if (await items.LockAndGetAsync(connection, transaction, shipId, cancellationToken) is not { } ship
            || ship.OwnerId != characterId || ship.LocationId != stationId || ship.Flag != ItemFlags.Hangar || ship.Singleton)
        {
            return null;
        }
        await statics.EnsureTypesAsync([ship.TypeId], cancellationToken);
        if (statics.Type(ship.TypeId) is not { CategoryId: ShipCategory } shipType)
        {
            return null;
        }

        var plan = new List<(Item Subsystem, int Flag)>();
        if (shipType.GroupId == StrategicCruisers.GroupId)
        {
            if (await PlanSubsystemsAsync(connection, transaction, characterId, stationId, ship, subsystemIds, cancellationToken) is not { } chosen)
            {
                return null;
            }
            plan.AddRange(chosen);
        }

        foreach (var (subsystem, flag) in plan)
        {
            await items.MoveAsync(connection, transaction, subsystem.Id, ship.Id, flag, cancellationToken);
            await modules.SetAsync(connection, transaction, subsystem.Id, ItemState.Offline, cancellationToken);
        }
        await items.MakeSingletonAsync(connection, transaction, ship.Id, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        var label = NormalizeName(name);
        var assembled = ship with { Singleton = true, Quantity = 1 };
        if (label is not null && await items.SetCustomNameAsync(ship.Id, label, cancellationToken))
        {
            assembled = assembled with { CustomName = label };
        }
        var after = await LoadShipAsync(characterId, shipId, cancellationToken);
        var events = before is null || after is null
            ? []
            : ShipChanges(ship.Id, before.World.Attributes(ship.Id), after.World.Attributes(ship.Id)).Cast<DogmaEvent>().ToList();
        var moved = plan.Select(p => new ItemMoved(p.Subsystem with { LocationId = ship.Id, Flag = p.Flag }, p.Subsystem.LocationId, p.Subsystem.Flag)).ToList();
        return new ShipChanged(ship.Id, ship.Id, moved, events, [new ItemChanged(assembled, ship.LocationId, ship.Flag, ship.Quantity)]);
    }

    /// <summary>
    /// The subsystems a Strategic Cruiser takes to be complete: one chosen subsystem for each class the hull lacks, each in the station's hangar, for this hull, and
    /// no class chosen twice. Null when the hull already has duplicate subsystems or the choice does not complete it.
    /// </summary>
    private async Task<List<(Item Subsystem, int Flag)>?> PlanSubsystemsAsync(
        Npgsql.NpgsqlConnection connection, Npgsql.NpgsqlTransaction transaction, long characterId, long stationId, Item ship,
        IReadOnlyList<long> subsystemIds, CancellationToken cancellationToken)
    {
        var contents = await items.InLocationAsync(ship.Id, characterId, cancellationToken);
        await statics.EnsureTypesAsync(contents.Select(i => i.TypeId), cancellationToken);
        var fittedFlags = contents.Where(i => IsSubsystem(i.TypeId)).Select(i => i.Flag).ToList();
        if (fittedFlags.Count != fittedFlags.Distinct().Count())
        {
            return null;
        }
        var missing = StrategicCruisers.Missing(fittedFlags);
        var chosen = new List<(Item Subsystem, int Flag)>();
        var taken = new HashSet<int>();
        foreach (var id in subsystemIds.Distinct())
        {
            if (await items.LockAndGetAsync(connection, transaction, id, cancellationToken) is not { } subsystem
                || subsystem.OwnerId != characterId || subsystem.LocationId != stationId || subsystem.Flag != ItemFlags.Hangar)
            {
                return null;
            }
            await statics.EnsureTypesAsync([subsystem.TypeId], cancellationToken);
            if (!IsSubsystem(subsystem.TypeId) || statics.Type(subsystem.TypeId) is not { } type)
            {
                return null;
            }
            if (!type.Attributes.TryGetValue(StrategicCruisers.FitsToShipTypeAttribute, out var fitsTo) || (int)fitsTo != ship.TypeId
                || !type.Attributes.TryGetValue(StrategicCruisers.SubSystemSlotAttribute, out var slot))
            {
                return null;
            }
            var flag = (int)slot;
            if (!missing.Contains(flag) || !taken.Add(flag))
            {
                return null;
            }
            chosen.Add((subsystem, flag));
        }
        return chosen.Count == missing.Count ? chosen : null;
    }

    private bool IsSubsystem(int typeId) => statics.Type(typeId)?.CategoryId == SubsystemCategory;

    /// <summary>The name a ship is given (EveJS <c>normalizeShipNameLabel</c>): no line breaks, trimmed, at most <see cref="ShipNameMaxLength"/> characters.</summary>
    public static string? NormalizeName(string? name)
    {
        if (name is null)
        {
            return null;
        }
        var label = System.Text.RegularExpressions.Regex.Replace(name, "[\r\n]+", " ").Trim();
        if (label.Length > ShipNameMaxLength)
        {
            label = label[..ShipNameMaxLength].Trim();
        }
        return label.Length == 0 ? null : label;
    }
}
