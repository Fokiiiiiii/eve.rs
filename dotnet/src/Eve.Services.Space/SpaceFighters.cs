using System.Collections.Concurrent;
using System.Text.Json;
using Eve.Dogma;
using Eve.Inventory;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;
using Eve.Services.Space.Effects;
using Eve.Services.Ships;
using Eve.Ships;
using Eve.Space;
using Eve.Universe;

namespace Eve.Services.Space;

/// <summary>
/// The player fighters of the pilots, as the host serves <c>fighterMgr</c> (EVE.js <c>fighterMgrService</c>). The fighter bay and the launch tubes are items of
/// the ship in the database: a squadron goes into a tube, out of a tube into space, and back into a tube here. A squadron out in space is the simulation's
/// (<see cref="ISimulation.LaunchFighterAsync"/>, <see cref="ISimulation.FighterOrderAsync"/>): it flies, and it lands or is let go as the simulation says
/// (<c>SimFighterLanded</c>, <c>SimFighterReleased</c>). Without a simulation no squadron goes out, and each such call is refused with its reason.
/// The abilities of a squadron run in the simulation as effects built from its type (<see cref="FighterAbilityEffects"/>). Their charges and cooldowns are
/// kept on the squadron's item, so they survive a restart; a squadron that is out in space when its pilot enters space again is taken up again
/// (<see cref="RebuildAsync"/>). A squadron lost (destroyed) is removed from the database and its pilot is told. The scoop of an abandoned squadron takes
/// it back into the bay or a tube (ADR 0015). Launch bomb is refused with its blocker.
/// </summary>
public sealed class SpaceFighters : ISpaceFighters, IDisposable
{
    /// <summary>The location of an item that is gone (the junkyard, as the drones' items are sent there).</summary>
    private const long Junkyard = 6;
    private const string NoShipText = "Your ship could not be found.";
    private const string NotInSpaceText = "That fighter is not in space.";
    private const string NotInBayOrTubeText = "A fighter can only be scooped into the fighter bay or a launch tube.";
    private const string EmptyTubeText = "That launch tube is empty.";
    private const string StillOutText = "That launch tube's squadron is still in space.";
    private const string NoSimulationText = "The space simulation is not running, so fighter squadrons cannot go out.";
    private const string NoSolarSystemText = "Your ship is not in space.";

    /// <summary>The orbit an order gives when it names no distance, metres (the reference's default).</summary>
    private const double DefaultOrbitMeters = 1_500;

    /// <summary>A squadron that is out in space: the item it is, the character and ship that launched it, the tube it came from and the pilot to tell.</summary>
    private sealed record Launched(
        long FighterId, int TypeId, long CharacterId, long CorporationId, long AllianceId, long WarFactionId,
        long ShipId, int SolarSystemId, int TubeFlag, ClientSession Pilot, ulong? SquadronSession = null);

    /// <summary>The members of each squadron out in space now (what its abilities land per member), and the state of its ability slots.</summary>
    private readonly ConcurrentDictionary<long, int> _sizes = new();
    private readonly ConcurrentDictionary<(long Fighter, int Slot), AbilitySlot> _slots = new();

    private readonly ISimulation? _sim;
    private readonly CharacterShips _ships;
    private readonly DogmaStatic _statics;
    private readonly Func<ClientSession, int?> _solarSystemOf;
    private readonly Func<int, double?>? _securityOf;
    private readonly Action<string>? _log;
    private readonly ConcurrentDictionary<long, Launched> _launched = new();
    /// <summary>The squadrons let go in space: their items stay there until they are scooped back or destroyed (the simulation keeps them).</summary>
    private readonly ConcurrentDictionary<long, Launched> _abandoned = new();
    private long _requests;

    public SpaceFighters(ISimulation? sim, CharacterShips ships, DogmaStatic statics, Func<ClientSession, int?> solarSystemOf, Action<string>? log = null,
        Func<int, double?>? securityOf = null)
    {
        _sim = sim;
        _ships = ships;
        _statics = statics;
        _solarSystemOf = solarSystemOf;
        _log = log;
        _securityOf = securityOf;
        if (_sim is not null)
        {
            _sim.Received += OnReceived;
            _sim.Lost += OnLost;
        }
    }

    public void Dispose()
    {
        if (_sim is not null)
        {
            _sim.Received -= OnReceived;
            _sim.Lost -= OnLost;
        }
    }

    public async ValueTask<PyValue> GetFightersForShipAsync(ServiceCall call)
    {
        if (ShipOf(call.Session) is not { } shipId || call.Session.CharacterId is not { } characterId)
        {
            throw MachoErrors.ThrowUserError(NoShipText);
        }
        var inTubes = (await _ships.FightersOfShipAsync(shipId, call.Cancellation))
            .Where(i => FighterTubes.IsTube(i.Flag))
            .OrderBy(i => i.Flag)
            .ToList();
        var inSpace = _solarSystemOf(call.Session) is { } system
            ? await _ships.FightersInSpaceAsync(characterId, system, call.Cancellation)
            : [];
        return new PyTuple([
            new PyList([.. inTubes.Select(i => FighterRow(i.Flag, i))]),
            new PyList([.. inSpace.Select(i => FighterRow(i.Flag, i))]),
            new PyDict([]),
        ]);
    }

    public async ValueTask<PyValue> LoadFightersToTubeAsync(ServiceCall call)
    {
        var (session, characterId, shipId) = Pilot(call);
        var tubeFlag = (int)IdOf(call.Arg(1));
        if (_launched.Values.Any(l => l.ShipId == shipId && l.TubeFlag == tubeFlag))
        {
            throw MachoErrors.ThrowUserError(StillOutText);
        }
        var move = await _ships.LoadFighterToTubeAsync(characterId, shipId, IdOf(call.Arg(0)), tubeFlag, call.Cancellation);
        if (!move.Accepted)
        {
            throw MachoErrors.ThrowUserError(MessageOf(move));
        }
        var squadron = move.Change!.After;
        ShipNotifications.SendItemChange(session, move.Change!, RowOf);
        session.SendNotification("OnFighterTubeContentUpdate", "clientID",
            new PyInt(tubeFlag), InventoryWire.Id(squadron.Id), new PyInt(squadron.TypeId),
            new PyInt((int)FighterTubes.InventorySize(squadron.Singleton, squadron.Quantity)));
        NotifyTubeState(session, tubeFlag, "READY");
        return PyBool.True;
    }

