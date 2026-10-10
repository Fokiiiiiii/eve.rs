using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Eve.Services.Ships;
using Eve.Space;

namespace Eve.Services.Space;

/// <summary>
/// The effects a pilot's modules run, refreshed while they run (FP-C4b and FP-C5). An overheat changes what an overheated module runs with (its overload bonus), and a
/// tracking computer that starts, stops, goes offline or overheats changes the numbers of the turrets it projects onto. The host rebuilds each running effect from the ship
/// as it flies (<see cref="ModulePackages"/>, the activation's own builder) and sends the simulation what changed (<see cref="SimUpdateEffect"/>); the simulation applies it at the
/// start of the effect's next cycle, as it applies an overheat.
/// </summary>
public sealed partial class SpaceSessions
{
    /// <summary>Raised when a reloading gun of a ship the session flies has no round left for its next cycle (<see cref="ISpaceRounds.GunRanDry"/>).</summary>
    public event Action<SpentGun>? GunRanDry;

    /// <summary>
    /// An effect a module runs: what it was asked for (what a refresh rebuilds it from), the key of the numbers the simulation holds (the encoded update, without the rounds the
    /// simulation spends), and what the gun does at the end of a round (whether it reloads from its hold, and the rounds a cycle takes).
    /// </summary>
    private sealed record RunningModule(
        string EffectName, long TargetId, int Repeat, IReadOnlyList<double>? Aim, IReadOnlyList<double>? AimEnd, byte[] Key, bool Reloads, long PerCycle, int ChargeTypeId)
    {
        public static RunningModule Of(string effectName, long targetId, int repeat, IReadOnlyList<double>? aim, IReadOnlyList<double>? aimEnd, SimEffect effect)
        {
            var charge = effect.Turret?.Charge;
            return new RunningModule(effectName, targetId, repeat, aim, aimEnd, SimCodec.Encode(UpdateOf(0, effect.ModuleId, effect)), charge?.Reloads == true, charge?.PerCycle ?? 0, charge?.TypeId ?? 0);
        }
    }

    /// <summary>The refresh of a running effect: the numbers the simulation applies at its next cycle. The rounds are the simulation's (it spends them), so none is sent.</summary>
    private static SimUpdateEffect UpdateOf(ulong sessionId, long moduleId, SimEffect effect) =>
        new(sessionId, moduleId, effect.DurationMs, effect.CapacitorNeed, effect.Turret is { } turret ? turret with { Charge = turret.Charge is { } charge ? charge with { Rounds = 0 } : null } : null, effect.OnChanges);

    /// <summary>
    /// Rebuilds each effect the pilot's modules run from the ship as it flies, and sends the simulation the ones whose numbers changed. Nothing is sent for an effect that
    /// is unchanged (so a refresh that changes nothing is silent).
    /// </summary>
    /// <summary>
    /// Stops the modules of a pilot's ship that its buffs block (activationBlocked, 1349: the disallowed weapons of a fleet grant): each running one is deactivated as
    /// its pilot asked, and the simulation ends it at the end of its cycle (track C6).
    /// </summary>
    public async Task EndBlockedModulesAsync(ClientSession session, CancellationToken cancellationToken)
    {
        if (!_pilots.TryGetValue((ulong)session.SessionId, out var pilot) || await FlownShipAsync(pilot, cancellationToken) is not { } snapshot)
        {
            return;
        }
        foreach (var (moduleId, running) in pilot.RunningModules.ToArray())
        {
            if (snapshot.World.Get(moduleId, Eve.Services.Space.Effects.ModuleAttributes.ActivationBlocked) > 0)
            {
                await DeactivateAsync(session, moduleId, running.EffectName, cancellationToken);
            }
        }
    }

    /// <summary>
    /// Tells the simulation that a ship's numbers changed through its buffs (a command burst's, protocol 43): the ship is sent again, and the modules it runs are
    /// refreshed (their numbers change while they run). Nothing is sent for a pilot who is not in space.
    /// </summary>
    public async Task BuffedAsync(ClientSession session, CancellationToken cancellationToken)
    {
        if (!_pilots.TryGetValue((ulong)session.SessionId, out var pilot))
        {
            return;
        }
        var sessionId = (ulong)session.SessionId;
        await SendShipAsync(sessionId, pilot, null, cancellationToken);
        await RefreshRunningAsync(pilot, sessionId, cancellationToken);
    }

    private async Task RefreshRunningAsync(Pilot pilot, ulong sessionId, CancellationToken cancellationToken)
    {
        if (pilot.RunningModules.IsEmpty || !_sim.IsConnected || await FlownShipAsync(pilot, cancellationToken) is not { } snapshot)
        {
            return;
        }
        foreach (var (moduleId, running) in pilot.RunningModules.ToArray())
        {
            var made = ModulePackages.Of(snapshot, _statics, moduleId, running.EffectName, running.TargetId, running.Repeat, running.Aim, running.AimEnd);
            if (made.Effect is not { } effect)
            {
                continue;
            }
            var update = UpdateOf(sessionId, moduleId, effect);
            // the key is the numbers only: the session is not part of what the simulation holds for the effect
            var key = SimCodec.Encode(UpdateOf(0, moduleId, effect));
            if (key.AsSpan().SequenceEqual(running.Key))
            {
                continue;
            }
            var charge = effect.Turret?.Charge;
            pilot.RunningModules[moduleId] = running with { Key = key, Reloads = charge?.Reloads == true, PerCycle = charge?.PerCycle ?? 0, ChargeTypeId = charge?.TypeId ?? 0 };
            await _sim.UpdateEffectAsync(update, cancellationToken).ConfigureAwait(false);
        }
    }
}
