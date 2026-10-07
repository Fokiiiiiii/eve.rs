using System.Collections.Concurrent;
using Eve.Crimewatch;
using Eve.Marshal;
using Eve.Scheduler;
using Eve.Universe;

namespace Eve.Services.Crimewatch;

/// <summary>An attack on the ship of another pilot, and what it made of the attacker (see <see cref="Aggression"/>).</summary>
/// <summary>A canonical (FirstCharacterID &lt; SecondCharacterID) crimewatch pair row.</summary>
/// <summary>One persisted security-status history row projected to the client's exact field names.</summary>
public sealed record SecurityStatusTransaction(
    int? EventTypeID,
    long EventDateUnixMs,
    long? OtherOwnerID,
    int? OtherTypeID,
    long? LocationID,
    double? Modification,
    double? NewValue);

public sealed record LimitedEngagement(long FirstCharacterID, long SecondCharacterID, long ExpiresAtUnixMs);

/// <summary>
/// Host persistence boundary for the actual security-status relation history and shared
/// crimewatchRuntime limited-engagement rows. Engagement reads return only rows whose expiry is
/// greater than nowUnixMs, optionally restricted to a character.
/// </summary>
public interface ICrimewatchPersistenceCallbacks
{
    /// <summary>Host should call RelationStore.HistoryAsync for security_status, RelationStore.World and int.MaxValue. Project AtMs, NewValue and exact Attrs keys eventTypeID/otherOwnerID/otherTypeID/locationID/modification; if eventTypeID is absent, use SecurityStatusEventTypeIds.FromHistoryReason(entry.Reason). Missing metadata stays null.</summary>
    Task<IReadOnlyList<SecurityStatusTransaction>> SecurityStatusTransactionsOfAsync(long characterId, CancellationToken cancellationToken);

    /// <summary>
    /// Creates a pending challenge with a Host-selected expiry and delivers OnDuelChallenge to the target.
    /// Returns that exact expiry FILETIME, or null when the Host rejects the invitation.
    /// </summary>
    Task<long?> StartDuelChallengeAsync(
        long challengerCharacterId,
        long targetCharacterId,
        long challengerCorporationId,
        long challengerAllianceId,
        CancellationToken cancellationToken);

    /// <summary>Reads active pair rows in the same store used by weapon legality.</summary>
    Task<IReadOnlyList<LimitedEngagement>> LimitedEngagementsAsync(long nowUnixMs, long? characterId, CancellationToken cancellationToken);

    /// <summary>Upserts key le:min:max with kind=limitedEngagement, firstCharacterID, secondCharacterID and engagement.expiresAtMs.</summary>
    Task UpsertLimitedEngagementAsync(long firstCharacterId, long secondCharacterId, long expiresAtUnixMs, CancellationToken cancellationToken);

    /// <summary>Deletes only the pair whose stored expiry equals expectedExpiresAtUnixMs and is due by nowUnixMs.</summary>
    Task<bool> ExpireLimitedEngagementAsync(long firstCharacterId, long secondCharacterId, long expectedExpiresAtUnixMs, long nowUnixMs, CancellationToken cancellationToken);

    /// <summary>
    /// Validates and consumes the pending duel challenge in one transaction. A decline consumes the challenge
    /// without creating an engagement; an accepted challenge writes the shared pair row with the supplied expiry.
    /// Returns true only when an accepted, unexpired challenge was committed.
    /// </summary>
    Task<bool> RespondToDuelChallengeAsync(
        long responderCharacterId,
        long challengerCharacterId,
        long challengeExpiresAtFileTime,
        bool accept,
        long? engagementExpiresAtUnixMs,
        CancellationToken cancellationToken);
}

public sealed record AggressionResult(AggressionRequest Request, AggressionOutcome Outcome);

/// <summary>Resolves active war relations and their accepted allies for crimewatch.</summary>
public sealed class RelationWars(Eve.Relations.RelationStore relations) : Eve.Services.IWars
{
    private enum WarSide { Aggressor, Defender }


    public readonly record struct AssistanceFacts(bool TargetInWar, bool HelperInWar, bool SameSide);
    private sealed record WarAllyAttributes(string? Side);

