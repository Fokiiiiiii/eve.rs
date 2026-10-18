using Eve.Inventory;
using Eve.Marshal;
using Eve.Scheduler;
using Eve.Services.Ships;
using Eve.Services.Space.Beacons;
using Eve.Ships;
using Eve.Space;

namespace Eve.Services.Space;

/// <summary>
/// The mobile deployables a pilot drops from the hold (WP-8 and E-03; EVE.js <c>launchMobileWarpDisruptorFromShip</c> and <c>launchMobileCynoInhibitorFromShip</c>): a
/// mobile warp disruptor and a Mobile Cynosural Inhibitor. Each is the host's item and its record (ADR 0012, kind <c>mobile_warp_disruptor</c> or
/// <c>mobile_cyno_inhibitor</c>): it is in space from the drop, anchored when its anchoring time is over (a durable job, <see cref="AnchorJob"/>), and it leaves with its
/// item when its record's time is up (the sweep). A restart puts it back in space with the anchored state its record gives.
/// </summary>
public sealed partial class SpaceContainers
{
    /// <summary>The record kind of a mobile warp disruptor (the sweep's entry for it).</summary>
    private const string DisruptorKind = "mobile_warp_disruptor";

    /// <summary>The record kind of a Mobile Cynosural Inhibitor.</summary>
    private const string InhibitorKind = "mobile_cyno_inhibitor";

    /// <summary>The record kind of a Mobile Cynosural Beacon (a cynosural field a pilot may jump to while it is anchored).</summary>
    internal const string BeaconKind = "mobile_cyno_beacon";

    /// <summary>The group of the Mobile Cynosural Beacon (SDE 4093); its types are 57319 (standard), 58906 (highsec authorized) and 59630 (covert).</summary>
    private const int MobileCynoBeaconGroup = 4093;

    /// <summary>The highsec authorized beacon: the SDE's own type with Polaris 5 for high security, so the security cutoff does not apply to it.</summary>
    private const int HighsecAuthorizedBeaconType = 58906;

    /// <summary>The security at and above which a standard or covert beacon cannot be deployed (EveJS <c>MOBILE_CYNO_SECURITY_CUTOFF</c>).</summary>
    private const double BeaconSecurityCutoff = 0.45;

    /// <summary>The durable job that anchors a mobile deployable when its anchoring time is over (<see cref="AnchorAsync"/>).</summary>
    public const string AnchorJob = "space.deployed.anchor";

    /// <summary>The group of the mobile warp disruptors (EVE.js <c>GROUP_MOBILE_WARP_DISRUPTOR</c>).</summary>
    private const int MobileWarpDisruptorGroup = 361;

    /// <summary>The group of the Mobile Cynosural Inhibitor (EVE.js <c>GROUP_CYNO_INHIBITOR</c>).</summary>
    private const int MobileCynoInhibitorGroup = 1249;

    /// <summary>The group of the Mobile Observatory (SDE 4107; its type is 58904).</summary>
    private const int MobileObservatoryGroup = 4107;

    /// <summary>The group of the player control towers (SDE 365; the Amarr Control Tower is 12235).</summary>
    private const int ControlTowerGroup = 365;

    /// <summary>
    /// The record kind of a player control tower launched from a ship (protocol 73, ST-19): anchored by its durable job when its anchoring time (the SDE's) is over,
    /// in space until it is removed. It has no fuel or modules yet: the placing, anchoring and removal are all it does.
    /// </summary>
    internal const string ControlTowerKind = "control_tower";

    /// <summary>A control tower's record never ends by time (its removal is the host's, see <see cref="UnanchorControlTowerAsync"/>).</summary>
    private const long NeverExpires = long.MaxValue;

    /// <summary>The record kind of a Mobile Observatory (protocol 72): anchored by its durable job, pinging its system while it is anchored.</summary>
    internal const string ObservatoryKind = "mobile_observatory";

    /// <summary>The durable job that pings a Mobile Observatory's system (<see cref="PingObservatoryAsync"/>).</summary>
    public const string ObservatoryPingJob = "space.deployed.observatory_ping";

