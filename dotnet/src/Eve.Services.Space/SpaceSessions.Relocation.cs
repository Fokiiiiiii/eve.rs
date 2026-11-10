using Eve.Fitting;
using Eve.Inventory;
using Eve.Macho;
using Eve.Marshal;
using Eve.Ships;
using Eve.Space;

namespace Eve.Services.Space;

public sealed partial class SpaceSessions
{
    /// <summary>How far off a pilot's ship a game master's relocation puts the game master, metres: the place is along x, facing the ship (a rule of this host).</summary>
    public const double RelocationOffsetMeters = 2_500d;

    /// <summary>
    /// A game master's relocation of the session's pilot into a solar system (EVE.js <c>jumpSessionToSolarSystem</c>, behind <c>/solar</c> and <c>/tele</c>). The
    /// active ship goes into space there, from its station hangar or from the scene it was in: the scene it left lets it go, the simulation takes it in the
    /// new one, and the pilot is told the session change and the item change as a jump or an undock tells them. With a <paramref name="position"/> the ship is
    /// put there facing <paramref name="direction"/>; without one it is put at the system's anchor. No gate, range, fuel or timer rule applies, and the session
    /// change timer starts as after a jump. Returns null when the pilot is there, else the words to tell the game master.
    /// </summary>
    private sealed record PublicMoveReceipt(string OperationId, long ShipId, SimSceneKey Source, SimSceneKey Destination,
        double[]? Position, double[]? Direction);
    private sealed class PendingDockedEntry(string operationId, ClientSession session, long characterId, SimEnter enter, int previousSystemId, long previousStationId, long previousStructureId)
    {
        public string OperationId { get; } = operationId;
        public ClientSession Session { get; } = session;
        public long CharacterId { get; } = characterId;
        public SimEnter Enter { get; } = enter;
        public int PreviousSystemId { get; } = previousSystemId;
        public long PreviousStationId { get; } = previousStationId;
        public long PreviousStructureId { get; } = previousStructureId;
        public SpaceMove? Moved { get; set; }
        public bool Attached { get; set; }
    }
    private readonly System.Collections.Concurrent.ConcurrentDictionary<ulong, PendingDockedEntry> _pendingDockedEntries = new();
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<ClientSession, SemaphoreSlim> _relocationGates = new();

    public async Task<string?> RelocateAsync(
        ClientSession session,
        int solarSystemId,
        IReadOnlyList<double>? position,
        IReadOnlyList<double>? direction,
        CancellationToken cancellationToken,
        string? operationId = null)
    {
        var id = (ulong)session.SessionId;
        var gate = _relocationGates.GetValue(session, _ => new SemaphoreSlim(1, 1));
        if (!await gate.WaitAsync(0, cancellationToken)) return "A space relocation is still being resolved.";
        try
        {
            if (_pendingDockedEntries.TryGetValue(id, out var pending))
            {
                if (!ReferenceEquals(pending.Session, session) || operationId is null || !Guid.TryParse(operationId, out var parsed) || parsed.ToString("D") != pending.OperationId ||
                    pending.Enter.Scene.LocationId != solarSystemId ||
                    !(pending.Enter.Position is null ? position is null : position is not null && pending.Enter.Position.SequenceEqual(position)) ||
                    !(pending.Enter.Direction is null ? direction is null : direction is not null && pending.Enter.Direction.SequenceEqual(direction)))
                    return "A different space relocation is still being resolved.";
                return await FinishDockedPublicEntryAsync(pending, cancellationToken);
            }
            if (!_pilots.TryGetValue(id, out var pilot))
                return await RelocateCoreAsync(session, solarSystemId, position, direction, cancellationToken, operationId);
            if (Interlocked.CompareExchange(ref pilot.PublicMoveInFlight, 1, 0) != 0)
                return "A space relocation is still being resolved.";
            try { return await RelocateCoreAsync(session, solarSystemId, position, direction, cancellationToken, operationId); }
            finally { Volatile.Write(ref pilot.PublicMoveInFlight, 0); }
        }
        finally { gate.Release(); }
    }