    public async Task<bool> AreAtWarAsync(long attackerCharacterId, long targetCharacterId, long nowUnixMs, CancellationToken cancellationToken = default)
    {
        var attackerOwners = await relations.ChainOfAsync(attackerCharacterId, cancellationToken);
        var targetOwners = await relations.ChainOfAsync(targetCharacterId, cancellationToken);
        var directWars = await relations.BetweenAsync(Eve.Relations.RelationKinds.War.Name, attackerOwners, targetOwners, cancellationToken);
        if (directWars.Any(war => Active(war, nowUnixMs)))
        {
            return true;
        }

        var owners = attackerOwners.Concat(targetOwners).Distinct().ToArray();
        var allyBatches = await Task.WhenAll(owners.Select(owner =>
            relations.FromAsync(Eve.Relations.RelationKinds.WarAlly.Name, owner, cancellationToken: cancellationToken)));
        var allies = allyBatches.SelectMany(rows => rows).Where(ally => Active(ally, nowUnixMs)).DistinctBy(ally => ally.Id).ToArray();
        if (allies.Length == 0)
        {
            return false;
        }

        var warReads = owners.SelectMany(owner => new[]
        {
            relations.FromAsync(Eve.Relations.RelationKinds.War.Name, owner, cancellationToken: cancellationToken),
            relations.ToAsync(Eve.Relations.RelationKinds.War.Name, owner, cancellationToken: cancellationToken),
        });
        var warBatches = await Task.WhenAll(warReads);
        var wars = warBatches.SelectMany(rows => rows).Concat(directWars).DistinctBy(war => war.Id).ToArray();
        var knownWarIds = wars.Select(war => war.Id).ToHashSet();

        bool OnSide(IReadOnlyList<long> chain, Eve.Relations.Relation war, WarSide side) =>
            chain.Contains(side == WarSide.Aggressor ? war.Subject : war.Object) ||
            allies.Any(ally => chain.Contains(ally.Subject) && ally.Object == war.Id && SideOf(ally) == side);

        foreach (var war in wars)
        {
            if (!Active(war, nowUnixMs))
            {
                continue;
            }
            var attackerIsAggressor = OnSide(attackerOwners, war, WarSide.Aggressor);
            var attackerIsDefender = OnSide(attackerOwners, war, WarSide.Defender);
            var targetIsAggressor = OnSide(targetOwners, war, WarSide.Aggressor);
            var targetIsDefender = OnSide(targetOwners, war, WarSide.Defender);
            if ((attackerIsAggressor && targetIsDefender) || (attackerIsDefender && targetIsAggressor))
            {
                return true;
            }
        }

        // When both characters are war allies, their active ally relations are the only rows reachable from their owner chains.
        foreach (var attackerAlly in allies.Where(ally => attackerOwners.Contains(ally.Subject)))
        {
            if (knownWarIds.Contains(attackerAlly.Object) || SideOf(attackerAlly) is not { } attackerSide)
            {
                continue;
            }
            if (allies.Any(targetAlly =>
                    targetOwners.Contains(targetAlly.Subject) &&
                    targetAlly.Object == attackerAlly.Object &&
                    SideOf(targetAlly) is { } targetSide &&
                    targetSide != attackerSide))
            {
                return true;
            }
        }

        return false;
    }


    public async Task<AssistanceFacts> AssistanceFactsAsync(
        long assistantCharacterId,
        long targetCharacterId,
        long nowUnixMs,
        CancellationToken cancellationToken = default)
    {
        if (assistantCharacterId <= 0 || targetCharacterId <= 0 || assistantCharacterId == targetCharacterId)
            return default;

        var chains = await Task.WhenAll(
            relations.ChainOfAsync(assistantCharacterId, cancellationToken),
            relations.ChainOfAsync(targetCharacterId, cancellationToken));
        var assistantOwners = chains[0];
        var targetOwners = chains[1];
        var owners = assistantOwners.Concat(targetOwners).Distinct().ToArray();
        var warReads = owners.SelectMany(owner => new[]
        {
            relations.FromAsync(Eve.Relations.RelationKinds.War.Name, owner, cancellationToken: cancellationToken),
            relations.ToAsync(Eve.Relations.RelationKinds.War.Name, owner, cancellationToken: cancellationToken),
        });
        var allyBatches = await Task.WhenAll(owners.Select(owner =>
            relations.FromAsync(Eve.Relations.RelationKinds.WarAlly.Name, owner, cancellationToken: cancellationToken)));
        var allies = allyBatches.SelectMany(rows => rows)
            .Where(ally => Active(ally, nowUnixMs))
            .DistinctBy(ally => ally.Id)
            .ToArray();
        var referencedWars = await Task.WhenAll(allies.Select(ally =>
            relations.GetByIdAsync(ally.Object, cancellationToken: cancellationToken)));
        var warBatches = await Task.WhenAll(warReads);
        var wars = warBatches.SelectMany(rows => rows)
            .Concat(referencedWars.Where(war => war?.Kind == Eve.Relations.RelationKinds.War.Name).Select(war => war!))
            .Where(war => Active(war, nowUnixMs))
            .DistinctBy(war => war.Id)
            .ToArray();

        bool OnSide(IReadOnlyList<long> chain, Eve.Relations.Relation war, WarSide side) =>
            chain.Contains(side == WarSide.Aggressor ? war.Subject : war.Object)
            || allies.Any(ally => chain.Contains(ally.Subject) && ally.Object == war.Id && SideOf(ally) == side);

        var targetWars = wars.Where(war =>
            OnSide(targetOwners, war, WarSide.Aggressor) || OnSide(targetOwners, war, WarSide.Defender)).ToArray();
        if (targetWars.Length == 0)
            return default;

        var helperInEveryWar = targetWars.All(war =>
            OnSide(assistantOwners, war, WarSide.Aggressor) || OnSide(assistantOwners, war, WarSide.Defender));
        var onSameSideInEveryWar = targetWars.All(war =>
            (OnSide(targetOwners, war, WarSide.Aggressor) && OnSide(assistantOwners, war, WarSide.Aggressor))
            || (OnSide(targetOwners, war, WarSide.Defender) && OnSide(assistantOwners, war, WarSide.Defender)));
        return new(true, helperInEveryWar, onSameSideInEveryWar);
    }

