using System.Text.Json;
using Eve.Data;
using Eve.Dogma;
using Eve.Events;
using Eve.Inventory;
using Eve.Marshal;
using Eve.Scheduler;
using Eve.Services;
using Eve.Services.Core;
using Eve.Services.Ships;
using Eve.Services.Space;
using Eve.Ships;
using Eve.Structures;
using Npgsql;

namespace Eve.Host;

/// <summary>
/// <c>structureDeployment.Anchor</c>: an Upwell structure item in the pilot's ship is anchored in space beside the ship. The gates are EveJS's
/// (<c>sovPlayerDeployment.js</c> <c>requireValidDeploymentSession</c>, <c>deployStructureFromInventoryItem</c>, <c>deployGenericStructureFromItem</c>) and the wiki's
/// (EVE University "Anchoring structures", "Upwell structures"): in space and not in warp, a Station Manager, Director or CEO of a player corporation at least
/// seven days old, 1000 km from every station, structure and belt, not in a trade hub, an extra-large structure not in high-security space. The item is
/// consumed and the structure created in one transaction with <c>structure.anchored</c>; the simulation shows the structure when a pilot is in the system.
/// </summary>
public sealed class StructureAnchorAuthority
{
    /// <summary>The event of a new anchored structure (the subscribers are the notices of ST-2 d and the placement).</summary>
    public const string AnchoredTopic = "structure.anchored";

    private const long DirectorRole = 1, StationManagerRole = 2048;
    private const long NpcCorporationFirst = 1_000_000, NpcCorporationLimit = 2_000_000;
    private const double MaximumDistanceFromShipM = 75_000;
    private const double MinimumDistanceFromObjectsM = 1_000_000;
    private const long MinimumCorporationAgeMs = 7 * StructureTimes.Day;
    private const int FleetHangarFlag = 155, InfrastructureHoldFlag = 185;
    private static readonly int[] SourceFlags = [ItemFlags.Cargo, FleetHangarFlag, InfrastructureHoldFlag];

    private readonly NpgsqlDataSource _source;
    private readonly StructureStore _store;
    private readonly StructureTimers _timers;
    private readonly StructureRulesSource _rules;
    private readonly DurableScheduler _scheduler;
    private readonly EventBus _events;
    private readonly PgItemStore _items;
    private readonly DogmaStatic _dogma;
    private readonly Func<NpgsqlConnection, NpgsqlTransaction, long, CancellationToken, Task<long?>> _corporationCreatedMs;
    private readonly Func<ClientSession, long, CancellationToken, Task<WarpCoordinates?>> _shipPosition;
    private readonly Func<ClientSession, bool> _isWarping;
    private readonly Func<long, Task> _placed;

    public StructureAnchorAuthority(
        NpgsqlDataSource source, StructureStore store, StructureTimers timers, StructureRulesSource rules, DurableScheduler scheduler, EventBus events,
        PgItemStore items, DogmaStatic dogma, Func<NpgsqlConnection, NpgsqlTransaction, long, CancellationToken, Task<long?>> corporationCreatedMs,
        Func<ClientSession, long, CancellationToken, Task<WarpCoordinates?>> shipPosition, Func<ClientSession, bool> isWarping, Func<long, Task> placed)
    {
        _source = source;
        _store = store;
        _timers = timers;
        _rules = rules;
        _scheduler = scheduler;
        _events = events;
        _items = items;
        _dogma = dogma;
        _corporationCreatedMs = corporationCreatedMs;
        _shipPosition = shipPosition;
        _isWarping = isWarping;
        _placed = placed;
    }

