using Eve.Ships;
using Eve.Space;

namespace Eve.Services.Space;

public sealed partial class SpaceSessions
{
    private static string CombatLogoffTimerKey(long shipId) => $"space:combat-logoff:{shipId}";

    private SemaphoreSlim CombatLogoffGate(long shipId) =>
        _combatLogoffGates.GetOrAdd(shipId, _ => new SemaphoreSlim(1, 1));

    internal async Task<long?> CombatLogoffUntilAsync(long characterId)
    {
        if (CrimewatchStateOf is not { } readState)
            return null;
        try
        {
            var state = await readState(characterId, CancellationToken.None).ConfigureAwait(false);
            var until = Math.Max(state.PvpUntilMs, state.NpcUntilMs);
            return until > _nowUnixMs() ? until : null;
        }
        catch (Exception error)
        {
            _log?.Invoke($"combat logoff timer of character {characterId} could not be read: {error.Message}");
            return null;
        }
    }

    private async Task<SemaphoreSlim?> BeginCombatLogoffEntryAsync(
        SpaceItem state,
        CancellationToken cancellationToken)
    {
        if (state.Kind != SpaceItemKind.CombatLogoffShip || _spaceItems is null)
            return null;

        var gate = CombatLogoffGate(state.ItemId);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = await _spaceItems.GetAsync(state.ItemId, cancellationToken).ConfigureAwait(false);
            if (current is not { Kind: SpaceItemKind.CombatLogoffShip })
            {
                gate.Release();
                return null;
            }

            var removed = await _sim.RemoveUnpilotedShipAsync(
                new SimRemoveUnpilotedShip(NextSpawnRequest(), SimSceneKey.SolarSystem(current.SolarSystemId), current.ItemId),
                cancellationToken).ConfigureAwait(false);
            // A simulation restart drops its in-memory scenes. There cannot be an unpiloted ball to remove until
            // the subsequent SimEnter rebuilds that scene from this durable pose.
            if (removed.Error is not null && !removed.Error.Equals("SCENE_NOT_LOADED", StringComparison.Ordinal))
                throw Notify("Space is not available right now. Try again in a moment.");
            return gate;
        }
        catch
        {
            gate.Release();
            throw;
        }
    }

    internal async Task ExpireCombatLogoffShipAsync(SpaceItem due)
    {
        if (due.Kind != SpaceItemKind.CombatLogoffShip || _spaceItems is null)
            return;

        var gate = CombatLogoffGate(due.ItemId);
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var current = await _spaceItems.GetAsync(due.ItemId).ConfigureAwait(false);
            if (current is not { Kind: SpaceItemKind.CombatLogoffShip } || current.ExpiresAtMs > _nowUnixMs())
                return;

            if (await CombatLogoffUntilAsync(current.CharacterId).ConfigureAwait(false) is { } extendedUntil)
            {
                await _spaceItems.SaveAsync(current with { ExpiresAtMs = extendedUntil }).ConfigureAwait(false);
                return;
            }

            var removed = await _sim.RemoveUnpilotedShipAsync(
                new SimRemoveUnpilotedShip(NextSpawnRequest(), SimSceneKey.SolarSystem(current.SolarSystemId), current.ItemId),
                CancellationToken.None).ConfigureAwait(false);
            if (removed.Error is not null && !removed.Error.Equals("SCENE_NOT_LOADED", StringComparison.Ordinal))
                throw new InvalidOperationException($"combat-logoff ship {current.ItemId} could not be removed: {removed.Error}");

            await _spaceItems.SaveAsync(current with
            {
                Kind = SpaceItemKind.ActiveShip,
                ExpiresAtMs = long.MaxValue,
            }).ConfigureAwait(false);
            _ejectedShips.TryRemove(current.ItemId, out _);
        }
        finally
        {
            gate.Release();
        }
    }

    internal async Task<bool> RestoreCombatLogoffShipAsync(SpaceItem state, CancellationToken cancellationToken)
    {
        if (state.Kind != SpaceItemKind.CombatLogoffShip || _spaceItems is null)
            return false;

        var gate = CombatLogoffGate(state.ItemId);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_pilots.Values.Any(pilot => pilot.ShipId == state.ItemId && pilot.SolarSystemId == state.SolarSystemId))
                return true;

            var persisted = await FindOfflineCombatLogoffShipAsync(
                state.CharacterId, state.SolarSystemId, state.ItemId, cancellationToken).ConfigureAwait(false);
            if (persisted is null)
                return false;

            var stagingSessionId = CombatLogoffRestorationSessionId(persisted.ItemId);
            // A Host can stop between the synthetic entry and its Ejected leave. Finish that transition first;
            // if it had already completed, the simulator treats this as an unknown session.
            _sim.Leave(stagingSessionId, LeaveWhy.Ejected);

            if (_pilots.Values.FirstOrDefault(pilot => pilot.SolarSystemId == persisted.SolarSystemId) is { } observer &&
                await PositionOfAsync(observer.Session, persisted.ItemId, cancellationToken).ConfigureAwait(false) is not null)
                return true;

            var snapshot = await _ships.LoadAsync(persisted.CharacterId, cancellationToken).ConfigureAwait(false);
            if (snapshot is null || snapshot.Ship.Id != persisted.ItemId || snapshot.Character.ShipId != persisted.ItemId ||
                snapshot.Character.SolarSystemId != persisted.SolarSystemId || snapshot.Ship.LocationId != persisted.SolarSystemId ||
                snapshot.Ship.Flag != CharacterShips.InSpaceFlag)
                return false;

            await SyncBuffsAsync(snapshot.World, snapshot.Ship.Id, cancellationToken).ConfigureAwait(false);
            var ship = SimShips.Of(snapshot, _statics, await SecurityStatusAsync(persisted.CharacterId, cancellationToken));
            string? refusal;
            try
            {
                await PrepareSolarSystemAsync(persisted.SolarSystemId, cancellationToken).ConfigureAwait(false);
                refusal = await _sim.EnterAsync(new SimEnter(
                    stagingSessionId,
                    SimSceneKey.SolarSystem(persisted.SolarSystemId),
                    EnterHow.InSpace,
                    0,
                    ship,
                    persisted.Position,
                    persisted.Direction), cancellationToken).ConfigureAwait(false);
            }
            catch (SimUnavailableException error)
            {
                _log?.Invoke($"combat-logoff ship {persisted.ItemId} could not be restored to the simulation: {error.Message}");
                return false;
            }

            if (refusal is not null)
            {
                if (refusal.StartsWith("ALREADY_IN_SPACE", StringComparison.Ordinal))
                {
                    _sim.Leave(stagingSessionId, LeaveWhy.Ejected);
                    return true;
                }
                _log?.Invoke($"combat-logoff ship {persisted.ItemId} could not be restored to the simulation: {refusal}");
                return false;
            }

            TellEnteredBuffs(stagingSessionId, persisted.ItemId, _nowUnixMs());
            try
            {
                await _sim.SetHeatProfileAsync(
                    stagingSessionId,
                    SimShips.HeatProfileOf(snapshot, _statics),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (SimUnavailableException error)
            {
                _log?.Invoke($"heat profile restore for combat-logoff ship {persisted.ItemId}: {error.Message}");
            }
            finally
            {
                _sim.Leave(stagingSessionId, LeaveWhy.Ejected);
            }
            return true;
        }
        finally
        {
            gate.Release();
        }
    }

    private static ulong CombatLogoffRestorationSessionId(long shipId)
    {
        const ulong stagingRange = 1UL << 60;
        if (shipId <= 0 || (ulong)shipId >= stagingRange)
            throw new ArgumentOutOfRangeException(nameof(shipId), "combat-logoff ship id is outside its reserved simulation session range");
        return SimUnpilotedSessionBase + stagingRange + (ulong)shipId;
    }
}