    /// <summary>Moves a docked capsule into a ship clone's system, settling the clone jump before the space entry is announced.</summary>
    public async Task<string?> RelocateForCloneJumpAsync(ClientSession session, int solarSystemId,
        IReadOnlyList<double> position, IReadOnlyList<double> direction,
        Func<CancellationToken, ValueTask<string?>> settle, CancellationToken cancellationToken)
    {
        var id = (ulong)session.SessionId;
        var gate = _relocationGates.GetValue(session, _ => new SemaphoreSlim(1, 1));
        if (!await gate.WaitAsync(0, cancellationToken)) return "A space relocation is still being resolved.";
        try
        {
            if (IsInSpace(session) || _pendingDockedEntries.ContainsKey(id))
                return "Dock before jumping to a ship clone.";
            return await RelocateCoreAsync(session, solarSystemId, position, direction, cancellationToken, null,
                settlement: settle);
        }
        finally { gate.Release(); }
    }

    public async Task<string?> RelocatePersistedAsync(ClientSession session, SpaceMove moved, string operationId, CancellationToken cancellationToken)
    {
        var gate = _relocationGates.GetValue(session, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            var sessionId = (ulong)session.SessionId;
            if (session.CharacterId is not { } characterId || !_pilots.TryGetValue(sessionId, out var pilot) ||
                !ReferenceEquals(pilot.Session, session) || pilot.CharacterId != characterId || pilot.ShipId != moved.Ship.Id ||
                moved.Ship.LocationId is <= 0 or > int.MaxValue || moved.PreviousLocationId is <= 0 or > int.MaxValue)
                return "The persisted space move does not match the active pilot.";
            if (IsInPublicSystem(session, moved.Ship.LocationId))
                return null;
            var resumingMove = pilot.LastPublicMove?.OperationId == operationId;
            if ((!resumingMove && pilot.SolarSystemId != moved.PreviousLocationId) || pilot.SimulationScene.InstanceId != 0)
                return "The active pilot is no longer in the move's source system.";
            if (Interlocked.CompareExchange(ref pilot.PublicMoveInFlight, 1, 0) != 0)
                return "A space relocation is still being resolved.";
            try { return await RelocateCoreAsync(session, (int)moved.Ship.LocationId, null, null, cancellationToken, operationId, moved); }
            finally { Volatile.Write(ref pilot.PublicMoveInFlight, 0); }
        }
        finally { gate.Release(); }
    }

    public void SchedulePersistedRelocationRetry(ClientSession session, SpaceMove moved, string operationId, string firstFailure)
    {
        Background(async () =>
        {
            var error = await RelocatePersistedAsync(session, moved, operationId, CancellationToken.None);
            if (error is not null)
                throw new InvalidOperationException($"Persisted relocation retry failed after '{firstFailure}': {error}");
        });
    }