    /// <summary>The anchoring of the item <c>request.ItemId</c> from the ship the pilot flies.</summary>
    public async Task AnchorAsync(ServiceCall call, StructureAnchorRequest request, CancellationToken cancellationToken = default)
    {
        var session = call.Session;
        var characterId = session.CharacterId ?? 0;
        if (characterId <= 0)
        {
            throw Notice("Select a character before deploying a structure.");
        }
        var systemId = (int)(session.GetInt("solarsystemid") ?? session.GetInt("solarsystemid2") ?? 0);
        var shipId = session.GetInt("shipid") ?? 0;
        if (systemId <= 0 || shipId <= 0)
        {
            throw Notice("You must be in space to deploy a structure.");
        }
        if (_isWarping(session))
        {
            throw MachoErrors.ThrowUserError("ShipInWarp");
        }
        var corporationId = session.GetInt("corporationID") ?? session.GetInt("corpid") ?? 0;
        if (corporationId is >= NpcCorporationFirst and < NpcCorporationLimit)
        {
            throw MachoErrors.ThrowUserError("DropNeedsPlayerCorp");
        }
        RequireRole(session);

        var weekday = request.ReinforceWeekday ?? StructureTimes.DefaultReinforceWeekday;
        var hour = request.ReinforceHour ?? StructureTimes.DefaultReinforceHour;
        if (weekday != StructureTimes.NoReinforceWeekday && (weekday < 0 || weekday > 6))
        {
            throw Notice("Invalid reinforcement day.");
        }
        if (hour < 0 || hour > 23)
        {
            throw Notice("Invalid reinforcement hour.");
        }

        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var item = await _items.LockAndGetAsync(connection, transaction, request.ItemId, cancellationToken);
        if (item is null || item.OwnerId != characterId || item.LocationId != shipId || !SourceFlags.Contains(item.Flag))
        {
            throw MachoErrors.ThrowUserError("TargetingAttemptCancelled");
        }
        if (!_rules.IsAnchorable(item.TypeId))
        {
            throw Notice("That structure type is not supported by the current deployment service.");
        }
        await _dogma.EnsureTypesAsync([item.TypeId], cancellationToken);
        var rules = _rules.RulesOf(item.TypeId, systemId);
        if (rules.Size == StructureSize.ExtraLarge && rules.Band == SecurityBand.HighSec)
        {
            throw MachoErrors.ThrowUserError("CantDeployBlocked", ("typeID", new PyInt(item.TypeId)));
        }

        var ship = await _shipPosition(session, shipId, cancellationToken);
        if (ship is not { } shipAt || shipAt.SolarSystemId != systemId)
        {
            throw Notice("You must be in space to deploy a structure.");
        }
        var (x, y, z) = Place(shipAt, request.X, request.Z);
        if (Distance(x, y, z, shipAt.X, shipAt.Y, shipAt.Z) > MaximumDistanceFromShipM)
        {
            throw Notice("That structure is too far away to deploy.");
        }
        await RequireFreeSpaceAsync(connection, transaction, systemId, item.TypeId, x, y, z, cancellationToken);
        if (await IsTradeHubAsync(connection, transaction, systemId, cancellationToken))
        {
            throw MachoErrors.ThrowUserError("CantDeployBlocked", ("typeID", new PyInt(item.TypeId)));
        }
        await RequireCorporationAgeAsync(connection, transaction, corporationId, cancellationToken);

        var name = StructureDeploymentAuthority.Normalize(request.StructureName);
        if (name.Length == 0)
        {
            name = _dogma.Type(item.TypeId)?.Name is { Length: > 0 } typeName ? typeName : $"Structure {item.TypeId}";
        }
        var profileId = request.ProfileId ?? 0;
        var allianceId = session.GetInt("allianceID") ?? session.GetInt("allianceid") ?? 0;

        await _items.DeleteAsync(connection, transaction, item.Id, cancellationToken);
        var transition = await _timers.AnchorAsync(connection, transaction, new NewStructure(
            item.Id, item.TypeId, systemId, x, y, z, request.RotationYaw ?? 0, corporationId, allianceId, name, request.Bio.Trim(), profileId,
            (int)weekday, (int)hour), cancellationToken);
        if (transition is null)
        {
            throw Notice("That inventory item ID is already in use by a live structure.");
        }
        await _events.PublishInTransactionAsync(connection, transaction, AnchoredTopic,
            new StructureAnchored(item.Id, systemId, item.TypeId, corporationId), $"structure:{item.Id}", cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        _scheduler.Wake();

        ShipNotifications.SendItemChange(session, new ItemChanged(item with { LocationId = CharacterShips.JunkLocation }, item.LocationId, item.Flag, item.Quantity), RowOf);
        await _placed(item.Id);
    }

    /// <summary>A Station Manager, a Director or the CEO (wiki "Anchoring structures"). The corporation's own check is in the record's gate.</summary>
    private static void RequireRole(ClientSession session)
    {
        var roles = session.GetInt("corprole") ?? 0;
        var ceo = session.Get("isCEO") is PyBool { Value: true };
        if (!ceo && (roles & (StationManagerRole | DirectorRole)) == 0)
        {
            throw MachoErrors.ThrowUserError("CrpAccessDenied", ("reason", new PyStr("Insufficient roles")));
        }
    }

    /// <summary>
    /// The placement of EveJS <c>resolvePositionFromClientRequest</c>: the client sends ship-relative offsets or ballpark coordinates; the one inside the deployment
    /// range is taken, and the relative reading is kept when both are.
    /// </summary>
    internal static (double X, double Y, double Z) Place(WarpCoordinates ship, double? x, double? z)
    {
        var rawX = x ?? 0;
        var rawZ = z ?? 0;
        var relative = Math.Sqrt(rawX * rawX + rawZ * rawZ);
        var absolute = Math.Sqrt((rawX - ship.X) * (rawX - ship.X) + (rawZ - ship.Z) * (rawZ - ship.Z));
        return absolute <= MaximumDistanceFromShipM && relative > MaximumDistanceFromShipM
            ? (rawX, ship.Y, rawZ)
            : (ship.X + rawX, ship.Y, ship.Z + rawZ);
    }

    private static double Distance(double ax, double ay, double az, double bx, double by, double bz) =>
        Math.Sqrt((ax - bx) * (ax - bx) + (ay - by) * (ay - by) + (az - bz) * (az - bz));

    /// <summary>1000 km from every station, belt and other structure of the system (wiki "Anchoring structures"; the blocker's type is the answer, as EveJS gives it).</summary>
    private async Task RequireFreeSpaceAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, int systemId, int typeId, double x, double y, double z, CancellationToken cancellationToken)
    {
        foreach (var structure in await _store.ListAsync(connection, transaction, includeGone: false, cancellationToken))
        {
            if (structure.SolarSystemId == systemId && Distance(x, y, z, structure.X, structure.Y, structure.Z) < MinimumDistanceFromObjectsM)
            {
                throw MachoErrors.ThrowUserError("CantDeployBlocked", ("typeID", new PyInt(structure.TypeId)));
            }
        }
        foreach (var (blockerType, bx, by, bz) in await StaticObjectsAsync(connection, transaction, "npcStations", systemId, cancellationToken))
        {
            if (Distance(x, y, z, bx, by, bz) < MinimumDistanceFromObjectsM)
            {
                throw MachoErrors.ThrowUserError("CantDeployBlocked", ("typeID", new PyInt(blockerType)));
            }
        }
        foreach (var (blockerType, bx, by, bz) in await StaticObjectsAsync(connection, transaction, "mapAsteroidBelts", systemId, cancellationToken))
        {
            if (Distance(x, y, z, bx, by, bz) < MinimumDistanceFromObjectsM)
            {
                throw MachoErrors.ThrowUserError("CantDeployBlocked", ("typeID", new PyInt(blockerType)));
            }
        }
    }