    /// <summary>The security at and above which an observatory cannot be deployed (EveJS <c>MOBILE_OBSERVATORY_SECURITY_CUTOFF</c>; the wiki: not in highsec).</summary>
    private const double ObservatorySecurityCutoff = 0.45;

    /// <summary>How close two observatories may be deployed, surface to surface (EveJS <c>MOBILE_OBSERVATORY_MIN_DISTANCE_FROM_OWN_GROUP_METERS</c>).</summary>
    private const double ObservatorySpacingMeters = 10_000;

    /// <summary>The distance an observatory keeps from a station, stargate or Upwell structure, surface to surface (EVE.js <c>MOBILE_OBSERVATORY_MIN_DISTANCE_FROM_STATION_STARGATE_UPWELL_METERS</c>).</summary>
    private const double ObservatoryLandmarkSpacingMeters = 1_000_000;

    /// <summary>The spacing from a player control tower: 100 km (the wiki, Mobile Observatory).</summary>
    private const double ObservatoryControlTowerSpacingMeters = 100_000;

    /// <summary>
    /// The radius of a deadspace pocket around its site's anchor: EveJS deadspaceWarpPolicy.js DEADSPACE_SITE_RADIUS_METERS (1e9 m). The wiki (Deadspace) gives no
    /// pocket size and the SDE none, so the EveJS parity constant is used (DEVIATIONS, ST-16).
    /// </summary>
    private const double DeadspaceSiteRadiusMeters = 1_000_000_000;

    /// <summary>The solar system ids of the Abyssal host locations (EVE.js <c>ABYSSAL_LOCATION_ID_MIN</c> to <c>ABYSSAL_LOCATION_ID_MAX</c>): an observatory is refused there.</summary>
    private const int AbyssalLocationMin = 32_000_001, AbyssalLocationMax = 32_000_200;

    /// <summary>
    /// The fixed bodies of a system (positions in metres) that a Mobile Observatory keeps away from: its stations, stargates and Upwell structures (EVE.js
    /// <c>collectPlacementBlockers</c>, kind stationStargateUpwell). Null: the check is not made.
    /// </summary>
    public Func<int, CancellationToken, Task<IReadOnlyList<double[]>>>? ReadObservatoryLandmarks { get; set; }

    /// <summary>The positions (metres) of the player control towers of a system; an observatory keeps 100 km from each.</summary>
    public Func<int, CancellationToken, Task<IReadOnlyList<double[]>>>? ReadControlTowers { get; set; }

    /// <summary>The anchors (metres) of the deadspace pockets of a system: the cosmic sites' positions. An observatory cannot be anchored inside one.</summary>
    public Func<int, CancellationToken, Task<IReadOnlyList<double[]>>>? ReadDeadspaceAnchors { get; set; }

    /// <summary>The anchoring time of an observatory: ten minutes (the wiki; EveJS <c>MOBILE_OBSERVATORY_ACTIVATION_DELAY_MS</c>; the SDE gives none for the type).</summary>
    private static readonly TimeSpan ObservatoryAnchoringTime = TimeSpan.FromMinutes(10);

    /// <summary>The time between an anchored observatory's pings: ten minutes (the wiki, "Mobile Observatory"; EveJS <c>MOBILE_OBSERVATORY_PING_INTERVAL_MS</c>).</summary>
    private static readonly TimeSpan ObservatoryPingInterval = TimeSpan.FromMinutes(10);

    /// <summary>How long an observatory lives: 100 minutes 30 seconds (EveJS <c>MOBILE_OBSERVATORY_LIFETIME_MS</c>; the wiki gives "up to 2 hours", see DEVIATIONS).</summary>
    private static readonly TimeSpan ObservatoryLifetime = TimeSpan.FromSeconds((60 + 40) * 60 + 30);

    /// <summary>The payload of the ping job: the observatory whose system it pings.</summary>
    public sealed record ObservatoryPingDue(long DeployableId);