    public async ValueTask<PyValue> UnloadTubeToFighterBayAsync(ServiceCall call)
    {
        var (session, characterId, shipId) = Pilot(call);
        var tubeFlag = (int)IdOf(call.Arg(0));
        var move = await _ships.UnloadTubeToBayAsync(characterId, shipId, tubeFlag, call.Cancellation);
        if (!move.Accepted)
        {
            throw MachoErrors.ThrowUserError(MessageOf(move));
        }
        ShipNotifications.SendItemChange(session, move.Change!, RowOf);
        session.SendNotification("OnFighterTubeContentEmpty", "clientID", new PyInt(tubeFlag));
        NotifyTubeState(session, tubeFlag, "EMPTY");
        return PyNone.Instance;
    }

    /// <summary>Each tube's squadron goes out of its tube into space: the answer names the tubes that did not launch, each with its reason.</summary>
    public async ValueTask<PyValue> LaunchFightersFromTubesAsync(ServiceCall call)
    {
        var (session, characterId, shipId) = Pilot(call);
        var entries = new List<KeyValuePair<PyValue, PyValue>>();
        foreach (var tube in IdsOf(call.Arg(0)))
        {
            var tubeFlag = (int)tube;
            var refusal = await LaunchFromTubeAsync(session, characterId, shipId, tubeFlag, call.Cancellation);
            if (refusal is not null)
            {
                entries.Add(new(new PyInt(tubeFlag), MachoErrors.UserErrorPayload(refusal)));
            }
        }
        return new PyDict(entries);
    }

    /// <summary>The squadrons out in space come back to their tubes: each that the simulation takes is told RECALLING; one that is not out is refused.</summary>
    public async ValueTask<PyValue> RecallFightersToTubesAsync(ServiceCall call)
    {
        var (session, characterId, _) = Pilot(call);
        var entries = new List<KeyValuePair<PyValue, PyValue>>();
        var sent = new List<long>();
        foreach (var id in IdsOf(call.Arg(0)))
        {
            if (await SquadronInSpaceAsync(session, characterId, id, call.Cancellation) is null)
            {
                entries.Add(new(InventoryWire.Id(id), MachoErrors.UserErrorPayload(NotInSpaceText)));
            }
            else
            {
                sent.Add(id);
            }
        }
        if (sent.Count > 0)
        {
            var reply = await OrderAsync(session, SimFighterOrders.Recall, 0, 0, sent, call.Cancellation);
            foreach (var id in sent)
            {
                var reason = ReasonOf(reply, id);
                if (reason is null || reason.Length > 0)
                {
                    entries.Add(new(InventoryWire.Id(id), MachoErrors.UserErrorPayload(reason ?? NoSimulationText)));
                    continue;
                }
                entries.Add(new(InventoryWire.Id(id), PyNone.Instance));
                if (_launched.TryGetValue(id, out var squadron))
                {
                    NotifyTubeState(session, squadron.TubeFlag, "RECALLING");
                }
            }
        }
        return new PyDict(entries);
    }

    /// <summary>
    /// Starts an ability slot of each squadron the pilot names (EVE.js <c>activateAbilitySlots</c>: the fighters, the slot and the target). Each is answered
    /// with what it did: nothing when it started, else the reason as a user error. The notice that it is running is queued after the answer.
    /// </summary>
    public async ValueTask<PyValue> CmdActivateAbilitySlotsAsync(ServiceCall call)
    {
        var (session, characterId, _) = Pilot(call);
        var slot = (int)IdOf(call.Arg(1));
        // a point-targeted ability (a LaunchBomb) names a point, any other names a target (the client's ActivateAbilitySlotsAtPoint and OnTarget)
        var point = PointOf(call.Arg(2));
        var target = point is null ? IdOf(call.Arg(2)) : 0;
        var entries = new List<KeyValuePair<PyValue, PyValue>>();
        foreach (var id in IdsOf(call.Arg(0)))
        {
            var refusal = await ActivateAbilityAsync(session, characterId, id, slot, target, point, call.Cancellation);
            entries.Add(new(InventoryWire.Id(id), refusal is null ? PyNone.Instance : MachoErrors.UserErrorPayload(refusal)));
        }
        return new PyDict(entries);
    }

    /// <summary>Stops an ability slot that is running on each squadron the pilot names; the notice that it stopped is queued after the answer.</summary>
    public async ValueTask<PyValue> CmdDeactivateAbilitySlotsAsync(ServiceCall call)
    {
        var (session, characterId, _) = Pilot(call);
        var slot = (int)IdOf(call.Arg(1));
        var entries = new List<KeyValuePair<PyValue, PyValue>>();
        foreach (var id in IdsOf(call.Arg(0)))
        {
            var refusal = await DeactivateAbilityAsync(session, characterId, id, slot, call.Cancellation);
            entries.Add(new(InventoryWire.Id(id), refusal is null ? PyNone.Instance : MachoErrors.UserErrorPayload(refusal)));
        }
        return new PyDict(entries);
    }

    /// <summary>The id of a squadron's ability module in the simulation: the squadron's item id and its slot (the slots are 0 to 7).</summary>
    private static long ModuleIdOf(long fighterId, int slot) => fighterId * 8 + slot;

