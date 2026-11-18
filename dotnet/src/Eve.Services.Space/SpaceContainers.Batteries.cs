using System.Collections.Concurrent;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Ships;
using Eve.Space;

namespace Eve.Services.Space;

/// <summary>
/// A POS battery's own targets (protocol 102). A battery locks on its own; the pilot that holds its control grant (<c>AssumeStructureControl</c>) asks for
/// its locks through the dogma location. The grant, the battery's state and its numbers are the host's and are checked here on every order; the lock is
/// the simulation's, and the simulation tells the holder only. A battery that is offline is taken out of the scene's lock state, so its locks are released.
/// </summary>
public sealed partial class SpaceContainers
{
    /// <summary>The SDE attribute of a battery's maxTargetRange (76): the reach of its locks, in metres.</summary>
    private const int BatteryMaxTargetRangeAttribute = 76;

    /// <summary>The SDE attribute of a battery's maxLockedTargets (192).</summary>
    private const int BatteryMaxLockedTargetsAttribute = 192;

    /// <summary>The SDE attribute of a battery's scanResolution (564): what a lock takes, against the target's signature.</summary>
    private const int BatteryScanResolutionAttribute = 564;

    /// <summary>One order or placement of a battery at a time, so an answer is matched to what asked for it.</summary>
    private readonly ConcurrentDictionary<long, SemaphoreSlim> _batteryGates = new();

    /// <summary>The order codes the simulation reads for a battery (protocol 102): lock, remove, clear, cancel.</summary>
    private static int BatteryOrderCode(TargetingOrder order) => order switch
    {
        TargetingOrder.Lock => 1,
        TargetingOrder.Remove => 2,
        TargetingOrder.Clear => 3,
        TargetingOrder.Cancel => 4,
        _ => 0,
    };

