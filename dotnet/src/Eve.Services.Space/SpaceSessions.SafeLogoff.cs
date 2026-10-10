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
    private const string SafeLogoffBlockedLabel = "UI/Inflight/SafeLogoff/ConditionsFailedHeader";
    private static readonly TimeSpan SafeLogoffDuration = TimeSpan.FromSeconds(30);

    private static PyValue SafeLogoffRefusal() => new PyList([new PyStr(SafeLogoffBlockedLabel)]);

    public async ValueTask<PyValue> SafeLogoffAsync(ClientSession session, CancellationToken cancellationToken)
    {
        var id = (ulong)session.SessionId;
        var readBlockers = LifecycleBlockers;
        if (readBlockers is null
            || !_pilots.TryGetValue(id, out var pilot) || !IsInSpace(session)
            || session.GetInt("shipid") != pilot.ShipId
            || _dying.ContainsKey(pilot.ShipId) || _deaths.ContainsKey(pilot.ShipId)
            || pilot.JumpCloak || HasTargetLocks(id, pilot.ShipId)
            || await _ships.LoadAsync(pilot.CharacterId, cancellationToken) is not { } snapshot
            || snapshot.Ship.Id != pilot.ShipId
            || snapshot.Fitted.Any(module => snapshot.StateOf(module.Id) is ItemState.Active or ItemState.Overloaded))
        {
            return SafeLogoffRefusal();
        }

        IReadOnlyList<string> blockers;
        try
        {
            blockers = await readBlockers(ShipLifecycleAction.SafeLogoff, session, null, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception error)
        {
            _log?.Invoke($"safe logoff conditions for session {session.SessionId} could not be checked: {error.Message}");
            return SafeLogoffRefusal();
        }
        if (blockers.Count != 0)
            return new PyList([.. blockers.Select(label => new PyStr(label))]);

        if (_safeLogoffs.TryAdd(id, 0))
        {
            _timers.Set($"space.safe-logoff:{id}", SafeLogoffDuration, () =>
            {
                if (_safeLogoffs.TryRemove(id, out _))
                    session.Close();
            });
        }
        return new PyList([]);
    }

    public PyValue AbortSafeLogoff(ClientSession session)
    {
        CancelSafeLogoff(session);
        return None;
    }

    public bool CancelSafeLogoff(ClientSession session)
    {
        var id = (ulong)session.SessionId;
        if (!_safeLogoffs.TryRemove(id, out _))
            return false;
        _timers.Cancel($"space.safe-logoff:{id}");
        return true;
    }

    private bool HasTargetLocks(ulong sessionId, long shipId) =>
        _lockedTargets.TryGetValue(sessionId, out var own) && own.Count > 0
        || _lockedTargets.Any(entry => entry.Key != sessionId
            && _pilots.ContainsKey(entry.Key)
            && entry.Value.Contains(shipId));

    private bool IsTargetedByAnotherPlayer(ulong sessionId, long shipId) =>
        _lockedTargets.Any(entry => entry.Key != sessionId
            && _pilots.ContainsKey(entry.Key)
            && entry.Value.Contains(shipId));
}