    private async Task<string?> RelocateCoreAsync(
        ClientSession session, int solarSystemId, IReadOnlyList<double>? position, IReadOnlyList<double>? direction,
        CancellationToken cancellationToken, string? operationId, SpaceMove? persistedMove = null,
        Func<CancellationToken, ValueTask<string?>>? settlement = null)
    {
        if ((position is null && direction is not null) ||
            (position is not null && (position.Count != 3 || position.Any(x => !double.IsFinite(x)) ||
                direction is null || direction.Count != 3 || direction.Any(x => !double.IsFinite(x)) || direction.All(x => x == 0))))
            return "The requested relocation position or direction is invalid.";
        if (session.CharacterId is not { } characterId)
        {
            return "Select a character before relocating.";
        }
        if (!_sim.IsConnected)
        {
            return "Space is not available right now. Try again in a moment.";
        }
        if (_systems.Get(solarSystemId) is not { } place)
        {
            return $"Solar system not found: {solarSystemId}.";
        }
        if (await _ships.LoadAsync(characterId, cancellationToken) is not { } snapshot)
        {
            return "Active ship not found for this character.";
        }

        var sessionId = (ulong)session.SessionId;
        var previousStationId = session.GetInt("stationid") ?? 0;
        var previousStructureId = session.GetInt("structureid") ?? 0;
        var wasInSpace = _pilots.TryGetValue(sessionId, out var pilot);
        var previousSystemId = wasInSpace ? pilot!.SolarSystemId : snapshot.Character.SolarSystemId;
        if (settlement is not null && wasInSpace)
            return "Dock before jumping to a ship clone.";
        if (operationId is not null && !Guid.TryParse(operationId, out _))
            return "The relocation operation is invalid.";
        if (wasInSpace && (pilot!.ShipId != snapshot.Ship.Id || pilot.SimulationScene.InstanceId != 0))
            return "The active ship is not in a public space scene.";
        var normalizedOperation = operationId is null ? Guid.NewGuid().ToString("D") : Guid.Parse(operationId).ToString("D");
        try
        {
            await PrepareSolarSystemAsync(solarSystemId, cancellationToken);
        }
        catch (SimUnavailableException)
        {
            return "Space is not available right now. Try again in a moment.";
        }
        if (!wasInSpace && operationId is not null)
        {
            await SyncBuffsAsync(snapshot.World, snapshot.Ship.Id, cancellationToken);
            var shipSpec = SimShips.Of(snapshot, _statics, await SecurityStatusAsync(characterId, cancellationToken));
            var enter = new SimEnter(sessionId, SimSceneKey.SolarSystem(solarSystemId), position is null ? EnterHow.Anchor : EnterHow.InSpace,
                0, shipSpec, position?.ToArray(), direction?.ToArray());
            var pending = new PendingDockedEntry(normalizedOperation, session, characterId, enter, previousSystemId, previousStationId, previousStructureId);
            if (!_pendingDockedEntries.TryAdd(sessionId, pending)) return "A space relocation is still being resolved.";
            return await FinishDockedPublicEntryAsync(pending, cancellationToken);
        }
        if (wasInSpace && pilot!.LastPublicEntry is { } lastEntry && lastEntry.OperationId == normalizedOperation)
        {
            if (pilot.ShipId != lastEntry.Enter.Ship.ShipId || pilot.SimulationScene != lastEntry.Enter.Scene || lastEntry.Enter.Scene.LocationId != solarSystemId ||
                !(lastEntry.Enter.Position is null ? position is null : position is not null && lastEntry.Enter.Position.SequenceEqual(position)) ||
                !(lastEntry.Enter.Direction is null ? direction is null : direction is not null && lastEntry.Enter.Direction.SequenceEqual(direction)))
                return "The relocation operation does not match its previous destination.";
            try
            {
                var replay = await _sim.EnterPublicParticipantInOrderAsync(new SimEnterPublicParticipant(SimClient.NextRequestId(), normalizedOperation, lastEntry.Enter), _ => { }, cancellationToken);
                return replay.Error is null ? null : "Space is not available right now. Try again in a moment.";
            }
            catch (SimUnavailableException) { return "Space is not available right now. Try again in a moment."; }
        }
        if (wasInSpace && pilot!.PendingPublicMove is { } unresolved && unresolved.OperationId != normalizedOperation)
            return "A different space relocation is still being resolved.";
        if (wasInSpace && pilot!.LastPublicMove is { } previous && previous.OperationId == normalizedOperation)
        {
            var destination = SimSceneKey.SolarSystem(solarSystemId);
            if (previous.ShipId != pilot.ShipId || previous.Destination != destination || pilot.SimulationScene != destination ||
                !(previous.Position is null ? position is null : position is not null && previous.Position.SequenceEqual(position)) ||
                !(previous.Direction is null ? direction is null : direction is not null && previous.Direction.SequenceEqual(direction)))
                return "The relocation operation does not match its previous destination.";
            var replay = new SimMovePublicParticipant(SimClient.NextRequestId(), normalizedOperation, sessionId, pilot.ShipId,
                previous.Source, previous.Destination, previous.Position is null, previous.Position ?? [], previous.Direction ?? []);
            try
            {
                var replayed = await _sim.MovePublicParticipantInOrderAsync(replay, _ => { }, cancellationToken);
                if (replayed.Error is not null) return "Space is not available right now. Try again in a moment.";
                if (pilot.PendingPublicMove is not null)
                {
                    if (!await SaveRelocatedConditionAsync(session, characterId, pilot.ShipId, solarSystemId, normalizedOperation, cancellationToken))
                        return "The relocated ship condition could not be saved. Try again in a moment.";
                    CompletePublicMove(previous.Source.LocationId, solarSystemId);
                }
                return null;
            }
            catch (SimUnavailableException) { return "Space is not available right now. Try again in a moment."; }
        }

        if (wasInSpace)
        {
            var destination = SimSceneKey.SolarSystem(solarSystemId);
            if (pilot!.PendingPublicMove is { } pending)
            {
                if (pending.OperationId != normalizedOperation || pending.ShipId != pilot.ShipId || pending.Destination != destination ||
                    !(pending.Position is null ? position is null : position is not null && pending.Position.SequenceEqual(position)) ||
                    !(pending.Direction is null ? direction is null : direction is not null && pending.Direction.SequenceEqual(direction)))
                    return "A different space relocation is still being resolved.";
            }
            else
                pilot.PendingPublicMove = new PublicMoveReceipt(normalizedOperation, pilot.ShipId, pilot.SimulationScene, destination,
                    position?.ToArray(), direction?.ToArray());
        }
        SpaceMove moved;
        if (persistedMove is { } recorded)
        {
            if (!wasInSpace || recorded.Ship.Id != snapshot.Ship.Id || recorded.Ship.LocationId != solarSystemId ||
                recorded.PreviousLocationId != previousSystemId)
            {
                if (wasInSpace) pilot!.PendingPublicMove = null;
                return "The persisted space move no longer matches the pilot.";
            }
            moved = recorded;
        }
        else
        {
            var relocation = await _ships.RelocateAsync(characterId, solarSystemId, place.ConstellationId, place.RegionId, cancellationToken);
            if (relocation.Moved is not { } newlyMoved)
            {
                if (wasInSpace) pilot!.PendingPublicMove = null;
                return relocation.Refusal == "NO_SHIP" ? "Active ship not found for this character." : "Solar-system jump failed.";
            }
            moved = newlyMoved;
        }

        await SyncBuffsAsync(snapshot.World, snapshot.Ship.Id, cancellationToken);
        var spec = SimShips.Of(snapshot, _statics, await SecurityStatusAsync(characterId, cancellationToken));
        if (wasInSpace)
        {
            await CancelShipSelfDestructAsync(pilot!.ShipId, cancellationToken);
            EndTetherPilot(pilot);
            await CancelRepairsAsync(pilot, cancellationToken);
            await _charges.CloseAsync(sessionId);
            var destination = SimSceneKey.SolarSystem(solarSystemId);
            var request = new SimMovePublicParticipant(SimClient.NextRequestId(), normalizedOperation,
                sessionId, pilot.ShipId, pilot.SimulationScene, destination, position is null,
                position?.ToArray() ?? [], direction?.ToArray() ?? []);
            SimMovePublicParticipantReply answer;
            List<Func<CancellationToken, ValueTask>> releases = [];
            try
            {
                try
                {
                answer = await _sim.MovePublicParticipantInOrderAsync(request, result =>
                {
                    if (result.Error is not null) return;
                    pilot.LastPublicMove = new PublicMoveReceipt(normalizedOperation, pilot.ShipId, request.Source, destination,
                        position?.ToArray(), direction?.ToArray());
                    pilot.SimulationScene = destination;
                    pilot.SolarSystemId = solarSystemId;
                    _warping.TryRemove(sessionId, out _);
                    var type = _statics.Type(moved.Ship.TypeId);
                    var row = InventoryWire.PackedRow(ShipRow(moved.Ship, type), new PyStr($"Undocking:{previousSystemId}"));
                    session.SendNotification("OnItemChange", "clientID", row,
                        new PyDict([new(new PyInt(3), InventoryWire.Id(previousSystemId))]), None);
                    session.Change(("solarsystemid", new PyInt(solarSystemId)),
                        ("solarsystemid2", new PyInt(solarSystemId)), ("constellationid", new PyInt(place.ConstellationId)),
                        ("regionid", new PyInt(place.RegionId)), ("locationid", new PyInt(solarSystemId)));
                    releases = DetachBoundObjects(session);
                }, cancellationToken);
                }
                finally
                {
                    using var cleanupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    await CompleteBoundObjectReleasesAsync(releases, cleanupTimeout.Token);
                }
            }
            catch (SimUnavailableException error)
            {
                _log?.Invoke($"the relocation of character {characterId} is pending: {error.Message}");
                return "Space is not available right now. Try again in a moment.";
            }
            if (answer.Error is not null)
            {
                _log?.Invoke($"the relocation of character {characterId} was refused: {answer.Error}");
                return "Space is not available right now. Try again in a moment.";
            }
            if (!await SaveRelocatedConditionAsync(session, characterId, pilot.ShipId, solarSystemId, normalizedOperation, cancellationToken))
                return "The relocated ship condition could not be saved. Try again in a moment.";
            CompletePublicMove(previousSystemId, solarSystemId);
            return null;
        }

        var spaceEnter = new SimEnter(sessionId, SimSceneKey.SolarSystem(solarSystemId), position is null ? EnterHow.Anchor : EnterHow.InSpace,
            0, spec, position, direction);
        string? refusal;
        string? settlementRefusal = null;
        Exception? settlementError = null;
        var attachedInHandoff = false;
        List<Func<CancellationToken, ValueTask>> entryReleases = [];

        void AttachEnteredShip()
        {
            TellEnteredBuffs(sessionId, snapshot.Ship.Id, _nowUnixMs());
            _pilots[sessionId] = new Pilot(session, characterId, moved.Ship.Id, moved.Ship.TypeId, solarSystemId, FileTimeNow());
            if (!wasInSpace)
                session.Closed += () => OnSessionClosed(session);
            var type = _statics.Type(moved.Ship.TypeId);
            var row = InventoryWire.PackedRow(ShipRow(moved.Ship, type), new PyStr(wasInSpace ? $"Undocking:{previousSystemId}" : $"Undocking:{previousStationId}"));
            if (wasInSpace)
            {
                session.SendNotification("OnItemChange", "clientID", row,
                    new PyDict([new(new PyInt(3), InventoryWire.Id(moved.PreviousLocationId))]), None);
                session.Change(
                    ("solarsystemid", new PyInt(solarSystemId)),
                    ("solarsystemid2", new PyInt(solarSystemId)),
                    ("constellationid", new PyInt(place.ConstellationId)),
                    ("regionid", new PyInt(place.RegionId)),
                    ("locationid", new PyInt(solarSystemId)));
            }
            else
            {
                session.SendNotification("OnItemsChanged", "charid", new PyList([row]), ChangedFrom(moved), None);
                session.Set("stationid2", None);
                session.Change(
                    ("stationid", None),
                    ("structureid", None),
                    ("solarsystemid", new PyInt(solarSystemId)),
                    ("solarsystemid2", new PyInt(solarSystemId)),
                    ("constellationid", new PyInt(place.ConstellationId)),
                    ("regionid", new PyInt(place.RegionId)),
                    ("locationid", new PyInt(solarSystemId)));
                DockedLocationChanged?.Invoke(session, previousStationId, previousStructureId);
            }
            entryReleases = DetachBoundObjects(session);
        }

        try
        {
            if (settlement is null)
            {
                refusal = await _sim.EnterAsync(spaceEnter, cancellationToken);
            }
            else
            {
                var request = new SimEnterPublicParticipant(SimClient.NextRequestId(), normalizedOperation, spaceEnter);
                var answer = await _sim.EnterPublicParticipantInOrderAwaitingAsync(request, async result =>
                {
                    if (result.Error is not null)
                        return;
                    try
                    {
                        settlementRefusal = await settlement(cancellationToken);
                    }
                    catch (Exception error)
                    {
                        settlementError = error;
                        return;
                    }
                    if (settlementRefusal is null)
                    {
                        AttachEnteredShip();
                        attachedInHandoff = true;
                    }
                }, cancellationToken);
                refusal = answer.Error;
            }
        }
        catch (SimUnavailableException error)
        {
            refusal = error.Message;
        }

        if (refusal is null && (settlementRefusal is not null || settlementError is not null))
        {
            _sim.Leave(sessionId, LeaveWhy.Jumped);
            if (!await _ships.RecoverFromSpaceAsync(characterId, PlaceOfStation, CancellationToken.None))
                throw new InvalidOperationException("A refused ship clone jump could not recover its capsule to the origin station.", settlementError);
            if (settlementError is not null)
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(settlementError).Throw();
            return settlementRefusal;
        }
        if (refusal is not null)
        {
            _log?.Invoke($"the relocation of character {characterId} to system {solarSystemId} was refused by the simulation: {refusal}");
            // the ship is not in any scene: it is recovered to the station its character last was at, as a ship the simulation loses is
            if (!await _ships.RecoverFromSpaceAsync(characterId, PlaceOfStation, CancellationToken.None))
                throw new InvalidOperationException("The capsule could not be recovered to the origin station after a refused relocation.");
            return "Space is not available right now. Try again in a moment.";
        }

        if (!attachedInHandoff)
            AttachEnteredShip();
        // the objects bound to the old place let go, as a jump's arrival lets them go (the client binds the new ballpark)
        await CompleteBoundObjectReleasesAsync(entryReleases, cancellationToken);
        if (wasInSpace && !_pilots.Values.Any(other => other.SolarSystemId == previousSystemId))
        {
            ForgetNpcsOf(previousSystemId);
        }
        SystemEntered?.Invoke(solarSystemId);
        _transitions.StartSessionChange(sessionId);
        await RefreshTetherPolicyAsync(session, cancellationToken);
        return null;
    }

