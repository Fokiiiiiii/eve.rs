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
/// Anchors an Upwell structure from a ship after validating its deployment point against the static system objects and live structures. The
/// inventory item is consumed with the structure row and <c>structure.anchored</c> event in one transaction; the simulation shows the structure
/// when a pilot is in the system.
/// </summary>
public sealed class StructureAnchorAuthority
{

    public const string AnchoredTopic = "structure.anchored";

    private const long DirectorRole = 1, StationManagerRole = 2048;
    private const long NpcCorporationFirst = 1_000_000, NpcCorporationLimit = 2_000_000;
    private const double MaximumDistanceFromShipM = 800_000;
    private const double MinimumMoonDistanceM = 4_000_000;
    private const double MaximumMiningBeaconDistanceM = 250_000;
    private const long MinimumCorporationAgeMs = 7 * StructureTimes.Day;
    private const int AutoMoonMinerTypeId = 81_826;
    private const int MoonMiningBeaconTypeId = 46_329;
    private const int MoonGroupId = 8, StargateGroupId = 10, UpwellJumpBridgeGroupId = 1_408, StructureCategoryId = 65;
    private const int FleetHangarFlag = 155, InfrastructureHoldFlag = 185;
    private static readonly int[] SourceFlags = [ItemFlags.Cargo, FleetHangarFlag, InfrastructureHoldFlag];
    private static readonly string[] StaticPlacementBallTables =
        ["mapAsteroidBelts", "mapMoons", "mapPlanets", "mapSecondarySuns", "mapStars", "mapStargates", "npcStations"];
    // V24 structures/deployment.py:10-39, 73-104; group/category IDs and distances match EveJS's
    // structureDeploymentPlacement.js:26-111. Radii come from the pinned SDE or each static ball record.
    private static readonly IReadOnlyDictionary<int, double> GroupDeploymentDistances = new Dictionary<int, double>
    {
        [1_915] = 0,
        [14] = 5_000, [186] = 5_000, [12] = 5_000, [306] = 5_000, [340] = 5_000, [448] = 5_000, [649] = 5_000, [1_978] = 5_000,
        [1_998] = 100_000, [226] = 100_000, [1_273] = 1_000_000, [1_876] = 2_000_000,
        [925] = 1_000_000, [1_408] = 500_000, [2_016] = 500_000, [2_017] = 200_000, [1_012] = 10_000_000,
    };
    private static readonly IReadOnlyDictionary<int, double> CategoryDeploymentDistances = new Dictionary<int, double>
    {
        [11] = 5_000, [6] = 5_000, [18] = 5_000, [87] = 5_000, [22] = 5_000, [25] = 5_000,
        [2] = 1_000_000, [46] = 1_000_000, [40] = 1_000_000, [3] = 1_000_000, [23] = 1_000_000, [65] = 1_000_000,
    };

    private readonly record struct PlacementBall(long ItemId, int TypeId, double X, double Y, double Z, double? Radius);

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
    private readonly Func<ClientSession, int?>? _solarSystemOf;
    private readonly Func<ClientSession, long?>? _shipIdOf;
    private readonly Func<ClientSession, bool> _isWarping;
    private readonly Func<long, Task> _placed;
    private readonly Func<long, CancellationToken, ValueTask<IReadOnlyList<StructureProfileRecord>>>? _profiles;
    private readonly Func<CancellationToken, ValueTask<(SovFwReadSnapshot Snapshot, IReadOnlyList<DevelopmentIndexRecord> DevelopmentIndices)>>? _sov;
    private readonly Func<NpgsqlConnection, NpgsqlTransaction, NewStructure, long, CancellationToken, Task<StructureTransition?>>? _anchorWithDuration;