    /// <summary>
    /// Carries out an order of a battery's holder: the battery must be a player-controlled battery whose control grant the pilot holds, it must be
    /// online, and its numbers must be in the type's dogma. The battery is placed (or updated) in the scene with its holder, and then the order is asked of
    /// the simulation. An offline battery is placed offline, which releases its locks there, and is then refused.
    /// </summary>
    private async Task<TargetingResult> BatteryTargetingAsync(ClientSession session, long batteryId, TargetingOrder order, long target, CancellationToken cancellationToken)
    {
        var characterId = session.CharacterId ?? 0;
        if (_deployed is null || _source is null || batteryId <= 0 || characterId <= 0)
        {
            return TargetingResult.Refusal("NO_CONTROL_GRANT");
        }
        var battery = await _deployed.GetAsync(batteryId, cancellationToken).ConfigureAwait(false);
        if (battery is not { Kind: StarbaseModuleKind })
        {
            return TargetingResult.Refusal("BATTERY_NOT_IN_SPACE");
        }
        await _statics.EnsureTypesAsync([battery.TypeId], cancellationToken).ConfigureAwait(false);
        if (!IsPlayerControlledBattery(battery.TypeId))
        {
            return TargetingResult.Refusal("NOT_A_BATTERY");
        }
        var attributes = _statics.Type(battery.TypeId)?.Attributes;
        if (attributes is null
            || !attributes.TryGetValue(BatteryMaxTargetRangeAttribute, out var maxTargetRange)
            || !attributes.TryGetValue(BatteryMaxLockedTargetsAttribute, out var maxLockedTargets)
            || !attributes.TryGetValue(BatteryScanResolutionAttribute, out var scanResolution))
        {
            return TargetingResult.Refusal("BATTERY_NUMBERS_MISSING");
        }

        await using var connection = await _source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var holder = await _deployed.BatteryControllerAsync(connection, null, batteryId, cancellationToken).ConfigureAwait(false);
        if (holder != characterId)
        {
            return TargetingResult.Refusal("NO_CONTROL_GRANT");
        }
        var state = await _deployed.TowerStateAsync(connection, null, batteryId, cancellationToken).ConfigureAwait(false);
        var online = state is TowerOnline or TowerOnlining;

        var gate = _batteryGates.GetOrAdd(batteryId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var placed = await _sim.PlaceBatteryAsync(
                new SimPlaceBattery(batteryId, battery.SolarSystemId, battery.TypeId, battery.GroupId, battery.Name, (ulong)session.SessionId,
                    battery.Position, battery.Radius, scanResolution, maxTargetRange, (int)maxLockedTargets, online),
                cancellationToken).ConfigureAwait(false);
            if (placed.Reason.Length > 0)
            {
                return TargetingResult.Refusal(placed.Reason);
            }
            if (!online)
            {
                return TargetingResult.Refusal("BATTERY_OFFLINE");
            }
            var code = BatteryOrderCode(order);
            if (code == 0)
            {
                return TargetingResult.Refusal("TARGET_ORDER_UNSUPPORTED");
            }
            var reply = await _sim.BatteryTargetAsync(
                new SimBatteryTarget(SimClient.NextRequestId(), batteryId, battery.SolarSystemId, (byte)code, target),
                cancellationToken).ConfigureAwait(false);
            return reply.Reason.Length > 0
                ? TargetingResult.Refusal(reply.Reason)
                : new TargetingResult(true, reply.Pending, reply.Targets);
        }
        catch (SimUnavailableException error)
        {
            _log?.Invoke($"battery {batteryId} targeting: {error.Message}");
            throw MachoErrors.ThrowUserError("CustomNotify", ("notify", new PyStr("Space is not available right now. Try again in a moment.")));
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Starts or stops a POS battery's fire (protocol 104) for the pilot that holds its control grant. A battery that is not player-controlled is not a
    /// battery order (null), so the dogma service handles the item as a ship module. A pilot without the grant is refused; the effect must be the battery
    /// type's own offensive effect; the battery must be online; a start needs a target (the simulation checks that the battery holds it). The simulation
    /// records only that the battery is set to fire: no damage, cycle, capacitor or charge is run for it yet.
    /// </summary>
    private async Task<TargetingResult?> BatteryFireAsync(ClientSession session, long batteryId, bool on, string effectName, long target, CancellationToken cancellationToken)
    {
        if (_deployed is null || _source is null || batteryId <= 0)
        {
            return null;
        }
        var characterId = session.CharacterId ?? 0;
        if (characterId <= 0)
        {
            return TargetingResult.Refusal("NO_CONTROL_GRANT");
        }
        var battery = await _deployed.GetAsync(batteryId, cancellationToken).ConfigureAwait(false);
        if (battery is not { Kind: StarbaseModuleKind })
        {
            return null;
        }
        await _statics.EnsureTypesAsync([battery.TypeId], cancellationToken).ConfigureAwait(false);
        if (!IsPlayerControlledBattery(battery.TypeId))
        {
            return null;
        }
        var attributes = _statics.Type(battery.TypeId)?.Attributes;
        if (attributes is null
            || !attributes.TryGetValue(BatteryMaxTargetRangeAttribute, out var maxTargetRange)
            || !attributes.TryGetValue(BatteryMaxLockedTargetsAttribute, out var maxLockedTargets)
            || !attributes.TryGetValue(BatteryScanResolutionAttribute, out var scanResolution))
        {
            return TargetingResult.Refusal("BATTERY_NUMBERS_MISSING");
        }

        await using var connection = await _source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var holder = await _deployed.BatteryControllerAsync(connection, null, batteryId, cancellationToken).ConfigureAwait(false);
        if (holder != characterId)
        {
            return TargetingResult.Refusal("NO_CONTROL_GRANT");
        }
        var offensive = await _statics.OffensiveEffectNameAsync(battery.TypeId, cancellationToken).ConfigureAwait(false);
        if (offensive is null || !string.Equals(offensive, effectName, StringComparison.OrdinalIgnoreCase))
        {
            return TargetingResult.Refusal("EFFECT_INVALID");
        }
        var state = await _deployed.TowerStateAsync(connection, null, batteryId, cancellationToken).ConfigureAwait(false);
        var online = state is TowerOnline or TowerOnlining;

        var gate = _batteryGates.GetOrAdd(batteryId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var placed = await _sim.PlaceBatteryAsync(
                new SimPlaceBattery(batteryId, battery.SolarSystemId, battery.TypeId, battery.GroupId, battery.Name, (ulong)session.SessionId,
                    battery.Position, battery.Radius, scanResolution, maxTargetRange, (int)maxLockedTargets, online),
                cancellationToken).ConfigureAwait(false);
            if (placed.Reason.Length > 0)
            {
                return TargetingResult.Refusal(placed.Reason);
            }
            if (!online)
            {
                return TargetingResult.Refusal("BATTERY_OFFLINE");
            }
            if (on && target <= 0)
            {
                return TargetingResult.Refusal("TARGET_NOT_LOCKED");
            }
            var reply = await _sim.BatteryFireAsync(
                new SimBatteryFire(SimClient.NextRequestId(), batteryId, battery.SolarSystemId, on, on ? target : 0),
                cancellationToken).ConfigureAwait(false);
            return reply.Reason.Length > 0
                ? TargetingResult.Refusal(reply.Reason)
                : new TargetingResult(true, false, Array.Empty<long>());
        }
        catch (SimUnavailableException error)
        {
            _log?.Invoke($"battery {batteryId} fire: {error.Message}");
            throw MachoErrors.ThrowUserError("CustomNotify", ("notify", new PyStr("Space is not available right now. Try again in a moment.")));
        }
        finally
        {
            gate.Release();
        }
    }
}