    private async Task<string?> FinishDockedPublicEntryAsync(PendingDockedEntry pending, CancellationToken cancellationToken)
    {
        var session = pending.Session;
        var id = (ulong)session.SessionId;
        var solarSystemId = pending.Enter.Scene.LocationId;
        if (_systems.Get(solarSystemId) is not { } place) return "Solar system not found.";
        try
        {
            await PrepareSolarSystemAsync(solarSystemId, cancellationToken);
        }
        catch (SimUnavailableException)
        {
            return "Space is not available right now. Try again in a moment.";
        }
        if (pending.Moved is null)
        {
            var relocation = await _ships.RelocateAsync(pending.CharacterId, solarSystemId, place.ConstellationId, place.RegionId, cancellationToken);
            if (relocation.Moved is not { } moved)
            {
                _pendingDockedEntries.TryRemove(id, out _);
                return "Solar-system jump failed.";
            }
            if (moved.Ship.Id != pending.Enter.Ship.ShipId) return "The active ship changed during relocation.";
            pending.Moved = moved;
        }
        var request = new SimEnterPublicParticipant(SimClient.NextRequestId(), pending.OperationId, pending.Enter);
        List<Func<CancellationToken, ValueTask>> releases = [];
        SimEnterPublicParticipantReply answer;
        try
        {
            try
            {
                answer = await _sim.EnterPublicParticipantInOrderAsync(request, result =>
                {
                    if (result.Error is not null || pending.Attached) return;
                    var moved = pending.Moved!;
                    var pilot = new Pilot(session, pending.CharacterId, moved.Ship.Id, moved.Ship.TypeId, solarSystemId, FileTimeNow());
                    pilot.LastPublicEntry = pending;
                    pilot.PendingPublicMove = new PublicMoveReceipt(pending.OperationId, moved.Ship.Id, pending.Enter.Scene, pending.Enter.Scene,
                        pending.Enter.Position?.ToArray(), pending.Enter.Direction?.ToArray());
                    _pilots[id] = pilot;
                    pending.Attached = true;
                    session.Closed += () => OnSessionClosed(session);
                    TellEnteredBuffs(id, moved.Ship.Id, _nowUnixMs());
                    var type = _statics.Type(moved.Ship.TypeId);
                    var row = InventoryWire.PackedRow(ShipRow(moved.Ship, type), new PyStr($"Undocking:{pending.PreviousStationId}"));
                    session.SendNotification("OnItemsChanged", "charid", new PyList([row]), ChangedFrom(moved), None);
                    session.Set("stationid2", None);
                    session.Change(("stationid", None), ("structureid", None), ("solarsystemid", new PyInt(solarSystemId)),
                        ("solarsystemid2", new PyInt(solarSystemId)), ("constellationid", new PyInt(place.ConstellationId)),
                        ("regionid", new PyInt(place.RegionId)), ("locationid", new PyInt(solarSystemId)));
                    DockedLocationChanged?.Invoke(session, pending.PreviousStationId, pending.PreviousStructureId);
                    releases = DetachBoundObjects(session);
                }, cancellationToken);
            }
            finally
            {
                using var cleanupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                await CompleteBoundObjectReleasesAsync(releases, cleanupTimeout.Token);
            }
        }
        catch (SimUnavailableException) { return "Space is not available right now. Try again in a moment."; }
        if (answer.Error is not null) return "Space is not available right now. Try again in a moment.";
        if (!_pilots.TryGetValue(id, out var attached) || attached.ShipId != pending.Enter.Ship.ShipId ||
            !await SaveRelocatedConditionAsync(session, pending.CharacterId, attached.ShipId, solarSystemId, pending.OperationId, cancellationToken))
            return "The relocated ship condition could not be saved. Try again in a moment.";
        _pendingDockedEntries.TryRemove(id, out _);
        CompletePublicMove(pending.PreviousSystemId, solarSystemId);
        _transitions.StartSessionChange(id);
        await RefreshTetherPolicyAsync(session, cancellationToken);
        return null;
    }

