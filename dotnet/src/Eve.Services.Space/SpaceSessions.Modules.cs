using System.Collections.Concurrent;
using Eve.Crimewatch;
using Eve.Events;
using Eve.Inventory;
using Eve.Scheduler;
using Eve.Dogma;
using Eve.Fitting;
using Eve.Macho;
using Eve.Marshal;
using Eve.Npc;
using Eve.Services.Ships;
using Eve.Ships;
using Eve.Space;
using Eve.Universe;
using ModuleAttributes = Eve.Services.Space.Effects.ModuleAttributes;

namespace Eve.Services.Space;

public sealed partial class SpaceSessions
{
    /// <summary>
    /// Whether a character is in a fleet, as the host's fleet store has it (the cynosural field generator's rule). Null when the host has no fleets, which
    /// refuses the lighting: no fleet is assumed.
    /// </summary>
    public Func<long, CancellationToken, Task<bool>>? IsInFleetAsync { get; set; }

    /// <summary>
    /// An order about the ship's targets. The simulation keeps the locks (a lock takes time, and ends when either ship warps or goes); this asks and
    /// hears back. A lock the simulation accepts ends the undocking protection, and the client is told once more that it is over, as the reference
    /// does after every lock it accepts. One order of a pilot is asked at a time (the answers are matched by what was asked).
    /// </summary>
    public async Task<TargetingResult?> OrderAsync(ClientSession session, TargetingOrder order, IReadOnlyList<long> targets, CancellationToken cancellationToken)
    {
        var id = (ulong)session.SessionId;
        CancelSafeLogoff(session);
        if (!_pilots.TryGetValue(id, out var pilot))
        {
            return null;
        }
        var gate = _targeting.GetOrAdd(id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        CommandReply reply;
        try
        {
            reply = await _sim.CommandAsync(id, (byte)order, [.. targets.Select(target => (double)target)], cancellationToken);
        }
        catch (SimUnavailableException error)
        {
            _log?.Invoke($"targeting of session {session.SessionId}: {error.Message}");
            throw Notify("Space is not available right now. Try again in a moment.");
        }
        finally
        {
            gate.Release();
        }
        if (!reply.Accepted)
        {
            return TargetingResult.Refusal(reply.Reason ?? "REFUSED", reply.TargetType, reply.DesiredRange, reply.ActualDistance);
        }

        if (order != TargetingOrder.Targeters)
        {
            if (reply.Targets.Count == 0)
                _lockedTargets.TryRemove(id, out _);
            else
                _lockedTargets[id] = new HashSet<long>(reply.Targets);
        }

        if (order == TargetingOrder.Lock)
        {
            foreach (var targetId in reply.Targets)
            {
                var target = _pilots.Values.FirstOrDefault(other => other.ShipId == targetId);
                if (target is not null)
                    CancelSafeLogoff(target.Session);
            }
            EndInvulnerability(pilot);
            session.SendNotification("OnInvulnCancelled", "shipid", InventoryWire.Id(pilot.ShipId));
        }
        return new TargetingResult(true, reply.Pending, reply.Targets);
    }

    /// <summary>The name of the effect an effect id of the static data names (the superweapon route names the effect by its id), or null when there is none.</summary>
    public string? EffectNameOf(long effectId) => effectId is > 0 and <= int.MaxValue ? _statics.Effect((int)effectId)?.Name : null;

    /// <summary>
    /// Starts an effect of a module. The dogma engine works out the package (the cycle, its cost, the ship's numbers with and without the effect) and the
    /// simulation runs it; an effect it accepts ends the undocking protection, as a movement does. A module order is asked one at a time for a pilot.
    /// </summary>
    /// <summary>
    /// Starts an effect of a module of the pilot's ship. <paramref name="aim"/> is the point a point-targeted effect is aimed at (an offset from the ship's centre), or null.
    /// </summary>
    public async Task<ModuleResult?> ActivateAsync(ClientSession session, long moduleId, string effectName, long targetId, int repeat, CancellationToken cancellationToken, IReadOnlyList<double>? aim = null, IReadOnlyList<double>? aimEnd = null)
    {
        CancelSafeLogoff(session);
        var id = (ulong)session.SessionId;
        if (!_pilots.TryGetValue(id, out var pilot) || session.CharacterId is not { } characterId)
        {
            return null;
        }
        // a gun is started with the rounds it has left: what was spent a moment ago is written first
        await _charges.FlushAsync(id).ConfigureAwait(false);
        if (await FlownShipAsync(pilot, cancellationToken) is not { } snapshot)
        {
            return null;
        }
        // a module under nanite repair does not run (the EVE University wiki, Overheating)
        if (pilot.Repairs.ContainsKey(moduleId) && snapshot.Fitted.FirstOrDefault(m => m.Id == moduleId) is { } repairing)
        {
            return ModuleResult.Refusal("MODULE_REPAIRING", repairing.TypeId, _statics.Type(repairing.TypeId)?.Name ?? "");
        }
        // where the module may be used (the client's dogmaLM refuses before it works out anything)
        if (snapshot.Fitted.Any(m => m.Id == moduleId)
            && _systems.Get(pilot.SolarSystemId) is { } place
            && ModuleRestrictions.Check(Security.ClassFromLevel(place.Security), attribute => snapshot.World.Get(moduleId, attribute)) is { } forbidden)
        {
            throw MachoErrors.ThrowUserError(forbidden);
        }
        // a cynosural field generator is lit by a fleet member and never in high-security space (EVE University wiki, Cynosural Field Generator: the
        // generator does not carry attribute 1970, so the rule is the wiki's; EveJS activationSpaceRestrictions.js says the same)
        if (snapshot.Fitted.FirstOrDefault(m => m.Id == moduleId) is { } cynoModule
            && _statics.Type(cynoModule.TypeId)?.GroupId == Eve.Services.Space.Effects.CynoFieldTypes.GeneratorGroup)
        {
            // the fleet is asked first: the order of the two refusals is the host's choice (EveJS checks the security first); with the fleet first both are reachable
            if (IsInFleetAsync is not { } inFleet || !await inFleet(pilot.CharacterId, cancellationToken).ConfigureAwait(false))
            {
                return ModuleResult.Refusal("CYNO_NOT_IN_FLEET", cynoModule.TypeId, _statics.Type(cynoModule.TypeId)?.Name ?? "");
            }
            if (_systems.Get(pilot.SolarSystemId) is { } cynoPlace && Security.IsHighSecOrHigher(Security.ClassFromLevel(cynoPlace.Security)))
            {
                throw MachoErrors.ThrowUserError(ModuleRestrictions.CantInHighSecSpace);
            }
        }
        // a module that may not be used on warp (disallowActivateOnWarp) is refused while the ship aligns to warp or is in it: the host's warp state
        if (snapshot.Fitted.FirstOrDefault(m => m.Id == moduleId) is { } warpModule
            && snapshot.World.Item(moduleId) is { State: >= ItemState.Online }
            && ModuleRestrictions.CheckWarp(attribute => snapshot.World.Get(moduleId, attribute), IsWarping(session)) is { } warpRefusal)
        {
            return ModuleResult.Refusal(warpRefusal, warpModule.TypeId, _statics.Type(warpModule.TypeId)?.Name ?? "");
        }
        // a transport field carries the capsuleer ships of the system and holds the capitals among them: the host lists them (the ships of a system are its own)
        await _statics.EnsureTypesAsync(_pilots.Values.Select(other => other.TypeId), cancellationToken).ConfigureAwait(false);
        var neighbours = _pilots.Values
            .Where(other => other.ShipId != pilot.ShipId && other.SolarSystemId == pilot.SolarSystemId)
            .Select(other => new TransportNeighbour(other.ShipId, _statics.Type(other.TypeId) is { } type && ShipSnapshot.CapitalGroupIds.Contains(type.GroupId)))
            .ToList();
        var made = ModulePackages.Of(snapshot.WithNeighbours(neighbours), _statics, moduleId, effectName, targetId, repeat, aim, aimEnd);
        if (made.Effect is not { } package)
        {
            return ModuleResult.Refusal(made.Reason ?? "REFUSED", made.ModuleType, made.ModuleName);
        }

        // a mining laser mines the family of its rock only (EVE.js isFamilyCompatibleWithYield): an ore laser takes ore, an ice laser takes ice
        if (package.Action is SimAction.Mining mining && TypeOfSpaceItemAsync is { } typeOfSpaceItem
            && await typeOfSpaceItem(package.TargetId, cancellationToken).ConfigureAwait(false) is { } rockTypeId)
        {
            await _statics.EnsureTypesAsync([rockTypeId], cancellationToken).ConfigureAwait(false);
            var rock = _statics.Type(rockTypeId);
            byte? rockFamily = rock is null || !MiningTargets.IsMineable(_statics, rock) ? null
                : MiningTargets.IsGasCloud(_statics, rock) ? SimMiningFamilies.Gas
                : _statics.GroupName(rock.GroupId).Equals("Ice", StringComparison.OrdinalIgnoreCase) ? SimMiningFamilies.Ice
                : SimMiningFamilies.Ore;
            if (rockFamily != mining.Family)
            {
                return ModuleResult.Refusal("TARGET_INVALID_FOR_MODULE", made.ModuleType, made.ModuleName);
            }
            // a crystal in the laser takes only the asteroids its list names (EVE.js isCrystalTargetCompatibleWithYield)
            if (snapshot.Fitted.FirstOrDefault(m => m.Id == moduleId) is { } laser && snapshot.ChargeOf(laser) is { Quantity: > 0 } crystal)
            {
                await _statics.EnsureTypesAsync([crystal.TypeId], cancellationToken).ConfigureAwait(false);
                var listId = _statics.AttributeId("specializationAsteroidTypeList") is { } listAttribute && _statics.Type(crystal.TypeId) is { } crystalType
                    ? (int)crystalType.Attributes.GetValueOrDefault(listAttribute, 0d)
                    : 0;
                if (!await _statics.TypeListNamesAsync(listId, rockTypeId, cancellationToken).ConfigureAwait(false))
                {
                    return ModuleResult.Refusal("CHARGE_NOT_COMPATIBLE", made.ModuleType, made.ModuleName);
                }
            }
        }

        // a tractor beam pulls a wreck or a can only when the pilot may take from it (EVE.js hasTractorLootRight); the simulation has the target's place, not the rights
        if (package.Action is SimAction.Tractor)
        {
            bool? mayPull = MayPullAsync is { } mayPullCheck
                ? await mayPullCheck(characterId, package.TargetId, cancellationToken).ConfigureAwait(false)
                : false;
            if (mayPull is false)
            {
                return ModuleResult.Refusal("TARGET_LOOT_RIGHTS_REQUIRED", made.ModuleType, made.ModuleName);
            }
        }

        var targetPilot = _pilots.Values.FirstOrDefault(other => other.ShipId == package.TargetId);
        var securityClass = _systems.Get(pilot.SolarSystemId) is { } system
            ? Security.ClassFromLevel(system.Security)
            : (SecurityClass?)null;
        AggressionRequest? assistanceRequest = null;
        Func<AggressionRequest, CancellationToken, Task>? recordAssistance = null;
        var assistance = package.Action is SimAction.Remote
        {
            What: SimRemote.Repair or SimRemote.MutadaptiveRepair or SimRemote.Transfer
                or SimRemote.Damp { RangePercent: >= 0, ResolutionPercent: >= 0, GravimetricStrengthPercent: >= 0, LadarStrengthPercent: >= 0, MagnetometricStrengthPercent: >= 0, RadarStrengthPercent: >= 0 }
                or SimRemote.Disrupt { RangePercent: >= 0, FalloffPercent: >= 0, TrackingPercent: >= 0 }
        };
        // the target's type (a pilot's ship, or an NPC's) carries what it refuses; the attributes are the static data's, as the reference's targets' are
        var targetTypeId = targetPilot?.TypeId;
        if (targetTypeId is null && _npcShips.TryGetValue(package.TargetId, out var npc))
        {
            targetTypeId = npc.TypeId;
        }
        // a target's resistance weakens a remote effect that lands on it (FP-8, EVE.js resolveTargetResistanceMultiplier): the host resolves it from the target's attributes
        IReadOnlyDictionary<int, double>? targetAttributes = null;
        // the target as it flies: the overlays of its running modules (a siege, a triage) are in the numbers it has
        if (targetPilot is { CharacterId: > 0 } resisting && await FlownShipAsync(resisting, cancellationToken) is { } resistingSnapshot)
            targetAttributes = resistingSnapshot.World.Attributes(resisting.ShipId);
        else if (targetTypeId is { } resistingType)
            targetAttributes = _statics.Type(resistingType)?.Attributes;
        if (package.Action is SimAction.Remote remoteAction && targetAttributes is not null)
        {
            var multiplier = Eve.Services.Space.Effects.RemoteResistance.MultiplierOf(
                Eve.Services.Space.Effects.RemoteResistance.AttributeOf(remoteAction.What, id => snapshot.World.Get(moduleId, id)), targetAttributes);
            package = package with { Action = remoteAction with { What = Eve.Services.Space.Effects.RemoteResistance.Weakened(remoteAction.What, multiplier) } };
        }

        if (package.TargetId > 0
            && targetTypeId is { } immuneTypeId
            && _statics.Type(immuneTypeId) is { } immuneType
            && immuneType.Attributes.TryGetValue(ModuleAttributes.DisallowOffensiveModifiers, out var immune) && immune > 0
            && (IsOffensive(package) || snapshot.World.Get(moduleId, ModuleAttributes.DisallowAgainstEwImmuneTarget) > 0))
        {
            // the client's own refusal for a target that cannot be hit by offensive modules (dogma/effects/util.py PreCheckIfImmune)
            throw MachoErrors.ThrowUserError("DeniedActivateTargetModuleDisallowed");
        }
        // a module that the ship's dogma blocks (activationBlocked 1349, a bastion's block on what needs its skill) does not start (fittingDogmaLocation ModuleIsBlocked)
        if (snapshot.World.Get(moduleId, ModuleAttributes.ActivationBlocked) > 0)
        {
            return ModuleResult.Refusal("MODULE_BLOCKED", made.ModuleType, made.ModuleName);
        }
        if (assistance)
        {
            if (targetTypeId is not { } targetTypeIdValue)
            {
                return ModuleResult.Refusal("REFUSED", made.ModuleType, made.ModuleName);
            }
            var targetType = _statics.Type(targetTypeIdValue);
            if (targetType is null
                || (targetType.Attributes.TryGetValue(ModuleAttributes.DisallowAssistance, out var disallowAssistance) && disallowAssistance > 0))
            {
                return ModuleResult.Refusal("REFUSED", made.ModuleType, made.ModuleName);
            }

            if (targetPilot is { CharacterId: > 0 } recipient
                && recipient.CharacterId != characterId
                && securityClass is { } assistanceWhere)
            {
                var request = new AggressionRequest(
                    characterId,
                    recipient.CharacterId,
                    recipient.ShipId,
                    pilot.SolarSystemId,
                    _systems.Get(pilot.SolarSystemId)?.Security ?? 0,
                    assistanceWhere,
                    await IsCapsuleAsync(recipient, assistanceWhere, cancellationToken));
                assistanceRequest = request;
                var policy = RemoteAssistancePolicyOf;
                recordAssistance = RecordRemoteAssistance;
                if (policy is null || recordAssistance is null)
                {
                    return ModuleResult.Refusal(WeaponSafety.Refusal, made.ModuleType, made.ModuleName);
                }
                var decision = await policy(request, SafetyLevel.Full, cancellationToken);
                if (!decision.Allowed && decision.BlockReason == Eve.Crimewatch.PolicyBlockReason.Safety)
                {
                    var level = SafetyLevelOf is { } ask
                        ? await ask(characterId, cancellationToken)
                        : SafetyLevel.Full;
                    decision = await policy(request, level, cancellationToken);
                }
                if (!decision.Allowed)
                {
                    return ModuleResult.Refusal(WeaponSafety.Refusal, made.ModuleType, made.ModuleName);
                }
            }
        }

        // an ECM burst jams friends too and draws CONCORD in high security space, so it is not activated there unless the pilot's safety is set to none (red)
        // (EVE University wiki, Electronic Countermeasures; EVE.js refuses only the smartbomb here)
        if (package.Action is SimAction.EcmBurst && securityClass is { } burstWhere && Security.IsHighSecOrHigher(burstWhere))
        {
            var burstSafety = SafetyLevelOf is { } askSafety ? await askSafety(characterId, cancellationToken) : SafetyLevel.Full;
            if (burstSafety != SafetyLevel.None)
            {
                return ModuleResult.Refusal(WeaponSafety.Refusal, made.ModuleType, made.ModuleName);
            }
        }

        // the safety of the pilot: a weapon is not started at the ship of another pilot where that would be a crime the safety the pilot has set does not allow
        var attack = IsOffensive(package);
        var victim = attack && targetPilot is { CharacterId: > 0 } && targetPilot.CharacterId != characterId
            ? targetPilot
            : null;
        if (securityClass is { } empire && (attack || package.Action is SimAction.Smartbomb))
        {
            var level = SafetyLevelOf is { } ask ? await ask(characterId, cancellationToken) : SafetyLevel.Full;
            if (victim is not null)
            {
                var targetIsCapsule = await IsCapsuleAsync(victim, empire, cancellationToken);
                if (WeaponSafety.RequiredLevel(empire, targetIsCapsule) is not null)
                {
                    var context = await WeaponSafetyContextAsync(characterId, victim.CharacterId, targetIsCapsule, cancellationToken);
                    if (WeaponSafety.StopsFire(empire, level, context))
                    {
                        return ModuleResult.Refusal(WeaponSafety.Refusal, made.ModuleType, made.ModuleName);
                    }
                }
            }
            // An untargeted smartbomb can hit neutral pilots as well as legal opponents, so its pilot-damage safety stays target-independent.
            if (package.Action is SimAction.Smartbomb bomb && WeaponSafety.StopsFire(empire, level))
            {
                package = package with { Action = bomb with { HurtsPilots = false } };
            }
        }
        var reply = await AskSimAsync(id, () => _sim.ActivateAsync(id, package, cancellationToken), session);
        if (!reply.Accepted)
        {
            var refusal = ModuleResult.Refusal(reply.Reason == "ALREADY_ACTIVE" ? "MODULE_ALREADY_ACTIVE" : reply.Reason ?? "REFUSED", made.ModuleType, made.ModuleName);
            // a target out of reach is told with how far the module reaches and how far the target is
            return reply.Reason == "TARGET_OUT_OF_RANGE"
                ? refusal with { TargetType = reply.TargetType, ShipType = snapshot.Ship.TypeId, DesiredRange = reply.DesiredRange, ActualDistance = reply.ActualDistance, EffectName = effectName }
                : refusal;
        }
        pilot.ActiveModules[moduleId] = ItemState.Active;
        pilot.RunningModules[moduleId] = RunningModule.Of(effectName, targetId, repeat, aim, aimEnd, package);
        await RefreshRunningAsync(pilot, id, cancellationToken).ConfigureAwait(false);
        AnnounceModeChange(pilot, snapshot, moduleId, started: true);
        await SendShipAsync(id, pilot, SimShips.VitalsOf(snapshot), cancellationToken);
        made.Accepted?.Invoke();
        // a module that may run in a gate cloak (canActivateInGateCloak: the warp core stabilizers and interdiction nullifiers) keeps the cloak of a jump
        EndInvulnerability(pilot, keepsJumpCloak: snapshot.World.Get(moduleId, ModuleAttributes.CanActivateInGateCloak) > 0);
        if (assistanceRequest is { } remoteRequest && recordAssistance is { } recordRemoteAssistance)
        {
            try
            {
                await recordRemoteAssistance(remoteRequest, cancellationToken);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                _log?.Invoke($"crimewatch could not record remote assistance by {characterId} on {remoteRequest.Target}: {error.Message}");
            }
        }
        if (victim is not null && securityClass is { } crimeWhere && Aggression is { } record)
        {
            // what the attack makes of the attacker is crimewatch's; the module runs whatever it keeps
            try
            {
                var security = _systems.Get(pilot.SolarSystemId)?.Security ?? 0;
                await record(new AggressionRequest(characterId, victim.CharacterId, victim.ShipId, pilot.SolarSystemId, security, crimeWhere, await IsCapsuleAsync(victim, crimeWhere, cancellationToken)), cancellationToken);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                _log?.Invoke($"crimewatch could not record the attack of {characterId} on {victim.CharacterId}: {error.Message}");
            }
        }
        return ModuleResult.Done;
    }

    /// <summary>
    /// Told after a pilot's module was started at the ship of another pilot: who attacked whom, where, and whether the ship attacked is a capsule (<see cref="AggressionRequest"/>).
    /// Crimewatch (timers, flags, what a crime brings) hangs here.
    /// </summary>
    public Func<AggressionRequest, CancellationToken, Task>? Aggression { get; set; }


    public Func<AggressionRequest, int, CancellationToken, Task<PolicyDecision>>? RemoteAssistancePolicyOf { get; set; }

    public Func<AggressionRequest, CancellationToken, Task>? RecordRemoteAssistance { get; set; }


    /// <summary>Host supplies the persisted Crimewatch state used to enforce criminal movement restrictions.</summary>
    public Func<long, CancellationToken, Task<CombatState>>? CrimewatchStateOf { get; set; }

    /// <summary>Host calls this after a criminal with an active timer enters high-security space.</summary>
    public Func<long, long, double, CancellationToken, Task>? CriminalEnteredSystem { get; set; }

    /// <summary>The security status of a character (its relation of that kind; 0 when it is not set). The ship in space shows it in its slim item.</summary>
    public Func<long, CancellationToken, Task<double>>? SecurityStatusOf { get; set; }

    private async Task<double> SecurityStatusAsync(long characterId, CancellationToken cancellationToken) =>
        SecurityStatusOf is { } ask ? await ask(characterId, cancellationToken) : 0;

    /// <summary>The ship safety level of a character (set by the host that keeps them; full when it is not set). See <see cref="WeaponSafety"/>.</summary>
    public Func<long, CancellationToken, Task<int>>? SafetyLevelOf { get; set; }


    /// <summary>Resolves current Crimewatch flags and pair-specific legal permissions for a player ship target.</summary>
    public Func<long, long, CancellationToken, Task<WeaponTargetContext>>? WeaponTargetContextOf { get; set; }

    /// <summary>Whether the pilot's ship is a capsule. Only a low-security attack depends on it (a capsule there is a crime of a criminal), so it is only looked up then.</summary>
    private async Task<WeaponTargetContext> WeaponSafetyContextAsync(
        long attackerCharacterId,
        long targetCharacterId,
        bool targetIsCapsule,
        CancellationToken cancellationToken)
    {
        var ordinaryPlayerAttack = new WeaponTargetContext(
            AttackerIsPlayer: true,
            TargetIsPlayerProperty: true,
            TargetIsCapsule: targetIsCapsule);
        if (WeaponTargetContextOf is not { } read)
        {
            return ordinaryPlayerAttack;
        }

        try
        {
            var resolved = await read(attackerCharacterId, targetCharacterId, cancellationToken);
            return resolved with
            {
                AttackerIsPlayer = true,
                TargetIsPlayerProperty = true,
                TargetIsCapsule = targetIsCapsule,
                SamePilot = false
            };
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            _log?.Invoke($"weapon safety facts for {attackerCharacterId} -> {targetCharacterId} failed: {error.Message}");
            return ordinaryPlayerAttack;
        }
    }

    /// <summary>
    /// The attack an ECM burst makes on each pilot it reached: the same record as a module started at the ship of another pilot (EVE.js records the aggression on
    /// every ship a burst reaches, before its roll). A reached ship that is not a pilot's (an NPC's) is not recorded.
    /// </summary>
    private async Task RecordEcmBurstAsync(Pilot burster, IReadOnlyList<SimBurstReach> reached)
    {
        if (Aggression is not { } record || _systems.Get(burster.SolarSystemId) is not { } system)
        {
            return;
        }
        var where = Security.ClassFromLevel(system.Security);
        foreach (var reach in reached)
        {
            if (_pilots.Values.FirstOrDefault(other => other.ShipId == reach.ShipId) is not { CharacterId: > 0 } victim
                || victim.CharacterId == burster.CharacterId)
            {
                continue;
            }
            try
            {
                var request = new AggressionRequest(
                    burster.CharacterId,
                    victim.CharacterId,
                    victim.ShipId,
                    burster.SolarSystemId,
                    system.Security,
                    where,
                    await IsCapsuleAsync(victim, where, CancellationToken.None));
                await record(request, CancellationToken.None);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                _log?.Invoke($"crimewatch could not record the ECM burst of {burster.CharacterId} on {victim.CharacterId}: {error.Message}");
            }
        }
    }

    private async Task<bool> IsCapsuleAsync(Pilot pilot, SecurityClass securityClass, CancellationToken cancellationToken)
    {
        if (!Security.IsLowSec(securityClass))
        {
            return false;
        }
        var snapshot = await _ships.LoadAsync(pilot.CharacterId, cancellationToken);
        return snapshot is not null && _statics.Type(snapshot.Ship.TypeId)?.GroupId == CapsuleGroup;
    }

    private const int CapsuleGroup = 29;

    /// <summary>Asks for an effect to stop at the end of the cycle it is in.</summary>
    public async Task<ModuleResult?> DeactivateAsync(ClientSession session, long moduleId, string effectName, CancellationToken cancellationToken)
    {
        var id = (ulong)session.SessionId;
        if (!_pilots.TryGetValue(id, out var pilot) || session.CharacterId is not { } characterId)
        {
            return null;
        }
        var snapshot = await FlownShipAsync(pilot, cancellationToken);
        var module = snapshot?.Fitted.FirstOrDefault(m => m.Id == moduleId);
        var type = module is null ? null : _statics.Type(module.TypeId);
        var reply = await AskSimAsync(id, () => _sim.CommandAsync(id, SimCommands.Deactivate, [moduleId], cancellationToken), session);
        if (reply.Accepted)
        {
            Eve.Services.Space.Effects.ProjectedEffect.MutadaptiveSpool.StopAfterCurrentCycle(moduleId);
            // the simulation ends the effect at the end of its cycle; the host takes the module out of its numbers now, and the ship is sent again
            if (pilot.ActiveModules.TryRemove(moduleId, out _))
                AnnounceModeChange(pilot, snapshot, moduleId, started: false);
            pilot.RunningModules.TryRemove(moduleId, out _);
            await SendShipAsync(id, pilot, snapshot is null ? null : SimShips.VitalsOf(snapshot), cancellationToken);
            await RefreshRunningAsync(pilot, id, cancellationToken).ConfigureAwait(false);
        }
        return reply.Accepted ? ModuleResult.Done : ModuleResult.Refusal(reply.Reason == "NOT_ACTIVE" ? "MODULE_NOT_ACTIVE" : reply.Reason ?? "REFUSED", module?.TypeId ?? 0, type?.Name ?? "");
    }

    /// <summary>
    /// The ship a pilot flies as dogma has it now: the stored module states, with the modules the pilot has started (<see cref="Pilot.ActiveModules"/>) active.
    /// Null when the ship is not the one the pilot flies.
    /// </summary>
    /// <summary>The group of the siege modules (SDE group 515); a siege module's start and stop are announced as the siege topics as well.</summary>
    private const int SiegeModuleGroupId = 515;

    /// <summary>A module of the ship started or stopped: the bus is told (<see cref="ShipModeTopics"/>), and a siege module's start and stop as the siege topics.</summary>
    private void AnnounceModeChange(Pilot pilot, ShipSnapshot? snapshot, long moduleId, bool started)
    {
        if (snapshot?.Fitted.FirstOrDefault(m => m.Id == moduleId) is not { } module)
        {
            return;
        }
        var payload = new ShipModeEvent(pilot.CharacterId, pilot.ShipId, moduleId, module.TypeId, pilot.SolarSystemId);
        var key = pilot.CharacterId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        Announce(started ? ShipModeTopics.ModuleStarted : ShipModeTopics.ModuleStopped, payload, key);
        if (_statics.Type(module.TypeId)?.GroupId == SiegeModuleGroupId)
        {
            Announce(started ? ShipModeTopics.SiegeEntered : ShipModeTopics.SiegeLeft, payload, key);
        }
    }

    private async Task<ShipSnapshot?> FlownShipAsync(Pilot pilot, CancellationToken cancellationToken)
    {
        if (await _ships.LoadAsync(pilot.CharacterId, cancellationToken) is not { } snapshot || snapshot.Ship.Id != pilot.ShipId)
        {
            return null;
        }
        foreach (var (moduleId, state) in pilot.ActiveModules)
        {
            snapshot.World.SetState(moduleId, state);
        }
        // an overheated module that runs is Overloaded to dogma: its overload effects apply to what it runs (DogmaWorld runs an Overload effect from Overloaded up)
        foreach (var moduleId in pilot.Overheated.Keys)
        {
            if (snapshot.World.Item(moduleId) is { State: >= ItemState.Active })
            {
                snapshot.World.SetState(moduleId, ItemState.Overloaded);
            }
        }
        return snapshot;
    }

    /// <summary>The overheat the pilot asked for, kept until it is asked off (FP-C4a); a module that goes offline is not Overloaded to dogma.</summary>
    private static void MarkOverheated(Pilot pilot, IEnumerable<long> moduleIds, bool active)
    {
        foreach (var moduleId in moduleIds)
        {
            if (active)
            {
                pilot.Overheated[moduleId] = 0;
            }
            else
            {
                pilot.Overheated.TryRemove(moduleId, out _);
            }
        }
    }

    /// <summary>
    /// The one path that tells the simulation what a ship in space is now: its resonances and numbers as dogma has them, with the modules the pilot
    /// has started. Every change of a module's state in space (activation, stop, online, offline) goes through here, so the simulation never keeps
    /// a number it was told before the change. <paramref name="before"/> is the ship's vitals before the change: when the change moves no resonance
    /// and no hit points, nothing is sent (null: send).
    /// </summary>
    private async Task SendShipAsync(ulong sessionId, Pilot pilot, SimVitals? before, CancellationToken cancellationToken)
    {
        if (!_sim.IsConnected || await FlownShipAsync(pilot, cancellationToken) is not { } snapshot)
        {
            return;
        }
        if (before is not null && SameVitals(before, SimShips.VitalsOf(snapshot)))
        {
            return;
        }
        _sim.ShipChanged(sessionId, SimShips.Of(snapshot, _statics, await SecurityStatusAsync(pilot.CharacterId, cancellationToken)));
    }

    /// <summary>Whether two ships take and resist the same (the lists are compared by value; a record's equality would compare them by reference).</summary>
    private static bool SameVitals(SimVitals a, SimVitals b) =>
        a.ShieldCapacity == b.ShieldCapacity && a.ArmorHp == b.ArmorHp && a.StructureHp == b.StructureHp
        && a.Resonances.Shield.SequenceEqual(b.Resonances.Shield) && a.Resonances.Armor.SequenceEqual(b.Resonances.Armor)
        && a.Resonances.Structure.SequenceEqual(b.Resonances.Structure);

    /// <summary>Whether the pilot's module runs in space: the host started it and the simulation has not ended it (<see cref="SimModuleEnded"/>).</summary>
    public bool IsModuleActive(ulong sessionId, long moduleId) =>
        _pilots.TryGetValue(sessionId, out var pilot) && pilot.ActiveModules.ContainsKey(moduleId);

    /// <inheritdoc/>
    public Task RefreshShipAsync(ClientSession session, CancellationToken cancellationToken) =>
        _pilots.TryGetValue((ulong)session.SessionId, out var pilot)
            ? SendShipAsync((ulong)session.SessionId, pilot, null, cancellationToken)
            : Task.CompletedTask;

    public async Task<ModuleResult?> OfflineAsync(ClientSession session, long moduleId, CancellationToken cancellationToken)
    {
        var id = (ulong)session.SessionId;
        if (!_pilots.TryGetValue(id, out var pilot)) return null;
        if (!_sim.IsConnected) return ModuleResult.Refusal("SIM_UNAVAILABLE");
        var snapshot = await FlownShipAsync(pilot, cancellationToken);
        if (snapshot is not null)
            await _sim.SetHeatProfileAsync(id, SimShips.HeatProfileOf(snapshot, _statics), cancellationToken);
        await _sim.OfflineModuleAsync(id, moduleId, cancellationToken);
        if (pilot.ActiveModules.TryRemove(moduleId, out _))
            AnnounceModeChange(pilot, snapshot, moduleId, started: false);
        pilot.RunningModules.TryRemove(moduleId, out _);
        Eve.Services.Space.Effects.ProjectedEffect.MutadaptiveSpool.Reset(moduleId);
        await RefreshRunningAsync(pilot, id, cancellationToken).ConfigureAwait(false);
        return ModuleResult.Done;
    }

    public async Task<ModuleResult?> OverloadAsync(ClientSession session, long moduleId, int effectId, bool active, CancellationToken cancellationToken)
    {
        var id = (ulong)session.SessionId;
        if (!_pilots.TryGetValue(id, out var overheating) || session.CharacterId is not { } characterId) return null;
        if (!_sim.IsConnected) return ModuleResult.Refusal("SIM_UNAVAILABLE");

        var snapshot = await _ships.LoadAsync(characterId, cancellationToken);
        var module = snapshot?.Fitted.FirstOrDefault(item => item.Id == moduleId);
        if (module is null || snapshot is null) return ModuleResult.Refusal("MODULE_NOT_FOUND");
        var type = _statics.Type(module.TypeId);
        if (active && !overheating.Repairs.IsEmpty) return ModuleResult.Refusal("REPAIR_IN_PROGRESS", module.TypeId, type?.Name ?? "");
        var overload = type?.EffectIds.Select(_statics.Effect)
            .FirstOrDefault(effect => effect is { Category: EffectDef.Overload } && effect.Id == effectId);
        if (overload is null) return ModuleResult.Refusal("MODULE_NOT_OVERLOADABLE", module.TypeId, type?.Name ?? "");
        if (snapshot.World.Get(moduleId, 3) >= snapshot.World.Get(moduleId, 9)) return ModuleResult.Refusal("MODULE_INCAPACITATED", module.TypeId, type?.Name ?? "");
        if (snapshot.World.Item(moduleId) is not { State: >= ItemState.Online })
            return ModuleResult.Refusal("MODULE_NOT_ONLINE", module.TypeId, type?.Name ?? "");
        if (active && snapshot.Skills.FirstOrDefault(skill => skill.TypeId == 28164)?.Level is not > 0)
            return ModuleResult.Refusal("THERMODYNAMICS_SKILL_REQUIRED", module.TypeId, type?.Name ?? "");

        await _sim.SetHeatProfileAsync(id, SimShips.HeatProfileOf(snapshot, _statics), cancellationToken);
        await _sim.SetModuleOverheatAsync(id, moduleId, active, cancellationToken);
        MarkOverheated(overheating, [moduleId], active);
        await RefreshRunningAsync(overheating, id, cancellationToken).ConfigureAwait(false);
        return ModuleResult.Done;
    }

    public async Task<ModuleResult?> OverloadRackAsync(ClientSession session, long moduleId, bool active, CancellationToken cancellationToken)
    {
        var id = (ulong)session.SessionId;
        if (!_pilots.TryGetValue(id, out var pilot)) return null;
        if (!_sim.IsConnected) return ModuleResult.Refusal("SIM_UNAVAILABLE");
        if (session.CharacterId is not { } characterId) return ModuleResult.Refusal("NO_SHIP_IN_SPACE");

        var snapshot = await _ships.LoadAsync(characterId, cancellationToken);
        var anchor = snapshot?.Fitted.FirstOrDefault(item => item.Id == moduleId);
        if (snapshot is null || anchor is null) return ModuleResult.Refusal("MODULE_NOT_FOUND");
        static int RackOf(int flag) => flag switch { >= 27 and <= 34 => 0, >= 19 and <= 26 => 1, >= 11 and <= 18 => 2, _ => -1 };
        if (active && !pilot.Repairs.IsEmpty) return ModuleResult.Refusal("REPAIR_IN_PROGRESS", anchor.TypeId, _statics.Type(anchor.TypeId)?.Name ?? "");
        var rack = RackOf(anchor.Flag);
        if (rack < 0) return ModuleResult.Refusal("MODULE_NOT_OVERLOADABLE", anchor.TypeId, _statics.Type(anchor.TypeId)?.Name ?? "");
        if (active && snapshot.Skills.FirstOrDefault(skill => skill.TypeId == 28164)?.Level is not > 0)
            return ModuleResult.Refusal("THERMODYNAMICS_SKILL_REQUIRED", anchor.TypeId, _statics.Type(anchor.TypeId)?.Name ?? "");

        bool Overloadable(Eve.Inventory.Item item) =>
            _statics.Type(item.TypeId)?.EffectIds.Select(_statics.Effect).Any(effect => effect?.Category == EffectDef.Overload) == true;
        var modules = snapshot.Fitted
            .Where(item => RackOf(item.Flag) == rack && snapshot.World.Item(item.Id) is { State: >= ItemState.Online } && Overloadable(item))
            .Where(item => snapshot.World.Get(item.Id, 3) < snapshot.World.Get(item.Id, 9))
            .OrderBy(item => item.Flag)
            .Select(item => item.Id)
            .ToArray();
        if (modules.Length == 0) return ModuleResult.Refusal("MODULE_NOT_OVERLOADABLE", anchor.TypeId, _statics.Type(anchor.TypeId)?.Name ?? "");

        await _sim.SetHeatProfileAsync(id, SimShips.HeatProfileOf(snapshot, _statics), cancellationToken);
        await _sim.SetRackOverheatAsync(id, rack, active, cancellationToken);
        MarkOverheated(pilot, modules, active);
        await RefreshRunningAsync(pilot, id, cancellationToken).ConfigureAwait(false);
        return ModuleResult.Done with { ModuleIds = modules };
    }

    /// <summary>What the ship in space has taken now, from the simulation.</summary>
    public async Task<SpaceCondition?> ConditionAsync(ClientSession session, CancellationToken cancellationToken)
    {
        var id = (ulong)session.SessionId;
        if (!_pilots.ContainsKey(id))
        {
            return null;
        }
        var reply = await AskSimAsync(id, () => _sim.CommandAsync(id, SimCommands.GetCondition, [], cancellationToken), session);
        return reply.Condition is { } condition ? new SpaceCondition(condition.Damage, condition.Charge, condition.ShieldCharge, condition.ArmorDamage) : null;
    }

    /// <summary>Mends the ship in space: the simulation tells the pilot and those who see it before it answers.</summary>
    public async Task<bool> HealAsync(ClientSession session, CancellationToken cancellationToken)
    {
        var id = (ulong)session.SessionId;
        if (!_pilots.ContainsKey(id))
        {
            return false;
        }
        var reply = await AskSimAsync(id, () => _sim.CommandAsync(id, SimCommands.Heal, [], cancellationToken), session);
        return reply.Accepted;
    }

    // ── hulls nobody flies ───────────────────────────────────────────────────
}
