using Eve.Ships;
using Eve.Space;

namespace Eve.Services.Space;

/// <summary>
/// The deployables the host keeps a record of (ADR 0012): a cynosural field the simulation says it lit. The record is the host's own, on its own clock: the
/// expiry sweep takes the field out when its time is up, and a restart puts a field back in space for what is left of its life. A record of a kind is run by its
/// entry in <see cref="_deployedExpiry"/> (one per kind, not an if chain).
/// </summary>
public sealed partial class SpaceContainers
{
    /// <summary>The record kind of a cynosural field (the only kind the simulation says it lit).</summary>
    internal const string CynoFieldKind = "cyno_field";

    /// <summary>The restore entries of the record kinds that have their own runtime (a CRAB): put back in space when a ship comes into the system, with what is left of the record's life.</summary>
    private readonly Dictionary<string, Func<DeployedRecord, long, Task>> _deployedRestore = new();

    /// <summary>The launch of a CRAB from the hold, set by the composition (<see cref="Beacons.AnalysisBeaconDirector.LaunchAsync"/>).</summary>
    public Func<ClientSession, long, CancellationToken, Task<long?>>? LaunchAnalysisBeacon { get; set; }

    /// <summary>
    /// Gives a record kind that has its own runtime its expiry (run by the sweep, before the record is forgotten) and its restore (run when a ship comes into the system).
    /// </summary>
    public void RegisterDeployedKind(string kind, Func<DeployedRecord, Task> expire, Func<DeployedRecord, long, Task> restore)
    {
        _deployedExpiry[kind] = expire;
        _deployedRestore[kind] = restore;
    }

    /// <summary>
    /// Whether a cynosural target is lit at <paramref name="now"/>: a cynosural field whose time is not up, or a Mobile Cynosural Beacon that is anchored (its
    /// activation time is past) and whose time is not up. The jump drive, the bridges and the conduit all take the same rule.
    /// </summary>
    internal static bool IsLitField(DeployedRecord field, long now) =>
        field.ExpiresAtMs > now && (field.Kind == CynoFieldKind || (field.Kind == BeaconKind && field.ActivateAtMs <= now));

    /// <summary>A cynosural field was lit: its record is kept until its time is up, for the life it had when it was lit.</summary>
    private async Task KeepDeployedAsync(SimDeployed lit)
    {
        if (_deployed is null)
        {
            return;
        }
        try
        {
            var record = new DeployedRecord(
                lit.DeployableId, CynoFieldKind, lit.SolarSystemId, lit.Name, lit.TypeId, lit.GroupId, [.. lit.Position], lit.Radius, lit.OwnerId, lit.CorporationId,
                lit.AllianceId, lit.ShipId, _nowUnixMs() + (long)lit.LifetimeMs);
            await _deployed.SaveAsync(record);
            await AnnounceFieldAsync(record, true);
        }
        catch (Exception error)
        {
            _log?.Invoke($"the cynosural field {lit.DeployableId} was not kept: {error.Message}");
        }
    }

    /// <summary>
    /// Tells the fleet of a field's lighter that the field is lit (or gone): the client's jump menu lists the fields it is told of (track A6). Set by the composition;
    /// a failure to tell is logged and does not undo the record.
    /// </summary>
    public Func<DeployedRecord, bool, Task>? FieldAnnounced { get; set; }

    private async Task AnnounceFieldAsync(DeployedRecord field, bool lit)
    {
        if (FieldAnnounced is not { } announce)
        {
            return;
        }
        try
        {
            await announce(field, lit);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            _log?.Invoke($"the cynosural field {field.DeployableId} was not announced: {error.Message}");
        }
    }

    /// <summary>
    /// A ship came into a system: the cynosural fields whose records say they are there and whose time is not up are put back in space, with the id they had and
    /// what is left of their life. A record whose time is up is left to the sweep.
    /// </summary>
    internal async Task RestoreDeployedAsync(int system)
    {
        if (_deployed is null)
        {
            return;
        }
        try
        {
            var now = _nowUnixMs();
            foreach (var record in await _deployed.InSystemAsync(system))
            {
                if (record.ExpiresAtMs <= now)
                {
                    continue;
                }
                if (_deployedRestore.TryGetValue(record.Kind, out var restore))
                {
                    await restore(record, now);
                    continue;
                }
                if (record.Kind == InhibitorKind)
                {
                    _sim.PlaceInhibitor(new SimPlaceInhibitor(
                        record.DeployableId, record.SolarSystemId, record.Name, record.TypeId, record.GroupId, record.Position, record.Radius, record.RangeMeters,
                        record.ActivateAtMs <= now, record.OwnerId, record.CorporationId, record.AllianceId, record.ShipId));
                    continue;
                }
                if (record.Kind == DisruptorKind)
                {
                    // anchored when its record says so; a disruptor still waiting to anchor is anchored by its durable job, which outlives the restart
                    _sim.PlaceDisruptor(new SimPlaceDisruptor(
                        record.DeployableId, record.SolarSystemId, record.Name, record.TypeId, record.GroupId, record.Position, record.Radius, record.RangeMeters,
                        record.ActivateAtMs <= now, record.OwnerId, record.CorporationId, record.AllianceId, record.ShipId));
                    continue;
                }
                if (record.Kind == ObservatoryKind)
                {
                    // anchored when its record says so; its ping job is durable and outlives the restart
                    _sim.PlaceObservatory(new SimPlaceObservatory(
                        record.DeployableId, record.SolarSystemId, record.Name, record.TypeId, record.GroupId, record.Position, record.Radius, record.ActivateAtMs <= now,
                        record.OwnerId, record.CorporationId, record.AllianceId, record.ShipId));
                    continue;
                }
                if (record.Kind == BeaconKind)
                {
                    _sim.PlaceCynoBeacon(new SimPlaceCynoBeacon(
                        record.DeployableId, record.SolarSystemId, record.Name, record.TypeId, record.GroupId, record.Position, record.Radius, record.ActivateAtMs <= now,
                        record.OwnerId, record.CorporationId, record.AllianceId, record.ShipId));
                    continue;
                }
                _sim.RestoreDeployed(new SimDeployed(
                    record.SolarSystemId, record.DeployableId, record.Name, record.TypeId, record.GroupId, record.Position, record.Radius, record.OwnerId,
                    record.CorporationId, record.AllianceId, record.ShipId, record.ExpiresAtMs - now));
            }
        }
        catch (Exception error)
        {
            _log?.Invoke($"what deployed lies in system {system} was not put back: {error.Message}");
        }
    }

    /// <summary>The sweep's part for the records: each one whose time is up goes by its kind's entry, and the record is forgotten.</summary>
    private async Task ExpireDeployedAsync(long nowUnixMs)
    {
        if (_deployed is null)
        {
            return;
        }
        foreach (var due in await _deployed.DueAsync(nowUnixMs))
        {
            if (_deployedExpiry.TryGetValue(due.Kind, out var expire))
            {
                await expire(due);
            }
            await _deployed.RemoveAsync(due.DeployableId);
        }
    }

    /// <summary>A cynosural field whose time is up is taken out of space (the simulation removes it too, at the same time, and this is then a no-op).</summary>
    private async Task ExpireCynoFieldAsync(DeployedRecord due)
    {
        _sim.RemoveDeployed(due.DeployableId);
        await AnnounceFieldAsync(due, false);
    }
}