    private async Task<string?> ActivateAbilityAsync(ClientSession session, long characterId, long fighterId, int slot, long target, double[]? point,
        CancellationToken cancellationToken)
    {
        if (_sim is null)
        {
            return NoSimulationText;
        }
        if (!_launched.TryGetValue(fighterId, out var squadron) || squadron.CharacterId != characterId || squadron.SquadronSession is not { } squadronSession)
        {
            return NotInSpaceText;
        }
        await _statics.EnsureTypesAsync([squadron.TypeId], cancellationToken).ConfigureAwait(false);
        if (_statics.Type(squadron.TypeId) is not { } type || FighterAbilityCatalogue.Default.Slot(squadron.TypeId, slot) is not { } entry
            || FighterAbilityCatalogue.Default.Ability(entry.AbilityId) is not { } ability)
        {
            return "That fighter has no such ability slot.";
        }
        if (SecurityRefusal(ability, squadron) is { } securityRefusal)
        {
            return Describe(securityRefusal);
        }
        // the bomb a LaunchBomb launches is the squadron type's own (its SDE attribute 2324); the blast reads its numbers
        var bombTypeId = (int)type.Attributes.GetValueOrDefault(FighterAbilityEffects.LaunchBombTypeAttribute, 0);
        if (bombTypeId > 0)
        {
            await _statics.EnsureTypesAsync([bombTypeId], cancellationToken).ConfigureAwait(false);
        }
        var bomb = bombTypeId > 0 ? _statics.Type(bombTypeId) : null;
        var state = _slots.GetOrAdd((fighterId, slot), _ => new AbilitySlot());
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (state.Begin(now, entry.Charges is { } charges ? charges.ChargeCount : null) is { } refused)
        {
            return Describe(refused);
        }
        var members = _sizes.GetValueOrDefault(fighterId, 1);
        var (effect, reason) = FighterAbilityEffects.Build(FighterAbilityEffects.EffectNameOf(ability.EffectFamily), type, _statics,
            ModuleIdOf(fighterId, slot), target, members, entry.CooldownSeconds, point, bomb);
        if (effect is null)
        {
            state.Cancel();
            return Describe(reason ?? UnsupportedEffectCode);
        }
        CommandReply reply;
        try
        {
            reply = await _sim.ActivateAsync(squadronSession, effect, cancellationToken).ConfigureAwait(false);
        }
        catch (SimUnavailableException error)
        {
            state.Cancel();
            _log?.Invoke($"fighters: {error.Message}");
            return NoSimulationText;
        }
        if (!reply.Accepted)
        {
            state.Cancel();
            return Describe(reply.Reason ?? UnsupportedEffectCode);
        }
        state.Started(now, effect.DurationMs, entry.CooldownSeconds);
        Keep(squadron);
        var pilot = squadron.Pilot;
        Later(() => pilot.SendNotification("OnFighterAbilitySlotActivated", "clientID",
            InventoryWire.Id(fighterId), new PyInt(slot), state.ChargesLeftValue is { } left ? new PyInt(left) : PyNone.Instance,
            FileTimeOf(now), new PyInt((int)Math.Max(1, effect.DurationMs)), state.CooldownNotice(FileTimeOf)));
        return null;
    }

    /// <summary>
    /// The refusal of an ability the system's security class forbids (EVE.js <c>buildAbilitySecurityRestrictionError</c>: a high-security system refuses an ability
    /// flagged for high security, a low-security one one flagged for low security), or null. A system the catalogue does not know is not refused.
    /// </summary>
    private string? SecurityRefusal(FighterAbility ability, Launched squadron)
    {
        if (_securityOf is null || _solarSystemOf(squadron.Pilot) is not { } system || _securityOf(system) is not { } level)
        {
            return null;
        }
        var securityClass = Security.ClassFromLevel(level);
        if (Security.IsHighSecOrHigher(securityClass) && ability.DisallowInHighSec)
        {
            return ModuleRestrictions.CantInHighSecSpace;
        }
        return Security.IsLowSec(securityClass) && ability.DisallowInLowSec ? ModuleRestrictions.CantInEmpireSpace : null;
    }

    private async Task<string?> DeactivateAbilityAsync(ClientSession session, long characterId, long fighterId, int slot, CancellationToken cancellationToken)
    {
        if (_sim is null)
        {
            return NoSimulationText;
        }
        if (!_launched.TryGetValue(fighterId, out var squadron) || squadron.CharacterId != characterId || squadron.SquadronSession is not { } squadronSession)
        {
            return NotInSpaceText;
        }
        if (!_slots.TryGetValue((fighterId, slot), out var state) || !state.IsActive)
        {
            return "That ability is not running.";
        }
        CommandReply reply;
        try
        {
            reply = await _sim.CommandAsync(squadronSession, SimCommands.Deactivate, [ModuleIdOf(fighterId, slot)], cancellationToken).ConfigureAwait(false);
        }
        catch (SimUnavailableException error)
        {
            _log?.Invoke($"fighters: {error.Message}");
            return NoSimulationText;
        }
        if (!reply.Accepted)
        {
            return Describe(reply.Reason ?? UnsupportedEffectCode);
        }
        // the ability ends at the end of its cycle in the simulation: the pilot is told it stopped (the notice of the reference, queued after the answer)
        var pilot = squadron.Pilot;
        Later(() => pilot.SendNotification("OnFighterAbilitySlotDeactivated", "clientID", InventoryWire.Id(fighterId), new PyInt(slot)));
        state.Stop();
        return null;
    }

    /// <summary>Whatever a squadron's ability ends at the simulation (its cycles are done, it was stopped, or its target was lost): the slot is free again.</summary>
    private void OnAbilityEnded(long fighterId, int slot)
    {
        if (!_launched.TryGetValue(fighterId, out var squadron) || !_slots.TryGetValue((fighterId, slot), out var state) || !state.IsActive)
        {
            return;
        }
        state.Stop();
        var pilot = squadron.Pilot;
        Later(() => pilot.SendNotification("OnFighterAbilitySlotDeactivated", "clientID", InventoryWire.Id(fighterId), new PyInt(slot)));
    }

    /// <summary>The reason an ability's refusal code is said to the pilot.</summary>
    private static string Describe(string reason) => reason switch
    {
        FighterAbilityEffects.PointRequired => "That fighter ability requires a target point.",
        FighterAbilityEffects.NoBomb => "That fighter has no bomb to launch.",
        ModuleRestrictions.CantInHighSecSpace => "That ability cannot be used in high security space.",
        ModuleRestrictions.CantInEmpireSpace => "That ability cannot be used in low security space.",
        "ALREADY_ACTIVE" => "That ability is already running.",
        "NO_COOLDOWN" => "That ability has no cooldown to jump on.",
        "TARGET_REQUIRED" => "That ability needs a target.",
        "MJD_COOLDOWN" => "That ability is still cooling down.",
        "NO_CHARGES" => "That ability has no charges left.",
        "COOLING_DOWN" => "That ability is still cooling down.",
        "NOT_ACTIVE" => "That ability is not running.",
        "UNSUPPORTED_EFFECT" => "That ability is not in the simulation yet (ADR 0015).",
        _ => reason,
    };

    private const string UnsupportedEffectCode = "UNSUPPORTED_EFFECT";

    /// <summary>The filetime of a moment, as the client reads a notice's time.</summary>
    private static PyValue FileTimeOf(long unixMs) => new PyLong(Py.FileTimeFromUnixMs(unixMs));

    /// <summary>Runs an action after the answer to the call is sent (the reference queues these notices after its reply).</summary>
    private void Later(Action action) => _ = Task.Run(async () =>
    {
        try
        {
            await Task.Delay(AfterAnswer).ConfigureAwait(false);
            action();
        }
        catch (Exception error)
        {
            _log?.Invoke($"fighters: a notice could not be sent: {error.Message}");
        }
    });

