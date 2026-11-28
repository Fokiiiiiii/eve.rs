using System.Collections.Concurrent;
using System.Text.Json;
using Eve.Ships;
using Eve.Space;
using Npgsql;

namespace Eve.Services.Space;

/// <summary>
/// The sentry of a POS control tower (protocol 106). The host publishes the tower's stored sentry policy, places each of its batteries that no pilot
/// holds, binds it to the policy, and says what it knows of each pilot in the tower's system toward that policy. The simulation drives the batteries
/// from those facts (its sentry pass); a battery a pilot holds is left to that pilot's orders. The host is the only source of the facts.
/// </summary>
public sealed partial class SpaceContainers
{
    /// <summary>What the host knows of a pilot toward a tower's policy: its corporation and alliance, its security status (null when unknown), the owner's standing toward it and whether the owner is at war with it.</summary>
    public sealed record SentryPilotFacts(long CorporationId, long AllianceId, double? SecurityStatus, double Standing, bool AtWar);

    /// <summary>The pilot and the tower owner a sentry fact is asked for: the owner's corporation and alliance, and whether the owner's alliance standings are used.</summary>
    public sealed record SentryFactsRequest(long CharacterId, long OwnerCorporationId, long OwnerAllianceId, bool UseAllianceStandings);

    private readonly ConcurrentDictionary<long, SemaphoreSlim> _sentryGates = new();

    /// <summary>Reads what the host knows of a pilot toward a tower's sentry policy. Null when the pilot's identity is not known, so it is not a target.</summary>
    public Func<SentryFactsRequest, CancellationToken, Task<SentryPilotFacts?>>? SentryPilotFactsOf { get; set; }

    /// <summary>
    /// Publishes a tower's sentry: its policy (online only when the tower is online, which means it is fuelled), each of its batteries that no pilot holds
    /// (placed with no holder, and bound to the tower when online), and the facts of the pilots in its system. The pilots' facts are sent as a snapshot:
    /// the old facts of the tower are forgotten first.
    /// </summary>
    private async Task ReconcileSentryAsync(long towerId, CancellationToken cancellationToken)
    {
        if (_deployed is null || _source is null || _space is null)
            return;
        var gate = _sentryGates.GetOrAdd(towerId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (await _deployed.GetAsync(towerId, cancellationToken).ConfigureAwait(false) is not { Kind: ControlTowerKind } tower)
                return;
            var system = tower.SolarSystemId;
            var highSecurity = _space.IsHighSecuritySystem(system);
            var towerOnline = await _deployed.TowerStateAsync(towerId, cancellationToken).ConfigureAwait(false) == TowerOnline
                && tower.ActivateAtMs != NeverAnchored && tower.ActivateAtMs <= _nowUnixMs();

            await using var connection = await _source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            var raw = await _deployed.TowerConfigurationAsync(connection, null, towerId, cancellationToken).ConfigureAwait(false);
            var configuration = Normalize(raw is null ? new PosMgrTowerConfiguration() :
                JsonSerializer.Deserialize<PosMgrTowerConfiguration>(raw, PosMgrJson) ?? new PosMgrTowerConfiguration());
            var sentry = configuration.Sentry;
            // An unknown system's security is not guessed: the policy is not online there, so nothing is named.
            if (!await _sim.QueueOneWayAsync(new SimSentryPolicy(towerId, system, towerOnline && highSecurity is not null, sentry.Standing, sentry.SecurityStatus,
                sentry.War, highSecurity ?? true, tower.OwnerId, tower.AllianceId), cancellationToken).ConfigureAwait(false))
                return;

            foreach (var module in await _deployed.ByControlTowerAsync(connection, null, towerId, cancellationToken).ConfigureAwait(false))
            {
                await _statics.EnsureTypesAsync([module.TypeId], cancellationToken).ConfigureAwait(false);
                if (!IsPlayerControlledBattery(module.TypeId) || BatteryNumbersOf(module.TypeId) is not { } numbers)
                    continue;
                // A pilot who holds the battery places it through its own orders, with its holder; this pass must not take the holder away. A battery whose grant
                // stands is no sentry's even when the tower put it offline (which clears its holder in the simulation), so it is unbound here.
                var batteryGate = _batteryGates.GetOrAdd(module.DeployableId, _ => new SemaphoreSlim(1, 1));
                await batteryGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    // the grant and the online state are read inside the battery gate, which a pilot's grant takes too, so a grant cannot land between this read and the placement
                    if (await _deployed.BatteryControllerAsync(connection, null, module.DeployableId, cancellationToken).ConfigureAwait(false) > 0)
                    {
                        if (!await _sim.QueueOneWayAsync(new SimSentryBattery(module.DeployableId, system, 0), cancellationToken).ConfigureAwait(false))
                            return;
                        continue;
                    }
                    var batteryOnline = towerOnline && await _deployed.TowerStateAsync(connection, null, module.DeployableId, cancellationToken).ConfigureAwait(false) == TowerOnline;
                    var placed = await _sim.PlaceBatteryAsync(
                        new SimPlaceBattery(module.DeployableId, system, module.TypeId, module.GroupId, module.Name, 0UL, module.Position, module.Radius,
                            numbers.ScanResolution, numbers.MaxTargetRange, numbers.MaxLockedTargets, batteryOnline),
                        cancellationToken).ConfigureAwait(false);
                    if (placed.Reason.Length > 0)
                    {
                        _log?.Invoke($"sentry battery {module.DeployableId} of tower {towerId} was not placed: {placed.Reason}");
                        continue;
                    }
                    if (!await _sim.QueueOneWayAsync(new SimSentryBattery(module.DeployableId, system, batteryOnline ? towerId : 0), cancellationToken).ConfigureAwait(false))
                        return;
                }
                finally
                {
                    batteryGate.Release();
                }
            }

            if (!await _sim.QueueOneWayAsync(new SimSentryPilot(towerId, system, 0, 0, 0, null, 0.0, false), cancellationToken).ConfigureAwait(false))
                return;
            if (SentryPilotFactsOf is not null)
            {
                foreach (var session in _space.SessionsInSystem(system))
                {
                    if (session.CharacterId is not { } characterId || characterId <= 0)
                        continue;
                    if (_space.ShipIdOf(session) is not { } shipId || shipId <= 0)
                        continue;
                    var facts = await SentryPilotFactsOf(new SentryFactsRequest(characterId, tower.OwnerId, tower.AllianceId, sentry.UseAllianceStandings),
                        cancellationToken).ConfigureAwait(false);
                    if (facts is not null && !await _sim.QueueOneWayAsync(new SimSentryPilot(towerId, system, shipId, facts.CorporationId,
                        facts.AllianceId, facts.SecurityStatus, facts.Standing, facts.AtWar), cancellationToken).ConfigureAwait(false))
                        return;
                }
            }
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>A system a ship has come into: each control tower of it is sent its sentry again, so the pilots that are there now are known.</summary>
    private async Task ReconcileSystemSentriesAsync(int solarSystemId)
    {
        if (_deployed is null)
            return;
        try
        {
            foreach (var record in await _deployed.InSystemAsync(solarSystemId).ConfigureAwait(false))
            {
                if (record.Kind == ControlTowerKind)
                    await ReconcileSentryAsync(record.DeployableId, CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            _log?.Invoke($"sentry of system {solarSystemId} was not published: {error.Message}");
        }
    }
}
