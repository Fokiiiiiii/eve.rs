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
    private const string CynoFieldKind = "cyno_field";

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
        }
        catch (Exception error)
        {
            _log?.Invoke($"the cynosural field {lit.DeployableId} was not kept: {error.Message}");
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
    private Task ExpireCynoFieldAsync(DeployedRecord due)
    {
        _sim.RemoveDeployed(due.DeployableId);
        return Task.CompletedTask;
    }
}
