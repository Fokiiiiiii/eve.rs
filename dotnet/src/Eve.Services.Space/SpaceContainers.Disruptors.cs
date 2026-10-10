using Eve.Inventory;
using Eve.Marshal;
using Eve.Scheduler;
using Eve.Services.Ships;
using Eve.Ships;
using Eve.Space;

namespace Eve.Services.Space;

/// <summary>
/// The mobile warp disruptors a pilot drops from the hold (WP-8; EVE.js <c>launchMobileWarpDisruptorFromShip</c>). The disruptor is the host's item and its record (ADR 0012,
/// kind <c>mobile_warp_disruptor</c>): it is in space from the drop, anchored when its anchoring delay is over (a durable job, <see cref="AnchorJob"/>), and it leaves with
/// its item when its record's time is up (the sweep). A restart puts it back in space with the anchored state its record gives.
/// </summary>
public sealed partial class SpaceContainers
{
    /// <summary>The record kind of a mobile warp disruptor (the sweep's entry for it).</summary>
    private const string DisruptorKind = "mobile_warp_disruptor";

    /// <summary>The durable job that anchors a disruptor when its anchoring delay is over (<see cref="AnchorAsync"/>).</summary>
    public const string AnchorJob = "space.deployed.anchor";

    /// <summary>The group of the mobile warp disruptors (EVE.js <c>GROUP_MOBILE_WARP_DISRUPTOR</c>).</summary>
    private const int MobileWarpDisruptorGroup = 361;
    private const int WarpScrambleRangeAttribute = 103;
    private const int AnchoringDelayAttribute = 556;
    private const int DisallowInEmpireSpaceAttribute = 1074;

    /// <summary>How far ahead of the ship a disruptor is dropped (EVE.js <c>DEPLOY_DISTANCE_METERS</c>).
    /// shortcut: the host has no heading for a ship, so it is dropped this far along x; upgrade when the heading is kept.</summary>
    private const double DropDistance = 2500;

    /// <summary>The ball radius of a disruptor. shortcut: the SDE radius (70 m for the small Mk I) is not carried by the host; upgrade when the type carries it.</summary>
    private const double DisruptorRadiusFallback = 40;

    /// <summary>The payload of the anchor job: the disruptor to anchor.</summary>
    public sealed record DisruptorAnchorDue(long DeployableId);

    /// <summary>
    /// How long a disruptor lives (EVE.js <c>CURRENT_SDE_LIFETIME_MS_BY_TYPE_ID</c>; the SDE gives no lifetime and the wiki names one only for the Tournament Bubble):
    /// two days for the Mk I, seven for the Mk II, fourteen for the Syndicate ones.
    /// </summary>
    private static TimeSpan LifetimeOf(int typeId) => TimeSpan.FromDays(typeId switch { 26888 or 26890 or 26892 => 7, 28770 or 28772 or 28774 => 14, _ => 2 });