    private static async Task<List<(int TypeId, double X, double Y, double Z)>> StaticObjectsAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string table, int systemId, CancellationToken cancellationToken)
    {
        var objects = new List<(int, double, double, double)>();
        await using var command = Sql.Command(connection, transaction,
            $"SELECT (doc->>'typeID')::int, (doc->'position'->>'x')::float8, (doc->'position'->>'y')::float8, (doc->'position'->>'z')::float8 FROM sde.\"{table}\" WHERE (doc->>'solarSystemID')::int = $1", systemId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            objects.Add((reader.GetInt32(0), reader.GetDouble(1), reader.GetDouble(2), reader.GetDouble(3)));
        }
        return objects;
    }

    private async Task RequireCorporationAgeAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long corporationId, CancellationToken cancellationToken)
    {
        var created = corporationId > 0 ? await _corporationCreatedMs(connection, transaction, corporationId, cancellationToken) : null;
        if (created is not { } createdMs || _scheduler.Now - createdMs < MinimumCorporationAgeMs)
        {
            throw Notice("Your corporation must be at least seven days old to anchor a structure.");
        }
    }

    /// <summary>
    /// The trade hubs by name (wiki "Anchoring structures": Jita, Amarr etc.; the five of EveJS <c>incursionPlanner.js</c> <c>TRADE_HUB_SYSTEM_NAMES</c>). The SDE's
    /// <c>hub</c> flag marks far more systems than the trade hubs, so it is not used.
    /// </summary>
    private static async Task<bool> IsTradeHubAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, int systemId, CancellationToken cancellationToken) =>
        await Sql.ScalarAsync(connection, transaction, "SELECT (doc->'name'->>'en') IN ('Jita', 'Amarr', 'Dodixie', 'Rens', 'Hek') FROM sde.\"mapSolarSystems\" WHERE key = $1", cancellationToken, systemId.ToString()) is true;

    private ItemRow RowOf(Item item)
    {
        var type = _dogma.Type(item.TypeId);
        return new ItemRow(item.Id, item.TypeId, item.OwnerId, item.LocationId, item.Flag, item.Quantity, type?.GroupId ?? 0, type?.CategoryId ?? 0);
    }

    /// <summary>The refusal the client shows: a named notice.</summary>
    private static Exception Notice(string message) => MachoErrors.ThrowUserError("CustomNotify", ("notify", new PyStr(message)));
}