    private static bool Active(Eve.Relations.Relation relation, long nowUnixMs) =>
        relation.SinceMs <= nowUnixMs && (relation.UntilMs is null || relation.UntilMs > nowUnixMs);

    private static WarSide? SideOf(Eve.Relations.Relation ally)
    {
        try
        {
            return ally.Attrs<WarAllyAttributes>()?.Side?.ToLowerInvariant() switch
            {
                "aggressor" => WarSide.Aggressor,
                "defender" => WarSide.Defender,
                _ => null,
            };
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// What crimewatch does as pilots attack one another: it keeps the timers and flags (<see cref="PgCrimewatchStore"/>, by the rules of <see cref="Aggression"/>) and tells the clients what
/// changed, as the reference does: the pilot its own timers (<c>OnWeaponsTimerUpdate</c>, <c>OnPvpTimerUpdate</c>, <c>OnNpcTimerUpdate</c>, <c>OnCriminalTimerUpdate</c>,
/// <c>OnDisapprovalTimerUpdate</c>, each <c>(state, expiry)</c>, only for the ones that changed) and everyone in space in the system who is flagged now and who is not any more
/// (<c>OnSystemCriminalFlagUpdates</c>: the ones that went idle, the new suspects, the new criminals). A timer that runs out is told when it does: the state is in PostgreSQL and the wait
/// for its end is <see cref="LocalTimers"/> (a process-local wait; a process that starts takes it up again with <see cref="ResumeAsync"/>). One step at a time: crimewatch is told of
/// an attack and of an end by the same door, and what it told last is what it compares with.
/// </summary>
public sealed record KillRightGrantRequest(
    long OwnerCharacterId,
    long TargetCharacterId,
    long SolarSystemId,
    long CreatedAtUnixMs);

public sealed record KillRightCreated(
    long KillRightId,
    long OwnerCharacterId,
    long TargetCharacterId,
    long SolarSystemId,
    long CreatedAtUnixMs);

public sealed record ConcordResponseRequest(
    long AttackerCharacterId,
    long AttackerShipId,
    long SolarSystemId,
    double Security,
    long CrimeAtUnixMs);

public sealed class CrimewatchRuntime(PgCrimewatchStore store, CharacterSessions sessions, Func<long> nowUnixMs, ICrimewatchPersistenceCallbacks persistence, Action<string>? log = null, Func<TimeSpan, CancellationToken, Task>? delay = null, LocalTimers? localTimers = null) : IDisposable
{
    private readonly bool _ownsTimers = localTimers is null;
    private readonly LocalTimers _timers = localTimers ?? new(log, delay);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<long, CombatTimers> _told = new();
    private readonly Dictionary<long, (IReadOnlyList<long> Criminals, IReadOnlyList<long> Suspects)> _flagsTold = [];
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(long CharacterId, long SolarSystemId), byte> _concordResponses = new();

    /// <summary>Whether the law lets the first pilot attack the second at this time (a war, a limited engagement, a corporation that may shoot its own). Nothing is legal until something says so.</summary>
    public Func<long, long, long, CancellationToken, Task<bool>>? Legal { get; set; }


    public Func<AggressionRequest, long, CancellationToken, Task<PolicyFacts>>? AttackPolicyFactsOf { get; set; }


    public Func<long, long, long, CancellationToken, Task<RelationWars.AssistanceFacts>>? WarAssistanceFactsOf { get; set; }


    /// <summary>Host persistence hook for a new kill right. Return its ID only when a new right was persisted.</summary>
    public Func<KillRightGrantRequest, CancellationToken, Task<long?>>? CreateKillRight { get; set; }

    /// <summary>Resolves system security when criminal timers are restored after a host restart.</summary>
    public Func<long, CancellationToken, Task<double?>>? SecurityOfSystem { get; set; }

    /// <summary>Raised after an attack was recorded and before the clients are told the timers: what a crime brings (the security status, a kill right, CONCORD) hangs here.</summary>
    public event Func<AggressionResult, Task>? Aggressed;

    /// <summary>Raised after a persisted illegal aggression has granted a kill right.</summary>
    public event Func<KillRightCreated, Task>? KillRightGranted;

    /// <summary>Raised when a delayed high-security response is due. The host connects this to SpaceSessions/NPC dispatch.</summary>
    public event Func<ConcordResponseRequest, Task>? ConcordResponseDue;

    public const long LimitedEngagementDurationMs = 5 * 60_000;

    private static string Key(long characterId) => $"crimewatch.end:{characterId}";

    /// <summary>
    /// A pilot attacked the ship of another pilot: its timers run and it may be flagged; what a crime brings (<see cref="Aggressed"/>) is done, and then the clients are told the timers
    /// (as in the reference, whose security status and kill right come before the timers).
    /// </summary>
    /// <summary>Active peer-to-expiry Unix-ms values for one character.</summary>
    public Task<IReadOnlyList<SecurityStatusTransaction>> SecurityStatusTransactionsOfAsync(
        long characterId,
        CancellationToken cancellationToken = default) =>
        persistence.SecurityStatusTransactionsOfAsync(characterId, cancellationToken);

    public async Task<IReadOnlyDictionary<long, long>> LimitedEngagementsOfAsync(
        long characterId,
        long nowUnixMs,
        CancellationToken cancellationToken = default)
    {
        if (characterId <= 0)
        {
            return new Dictionary<long, long>();
        }
        var result = new Dictionary<long, long>();
        foreach (var engagement in await persistence.LimitedEngagementsAsync(nowUnixMs, characterId, cancellationToken))
        {
            if (engagement.ExpiresAtUnixMs <= nowUnixMs)
            {
                continue;
            }
            if (engagement.FirstCharacterID == characterId)
            {
                result[engagement.SecondCharacterID] = engagement.ExpiresAtUnixMs;
            }
            else if (engagement.SecondCharacterID == characterId)
            {
                result[engagement.FirstCharacterID] = engagement.ExpiresAtUnixMs;
            }
        }
        return result;
    }

    /// <summary>Resolves an accepted duel challenge into the shared five-minute pair row.</summary>
    public Task<long?> StartDuelChallengeAsync(
        long challengerCharacterId,
        long targetCharacterId,
        long challengerCorporationId,
        long challengerAllianceId,
        CancellationToken cancellationToken = default) =>
        persistence.StartDuelChallengeAsync(
            challengerCharacterId,
            targetCharacterId,
            challengerCorporationId,
            challengerAllianceId,
            cancellationToken);

    public async Task<bool> RespondToDuelChallengeAsync(
        long responderCharacterId,
        long challengerCharacterId,
        long challengeExpiresAtFileTime,
        bool accept,
        CancellationToken cancellationToken = default)
    {
        if (responderCharacterId <= 0 || challengerCharacterId <= 0 ||
            responderCharacterId == challengerCharacterId || challengeExpiresAtFileTime <= 0)
        {
            return false;
        }
        var now = nowUnixMs();
        var engagementExpiresAt = accept ? now + LimitedEngagementDurationMs : (long?)null;
        var accepted = await persistence.RespondToDuelChallengeAsync(
            responderCharacterId,
            challengerCharacterId,
            challengeExpiresAtFileTime,
            accept,
            engagementExpiresAt,
            cancellationToken);
        if (!accept || !accepted)
        {
            return false;
        }

        var (first, second) = Pair(responderCharacterId, challengerCharacterId);
        var engagement = new LimitedEngagement(first, second, engagementExpiresAt!.Value);
        ScheduleLimitedEngagementExpiry(engagement);
        TellEngagement(engagement, created: true);
        return true;
    }

    private async Task RefreshLimitedEngagementAsync(
        long firstCharacterId,
        long secondCharacterId,
        long nowUnixMs,
        bool alreadyActive,
        CancellationToken cancellationToken)
    {
        var (first, second) = Pair(firstCharacterId, secondCharacterId);
        var engagement = new LimitedEngagement(first, second, nowUnixMs + LimitedEngagementDurationMs);
        await persistence.UpsertLimitedEngagementAsync(first, second, engagement.ExpiresAtUnixMs, cancellationToken);
        ScheduleLimitedEngagementExpiry(engagement);
        TellEngagement(engagement, created: !alreadyActive);
    }

    private void ScheduleLimitedEngagementExpiry(LimitedEngagement engagement)
    {
        var (first, second) = Pair(engagement.FirstCharacterID, engagement.SecondCharacterID);
        var afterMs = Math.Max(5, engagement.ExpiresAtUnixMs - nowUnixMs() + 5);
        _timers.Set(EngagementKey(first, second), TimeSpan.FromMilliseconds(afterMs),
            () => ExpireLimitedEngagementAsync(first, second, engagement.ExpiresAtUnixMs));
    }

    private async Task ExpireLimitedEngagementAsync(long firstCharacterId, long secondCharacterId, long expectedExpiresAtUnixMs)
    {
        var now = nowUnixMs();
        if (await persistence.ExpireLimitedEngagementAsync(
                firstCharacterId, secondCharacterId, expectedExpiresAtUnixMs, now, CancellationToken.None))
        {
            TellEngagementEnded(firstCharacterId, secondCharacterId);
            return;
        }

        foreach (var active in await persistence.LimitedEngagementsAsync(now, firstCharacterId, CancellationToken.None))
        {
            if (active.FirstCharacterID == firstCharacterId &&
                active.SecondCharacterID == secondCharacterId &&
                active.ExpiresAtUnixMs > now)
            {
                ScheduleLimitedEngagementExpiry(active);
                return;
            }
        }
    }

    private void TellEngagement(LimitedEngagement engagement, bool created)
    {
        var method = created ? "OnCrimewatchEngagementCreated" : "OnCrimewatchEngagementStartTimeout";
        var timeout = new PyLong(Py.FileTimeFromUnixMs(engagement.ExpiresAtUnixMs));
        sessions.Of(engagement.FirstCharacterID)?.SendNotification(method, "charid", InventoryWire.Id(engagement.SecondCharacterID), timeout);
        sessions.Of(engagement.SecondCharacterID)?.SendNotification(method, "charid", InventoryWire.Id(engagement.FirstCharacterID), timeout);
    }

    private void TellEngagementEnded(long firstCharacterId, long secondCharacterId)
    {
        sessions.Of(firstCharacterId)?.SendNotification("OnCrimewatchEngagementEnded", "charid", InventoryWire.Id(secondCharacterId));
        sessions.Of(secondCharacterId)?.SendNotification("OnCrimewatchEngagementEnded", "charid", InventoryWire.Id(firstCharacterId));
    }

    private static bool IsEmpire(SecurityClass where) =>
        Security.IsHighSecOrHigher(where) || Security.IsLowSec(where);

    private static (long First, long Second) Pair(long first, long second) =>
        first <= second ? (first, second) : (second, first);

    private static string EngagementKey(long first, long second) => $"crimewatch.engagement:{first}:{second}";

    public async Task<AggressionResult> RecordAggressionAsync(AggressionRequest request, CancellationToken cancellationToken = default)
    {
        var now = nowUnixMs();
        var targetState = await store.StateOfAsync(request.Target, cancellationToken);
        var targetFlag = targetState.FlagAt(now);
        var canPair = request.Attacker > 0 && request.Target > 0 && request.Attacker != request.Target;
        var activeEngagements = canPair
            ? await LimitedEngagementsOfAsync(request.Attacker, now, cancellationToken)
            : new Dictionary<long, long>();
        var alreadyEngaged = activeEngagements.ContainsKey(request.Target);
        // An LE is created by attacking a suspect/criminal in empire space. A legal war attack alone never creates one.
        var startsOrRenewsEngagement = canPair &&
            (alreadyEngaged || (IsEmpire(request.Where) && targetFlag is Flag.Suspect or Flag.Criminal));
        PolicyFacts policyFacts;
        if (AttackPolicyFactsOf is { } readPolicyFacts)
        {
            policyFacts = await readPolicyFacts(request, now, cancellationToken);
        }
        else
        {
            var activeWar = Legal is { } checkLegal &&
                await checkLegal(request.Attacker, request.Target, now, cancellationToken);
            policyFacts = new PolicyFacts(ActiveWar: activeWar);
        }
        policyFacts = policyFacts with
        {
            AttackerIsPlayer = request.Attacker > 0,
            TargetIsPlayerProperty = request.Target > 0,
            SamePilot = request.Attacker == request.Target,
            TargetIsCapsule = request.TargetIsCapsule,
            TargetFlag = targetFlag,
            ActiveLimitedEngagement = alreadyEngaged || policyFacts.ActiveLimitedEngagement,
        };
        AggressionOutcome? outcome = null;
        await store.UpdateAsync(request.Attacker, state => (outcome = Aggression.Record(
            state, targetState, request.Where, request.SolarSystemId, request.TargetIsCapsule, policyFacts, now)).Attacker, now, cancellationToken);
        var result = new AggressionResult(request, outcome!);
        try
        {
            if (startsOrRenewsEngagement)
            {
                await RefreshLimitedEngagementAsync(request.Attacker, request.Target, now, alreadyEngaged, cancellationToken);
            }
            if (result.Outcome.IsCriminalOffense)
            {
                await RecordCriminalConsequencesAsync(result.Request, result.Outcome.Attacker, now, cancellationToken);
            }
            if (Aggressed is { } handlers)
            {
                foreach (var handler in handlers.GetInvocationList().Cast<Func<AggressionResult, Task>>())
                {
                    await handler(result);
                }
            }
        }
        finally
        {
            await SyncAsync(request.Attacker, cancellationToken);
        }
        return result;
    }


    public async Task<PolicyDecision> EvaluateRemoteAssistanceAsync(
        AggressionRequest request,
        int safetyLevel,
        CancellationToken cancellationToken = default)
    {
        var now = nowUnixMs();
        var (recipient, facts) = await RemoteAssistanceFactsAsync(request, now, cancellationToken);
        return PolicyEngine.Evaluate(new PolicyRequest(PolicyAction.RemoteAssistance, request.Where, facts, safetyLevel));
    }

    public async Task<AggressionResult> RecordRemoteAssistanceAsync(
        AggressionRequest request,
        CancellationToken cancellationToken = default)
    {
        var now = nowUnixMs();
        var (recipient, facts) = await RemoteAssistanceFactsAsync(request, now, cancellationToken);
        AggressionOutcome? outcome = null;
        await store.UpdateAsync(request.Attacker, state =>
            (outcome = Aggression.Assist(state, recipient, request.Where, request.SolarSystemId, facts, now)).Attacker,
            now, cancellationToken);
        var result = new AggressionResult(request, outcome!);
        try
        {
            if (result.Outcome.IsCriminalOffense)
            {
                ScheduleConcordResponseForAggression(request, now);
            }
            if (Aggressed is { } handlers)
            {
                foreach (var handler in handlers.GetInvocationList().Cast<Func<AggressionResult, Task>>())
                    await handler(result);
            }
        }
        finally
        {
            await SyncAsync(request.Attacker, cancellationToken);
        }
        return result;
    }

    private async Task<(CombatState Recipient, PolicyFacts Facts)> RemoteAssistanceFactsAsync(
        AggressionRequest request,
        long now,
        CancellationToken cancellationToken)
    {
        var recipient = await store.StateOfAsync(request.Target, cancellationToken);
        PolicyFacts facts;
        if (AttackPolicyFactsOf is { } readPolicyFacts)
        {
            facts = await readPolicyFacts(request, now, cancellationToken);
        }
        else
        {
            var activeWar = Legal is { } checkLegal
                && await checkLegal(request.Attacker, request.Target, now, cancellationToken);
            facts = new PolicyFacts(ActiveWar: activeWar);
        }

        var assistantEngagements = request.Attacker > 0
            ? await LimitedEngagementsOfAsync(request.Attacker, now, cancellationToken)
            : new Dictionary<long, long>();
        var recipientEngagements = request.Target > 0
            ? await LimitedEngagementsOfAsync(request.Target, now, cancellationToken)
            : new Dictionary<long, long>();
        var sharedEngagements = recipientEngagements.Count != 0
            && recipientEngagements.Keys.All(assistantEngagements.ContainsKey);
        var war = WarAssistanceFactsOf is { } readWarFacts
            ? await readWarFacts(request.Attacker, request.Target, now, cancellationToken)
            : default;

        return (recipient, facts with
        {
            AttackerIsPlayer = request.Attacker > 0,
            TargetIsPlayerProperty = request.Target > 0,
            SamePilot = request.Attacker == request.Target,
            TargetIsCapsule = request.TargetIsCapsule,
            TargetFlag = recipient.FlagAt(now),
            ActiveLimitedEngagement = assistantEngagements.ContainsKey(request.Target),
            LimitedEngagementInterference = facts.LimitedEngagementInterference
                || (recipientEngagements.Count != 0 && !sharedEngagements),
            WarInterference = facts.WarInterference || (war.TargetInWar && !war.HelperInWar),
            OtherLegalPermission = facts.OtherLegalPermission || sharedEngagements || war.SameSide,
        });
    }

    private void ScheduleConcordResponseForAggression(AggressionRequest request, long now)
    {
        if (!Security.IsHighSecOrHigher(request.Where))
            return;
        var attackerShipId = sessions.Of(request.Attacker)?.GetInt("shipid") ?? 0;
        var response = new ConcordResponseRequest(
            request.Attacker,
            attackerShipId,
            request.SolarSystemId,
            request.Security,
            now);
        ScheduleConcordResponse(response, now + ConcordResponseDelayMs(request.Security), now);
    }


    private async Task RecordCriminalConsequencesAsync(
        AggressionRequest request,
        CombatState state,
        long now,
        CancellationToken cancellationToken)
    {
        if (CreateKillRight is { } createKillRight
            && request.Attacker > 0 && request.Target > 0 && request.Attacker != request.Target)
        {
            try
            {
                if (await createKillRight(new KillRightGrantRequest(
                        request.Target,
                        request.Attacker,
                        request.SolarSystemId,
                        now), cancellationToken) is { } killRightId)
                {
                    var created = new KillRightCreated(
                        killRightId,
                        request.Target,
                        request.Attacker,
                        request.SolarSystemId,
                        now);
                    if (KillRightGranted is { } handlers)
                    {
                        foreach (var handler in handlers.GetInvocationList().Cast<Func<KillRightCreated, Task>>())
                        {
                            try
                            {
                                await handler(created);
                            }
                            catch (Exception error)
                            {
                                log?.Invoke($"crimewatch could not notify of kill right {killRightId}: {error.Message}");
                            }
                        }
                    }
                }
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                log?.Invoke($"crimewatch could not persist a kill right for {request.Attacker}: {error.Message}");
            }
        }

        ScheduleConcordResponseForAggression(request, now);
    }

    /// <summary>Restores the delayed consequence for an active high-security criminal timer after a process restart.</summary>
    public async Task ResumeCriminalResponseAsync(
        long characterId,
        CancellationToken cancellationToken = default)
    {
        var now = nowUnixMs();
        var state = await store.StateOfAsync(characterId, cancellationToken);
        if (!state.IsCriminal(now) || state.SolarSystemId <= 0 || SecurityOfSystem is not { } securityOfSystem)
        {
            return;
        }
        var security = await securityOfSystem(state.SolarSystemId, cancellationToken);
        if (security is not { } level || level < 0.5)
        {
            return;
        }

        var attackerShipId = sessions.Of(characterId)?.GetInt("shipid") ?? 0;
        var response = new ConcordResponseRequest(characterId, attackerShipId, state.SolarSystemId, level, state.FlagUntilMs - CombatState.FlagDurationMs);
        var dueAt = response.CrimeAtUnixMs + ConcordResponseDelayMs(level);
        ScheduleConcordResponse(response, dueAt, now);
    }

    /// <summary>Applies CONCORD response when a criminal with an active timer enters high-security space.</summary>
    public async Task RespondToCriminalEntryAsync(
        long characterId,
        long solarSystemId,
        double security,
        CancellationToken cancellationToken = default)
    {
        if (characterId <= 0 || solarSystemId <= 0 || security < 0.5)
        {
            return;
        }
        var now = nowUnixMs();
        var state = await store.StateOfAsync(characterId, cancellationToken);
        if (!state.IsCriminal(now))
        {
            return;
        }
        var attackerShipId = sessions.Of(characterId)?.GetInt("shipid") ?? 0;
        ScheduleConcordResponse(
            new ConcordResponseRequest(characterId, attackerShipId, solarSystemId, security, now),
            now,
            now);
    }

    private void ScheduleConcordResponse(ConcordResponseRequest response, long dueAtUnixMs, long now)
    {
        if (!_concordResponses.TryAdd((response.AttackerCharacterId, response.SolarSystemId), 0))
        {
            return;
        }
        var delayMs = Math.Max(5, dueAtUnixMs - now + 5);
        _timers.Set(
            ConcordKey(response.AttackerCharacterId, response.SolarSystemId),
            TimeSpan.FromMilliseconds(delayMs),
            () => DispatchConcordResponseAsync(response));
    }

    private async Task DispatchConcordResponseAsync(ConcordResponseRequest response)
    {
        if (ConcordResponseDue is not { } handlers)
        {
            log?.Invoke($"crimewatch has no CONCORD response handler for {response.AttackerCharacterId} in {response.SolarSystemId}");
            return;
        }
        foreach (var handler in handlers.GetInvocationList().Cast<Func<ConcordResponseRequest, Task>>())
        {
            try
            {
                await handler(response);
            }
            catch (Exception error)
            {
                log?.Invoke($"CONCORD response for {response.AttackerCharacterId} in {response.SolarSystemId} failed: {error.Message}");
            }
        }
    }

    /// <summary>Estimated base delays from observed EVE University response times; CCP publishes no fixed response seconds.</summary>
    internal static long ConcordResponseDelayMs(double security) =>
        security >= 0.9 ? 6_000
        : security >= 0.8 ? 7_000
        : security >= 0.7 ? 10_000
        : security >= 0.6 ? 14_000
        : 19_000;

    private static string ConcordKey(long characterId, long solarSystemId) =>
        $"crime:concord:{characterId}:{solarSystemId}";

    /// <summary>Starts or extends the five-minute NPC timer and tells the pilot of the change.</summary>
    public Task RecordNpcTimerAsync(long characterId, CancellationToken cancellationToken = default) =>
        UpdateTimerAsync(
            characterId,
            (state, now) => state with { NpcUntilMs = Math.Max(state.NpcUntilMs, now + CombatState.NpcDurationMs) },
            cancellationToken);

    /// <summary>Starts or extends the five-minute disapproval timer and tells the pilot of the change.</summary>
    public Task RecordDisapprovalTimerAsync(long characterId, CancellationToken cancellationToken = default) =>
        UpdateTimerAsync(
            characterId,
            (state, now) => state with { DisapprovalUntilMs = Math.Max(state.DisapprovalUntilMs, now + CombatState.DisapprovalDurationMs) },
            cancellationToken);

    private async Task UpdateTimerAsync(
        long characterId,
        Func<CombatState, long, CombatState> update,
        CancellationToken cancellationToken)
    {
        if (characterId <= 0)
        {
            return;
        }
        var now = nowUnixMs();
        await store.UpdateAsync(characterId, state => update(state, now), now, cancellationToken);
        await SyncAsync(characterId, cancellationToken);
    }

    /// <summary>The pilot's state at this time (what <c>GetClientStates</c> answers from).</summary>
    public Task<CombatState> StateOfAsync(long characterId, CancellationToken cancellationToken = default) => store.StateOfAsync(characterId, cancellationToken);

    /// <summary>Tells the pilot's client the timers that are not what it was told, tells the system the flags that changed, and waits for the next end.</summary>
    public async Task SyncAsync(long characterId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var now = nowUnixMs();
            var state = await store.StateOfAsync(characterId, cancellationToken);
            var timers = state.TimersAt(now);
            var told = _told.GetValueOrDefault(characterId, CombatTimers.Idle);
            if (sessions.Of(characterId) is { } session)
            {
                Tell(session, "OnWeaponsTimerUpdate", told.Weapons, timers.Weapons);
                Tell(session, "OnPvpTimerUpdate", told.Pvp, timers.Pvp);
                Tell(session, "OnNpcTimerUpdate", told.Npc, timers.Npc);
                Tell(session, "OnCriminalTimerUpdate", told.Criminal, timers.Criminal);
                Tell(session, "OnDisapprovalTimerUpdate", told.Disapproval, timers.Disapproval);
            }
            if (timers == CombatTimers.Idle)
            {
                _told.TryRemove(characterId, out _);
            }
            else
            {
                _told[characterId] = timers;
            }
            await TellFlagsAsync(state.SolarSystemId, now, cancellationToken);
            if (state.NextEndAfter(now) is { } next)
            {
                // a moment after the end, so that what the clock says then is past it
                _timers.Set(Key(characterId), TimeSpan.FromMilliseconds(next - now + 5), () => SyncAsync(characterId));
            }
            else
            {
                _timers.Cancel(Key(characterId));
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>A process that starts: takes up the wait for the end of every timer and flag that still runs.</summary>
    public async Task ResumeAsync(CancellationToken cancellationToken = default)
    {
        var now = nowUnixMs();
        foreach (var engagement in await persistence.LimitedEngagementsAsync(now, null, cancellationToken))
        {
            if (engagement.ExpiresAtUnixMs > now)
            {
                ScheduleLimitedEngagementExpiry(engagement);
            }
        }
        foreach (var (characterId, state) in await store.ActiveAsync(now, cancellationToken))
        {
            await SyncAsync(characterId, cancellationToken);
            if (state.IsCriminal(now))
            {
                await ResumeCriminalResponseAsync(characterId, cancellationToken);
            }
        }
    }

    private static void Tell(ClientSession session, string name, TimerView was, TimerView now)
    {
        if (was != now)
        {
            session.SendNotification(name, "charid", new PyInt(now.State), Expiry(now));
        }
    }

    /// <summary>When a timer ends as the client is told it (a file time), or None while it is idle.</summary>
    internal static PyValue Expiry(TimerView timer) => timer.ExpiresAtMs is { } until ? new PyLong(Py.FileTimeFromUnixMs(until)) : PyNone.Instance;

    private async Task TellFlagsAsync(long solarSystemId, long now, CancellationToken cancellationToken)
    {
        if (solarSystemId <= 0)
        {
            return;
        }
        var (criminals, suspects) = await store.FlaggedInAsync(solarSystemId, now, cancellationToken);
        var (wasCriminals, wasSuspects) = _flagsTold.GetValueOrDefault(solarSystemId, ([], []));
        var idle = wasCriminals.Except(criminals).Concat(wasSuspects.Except(suspects)).Distinct().Order().ToList();
        var newSuspects = suspects.Except(wasSuspects).ToList();
        var newCriminals = criminals.Except(wasCriminals).ToList();
        if (criminals.Count == 0 && suspects.Count == 0)
        {
            _flagsTold.Remove(solarSystemId);
        }
        else
        {
            _flagsTold[solarSystemId] = (criminals, suspects);
        }
        if (idle.Count == 0 && newSuspects.Count == 0 && newCriminals.Count == 0)
        {
            return;
        }
        foreach (var session in sessions.InSpace(solarSystemId))
        {
            session.SendNotification("OnSystemCriminalFlagUpdates", "solarsystemid2", Ids(idle), Ids(newSuspects), Ids(newCriminals));
        }
    }

    private static PyValue Ids(IEnumerable<long> ids) => Py.Tuple([.. ids.Select(InventoryWire.Id)]);

    public void Dispose()
    {
        if (_ownsTimers) _timers.Dispose();
        _concordResponses.Clear();
    }
}