    private const int WarpScrambleRangeAttribute = 103;
    private const int AnchoringDelayAttribute = 556;
    private const int DisallowInEmpireSpaceAttribute = 1074;

    /// <summary>The radius attribute (162): the SDE's radius of the type, which the dogma statics carry as an intrinsic attribute.</summary>
    private const int RadiusAttribute = 162;

    /// <summary>How far ahead of the ship, along its heading, a mobile deployable is dropped (EVE.js <c>DEPLOY_DISTANCE_METERS</c>). The wiki gives no deployment
    /// distance and the SDE none for a ship's deployable, so the reference's value stands (DEVIATIONS, WP-8).</summary>
    private const double DropDistance = 2500;

    /// <summary>The anchoring time of a Mobile Cynosural Inhibitor: two minutes (the wiki; EVE.js gives one minute, which the wiki's rule overrides).</summary>
    private static readonly TimeSpan InhibitorAnchoringTime = TimeSpan.FromMinutes(2);

    /// <summary>How long a Mobile Cynosural Inhibitor lives: one hour (the wiki).</summary>
    private static readonly TimeSpan InhibitorLifetime = TimeSpan.FromHours(1);

    /// <summary>The activation time of a beacon: two minutes, and one minute for the covert one (EveJS <c>MOBILE_CYNO_ACTIVATION_DELAY_MS</c>,
    /// <c>COVERT_MOBILE_CYNO_ACTIVATION_DELAY_MS</c>; the SDE carries no anchoring delay for the type).</summary>
    private static TimeSpan BeaconActivationDelay(int typeId) => typeId == JumpDriveRules.CovertBeaconType ? TimeSpan.FromMinutes(1) : TimeSpan.FromMinutes(2);

    /// <summary>The life of a beacon: one hour, and thirty minutes for the covert one (EveJS <c>MOBILE_CYNO_LIFETIME_MS</c>, <c>COVERT_MOBILE_CYNO_LIFETIME_MS</c>).</summary>
    private static TimeSpan BeaconLifetime(int typeId) => typeId == JumpDriveRules.CovertBeaconType ? TimeSpan.FromMinutes(30) : TimeSpan.FromHours(1);

    /// <summary>The range of a Mobile Cynosural Inhibitor: 200 km (the wiki, "Mobile Cynosural Inhibitor"; the SDE carries none for the type).</summary>
    private const double InhibitorRangeMeters = 200_000;

    /// <summary>The closest two inhibitors may be dropped: two of 200 km fields would overlap inside 400 km (the wiki: a second inhibitor "cannot be placed where two cyno
    /// fields would overlap").</summary>
    private const double InhibitorMinimumSpacingMeters = 400_000;

    /// <summary>The payload of the anchor job: the mobile deployable to anchor.</summary>
    public sealed record DisruptorAnchorDue(long DeployableId);

    /// <summary>
    /// How long a mobile warp disruptor lives (EVE.js <c>CURRENT_SDE_LIFETIME_MS_BY_TYPE_ID</c>; the SDE gives no lifetime, and the wiki's only lifetime for a disruptor is
    /// the Tournament Bubble's two days, so the reference's table stands for the standard models): two days for the Mk I, seven for the Mk II, fourteen for the
    /// Syndicate ones.
    /// </summary>
    private static TimeSpan LifetimeOf(int typeId) => TimeSpan.FromDays(typeId switch { 26888 or 26890 or 26892 => 7, 28770 or 28772 or 28774 => 14, _ => 2 });