    private void CompletePublicMove(int previousSystemId, int solarSystemId)
    {
        if (!_pilots.Values.Any(other => other.SolarSystemId == previousSystemId)) ForgetNpcsOf(previousSystemId);
        SystemEntered?.Invoke(solarSystemId);
    }

    private async Task<bool> SaveRelocatedConditionAsync(ClientSession session, long characterId, long shipId, int solarSystemId, string operationId, CancellationToken cancellationToken)
    {
        var id = (ulong)session.SessionId;
        if (!_pilots.TryGetValue(id, out var pilot) || !ReferenceEquals(pilot.Session, session) ||
            pilot.ShipId != shipId || pilot.SolarSystemId != solarSystemId || pilot.PendingPublicMove?.OperationId != operationId)
            return false;
        var revision = await _ships.ShipConditionRevisionAsync(shipId, cancellationToken);
        var reply = await _sim.CommandAsync(id, SimCommands.GetCondition, [], cancellationToken);
        if (!reply.Accepted || reply.Condition is not { } condition ||
            pilot.PendingPublicMove?.OperationId != operationId || pilot.ShipId != shipId || pilot.SolarSystemId != solarSystemId)
            return false;
        if (!await _ships.TrySaveSpaceConditionAsync(characterId, shipId, solarSystemId, revision,
            new ShipCondition(condition.Damage, condition.Charge, condition.ShieldCharge, condition.ArmorDamage, false), cancellationToken))
            return false;
        pilot.PendingPublicMove = null;
        return true;
    }

