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
    /// Whether the holder's session is still where a battery's grant is kept: in space, in the system of the battery, and in the corporation that owns it.
    /// The grant is checked when it is assumed (as a control tower's access is); an order checks it again, so a pilot that has moved out of the system or
    /// left the owning corporation gives no order.
    /// </summary>
    private bool BatteryOperatorInPlace(ClientSession session, DeployedRecord battery)
    {
        var corporationId = session.GetInt("corpid") ?? session.GetInt("corporationid") ?? 0;
        var systemId = session.GetInt("solarsystemid2") ?? session.GetInt("solarsystemid") ?? 0;
        return corporationId > 0
            && battery.CorporationId == corporationId && battery.OwnerId == corporationId
            && systemId == battery.SolarSystemId
            && _space.IsInSpace(session);
    }

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
        if (BatteryNumbersOf(battery.TypeId) is not { } numbers)
        {
            return TargetingResult.Refusal("BATTERY_NUMBERS_MISSING");
        }

        var gate = _batteryGates.GetOrAdd(batteryId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // the holder and the online state are read inside the battery gate: a relinquish or an assume that ran while this order waited for the gate
            // must not be placed with the holder it has just taken away
            await using var connection = await _source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            var holder = await _deployed.BatteryControllerAsync(connection, null, batteryId, cancellationToken).ConfigureAwait(false);
            if (holder != characterId || !BatteryOperatorInPlace(session, battery))
            {
                return TargetingResult.Refusal("NO_CONTROL_GRANT");
            }
            var state = await _deployed.TowerStateAsync(connection, null, batteryId, cancellationToken).ConfigureAwait(false);
            var online = state is TowerOnline or TowerOnlining;

            var placed = await _sim.PlaceBatteryAsync(
                new SimPlaceBattery(batteryId, battery.SolarSystemId, battery.TypeId, battery.GroupId, battery.Name, (ulong)session.SessionId,
                    battery.Position, battery.Radius, numbers.ScanResolution, numbers.MaxTargetRange, numbers.MaxLockedTargets, online),
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
            // The grant is checked again once the scene has answered. A relinquish can run after the holder check above, so this order may be for a pilot that
            // no longer holds the grant: its lock is taken back and the battery is placed with no holder. A new holder's order places the battery with that holder.
            if (await _deployed.BatteryControllerAsync(connection, null, batteryId, cancellationToken).ConfigureAwait(false) != characterId)
            {
                if (reply.Reason.Length == 0)
                {
                    await _sim.BatteryTargetAsync(
                        new SimBatteryTarget(SimClient.NextRequestId(), batteryId, battery.SolarSystemId, (byte)BatteryOrderCode(TargetingOrder.Remove), target),
                        cancellationToken).ConfigureAwait(false);
                }
                await _sim.PlaceBatteryAsync(
                    new SimPlaceBattery(batteryId, battery.SolarSystemId, battery.TypeId, battery.GroupId, battery.Name, 0,
                        battery.Position, battery.Radius, numbers.ScanResolution, numbers.MaxTargetRange, numbers.MaxLockedTargets, online),
                    cancellationToken).ConfigureAwait(false);
                return TargetingResult.Refusal("NO_CONTROL_GRANT");
            }
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
    /// Whether an activation or deactivation of a module is refused because it is a player-controlled POS battery whose control grant the session's pilot does
    /// not hold in place. The grant is checked before the effect's name, as the reference checks it (starbaseControlRuntime's activateControlledBattery).
    /// Any other module is not refused here (false).
    /// </summary>
    private async Task<bool> BatteryControlRefusedAsync(ClientSession session, long moduleId, CancellationToken cancellationToken)
    {
        if (_deployed is null || _source is null || moduleId <= 0)
            return false;
        var battery = await _deployed.GetAsync(moduleId, cancellationToken).ConfigureAwait(false);
        if (battery is not { Kind: StarbaseModuleKind })
            return false;
        await _statics.EnsureTypesAsync([battery.TypeId], cancellationToken).ConfigureAwait(false);
        if (!IsPlayerControlledBattery(battery.TypeId))
            return false;
        var characterId = session.CharacterId ?? 0;
        await using var connection = await _source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var holder = await _deployed.BatteryControllerAsync(connection, null, moduleId, cancellationToken).ConfigureAwait(false);
        return characterId <= 0 || holder != characterId || !BatteryOperatorInPlace(session, battery);
    }

    /// <summary>A battery's numbers as its type's dogma gives them: its locks' range, its locked-target limit and its scan resolution.</summary>
    private readonly record struct BatteryNumbers(double MaxTargetRange, int MaxLockedTargets, double ScanResolution);

    /// <summary>A battery's numbers from its type's SDE attributes, or null when the type does not carry them.</summary>
    private BatteryNumbers? BatteryNumbersOf(int typeId)
    {
        var attributes = _statics.Type(typeId)?.Attributes;
        if (attributes is null
            || !attributes.TryGetValue(BatteryMaxTargetRangeAttribute, out var maxTargetRange)
            || !attributes.TryGetValue(BatteryMaxLockedTargetsAttribute, out var maxLockedTargets)
            || !attributes.TryGetValue(BatteryScanResolutionAttribute, out var scanResolution))
        {
            return null;
        }
        return new BatteryNumbers(maxTargetRange, (int)maxLockedTargets, scanResolution);
    }

    /// <summary>
    /// Starts or stops a POS battery's fire (protocol 104) for the pilot that holds its control grant. A battery that is not player-controlled is not a
    /// battery order (null), so the dogma service handles the item as a ship module. A pilot without the grant is refused; the effect must be the battery
    /// type's own offensive effect; the battery must be online for a start, and a start needs a target (the simulation checks that the battery holds it). A stop
    /// is always accepted: the holder's, or any pilot's when the battery is unheld, online or not. The simulation
    /// records only that the battery is set to fire: no damage, cycle, capacitor or charge is run for it yet.
    /// </summary>
    private async Task<TargetingResult?> BatteryFireAsync(ClientSession session, long batteryId, bool on, string effectName, long target, CancellationToken cancellationToken)
    {
        if (_deployed is null || _source is null || batteryId <= 0)
        {
            return null;
        }
        var characterId = session.CharacterId ?? 0;
        if (on && characterId <= 0)
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
        if (BatteryNumbersOf(battery.TypeId) is not { } numbers)
        {
            return TargetingResult.Refusal("BATTERY_NUMBERS_MISSING");
        }

        var gate = _batteryGates.GetOrAdd(batteryId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // the holder and the online state are read inside the battery gate, as the targeting order reads them, so a grant that changed while this
            // fire waited for the gate is seen before the battery is placed
            await using var connection = await _source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            var holder = await _deployed.BatteryControllerAsync(connection, null, batteryId, cancellationToken).ConfigureAwait(false) ?? 0;
            // A start needs the grant. A stop needs none when the battery is unheld, and the holder's stop is always accepted.
            if (holder != characterId && (on || holder != 0))
            {
                return TargetingResult.Refusal("NO_CONTROL_GRANT");
            }
            var offensive = await _statics.OffensiveEffectNameAsync(battery.TypeId, cancellationToken).ConfigureAwait(false);
            if (offensive is null || !string.Equals(offensive, effectName, StringComparison.OrdinalIgnoreCase))
            {
                return TargetingResult.Refusal("EFFECT_INVALID");
            }
            if (!on)
            {
                // A stop is never a placement: a placement names its holder, and an unheld battery has none. It is sent as it is, online or not.
                var stopped = await _sim.BatteryFireAsync(
                    new SimBatteryFire(SimClient.NextRequestId(), batteryId, battery.SolarSystemId, false, 0),
                    cancellationToken).ConfigureAwait(false);
                return stopped.Reason.Length > 0
                    ? TargetingResult.Refusal(stopped.Reason)
                    : new TargetingResult(true, false, Array.Empty<long>());
            }
            var state = await _deployed.TowerStateAsync(connection, null, batteryId, cancellationToken).ConfigureAwait(false);
            var online = state is TowerOnline or TowerOnlining;

            var placed = await _sim.PlaceBatteryAsync(
                new SimPlaceBattery(batteryId, battery.SolarSystemId, battery.TypeId, battery.GroupId, battery.Name, (ulong)session.SessionId,
                    battery.Position, battery.Radius, numbers.ScanResolution, numbers.MaxTargetRange, numbers.MaxLockedTargets, online),
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

    /// <summary>
    /// Puts the tower's batteries that are not online offline in the simulation, outside an order (the tower's fuel ran out, it went offline, or it was
    /// unanchored): the simulation lets go of their locks and tells the former holder <c>clear</c>. No holder is sent, so the grant stays with the host. Runs
    /// after the tower's state has committed. A simulation that is unavailable is logged; its locks then wait for its next placement of the battery.
    /// </summary>
    public async Task OfflineBatteriesAsync(long towerId, CancellationToken cancellationToken = default)
    {
        if (_deployed is null)
            return;
        foreach (var module in await _deployed.ByControlTowerAsync(towerId, cancellationToken).ConfigureAwait(false))
        {
            if (module is not { Kind: StarbaseModuleKind })
                continue;
            await _statics.EnsureTypesAsync([module.TypeId], cancellationToken).ConfigureAwait(false);
            if (!IsPlayerControlledBattery(module.TypeId))
                continue;
            var state = await _deployed.TowerStateAsync(module.DeployableId, cancellationToken).ConfigureAwait(false);
            if (state is TowerOnline or TowerOnlining || BatteryNumbersOf(module.TypeId) is not { } numbers)
                continue;

            var gate = _batteryGates.GetOrAdd(module.DeployableId, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await _sim.PlaceBatteryAsync(
                    new SimPlaceBattery(module.DeployableId, module.SolarSystemId, module.TypeId, module.GroupId, module.Name, 0,
                        module.Position, module.Radius, numbers.ScanResolution, numbers.MaxTargetRange, numbers.MaxLockedTargets, false),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (SimUnavailableException error)
            {
                _log?.Invoke($"battery {module.DeployableId} offline: {error.Message}");
            }
            finally
            {
                gate.Release();
            }
        }
    }

    /// <summary>
    /// Tells the simulation that a POS battery has no holder any more, after its control grant was released (its pilot relinquished it, or the pilot left
    /// the cluster). The scene lets go of the former holder's locks and tells that pilot <c>clear</c>; the battery keeps its online state. Nothing is sent
    /// when a grant exists again by the time the gate is taken, since the new holder's own order places the battery with that holder.
    /// </summary>
    private async Task ClearBatteryHolderAsync(long batteryId, CancellationToken cancellationToken)
    {
        if (_deployed is null || _source is null)
            return;
        var battery = await _deployed.GetAsync(batteryId, cancellationToken).ConfigureAwait(false);
        if (battery is not { Kind: StarbaseModuleKind })
            return;
        await _statics.EnsureTypesAsync([battery.TypeId], cancellationToken).ConfigureAwait(false);
        if (!IsPlayerControlledBattery(battery.TypeId) || BatteryNumbersOf(battery.TypeId) is not { } numbers)
            return;

        var gate = _batteryGates.GetOrAdd(batteryId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await _source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            if (await _deployed.BatteryControllerAsync(connection, null, batteryId, cancellationToken).ConfigureAwait(false) is not null)
                return;
            var state = await _deployed.TowerStateAsync(connection, null, batteryId, cancellationToken).ConfigureAwait(false);
            var online = state is TowerOnline or TowerOnlining;
            await _sim.PlaceBatteryAsync(
                new SimPlaceBattery(batteryId, battery.SolarSystemId, battery.TypeId, battery.GroupId, battery.Name, 0,
                    battery.Position, battery.Radius, numbers.ScanResolution, numbers.MaxTargetRange, numbers.MaxLockedTargets, online),
                cancellationToken).ConfigureAwait(false);
        }
        catch (SimUnavailableException error)
        {
            _log?.Invoke($"battery {batteryId} released: {error.Message}");
        }
        finally
        {
            gate.Release();
        }
    }
}
