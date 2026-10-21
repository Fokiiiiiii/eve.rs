using System.Collections.Concurrent;
using System.Text.Json;
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
    public const string ShipSelfDestructJobKind = "ship.self-destruct";
    private static readonly TimeSpan ShipSelfDestructDuration = TimeSpan.FromSeconds(120);
    private const string SelfDestructTimeField = "selfDestructTime";
    private static readonly JsonSerializerOptions SelfDestructJson = new(JsonSerializerDefaults.Web);

    private static string ShipSelfDestructJobKey(long shipId) => $"self-destruct:{shipId}";

    private static long FileTimeOfUnixMs(long unixMs) => checked(116444736000000000L + unixMs * 10_000L);

    private static void TellSelfDestruct(ClientSession session, string message) =>
        session.SendNotification("OnRemoteMessage", "clientID", new PyStr(message), new PyDict([]));

    private static MachoWrappedException SelfDestructError(string message) =>
        MachoErrors.ThrowUserError("CustomNotify", ("notify", new PyStr(message)));

    public async ValueTask<PyValue> StartSelfDestructAsync(ClientSession session, long? requestedShipId, CancellationToken cancellationToken)
    {
        if (TimedActions is not { } scheduler || !_pilots.TryGetValue((ulong)session.SessionId, out var pilot) ||
            !IsInSpace(session) || session.CharacterId != pilot.CharacterId || requestedShipId != pilot.ShipId ||
            session.GetInt("shipid") != pilot.ShipId || await _ships.LoadAsync(pilot.CharacterId, cancellationToken) is not { } flown ||
            flown.Ship.Id != pilot.ShipId)
            throw SelfDestructError("The active ship cannot self-destruct here.");

        var key = ShipSelfDestructJobKey(pilot.ShipId);
        var existing = await scheduler.FindAsync(ShipSelfDestructJobKind, key, cancellationToken);
        if (existing is not null)
        {
            if (existing.State != "pending")
                throw SelfDestructError("The ship self-destruct is already completing.");
            var prior = JsonSerializer.Deserialize<ShipSelfDestructCountdown>(existing.PayloadJson, SelfDestructJson);
            if (prior?.ShipId != pilot.ShipId || prior.CharacterId != pilot.CharacterId ||
                prior.SolarSystemId != pilot.SolarSystemId || prior.InstanceId != pilot.SimulationScene.InstanceId)
                throw SelfDestructError("The active ship cannot self-destruct here.");
            _sim.SetSlimField(pilot.ShipId, SelfDestructTimeField, new SimSlimInt(FileTimeOfUnixMs(existing.RunAtMs)));
            return None;
        }

        var expiresAtMs = checked(_nowUnixMs() + (long)ShipSelfDestructDuration.TotalMilliseconds);
        var countdown = new ShipSelfDestructCountdown(
            pilot.ShipId, pilot.CharacterId, pilot.SolarSystemId, pilot.SimulationScene.InstanceId, expiresAtMs);
        await scheduler.ScheduleAsync(new JobSpec(ShipSelfDestructJobKind, expiresAtMs)
        {
            Key = key, Payload = countdown, OnDuplicate = OnDuplicate.Keep,
        }, cancellationToken);
        var admitted = await scheduler.FindAsync(ShipSelfDestructJobKind, key, cancellationToken);
        var admittedPayload = admitted is { State: "pending" }
            ? JsonSerializer.Deserialize<ShipSelfDestructCountdown>(admitted.PayloadJson, SelfDestructJson)
            : null;
        if (admittedPayload != countdown)
            throw SelfDestructError("The ship self-destruct could not be started.");
        try
        {
            _sim.SetSlimField(pilot.ShipId, SelfDestructTimeField, new SimSlimInt(FileTimeOfUnixMs(expiresAtMs)));
        }
        catch
        {
            await scheduler.CancelAsync(ShipSelfDestructJobKind, key, CancellationToken.None);
            throw;
        }
        CancelSafeLogoff(session);
        TellSelfDestruct(session, "SelfDestructInitiatedOther");
        return None;
    }

    public async ValueTask<PyValue> AbortSelfDestructAsync(ClientSession session, long? requestedShipId, CancellationToken cancellationToken)
    {
        if (TimedActions is not { } scheduler || !_pilots.TryGetValue((ulong)session.SessionId, out var pilot) ||
            !IsInSpace(session) || session.CharacterId != pilot.CharacterId || requestedShipId != pilot.ShipId ||
            session.GetInt("shipid") != pilot.ShipId)
            throw SelfDestructError("The active ship cannot abort its self-destruct.");

        var key = ShipSelfDestructJobKey(pilot.ShipId);
        var job = await scheduler.FindAsync(ShipSelfDestructJobKind, key, cancellationToken);
        if (job is null) return None;
        var countdown = JsonSerializer.Deserialize<ShipSelfDestructCountdown>(job.PayloadJson, SelfDestructJson);
        if (job.State != "pending" || countdown?.CharacterId != pilot.CharacterId)
            throw SelfDestructError("The ship self-destruct can no longer be aborted.");
        if (!await scheduler.CancelAsync(ShipSelfDestructJobKind, key, cancellationToken))
            throw SelfDestructError("The ship self-destruct can no longer be aborted.");
        _sim.SetSlimField(pilot.ShipId, SelfDestructTimeField, new SimSlimNone());
        TellSelfDestruct(session, "SelfDestructAbortedOther2");
        return None;
    }

    public async Task RefreshSelfDestructCountdownAsync(ClientSession session, CancellationToken cancellationToken)
    {
        if (TimedActions is not { } scheduler || !_pilots.TryGetValue((ulong)session.SessionId, out var pilot) ||
            !IsInSpace(session) || session.GetInt("shipid") != pilot.ShipId) return;
        var job = await scheduler.FindAsync(ShipSelfDestructJobKind, ShipSelfDestructJobKey(pilot.ShipId), cancellationToken);
        if (job is not { State: "pending" }) return;
        var countdown = JsonSerializer.Deserialize<ShipSelfDestructCountdown>(job.PayloadJson, SelfDestructJson);
        if (countdown?.CharacterId == pilot.CharacterId && countdown.SolarSystemId == pilot.SolarSystemId &&
            countdown.InstanceId == pilot.SimulationScene.InstanceId)
            _sim.SetSlimField(pilot.ShipId, SelfDestructTimeField, new SimSlimInt(FileTimeOfUnixMs(job.RunAtMs)));
        else if (await scheduler.CancelAsync(ShipSelfDestructJobKind, ShipSelfDestructJobKey(pilot.ShipId), cancellationToken))
            _sim.SetSlimField(pilot.ShipId, SelfDestructTimeField, new SimSlimNone());
    }

    public async Task CompleteShipSelfDestructAsync(ShipSelfDestructCountdown countdown, CancellationToken cancellationToken)
    {
        if (_nowUnixMs() < countdown.ExpiresAtMs)
            throw new InvalidOperationException("A ship self-destruct job ran before its deadline.");
        var pilot = _pilots.Values.FirstOrDefault(candidate =>
            candidate.CharacterId == countdown.CharacterId && candidate.ShipId == countdown.ShipId);
        if (pilot is null)
        {
            _sim.SetSlimField(countdown.ShipId, SelfDestructTimeField, new SimSlimNone());
            return;
        }
        if (pilot.SolarSystemId != countdown.SolarSystemId || pilot.SimulationScene.InstanceId != countdown.InstanceId)
        {
            _sim.SetSlimField(countdown.ShipId, SelfDestructTimeField, new SimSlimNone());
            return;
        }
        TellSelfDestruct(pilot.Session, "SelfDestructImmediateOther");
        if (await SelfDestructAsync(pilot.Session, cancellationToken) is null)
            throw new InvalidOperationException("The ship self-destruct could not complete the active ship death.");
    }

    private async Task CancelShipSelfDestructAsync(long shipId, CancellationToken cancellationToken)
    {
        if (TimedActions is not { } scheduler) return;
        var key = ShipSelfDestructJobKey(shipId);
        if (!await scheduler.CancelAsync(ShipSelfDestructJobKind, key, cancellationToken)) return;
        _sim.SetSlimField(shipId, SelfDestructTimeField, new SimSlimNone());
    }

    /// <summary>How long <c>/suicide</c> waits for the simulation and the character's items before it says it failed.</summary>
    private static readonly TimeSpan SelfDestructWait = TimeSpan.FromSeconds(20);

    public async Task<SelfDestruct?> SelfDestructAsync(ClientSession session, CancellationToken cancellationToken)
    {
        if (!_pilots.TryGetValue((ulong)session.SessionId, out var pilot))
        {
            return null;
        }
        if (await _ships.LoadAsync(pilot.CharacterId, cancellationToken) is not { } flown || flown.Ship.Id != pilot.ShipId)
        {
            return null;
        }
        var waiting = new TaskCompletionSource<SelfDestruct?>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_deaths.TryAdd(pilot.ShipId, waiting))
        {
            return null; // it is being destroyed already
        }
        try
        {
            // the wreck is the pilot's own to loot (the host makes it when the simulation says the ship is gone), so nobody else is credited
            _sim.Destroy(pilot.ShipId, SimCredit.Nobody);
            return await waiting.Task.WaitAsync(SelfDestructWait, cancellationToken);
        }
        catch (TimeoutException)
        {
            return null;
        }
        finally
        {
            _deaths.TryRemove(pilot.ShipId, out _);
        }
    }

    /// <summary>The place an item that is destroyed is said to go to (the client's junkyard).</summary>
    private const long Junkyard = 6;

    /// <summary>The group of a ship's wreck, and of a corpse (<c>Biomass</c>).</summary>
    private const int WreckGroup = 186;
    private const int CorpseGroup = 14;

    private static ItemRow RowOf(Item item, TypeDef? type) =>
        new(item.Id, item.TypeId, item.OwnerId, item.LocationId, item.Flag, item.Singleton ? -1 : (int)Math.Min(item.Quantity, int.MaxValue), type?.GroupId ?? 0, type?.CategoryId ?? 0);

    /// <summary>
    /// The ship of a pilot was destroyed (the simulation has told the clients that watched it): what it held goes into a wreck or is destroyed, the pilot is in a
    /// capsule where the ship was, and the client is told, in the reference's order, of the objects that let go, the capsule, the modules that left, the items,
    /// the session change and the capsule's ball; then the wreck is put in space. What the simulation needs to go on (the capsule) is given to it first.
    /// </summary>
    private async Task FinishAbandonedShipDeathAsync(EjectedShip abandoned, SimDestroyed destroyed)
    {
        try
        {
            var seed = string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"ship:{destroyed.ShipId}:{_nowUnixMs()}");
            var wreckTypeId = abandoned.ShipTypeId == CharacterShips.CapsuleTypeId
                ? null
                : destroyed.WreckKind?.TypeId;
            var death = await _ships.DestroyAbandonedShipAsync(
                abandoned.OwnerCharacterId,
                abandoned.SolarSystemId,
                destroyed.ShipId,
                wreckTypeId,
                seed,
                replaceActiveShip: abandoned.StructureControlSessionId is not null);
            if (death is null)
            {
                _log?.Invoke($"unpiloted ship {destroyed.ShipId} was destroyed in space but is no longer owned by character {abandoned.OwnerCharacterId} at system {abandoned.SolarSystemId}");
                return;
            }

            _ejectedShips.TryRemove(destroyed.ShipId, out _);
            if (abandoned.ShipTypeId != CharacterShips.CapsuleTypeId)
            {
                await _statics.EnsureTypesAsync([
                    .. death.Changes.Select(change => change.Item.TypeId),
                    .. death.Wreck is { } wreck ? new[] { wreck.TypeId } : [],
                    .. death.Capsule is { } replacement ? new[] { replacement.TypeId } : []
                ]);

                if (death.Capsule is { } capsule
                    && abandoned.StructureControlSessionId is { } controllerSessionId
                    && _structureObservers.ContainsKey(controllerSessionId)
                    && _pilots.TryGetValue(controllerSessionId, out var controller)
                    && controller.CharacterId == abandoned.OwnerCharacterId
                    && controller.ShipId == destroyed.ShipId)
                {
                    var capsulePilot = new Pilot(
                        controller.Session, controller.CharacterId, capsule.Id, capsule.TypeId, abandoned.SolarSystemId, 0)
                    {
                        StructureControlDeath = controller.StructureControlDeath,
                        StructureControlCapsulePosition = controller.StructureControlCapsulePosition ?? destroyed.Position,
                        StructureControlCapsuleDirection = controller.StructureControlCapsuleDirection ?? destroyed.Direction
                    };
                    EndTetherPilot(controller);
                    controller.End();
                    if (!_pilots.TryUpdate(controllerSessionId, capsulePilot, controller))
                    {
                        controller.Session.Close();
                        throw new InvalidOperationException($"the capsule for destroyed ship {destroyed.ShipId} could not replace its controller pilot");
                    }
                }

                var killer = destroyed.KillerShipId == 0
                    ? null
                    : _pilots.Values.FirstOrDefault(other => other.ShipId == destroyed.KillerShipId);
                var npcKiller = destroyed.KillerShipId != 0
                    && _npcShips.TryGetValue(destroyed.KillerShipId, out var npc)
                    ? npc
                    : null;
                var cause = npcKiller?.Profile?.Id.Contains("concord_response", StringComparison.OrdinalIgnoreCase) == true
                    ? ShipLossCause.Concord
                    : killer is not null
                        ? ShipLossCause.Player
                        : npcKiller is not null
                            ? ShipLossCause.Npc
                            : ShipLossCause.Unknown;
                var loss = new ShipLoss(
                    abandoned.OwnerCharacterId,
                    death.Ship.Id,
                    death.Ship.TypeId,
                    cause,
                    abandoned.SolarSystemId,
                    cause == ShipLossCause.Player ? killer!.CharacterId : 0,
                    [.. death.Changes.Select(change => new ShipLossItem(change.Item.TypeId, change.Item.Quantity))]);
                Raise(ShipLost, loss, nameof(ShipLost));
                Announce(
                    "ship.lost",
                    new
                    {
                        characterId = loss.CharacterId,
                        shipId = loss.ShipId,
                        shipTypeId = loss.ShipTypeId,
                        cause = loss.Cause.ToString(),
                        solarSystemId = loss.SolarSystemId,
                        killerCharacterId = loss.KillerCharacterId
                    },
                    $"char:{loss.CharacterId}");
            }

            if (death.Wreck is { } left && destroyed.WreckKind is { } kind)
            {
                PlaceWreck(new SimPlaceWreck(
                    SimSceneKey.SolarSystem(abandoned.SolarSystemId),
                    left.Id,
                    WreckGroup,
                    kind,
                    destroyed.Position,
                    destroyed.Direction,
                    destroyed.Radius,
                    destroyed.ShipId,
                    new SimCredit(abandoned.OwnerCharacterId, abandoned.OwnerCorporationId, 0, 0),
                    death.Changes.All(change => change.Destroyed)));
            }
        }
        catch (Exception error)
        {
            if (abandoned.StructureControlSessionId is not null)
                _ejectedShips.TryAdd(destroyed.ShipId, abandoned);
            _log?.Invoke($"the loss of unpiloted ship {destroyed.ShipId} of character {abandoned.OwnerCharacterId} could not be processed: {error}");
        }
    }

    private void NotifyDeathClient(Pilot pilot, ShipDeath lost, Item? recoveredCapsule = null)
    {
        var session = pilot.Session;
        if (lost.Capsule is { } capsule)
            session.SendNotification(
                "OnPodOrCorvetteAssembly", "charid",
                InventoryWire.Id(capsule.Id), new PyInt(capsule.TypeId));
        if (lost.EscapeShip is { } escapeShip)
        {
            var fromEscapeBay = new PyDict([
                new(new PyInt(3), InventoryWire.Id(lost.Ship.Id)),
                new(new PyInt(4), new PyInt(CharacterShips.FrigateEscapeBayFlag))
            ]);
            session.SendNotification(
                "OnItemsChanged", "charid",
                new PyList([InventoryWire.PackedRow(RowOf(escapeShip, _statics.Type(escapeShip.TypeId)))]),
                fromEscapeBay,
                None);
        }
        var now = FileTimeNow();
        if (lost.Events.Count > 0)
            session.SendNotification(
                "OnMultiEvent", "clientID",
                Eve.Services.Ships.ShipNotifications.MultiEvent(
                    pilot.CharacterId, pilot.ShipId, lost.Events, now));
        foreach (var change in lost.Changes)
        {
            var type = _statics.Type(change.Item.TypeId);
            var changedItem = recoveredCapsule is { } recoveredItem && recoveredItem.Id == change.Item.Id ? recoveredItem : change.Item;
            var row = change.Destroyed ? changedItem with { LocationId = Junkyard } : changedItem;
            var from = change.Destroyed
                ? new PyDict([new(new PyInt(3), InventoryWire.Id(change.PreviousLocationId))])
                : new PyDict([
                    new(new PyInt(3), InventoryWire.Id(change.PreviousLocationId)),
                    new(new PyInt(4), new PyInt(change.PreviousFlag))
                ]);
            session.SendNotification(
                "OnItemsChanged", "charid",
                new PyList([InventoryWire.PackedRow(RowOf(row, type))]), from, None);
        }
        var shipType = _statics.Type(lost.Ship.TypeId);
        session.SendNotification(
            "OnItemsChanged", "charid",
            new PyList([InventoryWire.PackedRow(
                RowOf(lost.Ship with { LocationId = Junkyard }, shipType),
                new PyStr($"Undocking:{pilot.UndockedFrom}"))]),
            new PyDict([new(new PyInt(3), InventoryWire.Id(lost.Ship.LocationId))]),
            None);

    }

    private void NotifyDeathCosmetics(Pilot pilot, ShipDeath lost)
    {
        var session = pilot.Session;
        session.SendNotification(
            "OnShipCosmeticsChanged", "clientID", InventoryWire.Id(pilot.ShipId), new PyDict([]));
        session.SendNotification(
            "OnShipCosmeticsChanged", "clientID", InventoryWire.Id(lost.PilotShip.Id), new PyDict([]));
        session.SendNotification(
            "OnCurrentShipSkinChange", "clientID", InventoryWire.Id(lost.PilotShip.Id), new PyInt(0));
    }

    /// <summary>Takes the surviving ship (or the capsule) into the simulation. A refusal puts the pilot back in its station, as the recovery of a failed respawn does.</summary>
    private async Task<bool> TakeSurvivorAsync(Pilot pilot, SimRespawn respawn, string description)
    {
        string? refusal;
        try
        {
            refusal = await _sim.RespawnAsync(respawn, CancellationToken.None);
        }
        catch (Exception error)
        {
            refusal = error.Message;
        }
        if (refusal is null)
            return true;
        _log?.Invoke($"the simulation did not take {description} of character {pilot.CharacterId}: {refusal}");
        _pilots.TryRemove((ulong)pilot.Session.SessionId, out _);
        _sim.Leave((ulong)pilot.Session.SessionId, LeaveWhy.Docked);
        await _ships.RecoverFromSpaceAsync(pilot.CharacterId, PlaceOfStation);
        if (await _ships.LoadAsync(pilot.CharacterId) is { } recovered)
            CharacterSessionRelocation.Docked(pilot.Session, recovered.Character.StationId, recovered.Character.SolarSystemId,
                recovered.Character.ConstellationId, recovered.Character.RegionId, recovered.Ship.Id);
        return false;
    }

    private void NotifyWreckItem(Pilot pilot, ShipDeath lost, SimDestroyed destroyed)
    {
        var session = pilot.Session;
        if (lost.Wreck is { } wreckItem && destroyed.WreckKind is not null)
        {
            session.SendNotification(
                "OnItemChange", "clientID",
                InventoryWire.PackedRow(RowOf(wreckItem, _statics.Type(wreckItem.TypeId)), new PyStr("")),
                new PyDict([new(new PyInt(3), new PyInt(0))]),
                None);
        }
    }

    private async Task FinishDeathAsync(Pilot pilot, SimDestroyed destroyed)
    {
        var id = (ulong)pilot.Session.SessionId;
        if (!_dying.TryAdd(pilot.ShipId, 0))
            return;
        pilot.End();
        ShipDeath? death = null;
        SelfDestruct? done = null;
        var handedOver = false;
        var simulationFailed = false;
        // the survivor and the wreck enter the simulation after the client has the session change, the cosmetics and the skin (the reference's order)
        SimRespawn? pendingRespawn = null;
        SimPlaceWreck? pendingWreck = null;
        string pendingDescription = "";
        try
        {
            var session = pilot.Session;
            await _charges.CloseAsync(id).ConfigureAwait(false);
            if (await _ships.LoadAsync(pilot.CharacterId) is { } held
                && held.Ship.Id == pilot.ShipId
                && held.Ship.TypeId == CharacterShips.CapsuleTypeId)
            {
                done = await FinishPodDeathAsync(pilot, destroyed);
                handedOver = done is not null;
                return;
            }

            var seed = string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"ship:{pilot.ShipId}:{_nowUnixMs()}");
            death = await _transitions.ExecuteAsync(
                token => _ships.DestroyShipAsync(
                    pilot.CharacterId, pilot.ShipId, destroyed.WreckKind?.TypeId, seed, token),
                result => result is not null,
                async (result, token) =>
                {
                    var lost = result!;
                    var survivor = lost.PilotShip;
                    await _statics.EnsureTypesAsync([
                        CharacterShips.CapsuleTypeId,
                        survivor.TypeId,
                        .. lost.Changes.Select(change => change.Item.TypeId),
                        .. lost.Wreck is { } wreck ? new[] { wreck.TypeId } : []
                    ]);

                    if (await _ships.LoadAsync(pilot.CharacterId, token) is not { } flown
                        || flown.Ship.Id != survivor.Id)
                    {
                        _log?.Invoke($"surviving ship {survivor.Id} of character {pilot.CharacterId} could not be loaded after death");
                        simulationFailed = true;
                        await _ships.RecoverFromSpaceAsync(pilot.CharacterId, PlaceOfStation);
                        return;
                    }

                    pendingDescription = survivor.TypeId == CharacterShips.CapsuleTypeId
                        ? $"the capsule {survivor.Id}"
                        : $"surviving ship {survivor.Id}";
                    pendingRespawn = new SimRespawn(
                        id,
                        SimShips.Of(flown, _statics, await SecurityStatusAsync(pilot.CharacterId, token)),
                        destroyed.Position,
                        destroyed.Direction);

                    if (lost.Wreck is { } wreckItem && destroyed.WreckKind is { } kind)
                    {
                        pendingWreck = new SimPlaceWreck(
                            SimSceneKey.SolarSystem(pilot.SolarSystemId),
                            wreckItem.Id,
                            WreckGroup,
                            kind,
                            destroyed.Position,
                            destroyed.Direction,
                            destroyed.Radius,
                            pilot.ShipId,
                            new SimCredit(
                                lost.Character.Id,
                                lost.Character.CorporationId,
                                lost.Character.AllianceId ?? 0,
                                lost.Character.FactionId ?? 0),
                            lost.Changes.All(change => change.Destroyed));
                    }
                },
                async (result, transitionToken) =>
                {
                    var lost = result!;
                    ReleaseBoundObjects(session);
                    if (simulationFailed)
                    {
                        if (await _ships.LoadAsync(pilot.CharacterId, transitionToken) is not { } recovered
                            || recovered.Ship.LocationId != recovered.Character.StationId)
                        {
                            session.Close();
                            return true;
                        }
                        _pilots.TryRemove(id, out _);
                        NotifyDeathClient(pilot, lost, recovered.Ship);
                        var character = recovered.Character;
                        CharacterSessionRelocation.Docked(session, character.StationId, character.SolarSystemId, character.ConstellationId, character.RegionId, recovered.Ship.Id);
                        handedOver = true;
                        return true;
                    }

                    var survivor = lost.PilotShip;
                    var flying = new Pilot(
                        session, pilot.CharacterId, survivor.Id, survivor.TypeId, pilot.SolarSystemId, 0);
                    flying.End();
                    if (!_pilots.TryUpdate(id, flying, pilot))
                    {
                        await _ships.RecoverFromSpaceAsync(pilot.CharacterId, PlaceOfStation);
                        session.Close();
                        return true;
                    }
                    NotifyDeathClient(pilot, lost);
                    session.Change(("shipid", new PyLong(survivor.Id)));
                    Background(() => RefreshTetherPolicyAsync(session));
                    handedOver = true;
                    return true;
                },
                async (result, _) =>
                {
                    var lost = result!;
                    RaiseShipLost(pilot, lost, destroyed);
                    NotifyDeathCosmetics(pilot, lost);
                    var placed = pendingRespawn is not { } respawn || await TakeSurvivorAsync(pilot, respawn, pendingDescription);
                    NotifyWreckItem(pilot, lost, destroyed);
                    if (placed && pendingWreck is { } wreck)
                        PlaceWreck(wreck);
                },
                CancellationToken.None,
                id);

            if (death is null)
            {
                _log?.Invoke($"the ship {pilot.ShipId} of character {pilot.CharacterId} was destroyed in space but is not the character's ship in space any more");
                return;
            }
            if (handedOver)
            {
                done = new SelfDestruct(
                    death.Ship.Id,
                    new SelfDestructPart(
                        "Wreck",
                        death.Wreck is { } wreck ? _statics.Type(wreck.TypeId)?.Name : null,
                        death.Wreck?.Id),
                    new SelfDestructPart(
                        death.Capsule is null ? "Frigate Escape Bay" : "Capsule",
                        _statics.Type(death.PilotShip.TypeId)?.Name ?? "Capsule",
                        death.PilotShip.Id));
            }
        }
        catch (Exception error)
        {
            _log?.Invoke($"the death of the ship {pilot.ShipId} of character {pilot.CharacterId} could not be told: {error}");
        }
        finally
        {
            if (!handedOver && _pilots.TryRemove(id, out _))
                _sim.Leave(id, LeaveWhy.LoggedOff);
            _dying.TryRemove(pilot.ShipId, out _);
            if (_deaths.TryRemove(pilot.ShipId, out var waiting))
                waiting.TrySetResult(done);
        }
    }

    private void RaiseShipLost(Pilot pilot, ShipDeath death, SimDestroyed destroyed)
    {
        var killer = destroyed.KillerShipId == 0 ? null : _pilots.Values.FirstOrDefault(other => other.ShipId == destroyed.KillerShipId);
        var concord = _concordDestroyed.TryRemove(pilot.ShipId, out _)
            || (destroyed.KillerShipId != 0
                && _npcShips.TryGetValue(destroyed.KillerShipId, out var npc)
                && npc.Profile?.Id.Contains("concord_response", StringComparison.OrdinalIgnoreCase) == true);
        var cause = _deaths.ContainsKey(pilot.ShipId) ? ShipLossCause.SelfDestruct
            : concord ? ShipLossCause.Concord
            : killer is not null ? ShipLossCause.Player
            : destroyed.KillerShipId != 0 && _npcShips.ContainsKey(destroyed.KillerShipId) ? ShipLossCause.Npc
            : ShipLossCause.Unknown;
        var loss = new ShipLoss(pilot.CharacterId, death.Ship.Id, death.Ship.TypeId, cause, pilot.SolarSystemId, cause == ShipLossCause.Player ? killer!.CharacterId : 0,
            [.. death.Changes.Select(change => new ShipLossItem(change.Item.TypeId, change.Item.Quantity))]);
        Raise(ShipLost, loss, nameof(ShipLost));
        Announce("ship.lost", new { characterId = loss.CharacterId, shipId = loss.ShipId, shipTypeId = loss.ShipTypeId, cause = loss.Cause.ToString(), solarSystemId = loss.SolarSystemId, killerCharacterId = loss.KillerCharacterId }, $"char:{loss.CharacterId}");
    }

    private void RaisePodLost(Pilot pilot, Item capsule, SimDestroyed destroyed)
    {
        var killer = destroyed.KillerShipId == 0 ? null : _pilots.Values.FirstOrDefault(other => other.ShipId == destroyed.KillerShipId);
        var cause = killer is not null ? ShipLossCause.Player
            : destroyed.KillerShipId != 0 && _npcShips.ContainsKey(destroyed.KillerShipId) ? ShipLossCause.Npc
            : ShipLossCause.Unknown;
        Raise(PodLost, new PodLoss(pilot.CharacterId, capsule.Id, pilot.SolarSystemId, cause, cause == ShipLossCause.Player ? killer!.CharacterId : 0), nameof(PodLost));
    }

    /// <summary>The corpse of a pilot whose capsule was destroyed: <c>resolveCorpseTypeID</c> of EVE.js (the client's <c>genderFemale = 0</c>; anything else leaves the male one).</summary>
    private const int CorpseTypeMale = 25;
    private const int CorpseTypeFemale = 29148;

    /// <summary>The rookie ship type of a race (what a pilot wakes in after its capsule is destroyed), set by the host that knows the races.</summary>
    /// <summary>The type of an item in space (an asteroid, a wreck), when the host has it; null when there is none. A module's target is looked up here.</summary>
    public Func<long, CancellationToken, Task<int?>>? TypeOfSpaceItemAsync { get; set; }

    /// <summary>
    /// Whether a pilot may pull a wreck or a can with a tractor beam: the rights of its loot, as for taking from it. Null when the id is no wreck or can
    /// (the simulation then refuses the target as not there); no answer at all (null delegate) refuses the pull.
    /// </summary>
    public Func<long, long, CancellationToken, Task<bool?>>? MayPullAsync { get; set; }

    public Func<int, int?>? RookieShipOfRace { get; set; }

    /// <summary>
    /// The capsule of a pilot was destroyed (the simulation has told the clients that watched it): the pilot wakes in the clone at its home station in a rookie
    /// ship, and its corpse is put in space where the capsule was. The client is told in the reference's order: the session change into the station, the corpse
    /// and the capsule as items, the capsule's place.
    /// </summary>
    private async Task<SelfDestruct?> FinishPodDeathAsync(Pilot pilot, SimDestroyed destroyed)
    {
        var id = (ulong)pilot.Session.SessionId;
        var session = pilot.Session;
        if (await _ships.CharacterAsync(pilot.CharacterId) is not { } character
            || RookieShipOfRace?.Invoke(character.RaceId) is not { } rookie)
        {
            _log?.Invoke($"the capsule of character {pilot.CharacterId} was destroyed but there is no rookie ship to wake the pilot in");
            return null;
        }
        var stationId = character.HomeStationId > 0 && _stations.Get(character.HomeStationId) is not null
            ? character.HomeStationId
            : character.StationId;
        if (_stations.Get(stationId) is not { } station)
        {
            _log?.Invoke($"the capsule of character {pilot.CharacterId} was destroyed but its clone's station {stationId} is not known");
            return null;
        }
        var corpseType = character.Gender == 0 ? CorpseTypeFemale : CorpseTypeMale;
        await _statics.EnsureTypesAsync([CharacterShips.CapsuleTypeId, corpseType, rookie]);

        var pod = await _transitions.ExecuteAsync(
            token => _ships.DestroyCapsuleAsync(
                pilot.CharacterId,
                pilot.ShipId,
                corpseType,
                character.Name,
                rookie,
                new HomePlace(station.Id, station.SolarSystemId, station.ConstellationId, station.RegionId),
                token),
            result => result is not null,
            (result, token) =>
            {
                var death = result!;
                _pilots.TryRemove(id, out _);
                _sim.Leave(id, LeaveWhy.Docked);
                RaisePodLost(pilot, death.Capsule, destroyed);
                if (death.Corpse is { } corpse)
                {
                    var type = _statics.Type(corpse.TypeId);
                    var kind = new SimWreckKind(corpse.TypeId, corpse.CustomName ?? character.Name, type?.GraphicId ?? 0);
                    var radius = type?.Attributes.GetValueOrDefault(162) ?? 0;
                    PlaceWreck(new SimPlaceWreck(
                        SimSceneKey.SolarSystem(pilot.SolarSystemId),
                        corpse.Id,
                        CorpseGroup,
                        kind,
                        destroyed.Position,
                        destroyed.Direction,
                        radius > 0 ? radius : 1.0,
                        0,
                        new SimCredit(character.Id, 0, 0, 0),
                        true));
                }
                return Task.CompletedTask;
            },
            (result, _) =>
            {
                var death = result!;
                ReleaseBoundObjects(session);
                CharacterSessionRelocation.Docked(session, station.Id, station.SolarSystemId, station.ConstellationId, station.RegionId, death.Ship.Id);
                return Task.FromResult(true);
            },
            (result, _) =>
            {
                var death = result!;
                if (death.Corpse is { } corpse)
                {
                    session.SendNotification(
                        "OnItemChange", "clientID",
                        InventoryWire.PackedRow(RowOf(corpse, _statics.Type(corpse.TypeId)), new PyStr("")),
                        new PyDict([new(new PyInt(3), new PyInt(0))]),
                        None);
                }
                session.SendNotification(
                    "OnItemChange", "clientID",
                    InventoryWire.PackedRow(
                        RowOf(death.Capsule with { LocationId = Junkyard }, _statics.Type(death.Capsule.TypeId)),
                        new PyStr("")),
                    new PyDict([new(new PyInt(3), InventoryWire.Id(death.Capsule.LocationId))]),
                    None);
                session.SendNotification(
                    "OnCfgDataChanged", "charid", new PyStr("evelocations"),
                    new PyList([
                        InventoryWire.Id(death.Capsule.Id),
                        new PyStr(_statics.Type(death.Capsule.TypeId)?.Name ?? "Capsule"),
                        new PyInt(death.Capsule.LocationId),
                        new PyInt(0),
                        new PyInt(0),
                        new PyInt(0),
                        None
                    ]));
                return Task.CompletedTask;
            },
            CancellationToken.None,
            id);

        return pod is null
            ? null
            : new SelfDestruct(
                pod.Capsule.Id,
                new SelfDestructPart("Corpse", pod.Corpse?.CustomName, pod.Corpse?.Id),
                new SelfDestructPart("Ship", _statics.Type(pod.Ship.TypeId)?.Name, pod.Ship.Id));
    }
}
