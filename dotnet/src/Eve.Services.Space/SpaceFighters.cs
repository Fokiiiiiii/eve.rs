using System.Collections.Concurrent;
using Eve.Dogma;
using Eve.Inventory;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;
using Eve.Services.Ships;
using Eve.Ships;
using Eve.Space;

namespace Eve.Services.Space;

/// <summary>
/// The player fighters of the pilots, as the host serves <c>fighterMgr</c> (EVE.js <c>fighterMgrService</c>). The fighter bay and the launch tubes are items of
/// the ship in the database: a squadron goes into a tube, out of a tube into space, and back into a tube here. A squadron out in space is the simulation's
/// (<see cref="ISimulation.LaunchFighterAsync"/>, <see cref="ISimulation.FighterOrderAsync"/>): it flies, and it lands or is let go as the simulation says
/// (<c>SimFighterLanded</c>, <c>SimFighterReleased</c>). Without a simulation no squadron goes out, and each such call is refused with its reason.
/// Abilities, the scoop of an abandoned squadron and the point movement are not in the simulation yet, so they are refused with the reason (ADR 0015).
/// </summary>
public sealed class SpaceFighters : ISpaceFighters, IDisposable
{
    private const string NoShipText = "Your ship could not be found.";
    private const string NotInSpaceText = "That fighter is not in space.";
    private const string NotInBayOrTubeText = "A fighter can only be scooped into the fighter bay or a launch tube.";
    private const string EmptyTubeText = "That launch tube is empty.";
    private const string StillOutText = "That launch tube's squadron is still in space.";
    private const string NoSimulationText = "The space simulation is not running, so fighter squadrons cannot go out.";
    private const string NoSolarSystemText = "Your ship is not in space.";
    private const string NoPointText = "A squadron cannot be sent to a point yet: the simulation has no point course (ADR 0015, FIG-D-8).";
    private const string AbilityText = "Fighter abilities are not in the simulation yet (ADR 0015).";
    private const string ScoopText = "Scooping an abandoned squadron back is not in the simulation yet (ADR 0015, WP-R).";

    /// <summary>The orbit an order gives when it names no distance, metres (the reference's default).</summary>
    private const double DefaultOrbitMeters = 1_500;

    /// <summary>A squadron that is out in space: the item it is, the character and ship that launched it, the tube it came from and the pilot to tell.</summary>
    private sealed record Launched(long FighterId, int TypeId, long CharacterId, long ShipId, int SolarSystemId, int TubeFlag, ClientSession Pilot);

    private readonly ISimulation? _sim;
    private readonly CharacterShips _ships;
    private readonly DogmaStatic _statics;
    private readonly Func<ClientSession, int?> _solarSystemOf;
    private readonly Action<string>? _log;
    private readonly ConcurrentDictionary<long, Launched> _launched = new();
    private long _requests;

    public SpaceFighters(ISimulation? sim, CharacterShips ships, DogmaStatic statics, Func<ClientSession, int?> solarSystemOf, Action<string>? log = null)
    {
        _sim = sim;
        _ships = ships;
        _statics = statics;
        _solarSystemOf = solarSystemOf;
        _log = log;
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

    public ValueTask<PyValue> CmdActivateAbilitySlotsAsync(ServiceCall call) => RefuseAbilitiesAsync(call);

    public ValueTask<PyValue> CmdDeactivateAbilitySlotsAsync(ServiceCall call) => RefuseAbilitiesAsync(call);

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
        var command = call.Arg(1) is PyStr text ? text.Value : "";
        var (code, target, range) = command switch
        {
            "STOP" => (SimFighterOrders.Stop, 0L, 0.0),
            "ORBIT" => (SimFighterOrders.Orbit, IdOf(call.Arg(2)), NumberOf(call.Arg(3), DefaultOrbitMeters)),
            "FOLLOW" => (SimFighterOrders.Follow, IdOf(call.Arg(2)), NumberOf(call.Arg(3), DefaultOrbitMeters)),
            "GOTO_POINT" => throw MachoErrors.ThrowUserError(NoPointText),
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
        var (session, characterId, _) = Pilot(call);
        if (!IsFighterSlot((int)IdOf(call.Arg(1))))
        {
            throw MachoErrors.ThrowUserError(NotInBayOrTubeText);
        }
        var inSpace = await SquadronInSpaceAsync(session, characterId, IdOf(call.Arg(0)), call.Cancellation);
        throw MachoErrors.ThrowUserError(inSpace is null ? NotInSpaceText : ScoopText);
    }

    /// <summary>A squadron out in space gets no ability yet; one that is not out is refused with that reason.</summary>
    private async ValueTask<PyValue> RefuseAbilitiesAsync(ServiceCall call)
    {
        var (session, characterId, _) = Pilot(call);
        var entries = new List<KeyValuePair<PyValue, PyValue>>();
        foreach (var id in IdsOf(call.Arg(0)))
        {
            var text = await SquadronInSpaceAsync(session, characterId, id, call.Cancellation) is null ? NotInSpaceText : AbilityText;
            entries.Add(new(InventoryWire.Id(id), MachoErrors.UserErrorPayload(text)));
        }
        return new PyDict(entries);
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

        // the squadron is the pilot's ship: its owner and corporation are the character's, as a drone's are
        var character = snapshot.Character;
        var hull = SimShips.OfType(squadron.Id, type) with
        {
            OwnerId = character.Id,
            CorporationId = (int)character.CorporationId,
            AllianceId = (int)(character.AllianceId ?? 0),
            WarFactionId = (int)(character.FactionId ?? 0),
        };
        SimSpawnReply? reply = null;
        try
        {
            reply = await _sim.LaunchFighterAsync(new SimLaunchFighter((ulong)Interlocked.Increment(ref _requests), (ulong)session.SessionId, hull, type.GraphicId, 1.0), cancellationToken).ConfigureAwait(false);
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

        _launched[squadron.Id] = new Launched(squadron.Id, squadron.TypeId, characterId, shipId, system, tubeFlag, session);
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
        switch (message)
        {
            case SimFighterLanded landed when _launched.TryRemove(landed.FighterId, out var squadron):
                Background(() => LandAsync(squadron), $"fighter {squadron.FighterId} landing");
                break;
            case SimFighterReleased released when _launched.TryRemove(released.FighterId, out var squadron):
                ReleasedTo(squadron);
                break;
        }
    }

    /// <summary>The simulation lost: the squadrons out in space are no longer its (their items stay in space, as the drones' do).</summary>
    private void OnLost()
    {
        _launched.Clear();
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
}