    public StructureAnchorAuthority(
        NpgsqlDataSource source, StructureStore store, StructureTimers timers, StructureRulesSource rules, DurableScheduler scheduler, EventBus events,
        PgItemStore items, DogmaStatic dogma, Func<NpgsqlConnection, NpgsqlTransaction, long, CancellationToken, Task<long?>> corporationCreatedMs,
        Func<ClientSession, long, CancellationToken, Task<WarpCoordinates?>> shipPosition, Func<ClientSession, bool> isWarping, Func<long, Task> placed,
        Func<long, CancellationToken, ValueTask<IReadOnlyList<StructureProfileRecord>>>? profiles = null,
        Func<CancellationToken, ValueTask<(SovFwReadSnapshot Snapshot, IReadOnlyList<DevelopmentIndexRecord> DevelopmentIndices)>>? sov = null,
        Func<NpgsqlConnection, NpgsqlTransaction, NewStructure, long, CancellationToken, Task<StructureTransition?>>? anchorWithDuration = null,
        Func<ClientSession, int?>? solarSystemOf = null,
        Func<ClientSession, long?>? shipIdOf = null)
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
        _solarSystemOf = solarSystemOf;
        _shipIdOf = shipIdOf;
        _isWarping = isWarping;
        _placed = placed;
        _profiles = profiles;
        _sov = sov;
        _anchorWithDuration = anchorWithDuration;
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
        var systemId = _solarSystemOf?.Invoke(session) ?? (int)(session.GetInt("solarsystemid") ?? session.GetInt("solarsystemid2") ?? 0);
        var shipId = _shipIdOf?.Invoke(session) ?? session.GetInt("shipid") ?? 0;
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
        await _dogma.EnsureTypesAsync([item.TypeId], cancellationToken);
        var type = _dogma.Type(item.TypeId);
        var structureCategoryId = _dogma.CategoryId("Structure");
        if (!_rules.IsAnchorable(item.TypeId) || type is null ||
            structureCategoryId is not { } categoryId || type.CategoryId != categoryId)
        {
            throw Notice("That structure type is not supported by the current deployment service.");
        }
        var rules = _rules.RulesOf(item.TypeId, systemId);
        if (_rules.IsAnchorRestricted(item.TypeId, systemId, type.GroupId, type.CategoryId))
        {
            throw MachoErrors.ThrowUserError("CantDeployBlocked", ("typeID", new PyInt(item.TypeId)));
        }
        if (rules.Size == StructureSize.ExtraLarge && rules.Band == SecurityBand.HighSec)
        {
            throw MachoErrors.ThrowUserError("CantDeployBlocked", ("typeID", new PyInt(item.TypeId)));
        }

        var profileId = await ResolveProfileIdAsync(corporationId, request.ProfileId, cancellationToken);
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
        var allianceId = session.GetInt("allianceID") ?? session.GetInt("allianceid") ?? 0;
        var anchoringMs = await ResolveSovereigntyAnchoringMsAsync(rules, item.TypeId, systemId, allianceId, cancellationToken);
        if (anchoringMs != rules.AnchoringMs && _anchorWithDuration is null)
        {
            throw Notice("The sovereignty anchoring delay is unavailable.");
        }
        var structure = new NewStructure(
            item.Id, item.TypeId, systemId, x, y, z, request.RotationYaw ?? 0, corporationId, allianceId, name, request.Bio.Trim(), profileId,
            (int)weekday, (int)hour, AnchoringMs: anchoringMs);

        await _items.DeleteAsync(connection, transaction, item.Id, cancellationToken);
        var transition = _anchorWithDuration is null
            ? await _timers.AnchorAsync(connection, transaction, structure, cancellationToken)
            : await _anchorWithDuration(connection, transaction, structure, anchoringMs, cancellationToken);
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


