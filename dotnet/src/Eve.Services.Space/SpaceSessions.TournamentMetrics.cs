using System.Text.Json;
using Eve.Inventory;
using Eve.Ships;
using Eve.Space;

namespace Eve.Services.Space;

/// <summary>Rates are absent when the inputs could not be observed consistently.</summary>
public sealed record TournamentPotentialSnapshot(long CharacterId, long ShipId, SimSceneKey Scene,
    double? FittedDamagePerSecond, double? RunningDamagePerSecond,
    double? FittedRepairHpPerSecond, double? RunningRepairHpPerSecond, int? FittedControl, int? RunningControl, string? UnavailableReason);

/// <summary>Opaque pre-sample observation. A stable observation is telemetry, not a transaction or adjudication value.</summary>
public sealed class TournamentPotentialCapture
{
    internal object State { get; init; } = null!;
}

public sealed partial class SpaceSessions
{
    /// <summary>Controlled fighter identities and their tube assignments, from the fighter state owner.</summary>
    public Func<long, long, IReadOnlyList<TournamentFighterFlight>>? TournamentFighterFlightsOf { get; set; }

    internal IReadOnlyDictionary<long, (string Effect, long Target)> TournamentRunningForTest(ulong sessionId) =>
        _pilots.TryGetValue(sessionId, out var pilot)
            ? pilot.RunningModules.ToDictionary(x => x.Key, x => (x.Value.EffectName, x.Value.TargetId))
            : new Dictionary<long, (string Effect, long Target)>();

    private sealed record PotentialObservation(ulong SessionId, Pilot Pilot, ShipSnapshot Snapshot,
        IReadOnlyDictionary<long, string> Running, IReadOnlyDictionary<long, long> Targets, IReadOnlyList<Item> Companions,
        IReadOnlyList<TournamentDroneSource> Drones, IReadOnlyList<TournamentDroneSource> Fighters,
        IReadOnlyList<TournamentFighterFlight>? FighterFlights, string Fingerprint);

    private async Task<PotentialObservation?> ObserveTournamentPotentialAsync(long characterId, long shipId,
        SimSceneKey scene, CancellationToken cancellationToken)
    {
        var matches = _pilots.Where(pair => pair.Value.CharacterId == characterId && pair.Value.ShipId == shipId
            && pair.Value.SimulationScene == scene).ToArray();
        if (matches.Length != 1 || !_sim.IsConnected) return null;
        var (id, pilot) = (matches[0].Key, matches[0].Value);
        await _charges.FlushAsync(id).ConfigureAwait(false);
        var runningModules = pilot.RunningModules.ToArray();
        var running = runningModules.ToDictionary(pair => pair.Key, pair => pair.Value.EffectName);
        var targets = runningModules.ToDictionary(pair => pair.Key, pair => pair.Value.TargetId);
        if (await ModuleShipAsync(pilot, cancellationToken).ConfigureAwait(false) is not { } snapshot) return null;
        var companions = await _ships.TournamentCompanionsAsync(shipId, cancellationToken).ConfigureAwait(false);
        var drones = await _ships.TournamentDroneSourcesAsync(characterId, shipId, pilot.SolarSystemId, cancellationToken).ConfigureAwait(false);
        if (drones is null) return null;
        var fighters = await _ships.TournamentFighterSourcesAsync(characterId, shipId, pilot.SolarSystemId, cancellationToken).ConfigureAwait(false);
        if (fighters is null) return null;
        var fighterFlights = TournamentFighterFlightsOf?.Invoke(characterId, shipId);
        var sourceIdentity = await _ships.TournamentSourceIdentityAsync(characterId, shipId, cancellationToken).ConfigureAwait(false);
        if (!_pilots.TryGetValue(id, out var current) || !ReferenceEquals(current, pilot) || pilot.SimulationScene != scene) return null;
        // Compare all effective attributes of the hull, fitted modules, charges and pilot, as well as
        // their source identities and state. This brackets the native tick; it is not DB linearization.
        var ids = new[] { characterId, shipId }.Concat(snapshot.Fitted.Select(x => x.Id)).Concat(snapshot.Charges.Select(x => x.Id));
        var attributes = ids.Distinct().Order().Select(itemId => new { itemId, values = snapshot.World.Attributes(itemId).OrderBy(x => x.Key).ToArray() }).ToArray();
        var fingerprint = JsonSerializer.Serialize(new { snapshot.Ship, snapshot.Fitted, snapshot.Charges, snapshot.Skills,
            snapshot.ModuleConditions, snapshot.WeaponBanks, companions, drones, fighters, fighterFlights, sourceIdentity, running = running.OrderBy(x => x.Key).ToArray(),
            targets = targets.OrderBy(x => x.Key).ToArray(), attributes });
        return new(id, pilot, snapshot, running, targets, companions, drones, fighters, fighterFlights, fingerprint);
    }

    public async Task<TournamentPotentialCapture?> CaptureTournamentPotentialAsync(long characterId, long shipId,
        SimSceneKey scene, CancellationToken cancellationToken)
    {
        var observation = await ObserveTournamentPotentialAsync(characterId, shipId, scene, cancellationToken).ConfigureAwait(false);
        return observation is null ? null : new TournamentPotentialCapture { State = observation };
    }

    internal static bool TournamentRunningChargeMatches(Item? charge, SimTournamentMetricsRunning running) =>
        charge is null ? running.ChargeTypeId is null && running.Rounds is null
            : running.ChargeTypeId == charge.TypeId && running.Rounds == charge.Quantity;