    /// <summary>
    /// <c>ship.LaunchFromShip</c> of a mobile warp disruptor or a Mobile Cynosural Inhibitor in the hold: refused (the client's notice) when the pilot lacks the skill its
    /// type needs, when a disruptor is in empire space or has no range, when an inhibitor is too close to another; otherwise it goes out of the hold 2,500 m ahead of the
    /// ship on its heading and is anchored when its anchoring time is over. Answers the id of the item, or null when the item is neither (nothing is done).
    /// </summary>
    public async Task<long?> LaunchFromShipAsync(ClientSession session, long itemId, CancellationToken cancellationToken)
    {
        if (await _items.GetAsync(itemId, cancellationToken) is not { } item)
        {
            return null;
        }
        await _statics.EnsureTypesAsync([item.TypeId], cancellationToken);
        if (_statics.Type(item.TypeId) is not { } type || type.GroupId is not (MobileWarpDisruptorGroup or MobileCynoInhibitorGroup or MobileCynoBeaconGroup or MobileObservatoryGroup or ControlTowerGroup or AnalysisBeaconRules.Group))
        {
            return null;
        }
        if (_space.IsInAbyssalRun(session))
        {
            if (type.GroupId == AnalysisBeaconRules.Group)
                throw Refuse("Mobile Analysis Beacons cannot be deployed in Abyssal space.");
            if (type.GroupId == MobileCynoBeaconGroup)
                throw Refuse("Mobile Cynosural Beacons cannot be deployed in Abyssal space.");
            if (type.GroupId == MobileObservatoryGroup)
                throw Refuse("Mobile Observatories cannot be deployed in Abyssal space.");
            if (type.GroupId == ControlTowerGroup)
                throw Refuse("Control Towers cannot be deployed in Abyssal space.");
        }
        if (type.GroupId == AnalysisBeaconRules.Group)
        {
            // a CONCORD Rogue Analysis Beacon has its own runtime (Beacons/AnalysisBeaconDirector)
            return LaunchAnalysisBeacon is { } launchBeacon ? await launchBeacon(session, itemId, cancellationToken) : throw Unavailable();
        }
        var inhibitor = type.GroupId == MobileCynoInhibitorGroup;
        var beacon = type.GroupId == MobileCynoBeaconGroup;
        var observatory = type.GroupId == MobileObservatoryGroup;
        var tower = type.GroupId == ControlTowerGroup;
        if (session.CharacterId is not { } character || session.GetInt("shipid") is not { } shipId || shipId <= 0 || _deployed is null || _scheduler is null)
        {
            throw Unavailable();
        }
        var system = (int)(session.GetInt("solarsystemid2") ?? session.GetInt("solarsystemid") ?? 0);
        if (system <= 0 || !_space.IsInSpace(session))
        {
            throw Refuse("A mobile deployable is dropped from a ship in space.");
        }
        if (item.OwnerId != character || item.LocationId != shipId || item.Flag != CargoFlag)
        {
            throw Refuse("That item is not in your ship's cargo hold.");
        }
        if (await _ships.MissingSkillAsync(character, type.Attributes, cancellationToken) is { } missing)
        {
            await _statics.EnsureTypesAsync([missing.SkillTypeId], cancellationToken);
            throw Refuse($"{type.Name} deployment requires {_statics.Type(missing.SkillTypeId)?.Name ?? "a skill"} {missing.Level}.");
        }
        // the empire space rule is the SDE's (disallowInEmpireSpace on the type): a Mobile Cynosural Inhibitor carries none
        if (_space.SecurityOfSystem(system) is { } security && security > 0 && type.Attributes.GetValueOrDefault(DisallowInEmpireSpaceAttribute) > 0)
        {
            throw Refuse("Mobile Warp Disruptors cannot be activated in empire space.");
        }
        var range = inhibitor ? InhibitorRangeMeters : beacon || observatory || tower ? 0 : type.Attributes.GetValueOrDefault(WarpScrambleRangeAttribute);
        if (!inhibitor && !beacon && !observatory && !tower && range <= 0)
        {
            throw Refuse("That mobile warp disruptor has no warp disruption range.");
        }
        // the security cutoff of a beacon (EveJS); the highsec authorized one is exempt, by its own type
        if (beacon && item.TypeId != HighsecAuthorizedBeaconType && _space.SecurityOfSystem(system) is { } beaconSecurity && beaconSecurity >= BeaconSecurityCutoff)
        {
            throw Refuse("Mobile Cynosural Beacons can only be deployed below 0.5 security.");
        }
        // Pochven: a region-wide cynosural jammer stops anyone lighting a cyno there (the wiki, "Pochven"); the text is EveJS's. A beacon may be lit in wormhole
        // space (the wiki, "Cynosural field": "they can be lit in wormhole space"); EveJS refuses it there, see DEVIATIONS, Track A8c
        if (beacon && _space.RegionOfSystem(system) == JumpDriveRules.PochvenRegionId)
        {
            throw Refuse("Mobile Cynosural Beacons cannot be deployed in Pochven.");
        }
        // a Mobile Observatory is not deployed in wormhole space, in Pochven or above the security cutoff (the wiki, "Mobile Observatory"; EveJS's texts)
        if (observatory && system is >= AbyssalLocationMin and <= AbyssalLocationMax)
        {
            throw Refuse("Mobile Observatories cannot be deployed in Abyssal space.");
        }
        if (observatory && Eve.Services.Core.LocatorRules.IsWormhole(system))
        {
            throw Refuse("Mobile Observatories cannot be deployed in wormhole space.");
        }
        if (observatory && _space.RegionOfSystem(system) == JumpDriveRules.PochvenRegionId)
        {
            throw Refuse("Mobile Observatories cannot be deployed in Pochven.");
        }
        if (observatory && _space.SecurityOfSystem(system) is { } observatorySecurity && observatorySecurity >= ObservatorySecurityCutoff)
        {
            throw Refuse("Mobile Observatories can only be deployed below 0.5 security.");
        }
        var radius = type.Attributes.GetValueOrDefault(RadiusAttribute);
        if (radius <= 0)
        {
            throw Refuse("That mobile deployable has no size in the static data.");
        }
        // the point ahead of the ship on its heading, as the simulation has the ship (a ship with no heading is not dropped on a guess)
        if (await _space.DropPointAsync(session, DropDistance, cancellationToken) is not { } point)
        {
            throw Unavailable();
        }
        var position = new[] { point.X, point.Y, point.Z };
        var now = _nowUnixMs();
        if (inhibitor && await CloseToAnotherInhibitorAsync(system, position, now, cancellationToken))
        {
            throw Refuse("Another Mobile Cynosural Inhibitor is too close to deploy one here.");
        }
        if (observatory && await CloseToAnotherObservatoryAsync(system, position, radius, now, cancellationToken))
        {
            throw Refuse("Another Mobile Observatory is too close to deploy one here.");
        }
        if (observatory && await CloseToLandmarkAsync(system, position, radius, cancellationToken))
        {
            throw Refuse("A station, stargate or Upwell structure is too close to deploy a Mobile Observatory here.");
        }
        if (observatory && await CloseToControlTowerAsync(system, position, radius, cancellationToken))
        {
            throw Refuse("A player control tower is too close to deploy a Mobile Observatory here.");
        }
        if (observatory && await InDeadspaceAsync(system, position, cancellationToken))
        {
            throw Refuse("Mobile Observatories cannot be anchored in deadspace.");
        }

        var dropped = await _ships.DropDisruptorAsync(character, shipId, itemId, system, cancellationToken)
            ?? throw Refuse("That item is no longer in your ship's cargo hold.");
        var (deployable, changes) = dropped;
        foreach (var change in changes)
        {
            ShipNotifications.SendItemChange(session, change, RowOf);
        }

        var anchoringDelayMs = inhibitor
            ? (long)InhibitorAnchoringTime.TotalMilliseconds
            : observatory
                ? (long)ObservatoryAnchoringTime.TotalMilliseconds
            : beacon
                ? (long)BeaconActivationDelay(item.TypeId).TotalMilliseconds
                : (long)Math.Max(0, type.Attributes.GetValueOrDefault(AnchoringDelayAttribute));
        var activateAt = now + anchoringDelayMs;
        var lifetime = inhibitor ? InhibitorLifetime : observatory ? ObservatoryLifetime : beacon ? BeaconLifetime(item.TypeId) : LifetimeOf(item.TypeId);
        var expiresAt = tower ? NeverExpires : now + (long)lifetime.TotalMilliseconds;
        var corporation = (int)(session.GetInt("corpid") ?? 0);
        var alliance = (int)(session.GetInt("allianceid") ?? 0);
        await _deployed.SaveAsync(new DeployedRecord(
            deployable.Id, inhibitor ? InhibitorKind : beacon ? BeaconKind : observatory ? ObservatoryKind : tower ? ControlTowerKind : DisruptorKind, system, type.Name, item.TypeId, type.GroupId, position, radius, character, corporation,
            alliance, shipId, expiresAt, activateAt, range), cancellationToken);
        if (inhibitor)
        {
            _sim.PlaceInhibitor(new SimPlaceInhibitor(
                deployable.Id, system, type.Name, item.TypeId, type.GroupId, position, radius, range, anchoringDelayMs <= 0, character, corporation, alliance, shipId));
        }
        else if (beacon)
        {
            _sim.PlaceCynoBeacon(new SimPlaceCynoBeacon(
                deployable.Id, system, type.Name, item.TypeId, type.GroupId, position, radius, anchoringDelayMs <= 0, character, corporation, alliance, shipId));
        }
        else if (observatory)
        {
            _sim.PlaceObservatory(new SimPlaceObservatory(
                deployable.Id, system, type.Name, item.TypeId, type.GroupId, position, radius, anchoringDelayMs <= 0, character, corporation, alliance, shipId));
        }
        else if (tower)
        {
            _sim.PlaceControlTower(new SimPlaceControlTower(
                deployable.Id, system, type.Name, item.TypeId, type.GroupId, position, radius, anchoringDelayMs <= 0, character, corporation, alliance, shipId));
        }
        else
        {
            _sim.PlaceDisruptor(new SimPlaceDisruptor(
                deployable.Id, system, type.Name, item.TypeId, type.GroupId, position, radius, range, anchoringDelayMs <= 0, character, corporation, alliance, shipId));
        }
        if (anchoringDelayMs > 0)
        {
            await _scheduler.ScheduleAsync(new JobSpec(AnchorJob, activateAt)
            {
                Key = $"deployable:{deployable.Id}",
                Payload = new DisruptorAnchorDue(deployable.Id),
                OnDuplicate = OnDuplicate.Replace,
            }, cancellationToken);
        }
        // a control tower's fuel period starts when it is anchored: the first hour is paid then, and an empty bay leaves it offline (ST-23)
        if (tower && _scheduler is not null)
        {
            await _scheduler.ScheduleAsync(new JobSpec(ControlTowerFuelJob, activateAt)
            {
                Key = $"tower:{deployable.Id}",
                Payload = new ControlTowerFuelDue(deployable.Id),
                OnDuplicate = OnDuplicate.Replace,
            }, cancellationToken);
        }
        return deployable.Id;
    }