    /// <summary>How long after its answer a queued notice is sent (a reply is sent within this, the reference's setImmediate).</summary>
    private static readonly TimeSpan AfterAnswer = TimeSpan.FromMilliseconds(50);

    /// <summary>The state of one ability slot of a squadron: whether it runs, when it may run again, and the charges it has left.</summary>
    private sealed class AbilitySlot
    {
        private readonly object _lock = new();
        private bool _active;
        private long _activeSince;
        private double _duration;
        private long _cooldownUntil;
        private long _cooldownStart;
        private int? _charges;

        public bool IsActive { get { lock (_lock) { return _active; } } }

        /// <summary>The charges left, or null for a slot without charges.</summary>
        public int? ChargesLeftValue { get { lock (_lock) { return _charges; } } }

        /// <summary>The charges left and the cooldown's end (Unix milliseconds, 0 for none), as the squadron's item keeps them.</summary>
        public (int? Charges, long CooldownUntilMs) Persisted { get { lock (_lock) { return (_charges, _cooldownUntil); } } }

        /// <summary>A slot resumed from what its item kept: its charges, and a cooldown that may still run.</summary>
        public static AbilitySlot Resumed(int? charges, long cooldownUntilMs) => new() { _charges = charges, _cooldownUntil = cooldownUntilMs };

        /// <summary>Checks the slot may start now, and holds it (returns the refusal when it may not).</summary>
        public string? Begin(long now, int? charges)
        {
            lock (_lock)
            {
                if (_active)
                {
                    return "ALREADY_ACTIVE";
                }
                if (now < _cooldownUntil)
                {
                    return "COOLING_DOWN";
                }
                if (charges is not null)
                {
                    _charges ??= charges;
                    if (_charges <= 0)
                    {
                        return "NO_CHARGES";
                    }
                }
                _active = true;
                return null;
            }
        }

        /// <summary>The simulation took the start: the slot runs for its duration, its cooldown begins and a charge is spent.</summary>
        public void Started(long now, double durationMs, double? cooldownSeconds)
        {
            lock (_lock)
            {
                _activeSince = now;
                _duration = durationMs;
                if (_charges is { } left)
                {
                    _charges = left - 1;
                }
                if (cooldownSeconds is > 0)
                {
                    _cooldownStart = now;
                    _cooldownUntil = now + (long)Math.Round(cooldownSeconds.Value * 1000);
                }
            }
        }

        /// <summary>The start was refused: the slot is as it was.</summary>
        public void Cancel()
        {
            lock (_lock)
            {
                _active = false;
            }
        }

        public void Stop()
        {
            lock (_lock)
            {
                _active = false;
            }
        }

        /// <summary>The cooldown the notice tells, as the reference's pair of times, or none when the slot has none running.</summary>
        public PyValue CooldownNotice(Func<long, PyValue> filetime)
        {
            lock (_lock)
            {
                return _cooldownUntil > _cooldownStart && _cooldownStart > 0
                    ? new PyList([filetime(_cooldownStart), filetime(_cooldownUntil)])
                    : PyNone.Instance;
            }
        }
    }

    /// <summary>The squadrons out in space are given the movement order: stop, orbit or follow. A point is not in the simulation yet, and is refused.</summary>
    public async ValueTask<PyValue> ExecuteMovementCommandOnFightersAsync(ServiceCall call)
    {
        var (session, characterId, _) = Pilot(call);
        var inSpace = new List<long>();
        foreach (var id in IdsOf(call.Arg(0)))
        {
            if (await SquadronInSpaceAsync(session, characterId, id, call.Cancellation) is not null)
            {
                inSpace.Add(id);
            }
        }
        if (inSpace.Count == 0)
        {
            throw MachoErrors.ThrowUserError("None of the selected fighters is in space.");
        }
        var command = Eve.Macho.JsSemantics.StringValue(call.Arg(1)) ?? "";
        if (command == "GOTO_POINT")
        {
            return await GotoPointAsync(session, inSpace, call.Arg(2), call.Cancellation);
        }
        var (code, target, range) = command switch
        {
            "STOP" => (SimFighterOrders.Stop, 0L, 0.0),
            "ORBIT" => (SimFighterOrders.Orbit, IdOf(call.Arg(2)), NumberOf(call.Arg(3), DefaultOrbitMeters)),
            "FOLLOW" => (SimFighterOrders.Follow, IdOf(call.Arg(2)), NumberOf(call.Arg(3), DefaultOrbitMeters)),
            _ => throw MachoErrors.ThrowUserError($"{command} is not a movement order for fighters."),
        };
        var reply = await OrderAsync(session, code, target, range, inSpace, call.Cancellation);
        foreach (var id in inSpace)
        {
            var reason = ReasonOf(reply, id);
            if (reason is null)
            {
                throw MachoErrors.ThrowUserError(NoSimulationText);
            }
            if (reason.Length > 0)
            {
                throw MachoErrors.ThrowUserError(reason);
            }
        }
        return PyNone.Instance;
    }

    /// <summary>The squadrons fly to the point the pilot names (GOTO_POINT, the reference's `issueFighterGotoPoint`); each the simulation refuses is refused with its reason.</summary>
    private async Task<PyValue> GotoPointAsync(ClientSession session, IReadOnlyList<long> fighters, PyValue pointValue, CancellationToken cancellationToken)
    {
        if (PointOf(pointValue) is not { } point)
        {
            throw MachoErrors.ThrowUserError("A fighter flies to a point of three coordinates.");
        }
        if (_sim is null)
        {
            throw MachoErrors.ThrowUserError(NoSimulationText);
        }
        CommandReply? reply;
        try
        {
            reply = await _sim.FighterGotoPointAsync((ulong)session.SessionId, fighters, point, cancellationToken).ConfigureAwait(false);
        }
        catch (SimUnavailableException error)
        {
            _log?.Invoke($"fighters: {error.Message}");
            throw MachoErrors.ThrowUserError(NoSimulationText);
        }
        foreach (var id in fighters)
        {
            var reason = ReasonOf(reply, id) ?? NoSimulationText;
            if (reason.Length > 0)
            {
                throw MachoErrors.ThrowUserError(reason);
            }
        }
        return PyNone.Instance;
    }