    private async Task RequireFreeSpaceAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, int systemId, int typeId, double x, double y, double z, CancellationToken cancellationToken)
    {
        var balls = new List<PlacementBall>();
        foreach (var table in StaticPlacementBallTables)
        {
            balls.AddRange(await StaticPlacementBallsAsync(connection, transaction, table, systemId, cancellationToken));
        }
        foreach (var structure in await _store.ListAsync(connection, transaction, includeGone: false, cancellationToken))
        {
            if (structure.SolarSystemId == systemId)
                balls.Add(new PlacementBall(structure.Snapshot.StructureId, structure.TypeId, structure.X, structure.Y, structure.Z, null));
        }

        await _dogma.EnsureTypesAsync(balls.Select(ball => ball.TypeId).Append(typeId), cancellationToken);
        var deployedType = _dogma.Type(typeId)
            ?? throw new InvalidOperationException($"structure type {typeId} has no static dogma data");
        var deployedRadius = Radius(deployedType);
        var deployedDistance = DeploymentDistance(deployedType);
        var isDrillingPlatform = _dogma.GroupName(deployedType.GroupId) is "Refinery" or "Upwell Moon Drill";
        var hasMiningBeaconInRange = false;
        foreach (var ball in balls)
        {
            var blocker = _dogma.Type(ball.TypeId)
                ?? throw new InvalidOperationException($"placement blocker type {ball.TypeId} has no static dogma data");
            var blockerRadius = ball.Radius ?? Radius(blocker);
            var distance = Distance(x, y, z, ball.X, ball.Y, ball.Z);
            if (blocker.GroupId == MoonGroupId)
            {
                var beacon = Eve.Services.Space.MoonMiningPointCatalog.Resolve(ball.ItemId, systemId);
                var beaconInRange = beacon is { } point && Distance(x, y, z, point.X, point.Y, point.Z) < MaximumMiningBeaconDistanceM;
                hasMiningBeaconInRange |= beaconInRange;
                if (distance < MinimumMoonDistanceM + deployedRadius + blockerRadius && !(isDrillingPlatform && beaconInRange))
                    throw MachoErrors.ThrowUserError("CantDeployBlocked", ("typeID", new PyInt(ball.TypeId)));
            }

            if (deployedType.GroupId == UpwellJumpBridgeGroupId && blocker.GroupId == StargateGroupId &&
                distance < 100_000_000 + deployedRadius + blockerRadius)
                throw MachoErrors.ThrowUserError("CantDeployBlocked", ("typeID", new PyInt(ball.TypeId)));

            var minimum = DeploymentDistance(blocker);
            if (blocker.CategoryId == StructureCategoryId)
                minimum = Math.Min(minimum, deployedDistance);
            if (minimum > 0 && distance < minimum + deployedRadius + blockerRadius)
                throw MachoErrors.ThrowUserError("CantDeployBlocked", ("typeID", new PyInt(ball.TypeId)));
        }
        if (typeId == AutoMoonMinerTypeId && !hasMiningBeaconInRange)
            throw MachoErrors.ThrowUserError("CantDeployBlocked", ("typeID", new PyInt(MoonMiningBeaconTypeId)));
    }

    private static async Task<List<PlacementBall>> StaticPlacementBallsAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string table, int systemId, CancellationToken cancellationToken)
    {
        var objects = new List<PlacementBall>();
        await using var command = Sql.Command(connection, transaction,
            $"SELECT key, (doc->>'typeID')::int, (doc->>'radius')::float8, " +
            "COALESCE((doc->'position'->>'x')::float8, 0), COALESCE((doc->'position'->>'y')::float8, 0), " +
            $"COALESCE((doc->'position'->>'z')::float8, 0) FROM sde.\"{table}\" WHERE (doc->>'solarSystemID')::int = $1", systemId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var itemId = long.Parse(reader.GetString(0), System.Globalization.CultureInfo.InvariantCulture);
            objects.Add(new PlacementBall(itemId, reader.GetInt32(1), reader.GetDouble(3), reader.GetDouble(4), reader.GetDouble(5),
                reader.IsDBNull(2) ? null : reader.GetDouble(2)));
        }
        return objects;
    }

    internal static double Radius(TypeDef type) =>
        type.Attributes.TryGetValue(162, out var radius) && double.IsFinite(radius) && radius >= 0 ? radius : 0;

    internal static double DeploymentDistance(TypeDef type) =>
        GroupDeploymentDistances.TryGetValue(type.GroupId, out var groupDistance)
            ? groupDistance
            : CategoryDeploymentDistances.GetValueOrDefault(type.CategoryId);

    private async Task RequireCorporationAgeAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long corporationId, CancellationToken cancellationToken)
    {
        var created = corporationId > 0 ? await _corporationCreatedMs(connection, transaction, corporationId, cancellationToken) : null;
        if (created is not { } createdMs || _scheduler.Now - createdMs < MinimumCorporationAgeMs)
        {
            throw Notice("Your corporation must be at least seven days old to anchor a structure.");
        }
    }

    /// <summary>

    /// <c>hub</c> flag marks far more systems than the trade hubs, so it is not used.
    /// </summary>
    private static async Task<bool> IsTradeHubAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, int systemId, CancellationToken cancellationToken) =>
        await Sql.ScalarAsync(connection, transaction, "SELECT (doc->'name'->>'en') IN ('Jita', 'Amarr', 'Dodixie', 'Rens', 'Hek') FROM sde.\"mapSolarSystems\" WHERE key = $1", cancellationToken, systemId.ToString()) is true;

    private async ValueTask<long> ResolveProfileIdAsync(long corporationId, long? requestedProfileId, CancellationToken cancellationToken)
    {
        var readProfiles = _profiles ?? throw Notice("Structure profile data is unavailable.");
        var profiles = await readProfiles(corporationId, cancellationToken).ConfigureAwait(false);
        if (requestedProfileId is > 0 &&
            profiles.FirstOrDefault(profile => profile.ProfileId == requestedProfileId.Value) is { } requested)
            return requested.ProfileId;
        return profiles.FirstOrDefault(profile => profile.ProfileId > 0 && profile.IsDefault)?.ProfileId
            ?? throw Notice("The corporation has no usable default structure profile.");
    }

    internal async ValueTask<long> ResolveSovereigntyAnchoringMsAsync(
        StructureRules rules, int typeId, int systemId, long allianceId, CancellationToken cancellationToken)
    {
        if (rules.Band != SecurityBand.NullSec)
            return rules.AnchoringMs;
        var readSov = _sov ?? throw Notice("Sovereignty data is unavailable for this system.");
        var (snapshot, developmentIndices) = await readSov(cancellationToken).ConfigureAwait(false);
        var claimAllianceId = snapshot.ClaimsBySolarSystemId.TryGetValue(systemId, out var claim)
            ? claim.AllianceId
            : null;
        var hub = snapshot.HubsBySolarSystemId.TryGetValue(systemId, out var currentHub) ? currentHub : null;
        var ownerAllianceId = claimAllianceId ?? hub?.AllianceId;
        var development = developmentIndices.FirstOrDefault(index => index.SolarSystemId == systemId);
        if (ownerAllianceId is > 0 && ownerAllianceId != allianceId && rules.Size == StructureSize.Medium)
        {
            if (development is null)
                throw Notice("Sovereignty development indices are unavailable for this system.");

            if (ActivityDefenseMultiplier(development) > 4.0)
                throw MachoErrors.ThrowUserError("CantDeployBlocked", ("typeID", new PyInt(typeId)));
        }
        if (hub is { HubId: > 0, AllianceId: > 0 } && hub.AllianceId != allianceId)
        {
            if (development is null)
                throw Notice("Sovereignty development indices are unavailable for this system.");
            return checked(rules.AnchoringMs + StrategicIndexLevel(development.ClaimedFor) * StructureTimes.Day);
        }
        return rules.AnchoringMs;
    }

    private const long DevelopmentIndexLevelWidth = 100_000L * 96;


    internal static double ActivityDefenseMultiplier(DevelopmentIndexRecord indices)
    {
        var militaryLevel = OperationalIndexLevel(indices.MilitaryPoints);
        var industrialLevel = OperationalIndexLevel(indices.IndustrialPoints);
        var total = 1.0 + OperationalIndexBonus(militaryLevel) + OperationalIndexBonus(industrialLevel) +
            StrategicIndexBonus(indices.ClaimedFor);
        return Math.Max(1.0, Math.Min(6.0, total));
    }

    private static int OperationalIndexLevel(long points) =>
        (int)Math.Min(5, Math.Max(0, points) / DevelopmentIndexLevelWidth);

    private static double OperationalIndexBonus(int level) => level switch
    {
        1 => 0.6,
        2 => 1.2,
        3 => 1.7,
        4 => 2.1,
        5 => 2.5,
        _ => 0,
    };

    private static double StrategicIndexBonus(long claimedForDays) => StrategicIndexLevel(claimedForDays) switch
    {
        1 => 0.4,
        2 => 0.6,
        3 => 0.8,
        4 => 0.9,
        5 => 1.0,
        _ => 0,
    };

    private static int StrategicIndexLevel(long claimedForDays) => claimedForDays switch
    {
        >= 100 => 5,
        >= 65 => 4,
        >= 35 => 3,
        >= 21 => 2,
        >= 7 => 1,
        _ => 0,
    };

    private ItemRow RowOf(Item item)
    {
        var type = _dogma.Type(item.TypeId);
        return new ItemRow(item.Id, item.TypeId, item.OwnerId, item.LocationId, item.Flag, item.Quantity, type?.GroupId ?? 0, type?.CategoryId ?? 0);
    }

    /// <summary>The refusal the client shows: a named notice.</summary>
    private static Exception Notice(string message) => MachoErrors.ThrowUserError("CustomNotify", ("notify", new PyStr(message)));
}