    /// <summary>Whether a station, stargate or Upwell structure of the system lies within the spacing of <paramref name="position"/>, surface to surface.</summary>
    private async Task<bool> CloseToLandmarkAsync(int system, double[] position, double radius, CancellationToken cancellationToken)
    {
        if (ReadObservatoryLandmarks is not { } read)
        {
            return false;
        }
        // shortcut: the landmarks' radii are not subtracted (a few km against a 1 000 km spacing), upgrade when the SDE radii are read
        foreach (var body in await read(system, cancellationToken))
        {
            if (MetersBetween(body, position) - radius < ObservatoryLandmarkSpacingMeters)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Whether <paramref name="position"/> lies inside a deadspace pocket of the system (within <see cref="DeadspaceSiteRadiusMeters"/> of an anchor).</summary>
    private async Task<bool> InDeadspaceAsync(int system, double[] position, CancellationToken cancellationToken)
    {
        if (ReadDeadspaceAnchors is { } read)
        {
            foreach (var anchor in await read(system, cancellationToken))
            {
                if (MetersBetween(anchor, position) <= DeadspaceSiteRadiusMeters)
                {
                    return true;
                }
            }
        }
        // a dungeon's pocket is its record (ST-23): the body in the scene at the origin, with the pocket's radius
        if (_deployed is not null)
        {
            foreach (var record in await _deployed.InSystemAsync(system, cancellationToken))
            {
                if (record.Kind == DeadspacePocketKind && MetersBetween(record.Position, position) <= record.Radius)
                {
                    return true;
                }
            }
        }
        return false;
    }

    /// <summary>Whether a player control tower of the system lies within 100 km of <paramref name="position"/>, surface to surface.</summary>
    private async Task<bool> CloseToControlTowerAsync(int system, double[] position, double radius, CancellationToken cancellationToken)
    {
        // shortcut: the towers' radii are not subtracted (a few km against a 100 km spacing), upgrade when the SDE radii are read
        if (ReadControlTowers is { } read)
        {
            foreach (var tower in await read(system, cancellationToken))
            {
                if (MetersBetween(tower, position) - radius < ObservatoryControlTowerSpacingMeters)
                {
                    return true;
                }
            }
        }
        // a tower launched from a ship is a record of the host (ST-19), not a structure
        foreach (var tower in await _deployed!.InSystemAsync(system, cancellationToken))
        {
            if (tower.Kind == ControlTowerKind && MetersBetween(tower.Position, position) - radius < ObservatoryControlTowerSpacingMeters)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Whether an observatory on record in the system lies within the spacing of <paramref name="position"/>, surface to surface (its time not up).</summary>
    private async Task<bool> CloseToAnotherObservatoryAsync(int system, double[] position, double radius, long now, CancellationToken cancellationToken)
    {
        foreach (var other in await _deployed!.InSystemAsync(system, cancellationToken))
        {
            if (other.Kind == ObservatoryKind && other.ExpiresAtMs > now && MetersBetween(other.Position, position) - other.Radius - radius < ObservatorySpacingMeters)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// The ping job: an anchored observatory pings its system (the simulation decloaks the cloaked ships there with a chance, see <c>remote_decloak_ping</c>) and
    /// its next ping is due in ten minutes while its record's time is not up. A record that is gone or not anchored pings nothing.
    /// </summary>
    public async Task PingObservatoryAsync(long deployableId, CancellationToken cancellationToken = default)
    {
        if (_deployed is null || _scheduler is null || await _deployed.GetAsync(deployableId, cancellationToken) is not { Kind: ObservatoryKind } record)
        {
            return;
        }
        var now = _nowUnixMs();
        if (record.ExpiresAtMs <= now || record.ActivateAtMs > now)
        {
            return;
        }
        _sim.RemoteDecloakPing(record.SolarSystemId, deployableId);
        var next = now + (long)ObservatoryPingInterval.TotalMilliseconds;
        if (next < record.ExpiresAtMs)
        {
            await _scheduler.ScheduleAsync(new JobSpec(ObservatoryPingJob, next)
            {
                Key = $"observatory:{deployableId}",
                Payload = new ObservatoryPingDue(deployableId),
                OnDuplicate = OnDuplicate.Replace,
            }, cancellationToken);
        }
    }

    /// <summary>Whether a Mobile Cynosural Inhibitor on record in the system lies within the spacing of <paramref name="position"/> (the record's time not up).</summary>
    private async Task<bool> CloseToAnotherInhibitorAsync(int system, double[] position, long now, CancellationToken cancellationToken)
    {
        foreach (var other in await _deployed!.InSystemAsync(system, cancellationToken))
        {
            if (other.Kind != InhibitorKind || other.ExpiresAtMs <= now)
            {
                continue;
            }
            var dx = other.Position[0] - position[0];
            var dy = other.Position[1] - position[1];
            var dz = other.Position[2] - position[2];
            if (Math.Sqrt(dx * dx + dy * dy + dz * dz) < InhibitorMinimumSpacingMeters)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// The anchoring time of a mobile deployable is over (the durable job): the simulation makes it anchored (a disruptor's warp disruption on, an inhibitor's fields
    /// refused). A deployable whose record is gone is left alone.
    /// </summary>
    public async Task AnchorAsync(long deployableId, CancellationToken cancellationToken = default)
    {
        if (_deployed is null || await _deployed.GetAsync(deployableId, cancellationToken) is not { Kind: DisruptorKind or InhibitorKind or BeaconKind or ObservatoryKind or ControlTowerKind } record)
        {
            return;
        }
        if (record.Kind == BeaconKind && await InhibitedAsync(record, _nowUnixMs(), cancellationToken))
        {
            // its activation is over and refused: the record keeps it unlit to its end (it is never lit, and a jump to it is refused as not lit)
            _log?.Invoke($"the beacon {deployableId} was not lit: an anchored Mobile Cynosural Inhibitor covers it");
            await _deployed.SaveAsync(record with { ActivateAtMs = NeverLit }, cancellationToken);
            return;
        }
        _sim.SetDisruptorActive(deployableId, true);
        if (record.Kind == ObservatoryKind && _scheduler is not null)
        {
            // the first ping comes when the observatory is anchored (the wiki), then one every ten minutes while it is
            await _scheduler.ScheduleAsync(new JobSpec(ObservatoryPingJob, _nowUnixMs())
            {
                Key = $"observatory:{deployableId}",
                Payload = new ObservatoryPingDue(deployableId),
                OnDuplicate = OnDuplicate.Replace,
            }, cancellationToken);
        }
        // a beacon that is lit is told to its fleet, as a field is when it is lit (the client's jump menu lists it)
        if (record.Kind == BeaconKind)
        {
            await AnnounceFieldAsync(record, true);
        }
    }

    /// <summary>
    /// The activation time of a beacon that could not be lit: never (the wiki: an anchored Mobile Cynosural Inhibitor "prevents the activation of cynos" within its
    /// range; the record is kept to its end, so a restart does not light it either).
    /// </summary>
    internal const long NeverLit = long.MaxValue;

    /// <summary>
    /// Whether an anchored Mobile Cynosural Inhibitor covers a beacon at its activation: an inhibitor in its system whose anchoring time is past, whose record's time
    /// is not up and whose range (the record's <c>RangeMeters</c>, 200 km) reaches the beacon. A covert beacon is not stopped (the wiki: "not covert cynos"). An
    /// existing lit beacon is not asked: an inhibitor does not remove existing cynos (the wiki, Cynosural field).
    /// </summary>
    private async Task<bool> InhibitedAsync(DeployedRecord beacon, long now, CancellationToken cancellationToken)
    {
        if (beacon.TypeId == JumpDriveRules.CovertBeaconType)
        {
            return false;
        }
        foreach (var other in await _deployed!.InSystemAsync(beacon.SolarSystemId, cancellationToken))
        {
            if (other.Kind == InhibitorKind && other.ActivateAtMs <= now && other.ExpiresAtMs > now && MetersBetween(other.Position, beacon.Position) <= other.RangeMeters)
            {
                return true;
            }
        }
        return false;
    }

    private static double MetersBetween(double[] a, double[] b)
    {
        var dx = a[0] - b[0];
        var dy = a[1] - b[1];
        var dz = a[2] - b[2];
        return Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    /// <summary>A beacon whose time is up is taken out of space, and its fleet is told it is gone.</summary>
    private async Task ExpireBeaconAsync(DeployedRecord due)
    {
        await ExpireDisruptorAsync(due);
        await AnnounceFieldAsync(due, false);
    }

    /// <summary>A mobile deployable whose time is up leaves space and its item is deleted (the sweep's entry for its kind).</summary>
    private async Task ExpireDisruptorAsync(DeployedRecord due)
    {
        _sim.RemoveDeployed(due.DeployableId);
        await _ships.DeleteDisruptorAsync(due.DeployableId);
    }

    private static MachoWrappedException Refuse(string text) => MachoErrors.ThrowUserError("CustomNotify", ("notify", new PyStr(text)));
}