    /// <summary>
    /// The squadron out in space is let go: it stays in space under nobody (the simulation releases it and the host is told of it, as the reference does).
    /// A squadron that is not out in space is refused with that reason.
    /// </summary>
    public async ValueTask<PyValue> CmdAbandonFighterAsync(ServiceCall call)
    {
        var (session, characterId, _) = Pilot(call);
        var id = IdOf(call.Arg(0));
        if (await SquadronInSpaceAsync(session, characterId, id, call.Cancellation) is null)
        {
            throw MachoErrors.ThrowUserError(NotInSpaceText);
        }
        var reply = await OrderAsync(session, SimFighterOrders.Abandon, 0, 0, [id], call.Cancellation);
        var reason = ReasonOf(reply, id) ?? NoSimulationText;
        if (reason.Length > 0)
        {
            throw MachoErrors.ThrowUserError(reason);
        }
        return PyBool.True;
    }

    /// <summary>A squadron in space cannot be scooped back yet (the simulation has no scoop); a squadron out of space says so.</summary>
    public async ValueTask<PyValue> CmdScoopAbandonedFighterFromSpaceAsync(ServiceCall call)
    {
        var (session, characterId, shipId) = Pilot(call);
        var flag = (int)IdOf(call.Arg(1));
        if (!IsFighterSlot(flag))
        {
            throw MachoErrors.ThrowUserError(NotInBayOrTubeText);
        }
        var id = IdOf(call.Arg(0));
        if (_sim is null)
        {
            throw MachoErrors.ThrowUserError(NoSimulationText);
        }
        // only a squadron this ship let go, and that the simulation still has (it is not recorded once the host restarts: NotInSpaceText)
        if (!_abandoned.TryGetValue(id, out var squadron) || squadron.CharacterId != characterId || squadron.ShipId != shipId)
        {
            throw MachoErrors.ThrowUserError(NotInSpaceText);
        }
        if (_solarSystemOf(session) is not { } system)
        {
            throw MachoErrors.ThrowUserError(NoSolarSystemText);
        }
        // the database takes the squadron in first (the room and the tube rules), then the simulation is asked; a refusal puts it back in space
        var move = await _ships.ScoopFighterAsync(characterId, shipId, id, system, flag, call.Cancellation);
        if (!move.Accepted)
        {
            throw MachoErrors.ThrowUserError(MessageOf(move));
        }
        var reason = ReasonOf(await OrderAsync(session, SimFighterOrders.Scoop, 0, 0, [id], call.Cancellation), id);
        if (reason is null || reason.Length > 0)
        {
            await _ships.ReleaseScoopedFighterAsync(characterId, shipId, id, system, call.Cancellation);
            throw MachoErrors.ThrowUserError(reason ?? NoSimulationText);
        }
        _abandoned.TryRemove(id, out _);
        var taken = move.Change!.After;
        ShipNotifications.SendItemChange(session, move.Change!, RowOf);
        if (FighterTubes.IsTube(flag))
        {
            session.SendNotification("OnFighterTubeContentUpdate", "clientID",
                new PyInt(flag), InventoryWire.Id(taken.Id), new PyInt(taken.TypeId),
                new PyInt((int)FighterTubes.InventorySize(taken.Singleton, taken.Quantity)));
            NotifyTubeState(session, flag, "READY");
        }
        return PyBool.True;
    }

    /// <summary>
    /// Takes a squadron out of its tube and sends it out into space: the database moves it first, then the simulation launches it; a launch the simulation
    /// refuses is put back in its tube. Null when the squadron went out, else the reason it did not.
    /// </summary>
    private async Task<string?> LaunchFromTubeAsync(ClientSession session, long characterId, long shipId, int tubeFlag, CancellationToken cancellationToken)
    {
        if (_sim is null)
        {
            return NoSimulationText;
        }
        if (!FighterTubes.IsTube(tubeFlag))
        {
            return "That is not a fighter launch tube.";
        }
        if (!(await _ships.FightersOfShipAsync(shipId, cancellationToken)).Any(i => i.Flag == tubeFlag))
        {
            return EmptyTubeText;
        }
        if (_solarSystemOf(session) is not { } system)
        {
            return NoSolarSystemText;
        }
        var move = await _ships.LaunchFighterFromTubeAsync(characterId, shipId, tubeFlag, system, cancellationToken);
        if (!move.Accepted)
        {
            return MessageOf(move);
        }
        var squadron = move.Change!.After;
        var snapshot = await _ships.LoadAsync(characterId, cancellationToken);
        await _statics.EnsureTypesAsync([squadron.TypeId], cancellationToken);
        if (snapshot is null || _statics.Type(squadron.TypeId) is not { } type)
        {
            await _ships.TakeFighterIntoTubeAsync(characterId, shipId, squadron.Id, tubeFlag, system, cancellationToken);
            return NoShipText;
        }

        var character = snapshot.Character;
        // the squadron's health is that of all its members together: each member is as tough as one ship of the type (the simulation loses a member as
        // its points run out, and tells the size)
        var members = (int)Math.Clamp(FighterTubes.InventorySize(squadron.Singleton, squadron.Quantity), 1, 64);
        var hull = SimShips.OfType(squadron.Id, type) with
        {
            OwnerId = character.Id,
            CorporationId = (int)character.CorporationId,
            AllianceId = (int)(character.AllianceId ?? 0),
            WarFactionId = (int)(character.FactionId ?? 0),
        };
        if (hull.Vitals is { } vitals)
        {
            hull = hull with
            {
                Vitals = vitals with
                {
                    ShieldCapacity = vitals.ShieldCapacity * members,
                    ArmorHp = vitals.ArmorHp * members,
                    StructureHp = vitals.StructureHp * members,
                },
            };
        }
        SimSpawnReply? reply = null;
        try
        {
            reply = await _sim.LaunchFighterAsync(new SimLaunchFighter((ulong)Interlocked.Increment(ref _requests), (ulong)session.SessionId, hull, type.GraphicId, 1.0, members), cancellationToken).ConfigureAwait(false);
        }
        catch (SimUnavailableException error)
        {
            _log?.Invoke($"fighters: {error.Message}");
        }
        if (reply?.Session is null)
        {
            _log?.Invoke($"fighters: the simulation did not launch fighter {squadron.Id}: {reply?.Error ?? "no answer"}");
            await _ships.TakeFighterIntoTubeAsync(characterId, shipId, squadron.Id, tubeFlag, system, cancellationToken);
            return reply?.Error ?? NoSimulationText;
        }

        var launched = new Launched(
            squadron.Id, squadron.TypeId, characterId,
            character.CorporationId, character.AllianceId ?? 0, character.FactionId ?? 0,
            shipId, system, tubeFlag, session, reply.Session);
        _launched[squadron.Id] = launched;
        _sizes[squadron.Id] = members;
        Resume(squadron.Id, squadron.CustomInfo);
        Keep(launched);
        ShipNotifications.SendItemChange(session, move.Change!, RowOf);
        // the order of the reference: the tube is empty, the squadron is added to its controller, and the tube is in space
        session.SendNotification("OnFighterTubeContentEmpty", "clientID", new PyInt(tubeFlag));
        session.SendNotification("OnFighterAddedToController", "clientID",
            InventoryWire.Id(squadron.Id), new PyInt(squadron.TypeId), new PyInt(tubeFlag),
            new PyInt((int)FighterTubes.InventorySize(squadron.Singleton, squadron.Quantity)), new PyDict([]));
        NotifyTubeState(session, tubeFlag, "INSPACE");
        return null;
    }

