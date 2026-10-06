using System.Collections.Concurrent;
using Eve.Crimewatch;
using Eve.Marshal;

namespace Eve.Services.Crimewatch;

/// <summary>
/// What a crime costs the attacker in security status (<see cref="SecurityPenalty"/>), done when <see cref="CrimewatchRuntime"/> says a crime was committed: the new status is written
/// (the character's <c>security_status</c> relation, with its history), the pilot is told as the reference tells it (<c>OnModuleAttributeChanges</c> for the pilot's security status attribute on
/// its ship, then <c>OnSecurityStatusUpdate</c>) and the ship in space is given its new status, which those who see it are shown in its slim item. A pilot that lost for a ship loses nothing more
/// for the same ship for a minute (the reference's window: a gun that fires at a target all the while costs one penalty, not one per cycle).
/// </summary>
/// <summary>The exact source values the Host writer must persist in relations.relation_history.attrs.</summary>
public sealed record SecurityStatusPenaltyChange(
    long CharacterId,
    double PreviousStatus,
    double NextStatus,
    string Reason,
    int EventTypeID,
    long OtherOwnerID,
    int? OtherTypeID,
    long LocationID,
    double Modification,
    long TargetShipItemId);

public sealed class SecurityStatusPenalties(
    CrimewatchRuntime runtime,
    CharacterSessions sessions,
    Func<long, CancellationToken, Task<double>> statusOf,
    Func<AggressionRequest, CancellationToken, Task<int?>> targetTypeIdOf,
    Func<SecurityStatusPenaltyChange, CancellationToken, Task> setStatus,
    Func<long> nowUnixMs,
    Func<long, CancellationToken, Task>? changed = null)
{
    /// <summary>The attribute of a pilot that holds its security status (<c>pilotSecurityStatus</c>).</summary>
    public const int PilotSecurityStatus = 2610;

    /// <summary>The reason written to the history of the relation.</summary>
    public const string Reason = "aggression";

    private readonly ConcurrentDictionary<(long Attacker, string Target), long> _recent = new();

    /// <summary>Starts listening to the crimes crimewatch records.</summary>
    public void Attach() => runtime.Aggressed += OnAggressionAsync;

    /// <summary>A crime was committed: the penalty, if there is one to pay.</summary>
    public async Task OnAggressionAsync(AggressionResult result)
    {
        if (!result.Outcome.IsCriminalOffense)
        {
            return;
        }
        var request = result.Request;
        var now = nowUnixMs();
        foreach (var (key, until) in _recent)
        {
            if (until <= now)
            {
                _recent.TryRemove(new KeyValuePair<(long, string), long>(key, until));
            }
        }
        var target = (request.Attacker, $"{request.TargetShipId}:{(request.TargetIsCapsule ? "capsule" : "ship")}");
        if (_recent.TryGetValue(target, out var window) && window > now)
        {
            return;
        }
        var previous = SecurityPenalty.Clamp(await statusOf(request.Attacker, CancellationToken.None));
        var theirs = SecurityPenalty.Clamp(await statusOf(request.Target, CancellationToken.None));
        var next = SecurityPenalty.Next(previous, theirs, request.Security, request.TargetIsCapsule);
        if (next == previous)
        {
            _recent[target] = now + SecurityPenalty.DuplicateWindowMs;
            return;
        }

        var modification = (request.TargetIsCapsule ? SecurityPenalty.CapsuleModifier : SecurityPenalty.ShipModifier)
            * SecurityPenalty.PseudoSecurity(request.Security)
            * (1 + (theirs - previous) / 100);
        var typeId = await targetTypeIdOf(request, CancellationToken.None);
        await setStatus(new SecurityStatusPenaltyChange(
            request.Attacker,
            previous,
            next,
            Reason,
            SecurityStatusEventTypeIds.IllegalAggression,
            request.Target,
            typeId,
            request.SolarSystemId,
            modification,
            request.TargetShipId), CancellationToken.None);
        // Start the duplicate window only once the status history and value were persisted.
        _recent[target] = now + SecurityPenalty.DuplicateWindowMs;
        if (changed is not null)
        {
            await changed(request.Attacker, CancellationToken.None);
        }
        Tell(request.Attacker, previous, next, now);
    }

    private void Tell(long character, double previous, double next, long nowMs)
    {
        if (sessions.Of(character) is not { } session)
        {
            return;
        }
        if (session.GetInt("shipid") is { } ship and > 0)
        {
            var when = new PyLong(Py.FileTimeFromUnixMs(nowMs));
            session.SendNotification("OnModuleAttributeChanges", "clientID", new PyList([Py.Tuple(
                new PyStr("OnModuleAttributeChange"), InventoryWire.Id(character), InventoryWire.Id(ship), new PyInt(PilotSecurityStatus), when, Py.Number(next), Py.Number(previous), when)]));
        }
        session.SendNotification("OnSecurityStatusUpdate", "charid", Py.Number(next));
    }
}