    /// <summary>
    /// <c>ship.LaunchFromShip</c> of a mobile warp disruptor in the hold: refused (the client's notice) when the pilot lacks the skill its type needs, when the solar system
    /// is empire space, or when it has no warp disruption range; otherwise it goes out of the hold 2,500 m from the ship and is anchored when its anchoring delay is over.
    /// Answers the id of the disruptor, or null when the item is not a mobile warp disruptor (nothing is done).
    /// </summary>
    public async Task<long?> LaunchFromShipAsync(ClientSession session, long itemId, CancellationToken cancellationToken)
    {
        if (await _items.GetAsync(itemId, cancellationToken) is not { } item)
        {
            return null;
        }
        await _statics.EnsureTypesAsync([item.TypeId], cancellationToken);
        if (_statics.Type(item.TypeId) is not { } type || type.GroupId != MobileWarpDisruptorGroup)
        {
            return null;
        }
        if (session.CharacterId is not { } character || session.GetInt("shipid") is not { } shipId || shipId <= 0 || _deployed is null || _scheduler is null)
        {
            throw Unavailable();
        }
        var system = (int)(session.GetInt("solarsystemid2") ?? session.GetInt("solarsystemid") ?? 0);
        if (system <= 0 || !_space.IsInSpace(session))
        {
            throw Refuse("A mobile warp disruptor is dropped from a ship in space.");
        }
        if (item.OwnerId != character || item.LocationId != shipId || item.Flag != CargoFlag)
        {
            throw Refuse("That mobile warp disruptor is not in your ship's cargo hold.");
        }
        if (await _ships.MissingSkillAsync(character, type.Attributes, cancellationToken) is { } missing)
        {
            await _statics.EnsureTypesAsync([missing.SkillTypeId], cancellationToken);
            throw Refuse($"{type.Name} deployment requires {_statics.Type(missing.SkillTypeId)?.Name ?? "a skill"} {missing.Level}.");
        }
        if (_space.SecurityOfSystem(system) is { } security && security > 0 && type.Attributes.GetValueOrDefault(DisallowInEmpireSpaceAttribute) > 0)
        {
            throw Refuse("Mobile Warp Disruptors cannot be activated in empire space.");
        }
        var range = type.Attributes.GetValueOrDefault(WarpScrambleRangeAttribute);
        if (range <= 0)
        {
            throw Refuse("That mobile warp disruptor has no warp disruption range.");
        }
        if (await _space.PositionOfAsync(session, shipId, cancellationToken) is not { } origin)
        {
            throw Unavailable();
        }
        var position = new[] { origin.X + DropDistance, origin.Y, origin.Z };

        var dropped = await _ships.DropDisruptorAsync(character, shipId, itemId, system, cancellationToken)
            ?? throw Refuse("That mobile warp disruptor is no longer in your ship's cargo hold.");
        var (disruptor, changes) = dropped;
        foreach (var change in changes)
        {
            ShipNotifications.SendItemChange(session, change, RowOf);
        }

        var now = _nowUnixMs();
        var anchoringDelayMs = (long)Math.Max(0, type.Attributes.GetValueOrDefault(AnchoringDelayAttribute));
        var activateAt = now + anchoringDelayMs;
        var corporation = (int)(session.GetInt("corpid") ?? 0);
        var alliance = (int)(session.GetInt("allianceid") ?? 0);
        await _deployed.SaveAsync(new DeployedRecord(
            disruptor.Id, DisruptorKind, system, type.Name, item.TypeId, MobileWarpDisruptorGroup, position, DisruptorRadiusFallback, character, corporation, alliance,
            shipId, now + (long)LifetimeOf(item.TypeId).TotalMilliseconds, activateAt, range), cancellationToken);
        _sim.PlaceDisruptor(new SimPlaceDisruptor(
            disruptor.Id, system, type.Name, item.TypeId, MobileWarpDisruptorGroup, position, DisruptorRadiusFallback, range, anchoringDelayMs <= 0,
            character, corporation, alliance, shipId));
        if (anchoringDelayMs > 0)
        {
            await _scheduler.ScheduleAsync(new JobSpec(AnchorJob, activateAt)
            {
                Key = $"disruptor:{disruptor.Id}",
                Payload = new DisruptorAnchorDue(disruptor.Id),
                OnDuplicate = OnDuplicate.Replace,
            }, cancellationToken);
        }
        return disruptor.Id;
    }

    /// <summary>
    /// The anchoring delay of a disruptor is over (the durable job): the simulation turns its warp disruption on. A disruptor whose record is gone is left alone.
    /// </summary>
    public async Task AnchorAsync(long deployableId, CancellationToken cancellationToken = default)
    {
        if (_deployed is not null && await _deployed.GetAsync(deployableId, cancellationToken) is { Kind: DisruptorKind })
        {
            _sim.SetDisruptorActive(deployableId, true);
        }
    }

    /// <summary>A disruptor whose time is up leaves space and its item is deleted (the sweep's entry for its kind).</summary>
    private async Task ExpireDisruptorAsync(DeployedRecord due)
    {
        _sim.RemoveDeployed(due.DeployableId);
        await _ships.DeleteDisruptorAsync(due.DeployableId);
    }

    private static MachoWrappedException Refuse(string text) => MachoErrors.ThrowUserError("CustomNotify", ("notify", new PyStr(text)));
}