    private void OnReceived(SimMessage message)
    {
        try
        {
            switch (message)
            {
                case SimFighterLanded landed when _launched.TryRemove(landed.FighterId, out var squadron):
                    Background(() => LandAsync(squadron), $"fighter {squadron.FighterId} landing");
                    break;
                case SimFighterReleased released when _launched.TryRemove(released.FighterId, out var squadron):
                    _abandoned[released.FighterId] = squadron;
                    ReleasedTo(squadron);
                    break;
                case SimFighterSize size when _launched.TryGetValue(size.FighterId, out var squadron):
                    _sizes[size.FighterId] = size.Size;
                    Background(() => ResizeAsync(squadron, size.Size), $"fighter {size.FighterId} size");
                    break;
                // a squadron let go loses members too: its item keeps the size it has now (nobody is told, as nobody controls it)
                case SimFighterSize size when _abandoned.TryGetValue(size.FighterId, out var abandoned):
                    _sizes[size.FighterId] = size.Size;
                    Background(() => _ships.SetSquadronSizeAsync(abandoned.CharacterId, size.FighterId, size.Size), $"fighter {size.FighterId} size");
                    break;
                case SimModuleEnded ended when _launched.ContainsKey(ended.ModuleId / 8) && ended.ModuleId % 8 is >= 0 and <= 7:
                    OnAbilityEnded(ended.ModuleId / 8, (int)(ended.ModuleId % 8));
                    break;
                case SimDestroyed destroyed when _launched.TryRemove(destroyed.ShipId, out var lost):
                    Background(() => LoseAsync(lost), $"fighter {destroyed.ShipId} destroyed");
                    break;
                case SimDestroyed destroyed when _abandoned.TryRemove(destroyed.ShipId, out var abandoned):
                    Background(() => LoseAsync(abandoned, controlled: false), $"fighter {destroyed.ShipId} destroyed");
                    break;
            }
        }
        catch (Exception error)
        {
            _log?.Invoke($"fighters: a message of the simulation could not be handled: {error}");
        }
    }

    /// <summary>The simulation lost: the squadrons out in space are no longer its (their items stay in space, as the drones' do).</summary>
    private void OnLost()
    {
        _launched.Clear();
        _abandoned.Clear();
        _sizes.Clear();
        _slots.Clear();
    }

    /// <summary>What a squadron keeps on its item between its launches: the ship and tube it came from, and each ability slot's charges and cooldown.</summary>
    private sealed record StoredState(long Ship, int Tube, Dictionary<string, StoredSlot> Slots);

    /// <summary>One ability slot as it is kept: the charges left (null for none) and the cooldown's end (Unix milliseconds, 0 for none).</summary>
    private sealed record StoredSlot(int? Charges, long CooldownUntilMs);

public (long CharacterId, int TypeId, int SolarSystemId, long CorporationId, long AllianceId, long WarFactionId)? DamageOwnerOf(long fighterId)
    {
        if (fighterId <= 0)
            return null;
        if (_launched.TryGetValue(fighterId, out var launched))
            return (launched.CharacterId, launched.TypeId, launched.SolarSystemId, launched.CorporationId, launched.AllianceId, launched.WarFactionId);
        if (_abandoned.TryGetValue(fighterId, out var abandoned))
            return (abandoned.CharacterId, abandoned.TypeId, abandoned.SolarSystemId, abandoned.CorporationId, abandoned.AllianceId, abandoned.WarFactionId);
        return null;
    }

