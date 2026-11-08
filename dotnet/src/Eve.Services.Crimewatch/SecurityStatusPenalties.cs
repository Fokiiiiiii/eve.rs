using Eve.Crimewatch;
using Eve.Marshal;
using Eve.Services;
using Npgsql;

namespace Eve.Services.Crimewatch;

/// <summary>
/// What a crime costs the attacker in security status (<see cref="SecurityPenalty"/>), done when <see cref="CrimewatchRuntime"/> says a crime was committed: the new status is written
/// (the character's <c>security_status</c> relation, with its history), the pilot is told as the reference tells it (<c>OnModuleAttributeChanges</c> for the pilot's security status attribute on
/// its ship, then <c>OnSecurityStatusUpdate</c>) and the ship in space is given its new status, which those who see it are shown in its slim item. It remembers each target ship-or-capsule
/// item for the one-minute weapon-timer duplicate window.
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
    Func<SecurityStatusPenaltyChange, NpgsqlConnection, NpgsqlTransaction, CancellationToken, Task> setStatus,
    Func<long> nowUnixMs,
    Func<long, CancellationToken, Task>? changed = null)
{
    /// <summary>The V24/EveJS duplicate security-status penalty window is the 60-second weapon timer.</summary>
    public const long DuplicateWindowMs = 60_000;

    /// <summary>The attribute of a pilot that holds its security status (<c>pilotSecurityStatus</c>).</summary>
    public const int PilotSecurityStatus = 2610;

    /// <summary>The reason written to the history of the relation.</summary>
    public const string Reason = "aggression";

    // ponytail: one process-local gate closes read/modify/write races; multiple hosts need atomic persistence.
    private readonly SemaphoreSlim _penaltyGate = new(1, 1);

    /// <summary>Starts listening to the crimes crimewatch records.</summary>
    public void Attach() => runtime.Aggressed += OnAggressionAsync;

    /// <summary>A crime was committed: the penalty, if there is one to pay.</summary>
    public async Task OnAggressionAsync(AggressionResult result)
    {
        var request = result.Request;
        await _penaltyGate.WaitAsync();
        try
        {
            var now = nowUnixMs();
            if (result.Action != PolicyAction.Attack ||
                result.Outcome.Reason is not (AggressionReason.Criminal or AggressionReason.Suspect) ||
                request.Attacker <= 0 || request.Target <= 0 || request.Attacker == request.Target)
            {
                return;
            }

            var previous = 0d;
            var next = 0d;
            var statusChanged = false;
            var remembered = await runtime.TryRememberSecurityStatusPenaltyAsync(
                request.Attacker,
                request.TargetShipId,
                request.TargetIsCapsule,
                checked(now + DuplicateWindowMs),
                now,
                async (connection, transaction, cancellationToken) =>
                {
                    previous = SecurityPenalty.Clamp(await statusOf(request.Attacker, cancellationToken));
                    var theirs = SecurityPenalty.Clamp(await statusOf(request.Target, cancellationToken));
                    next = SecurityPenalty.Next(previous, theirs, request.Security, request.TargetIsCapsule);
                    if (next == previous)
                        return;

                    var modification = (request.TargetIsCapsule ? SecurityPenalty.CapsuleModifier : SecurityPenalty.ShipModifier)
                        * SecurityPenalty.PseudoSecurity(request.Security)
                        * (1 + (theirs - previous) / 100);
                    var typeId = await targetTypeIdOf(request, cancellationToken);
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
                        request.TargetShipId), connection, transaction, cancellationToken);
                    statusChanged = true;
                },
                CancellationToken.None);
            if (!remembered || !statusChanged)
                return;
            if (changed is not null)
            {
                await changed(request.Attacker, CancellationToken.None);
            }
            Tell(request.Attacker, previous, next, now);
        }
        finally
        {
            _penaltyGate.Release();
        }
    }

    private void Tell(long character, double previous, double next, long nowMs)
    {
        if (sessions.Of(character) is { } session)
            Tell(session, previous, next, nowMs);
    }

    public static void Tell(ClientSession session, double previous, double next, long nowMs)
    {
        if (session.CharacterId is not { } character)
            return;
        if (session.GetInt("shipid") is { } ship and > 0)
        {
            var when = new PyLong(Py.FileTimeFromUnixMs(nowMs));
            session.SendNotification("OnModuleAttributeChanges", "clientID", new PyList([Py.Tuple(
                new PyStr("OnModuleAttributeChange"), InventoryWire.Id(character), InventoryWire.Id(ship), new PyInt(PilotSecurityStatus), when, Py.Number(next), Py.Number(previous), when)]));
        }
        session.SendNotification("OnSecurityStatusUpdate", "charid", Py.Number(next));
    }
}
