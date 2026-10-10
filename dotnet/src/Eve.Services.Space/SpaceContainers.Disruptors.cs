using Eve.Inventory;
using Eve.Marshal;
using Eve.Scheduler;
using Eve.Services.Ships;
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

    /// <summary>The durable job that anchors a mobile deployable when its anchoring time is over (<see cref="AnchorAsync"/>).</summary>
    public const string AnchorJob = "space.deployed.anchor";

    /// <summary>The group of the mobile warp disruptors (EVE.js <c>GROUP_MOBILE_WARP_DISRUPTOR</c>).</summary>
    private const int MobileWarpDisruptorGroup = 361;

    /// <summary>The group of the Mobile Cynosural Inhibitor (EVE.js <c>GROUP_CYNO_INHIBITOR</c>).</summary>
    private const int MobileCynoInhibitorGroup = 1249;

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

    /// <summary>The range of a Mobile Cynosural Inhibitor: 100 km (the wiki; the SDE carries none for the type).</summary>
    private const double InhibitorRangeMeters = 100_000;

    /// <summary>The closest two inhibitors may be dropped: two of 100 km fields would overlap inside 200 km (the wiki: a second inhibitor "cannot be placed where two cyno
    /// fields would overlap").</summary>
    private const double InhibitorMinimumSpacingMeters = 200_000;

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
        if (_statics.Type(item.TypeId) is not { } type || type.GroupId is not (MobileWarpDisruptorGroup or MobileCynoInhibitorGroup))
        {
            return null;
        }
        var inhibitor = type.GroupId == MobileCynoInhibitorGroup;
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
        var range = inhibitor ? InhibitorRangeMeters : type.Attributes.GetValueOrDefault(WarpScrambleRangeAttribute);
        if (!inhibitor && range <= 0)
        {
            throw Refuse("That mobile warp disruptor has no warp disruption range.");
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

        var dropped = await _ships.DropDisruptorAsync(character, shipId, itemId, system, cancellationToken)
            ?? throw Refuse("That item is no longer in your ship's cargo hold.");
        var (deployable, changes) = dropped;
        foreach (var change in changes)
        {
            ShipNotifications.SendItemChange(session, change, RowOf);
        }

        var anchoringDelayMs = inhibitor
            ? (long)InhibitorAnchoringTime.TotalMilliseconds
            : (long)Math.Max(0, type.Attributes.GetValueOrDefault(AnchoringDelayAttribute));
        var activateAt = now + anchoringDelayMs;
        var lifetime = inhibitor ? InhibitorLifetime : LifetimeOf(item.TypeId);
        var corporation = (int)(session.GetInt("corpid") ?? 0);
        var alliance = (int)(session.GetInt("allianceid") ?? 0);
        await _deployed.SaveAsync(new DeployedRecord(
            deployable.Id, inhibitor ? InhibitorKind : DisruptorKind, system, type.Name, item.TypeId, type.GroupId, position, radius, character, corporation,
            alliance, shipId, now + (long)lifetime.TotalMilliseconds, activateAt, range), cancellationToken);
        if (inhibitor)
        {
            _sim.PlaceInhibitor(new SimPlaceInhibitor(
                deployable.Id, system, type.Name, item.TypeId, type.GroupId, position, radius, range, anchoringDelayMs <= 0, character, corporation, alliance, shipId));
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
        return deployable.Id;
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
        if (_deployed is not null && await _deployed.GetAsync(deployableId, cancellationToken) is { Kind: DisruptorKind or InhibitorKind })
        {
            _sim.SetDisruptorActive(deployableId, true);
        }
    }

    /// <summary>A mobile deployable whose time is up leaves space and its item is deleted (the sweep's entry for its kind).</summary>
    private async Task ExpireDisruptorAsync(DeployedRecord due)
    {
        _sim.RemoveDeployed(due.DeployableId);
        await _ships.DeleteDisruptorAsync(due.DeployableId);
    }

    private static MachoWrappedException Refuse(string text) => MachoErrors.ThrowUserError("CustomNotify", ("notify", new PyStr(text)));
}