    private static StoredState? StateOf(string? customInfo)
    {
        if (string.IsNullOrEmpty(customInfo))
        {
            return null;
        }
        try
        {
            return JsonSerializer.Deserialize<StoredState>(customInfo);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The squadron's state as its item keeps it.</summary>
    private string StateText(Launched squadron)
    {
        var slots = new Dictionary<string, StoredSlot>();
        foreach (var (key, state) in _slots.Where(entry => entry.Key.Fighter == squadron.FighterId))
        {
            var (charges, cooldownUntil) = state.Persisted;
            slots[key.Slot.ToString()] = new StoredSlot(charges, cooldownUntil);
        }
        return JsonSerializer.Serialize(new StoredState(squadron.ShipId, squadron.TubeFlag, slots));
    }

    /// <summary>Keeps the squadron's state on its item, so that a restart does not refill its charges or lift its cooldowns.</summary>
    private void Keep(Launched squadron) =>
        Background(() => _ships.SetSquadronStateAsync(squadron.CharacterId, squadron.FighterId, StateText(squadron)), $"fighter {squadron.FighterId} state");

    /// <summary>The ability slots of a squadron as its item kept them (a cooldown that still runs is still running).</summary>
    private void Resume(long fighterId, string? customInfo)
    {
        if (StateOf(customInfo) is not { } state)
        {
            return;
        }
        foreach (var (slot, stored) in state.Slots ?? [])
        {
            if (int.TryParse(slot, out var number))
            {
                _slots[(fighterId, number)] = AbilitySlot.Resumed(stored.Charges, stored.CooldownUntilMs);
            }
        }
    }

    /// <summary>
    /// The pilot's ship came into space again: the squadrons its character has out in the solar system are taken up again, as the simulation holds none of
    /// them after a restart. One launched from this ship is controlled by it again; one let go is let go again. Its charges and cooldowns come back from its item.
    /// </summary>
    public async Task RebuildAsync(ClientSession session, long characterId)
    {
        if (_sim is null || ShipOf(session) is not { } shipId || _solarSystemOf(session) is not { } system)
        {
            return;
        }
        foreach (var squadron in await _ships.FightersInSpaceAsync(characterId, system))
        {
            if (_launched.ContainsKey(squadron.Id) || _abandoned.ContainsKey(squadron.Id))
            {
                continue;
            }
            await RelaunchAsync(session, characterId, shipId, system, squadron);
        }
    }

    private async Task RelaunchAsync(ClientSession session, long characterId, long shipId, int system, Item squadron)
    {
        await _statics.EnsureTypesAsync([squadron.TypeId]);
        if (_statics.Type(squadron.TypeId) is not { } type || await _ships.LoadAsync(characterId) is not { } snapshot)
        {
            return;
        }
        var members = (int)Math.Clamp(FighterTubes.InventorySize(squadron.Singleton, squadron.Quantity), 1, 64);
        var hull = SimShips.OfType(squadron.Id, type) with
        {
            OwnerId = characterId,
            CorporationId = (int)snapshot.Character.CorporationId,
            AllianceId = (int)(snapshot.Character.AllianceId ?? 0),
            WarFactionId = (int)(snapshot.Character.FactionId ?? 0),
        };
        if (hull.Vitals is { } vitals)
        {
            hull = hull with
            {
                Vitals = vitals with
                {
                    ShieldCapacity = vitals.ShieldCapacity * members,
                    ArmorHp = vitals.ArmorHp * members,
                    StructureHp = vitals.StructureHp * members,
                },
            };
        }
        SimSpawnReply? reply = null;
        try
        {
            reply = await _sim!.LaunchFighterAsync(new SimLaunchFighter((ulong)Interlocked.Increment(ref _requests), (ulong)session.SessionId, hull, type.GraphicId, 1.0, members));
        }
        catch (SimUnavailableException error)
        {
            _log?.Invoke($"fighters: {error.Message}");
        }
        if (reply?.Session is null)
        {
            _log?.Invoke($"fighters: fighter {squadron.Id} was not taken up again: {reply?.Error ?? "no answer"}");
            return;
        }
        var state = StateOf(squadron.CustomInfo);
        var controlled = state is not null && state.Ship == shipId;
        var tube = state?.Tube ?? 0;
        var launched = new Launched(
            squadron.Id, squadron.TypeId, characterId,
            snapshot.Character.CorporationId, snapshot.Character.AllianceId ?? 0, snapshot.Character.FactionId ?? 0,
            state?.Ship ?? shipId, system, tube, session, reply.Session);
        _launched[squadron.Id] = launched;
        _sizes[squadron.Id] = members;
        Resume(squadron.Id, squadron.CustomInfo);
        if (controlled)
        {
            session.SendNotification("OnFighterAddedToController", "clientID", InventoryWire.Id(squadron.Id), new PyInt(squadron.TypeId), new PyInt(tube),
                new PyInt(members), new PyDict([]));
        }
        else
        {
            // let go as it was: the simulation tells the release, and the squadron is held as abandoned
            await OrderAsync(session, SimFighterOrders.Abandon, 0, 0, [squadron.Id], CancellationToken.None);
        }
    }

    /// <summary>A squadron that is no longer the host's (destroyed) keeps no size and no ability state.</summary>
    private void Forget(long fighterId)
    {
        _sizes.TryRemove(fighterId, out _);
        foreach (var key in _slots.Keys.Where(key => key.Fighter == fighterId).ToList())
        {
            _slots.TryRemove(key, out _);
        }
    }

    /// <summary>A squadron out in space lost members: the item keeps the members it has, and the pilot is told the size (the reference's notice).</summary>
    private async Task ResizeAsync(Launched squadron, int size)
    {
        await _ships.SetSquadronSizeAsync(squadron.CharacterId, squadron.FighterId, size).ConfigureAwait(false);
        squadron.Pilot.SendNotification("OnInSpaceSquadronSizeChanged", "clientID", InventoryWire.Id(squadron.FighterId), new PyInt(size));
    }

    /// <summary>
    /// A squadron was destroyed: its item is deleted and the pilot is told it is gone from its controller and its tube is empty (the drone path deletes
    /// the item the same way, and the client is told it went to the junkyard).
    /// </summary>
    private async Task LoseAsync(Launched squadron, bool controlled = true)
    {
        _abandoned.TryRemove(squadron.FighterId, out _);
        Forget(squadron.FighterId);
        if (await _ships.RemoveFighterAsync(squadron.CharacterId, squadron.FighterId).ConfigureAwait(false) is not { } removed)
        {
            return;
        }
        await _statics.EnsureTypesAsync([removed.TypeId]).ConfigureAwait(false);
        ShipNotifications.SendItemChange(squadron.Pilot, new ItemChanged(removed with { LocationId = Junkyard }, removed.LocationId, removed.Flag, removed.Quantity), RowOf);
        // a squadron that was let go was already removed from its controller and its tube is already empty
        if (controlled)
        {
            ReleasedTo(squadron);
        }
    }

    /// <summary>A squadron came back and lands in its tube: it is moved in, and its tube is told READY as the reference does.</summary>
    private async Task LandAsync(Launched squadron)
    {
        var move = await _ships.TakeFighterIntoTubeAsync(squadron.CharacterId, squadron.ShipId, squadron.FighterId, squadron.TubeFlag, squadron.SolarSystemId, CancellationToken.None);
        if (!move.Accepted)
        {
            // the tube is not free, so the squadron stays out, and the pilot may recall it again
            _log?.Invoke($"fighters: fighter {squadron.FighterId} did not land: {MessageOf(move)}");
            _launched[squadron.FighterId] = squadron;
            return;
        }
        var pilot = squadron.Pilot;
        NotifyTubeState(pilot, squadron.TubeFlag, "LANDING");
        ShipNotifications.SendItemChange(pilot, move.Change!, RowOf);
        pilot.SendNotification("OnFighterRemovedFromController", "clientID", InventoryWire.Id(squadron.FighterId));
        var landed = move.Change!.After;
        pilot.SendNotification("OnFighterTubeContentUpdate", "clientID",
            new PyInt(squadron.TubeFlag), InventoryWire.Id(landed.Id), new PyInt(landed.TypeId),
            new PyInt((int)FighterTubes.InventorySize(landed.Singleton, landed.Quantity)));
        NotifyTubeState(pilot, squadron.TubeFlag, "READY");
    }

    /// <summary>A squadron that was let go is no longer the pilot's: its tube is empty and the pilot is told it is removed from its controller.</summary>
    private static void ReleasedTo(Launched squadron)
    {
        squadron.Pilot.SendNotification("OnFighterRemovedFromController", "clientID", InventoryWire.Id(squadron.FighterId));
        NotifyTubeState(squadron.Pilot, squadron.TubeFlag, "EMPTY");
    }

    private void Background(Func<Task> work, string what) => _ = Task.Run(async () =>
    {
        try
        {
            await work().ConfigureAwait(false);
        }
        catch (Exception error)
        {
            _log?.Invoke($"fighters: {what} failed: {error.Message}");
        }
    });

    /// <summary>
    /// Gives the squadrons of the pilot an order and answers what the simulation said of it: null when there is no simulation to ask (or it cannot be reached),
    /// else the answer.
    /// </summary>
    private async Task<CommandReply?> OrderAsync(ClientSession session, byte command, long target, double range, IReadOnlyList<long> fighters, CancellationToken cancellationToken)
    {
        if (_sim is null)
        {
            return null;
        }
        try
        {
            return await _sim.FighterOrderAsync((ulong)session.SessionId, command, target, fighters, range, cancellationToken).ConfigureAwait(false);
        }
        catch (SimUnavailableException error)
        {
            _log?.Invoke($"fighters: {error.Message}");
            return null;
        }
    }

    /// <summary>What the simulation said of a squadron: null when it did not answer, "" when it took the order, else the player's reason.</summary>
    private static string? ReasonOf(CommandReply? reply, long fighterId) =>
        reply?.Fighters.FirstOrDefault(result => result.FighterId == fighterId) is { } answer
            ? answer.Reason switch
            {
                "" => "",
                "NOT_CONTROLLED" => NotInSpaceText,
                "NO_TARGET" => "That target is not in space.",
                "NOT_ABANDONED" => "That fighter is not let go in space.",
                "NOT_YOURS" => "That fighter was launched from another ship.",
                "TOO_FAR" => "That fighter is too far away to be scooped back into your ship.",
                var other => other,
            }
            : null;

    /// <summary>The squadron as the database has it when it is the pilot's and out in the pilot's solar system (else null).</summary>
    private async Task<Item?> SquadronInSpaceAsync(ClientSession session, long characterId, long fighterId, CancellationToken cancellationToken)
    {
        if (_solarSystemOf(session) is not { } system || await _ships.ItemAsync(fighterId, cancellationToken) is not { } item)
        {
            return null;
        }
        await _statics.EnsureTypesAsync([item.TypeId], cancellationToken);
        return item.OwnerId == characterId && item.LocationId == system && item.Flag == CharacterShips.InSpaceFlag
            && _statics.Type(item.TypeId)?.CategoryId == CharacterShips.FighterCategory
            ? item
            : null;
    }

    private (ClientSession Session, long CharacterId, long ShipId) Pilot(ServiceCall call)
    {
        if (call.Session.CharacterId is not { } characterId || ShipOf(call.Session) is not { } shipId)
        {
            throw MachoErrors.ThrowUserError(NoShipText);
        }
        return (call.Session, characterId, shipId);
    }

    private static long? ShipOf(ClientSession session) => session.GetInt("shipid");

    /// <summary>A fighter bay or launch tube: the bay (158) and the five tubes (159 to 163) hold squadrons.</summary>
    private static bool IsFighterSlot(int flag) => flag == CharacterShips.FighterBayFlag || FighterTubes.IsTube(flag);

    /// <summary>The tube's state notice: the state name, then the times (none for a state without a timer) and no error.</summary>
    private static void NotifyTubeState(ClientSession session, int tubeFlag, string state) =>
        session.SendNotification("OnFighterTubeTaskStatus", "clientID", new PyInt(tubeFlag), new PyStr(state), PyNone.Instance, PyNone.Instance, PyNone.Instance);

    private static string MessageOf(FighterMove move) => move.Refusal switch
    {
        FighterMoveRefusal.NoShip => NoShipText,
        FighterMoveRefusal.NotInBay => "That squadron is not in your ship's fighter bay.",
        FighterMoveRefusal.NotInSpace => NotInSpaceText,
        FighterMoveRefusal.NotASquadron => "That item is not a fighter squadron.",
        FighterMoveRefusal.NoSuchTube => "That is not a fighter launch tube.",
        FighterMoveRefusal.TubeOccupied => "That launch tube already holds a squadron.",
        FighterMoveRefusal.TubeEmpty => EmptyTubeText,
        FighterMoveRefusal.NoSlotForClass => "Your ship has no launch slots for that squadron class.",
        FighterMoveRefusal.ClassSlotsFull => "Your ship's launch slots for that squadron class are full.",
        FighterMoveRefusal.TubesFull => "All of your ship's launch tubes are full.",
        FighterMoveRefusal.BayFull => $"There is not enough room in the fighter bay: {move.Available} m3 is free and {move.Volume} m3 is needed.",
        _ => throw new ArgumentOutOfRangeException(nameof(move)),
    };

    private static PyValue FighterRow(int flag, Item squadron) => new PyTuple([
        new PyInt(flag),
        InventoryWire.Id(squadron.Id),
        new PyInt(squadron.TypeId),
        new PyInt((int)FighterTubes.InventorySize(squadron.Singleton, squadron.Quantity)),
    ]);

    private ItemRow RowOf(Item item)
    {
        var type = _statics.Type(item.TypeId);
        return new ItemRow(item.Id, item.TypeId, item.OwnerId, item.LocationId, item.Flag, item.Singleton ? -1 : item.Quantity, type?.GroupId ?? 0, type?.CategoryId ?? 0);
    }

    /// <summary>The ids a call names, as the drone orders read them (lists, tuples, sets), each once.</summary>
    private static IReadOnlyList<long> IdsOf(PyValue value) => SpaceDrones.IdsOf(value);

    private static long IdOf(PyValue value) => value switch
    {
        PyInt integer => integer.Value,
        PyLong integer => integer.Value,
        _ => 0,
    };

    private static double NumberOf(PyValue value, double fallback) => value switch
    {
        PyReal real => real.Value,
        PyInt integer => integer.Value,
        PyLong integer => integer.Value,
        _ => fallback,
    };

    /// <summary>The point a GOTO_POINT names: three numbers in a tuple or a list (metres), or null when it is not three numbers.</summary>
    private static double[]? PointOf(PyValue value)
    {
        var parts = (value switch
        {
            PyTuple tuple => tuple.Items.ToArray(),
            PyList list => list.Items.ToArray(),
            _ => null,
        });
        if (parts is not { Length: 3 } || !parts.All(part => part is PyReal or PyInt or PyLong))
        {
            return null;
        }
        return parts.Select(part => NumberOf(part, 0)).ToArray();
    }
}