    /// <summary>
    /// Where a connected pilot is, exactly (<c>/tr</c>): the place of its ship in space, or only its system (a null place) when it is docked; null when it is
    /// in no system.
    /// </summary>
    public async Task<(int SolarSystemId, IReadOnlyList<double>? Position)?> PilotPlaceAsync(ClientSession target, CancellationToken cancellationToken)
    {
        if (IsInSpace(target) && ShipIdOf(target) is { } shipId && await ScenePositionOfAsync(target, shipId, cancellationToken) is { } at)
        {
            return (at.SolarSystemId, new double[] { at.X, at.Y, at.Z });
        }
        if (target.GetInt("solarsystemid2") is { } system and > 0)
        {
            return ((int)system, null);
        }
        return null;
    }

    /// <summary>
    /// Where an item of the pilot's solar system is (<c>/warpto</c>): the simulation's position of a celestial or of a pilot's ship. Null when the pilot is in no
    /// system or the simulation does not know the item there.
    /// </summary>
    public async Task<(int SolarSystemId, IReadOnlyList<double> Position)?> ItemPlaceAsync(ClientSession caller, long itemId, CancellationToken cancellationToken)
    {
        if (SolarSystemOf(caller) is not { } system)
        {
            return null;
        }
        CommandReply reply;
        try
        {
            reply = await _sim.CommandAsync((ulong)caller.SessionId, SimCommands.PositionOf, [itemId], cancellationToken);
        }
        catch (SimUnavailableException error)
        {
            _log?.Invoke($"item position query of session {caller.SessionId}: {error.Message}");
            throw Notify("Space is not available right now. Try again in a moment.");
        }
        return reply.Accepted && reply.Position.Count >= 3 ? (system, new double[] { reply.Position[0], reply.Position[1], reply.Position[2] }) : null;
    }

    /// <summary>
    /// Where a game master goes to be with a connected pilot (<c>/tele</c>): <see cref="RelocationOffsetMeters"/> off the pilot's ship in space, facing it; a pilot
    /// that is docked is found at the anchor of its system. Null when the pilot is in no system.
    /// </summary>
    public async Task<(int SolarSystemId, IReadOnlyList<double>? Position, IReadOnlyList<double>? Direction)?> PilotDestinationAsync(ClientSession target, CancellationToken cancellationToken)
    {
        if (IsInSpace(target) && ShipIdOf(target) is { } shipId && await ScenePositionOfAsync(target, shipId, cancellationToken) is { } at)
        {
            return (at.SolarSystemId, [at.X + RelocationOffsetMeters, at.Y, at.Z], [-1d, 0d, 0d]);
        }
        if (target.GetInt("solarsystemid2") is { } system and > 0)
        {
            // the anchor: no place given, so the simulation puts the ship at the system's anchor
            IReadOnlyList<double>? noPlace = null;
            return ((int)system, noPlace, noPlace);
        }
        return null;
    }
}
