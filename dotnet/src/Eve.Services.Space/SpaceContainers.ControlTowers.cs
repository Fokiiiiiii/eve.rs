using Eve.Dogma;
using Eve.Inventory;
using Eve.Macho;
using Eve.Scheduler;
using Eve.Ships;
using Eve.Space;
using Eve.Structures;
using Npgsql;

namespace Eve.Services.Space;

/// <summary>
/// The fuel and the unanchoring of a player control tower (ST-23; EVE.js <c>controlTowerRuntime.js</c> and <c>posMgr.UnanchorStructure</c>). A tower's fuel
/// period is one hour: the job pays the bay's fuel blocks of its race for the period, and an empty bay takes it offline until the next period finds fuel.
/// The unanchoring is asked by its corporation while the tower is offline; after the SDE's unanchoring delay the tower leaves space, and its item is not
/// deleted: it is dropped at its place as an unanchor drop (the structure rule, <see cref="PlaceUnanchorDropAsync"/>), where the corporation can scoop it.
/// The simulation has no online state: a tower is anchored in space whether or not it is fuelled (DEVIATIONS, ST-23).
/// </summary>
public sealed partial class SpaceContainers
{
    /// <summary>The durable job of a control tower's fuel period (keyed <c>tower:{id}</c>).</summary>
    public const string ControlTowerFuelJob = "space.control_tower.fuel_hour";

    /// <summary>The durable job that completes a control tower's unanchoring (keyed <c>tower:{id}</c>).</summary>
    public const string ControlTowerUnanchorJob = "space.control_tower.unanchor";

    /// <summary>The fuel state of a control tower: its period was paid.</summary>
    public const string TowerOnline = "online";

    /// <summary>The fuel state of a control tower whose last period was not paid (or that is not yet fuelled).</summary>
    public const string TowerOffline = "offline";

    /// <summary>The fuel state of a control tower whose unanchoring has been asked: it burns no more fuel.</summary>
    public const string TowerUnanchoring = "unanchoring";

    /// <summary>The payload of the fuel job: the tower whose period it pays.</summary>
    public sealed record ControlTowerFuelDue(long DeployableId);

    /// <summary>The payload of the unanchor job: the tower that leaves space.</summary>
    public sealed record ControlTowerUnanchorDue(long DeployableId);

    /// <summary>
    /// Pays a control tower's fuel period in the job's transaction and schedules the next one. A tower that is unanchoring, or is not recorded, is left alone.
    /// </summary>
    public async Task ControlTowerFuelAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long towerId, long periodStartMs, CancellationToken cancellationToken = default)
    {
        if (_deployed is null || _scheduler is null || await _deployed.GetAsync(towerId, cancellationToken) is not { Kind: ControlTowerKind } record)
        {
            return;
        }
        if (await _deployed.TowerStateAsync(towerId, cancellationToken) == TowerUnanchoring)
        {
            return;
        }
        await _statics.EnsureTypesAsync([record.TypeId], cancellationToken);
        var type = _statics.Type(record.TypeId)
            ?? throw new InvalidOperationException($"SDE type {record.TypeId} for control tower {towerId} is missing.");
        var paid = await _ships.TakeTowerFuelAsync(
            connection, transaction, towerId, ControlTowerFuelRules.FuelBlockOf(type), ControlTowerFuelRules.FuelPerPeriod(type), cancellationToken);
        await _deployed.SetTowerStateAsync(connection, transaction, towerId, paid ? TowerOnline : TowerOffline, cancellationToken);
        await _scheduler.ScheduleAsync(connection, transaction, new JobSpec(ControlTowerFuelJob, periodStartMs + ControlTowerFuelRules.PeriodMs(type))
        {
            Key = $"tower:{towerId}",
            Payload = new ControlTowerFuelDue(towerId),
            OnDuplicate = OnDuplicate.Replace,
        }, cancellationToken);
    }

    /// <summary>
    /// posMgr.UnanchorStructure: the launching corporation asks for a control tower to be unanchored. It must be anchored and offline, in the pilot's system; the
    /// unanchoring runs for the SDE's delay (the unanchor job) and burns no fuel meanwhile. Asking again while it runs is accepted.
    /// </summary>
    public async Task UnanchorControlTowerAsync(ClientSession session, long towerId, CancellationToken cancellationToken = default)
    {
        if (_deployed is null || _scheduler is null || await _deployed.GetAsync(towerId, cancellationToken) is not { Kind: ControlTowerKind } record)
        {
            throw Refuse("That control tower is not in space.");
        }
        var system = (int)(session.GetInt("solarsystemid2") ?? session.GetInt("solarsystemid") ?? 0);
        if (record.CorporationId != (int)(session.GetInt("corpid") ?? 0) || record.SolarSystemId != system)
        {
            throw Refuse("Only your corporation's control tower in your system can be unanchored.");
        }
        if (record.ActivateAtMs > _nowUnixMs())
        {
            throw Refuse("The control tower is not anchored yet.");
        }
        var state = await _deployed.TowerStateAsync(towerId, cancellationToken);
        if (state == TowerUnanchoring)
        {
            return;
        }
        if (state != TowerOffline)
        {
            throw Refuse("The control tower must be offline before it can be unanchored.");
        }
        await _statics.EnsureTypesAsync([record.TypeId], cancellationToken);
        var type = _statics.Type(record.TypeId)
            ?? throw new InvalidOperationException($"SDE type {record.TypeId} for control tower {towerId} is missing.");
        await _deployed.SetTowerStateAsync(towerId, TowerUnanchoring, cancellationToken);
        await _scheduler.ScheduleAsync(new JobSpec(ControlTowerUnanchorJob, _nowUnixMs() + ControlTowerFuelRules.UnanchoringDelayMs(type))
        {
            Key = $"tower:{towerId}",
            Payload = new ControlTowerUnanchorDue(towerId),
            OnDuplicate = OnDuplicate.Replace,
        }, cancellationToken);
    }

    /// <summary>
    /// The end of a control tower's unanchoring (the unanchor job): it leaves space, its record is forgotten, and its item stays, dropped where the tower was
    /// (the unanchor drop rule: a marked item in space, scoopable by its corporation). A tower that is no longer unanchoring is left alone.
    /// </summary>
    public async Task CompleteControlTowerUnanchorAsync(long towerId, CancellationToken cancellationToken = default)
    {
        if (_deployed is null || await _deployed.GetAsync(towerId, cancellationToken) is not { Kind: ControlTowerKind } record
            || await _deployed.TowerStateAsync(towerId, cancellationToken) != TowerUnanchoring)
        {
            return;
        }
        _sim.RemoveDeployed(towerId);
        if (await _items.GetAsync(towerId, cancellationToken) is null)
        {
            // the item is gone (a manual removal): there is nothing to drop
            await _deployed.RemoveAsync(towerId, cancellationToken);
            return;
        }
        await _ships.SetCustomInfoAsync(towerId, StructureUnanchorDrop.HullMarker(towerId), cancellationToken);
        await _spaceItems.SaveAsync(new SpaceItem(
            towerId, record.SolarSystemId, record.Position, [1.0, 0.0, 0.0], record.Radius, record.GroupId, record.ShipId,
            record.OwnerId, record.CorporationId, record.AllianceId, 0, NeverExpires), cancellationToken);
        await PlaceUnanchorDropAsync(towerId, StructureUnanchorDrop.HullMarker(towerId), cancellationToken);
        await _deployed.RemoveAsync(towerId, cancellationToken);
    }
}