    /// <summary>Completes a pre-sample capture after one shared native scene sample for the roster.</summary>
    public async Task<TournamentPotentialSnapshot> CompleteTournamentPotentialAsync(TournamentPotentialCapture capture,
        SimSceneKey scene, SimTournamentMetricsPilotSample native, IReadOnlySet<long> friendlyShips, CancellationToken cancellationToken)
    {
        var before = (PotentialObservation)capture.State;
        var characterId = native.Pilot.CharacterId; var shipId = native.Pilot.ShipId;
        TournamentPotentialSnapshot Unavailable(string reason) => new(characterId, shipId, scene, null, null, null, null, null, null, reason);
        if (before.Pilot.CharacterId != characterId || before.Pilot.ShipId != shipId || before.Pilot.SimulationScene != scene)
            return Unavailable("IDENTITY_MISMATCH");
        var after = await ObserveTournamentPotentialAsync(characterId, shipId, scene, cancellationToken).ConfigureAwait(false);
        if (after is null || after.SessionId != before.SessionId || !ReferenceEquals(before.Pilot, after.Pilot)
            || before.Fingerprint != after.Fingerprint) return Unavailable("HOST_STATE_CHANGED");
        var fit = after.Snapshot.Fitted.OrderBy(x => x.Id).ToArray();
        var nativeFit = native.Fitted.OrderBy(x => x.ItemId).ToArray();
        if (fit.Length != nativeFit.Length || nativeFit.Select(x => x.ItemId).Distinct().Count() != nativeFit.Length)
            return Unavailable("FIT_MISMATCH");
        for (var i = 0; i < fit.Length; i++)
        {
            var item = fit[i]; var actual = nativeFit[i];
            if (item.Id != actual.ItemId || item.TypeId != actual.TypeId || item.Flag != actual.Flag)
                return Unavailable("FIT_MISMATCH");
            var hot = after.Snapshot.StateOf(item.Id) == Eve.Dogma.ItemState.Overloaded;
            if (after.Running.ContainsKey(item.Id) && actual.Heat == SimTournamentMetricsHeat.Unknown)
                return Unavailable("HEAT_UNAVAILABLE");
            if (actual.Heat != SimTournamentMetricsHeat.Unknown && hot != (actual.Heat == SimTournamentMetricsHeat.On))
                return Unavailable("HEAT_MISMATCH");
        }
        if (native.Running.Count != after.Running.Count || native.Running.Select(x => x.ItemId).Distinct().Count() != native.Running.Count)
            return Unavailable($"RUNNING_COUNT_MISMATCH:{native.Running.Count}:{after.Running.Count}");
        foreach (var running in native.Running)
        {
            if (!after.Running.TryGetValue(running.ItemId, out var name)) return Unavailable("RUNNING_ITEM_MISMATCH");
            if (!after.Targets.TryGetValue(running.ItemId, out var target) || target != running.TargetId)
                return Unavailable($"RUNNING_TARGET_MISMATCH:{target}:{running.TargetId}");
            if (_statics.Effect(running.EffectId) is not { } effect) return Unavailable($"RUNNING_EFFECT_MISSING:{running.EffectId}");
            var module = fit.FirstOrDefault(x => x.Id == running.ItemId);
            if (module is null || _statics.Type(module.TypeId) is not { } type) return Unavailable("RUNNING_MISMATCH");
            var normalized = Effects.MissileEffect.LauncherEffectFor(type, _statics, name)?.Name ?? name;
            if (!string.Equals(effect.Name, normalized, StringComparison.OrdinalIgnoreCase)) return Unavailable($"RUNNING_EFFECT_MISMATCH:{normalized}:{effect.Name}");
            var charge = after.Snapshot.ChargeOf(module);
            if (!TournamentRunningChargeMatches(charge, running))
                return Unavailable("CHARGE_MISMATCH");
        }
        var hasFighters = after.Companions.Any(x => x.Flag != CharacterShips.DroneBayFlag)
            || native.Companions.Any(x => x.Kind == SimTournamentCompanionKind.Fighter);
        if (hasFighters && after.FighterFlights is null) return Unavailable("FIGHTER_OWNER_UNAVAILABLE");
        if (TournamentFighterMetrics.HasUnobservedControl(native.Companions)) return Unavailable("FIGHTER_CONTROL_UNAVAILABLE");
        var fighter = TournamentFighterMetrics.Calculate(after.Snapshot, _statics, after.Fighters, after.FighterFlights ?? [], native.Companions);
        if (fighter.Dps is not { } fighterDps) return Unavailable(fighter.Reason ?? "FIGHTER_UNAVAILABLE");
        var drone = TournamentDroneMetrics.Calculate(after.Snapshot, _statics, after.Drones, native.Companions);
        if (drone.Dps is not { } droneDps) return Unavailable(drone.Reason ?? "DRONE_UNAVAILABLE");
        var result = TournamentPotentialFit.Calculate(after.Snapshot, _statics, after.Running, auxiliaryComplete: true, after.Targets, friendlyShips);
        if (result.Totals is not { } totals) return Unavailable(result.UnavailableReason ?? "FIT_UNAVAILABLE");
        var controls = TournamentPotentialFit.Controls(after.Snapshot, _statics, after.Running);
        return new(characterId, shipId, scene, totals.FittedDamagePerSecond + droneDps + fighterDps, totals.RunningDamagePerSecond,
            totals.FittedRepairHpPerSecond, totals.RunningRepairHpPerSecond, controls.Fitted, controls.Running, null);
    }
}
