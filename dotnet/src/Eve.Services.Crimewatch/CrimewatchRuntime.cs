using System.Collections.Concurrent;
using Eve.Crimewatch;
using Eve.Marshal;
using Eve.Scheduler;
using Eve.Universe;
using Npgsql;

namespace Eve.Services.Crimewatch;

public sealed class CrimewatchRuntime(PgCrimewatchStore store, CharacterSessions sessions, Func<long> nowUnixMs, ICrimewatchPersistenceCallbacks persistence, Action<string>? log = null, Func<TimeSpan, CancellationToken, Task>? delay = null, LocalTimers? localTimers = null) : IDisposable
{
    private readonly bool _ownsTimers = localTimers is null;
    private readonly LocalTimers _timers = localTimers ?? new(log, delay);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _engagementGate = new(1, 1);
    private readonly ConcurrentDictionary<long, CombatTimers> _told = new();
    private readonly Dictionary<long, (IReadOnlyList<long> Criminals, IReadOnlyList<long> Suspects)> _flagsTold = [];
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(long CharacterId, long SolarSystemId), byte> _concordResponses = new();
    private readonly ConcurrentDictionary<(long CharacterId, long ModuleId), ModuleCombatActivity> _moduleCombatActivities = new();
    private readonly ConcurrentDictionary<(long FirstCharacterId, long SecondCharacterId), byte> _ongoingEngagements = new();
    private bool _moduleCombatRefreshScheduled;

    private sealed record ModuleCombatActivity(
        long AttackerCharacterId,
        long TargetCharacterId,
        long SolarSystemId,
        AggressionReason Reason);

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
    public const long LimitedEngagementTimeoutOngoing = -1;

    private static string Key(long characterId) => $"crimewatch.end:{characterId}";

    /// <summary>
    /// A pilot attacked the ship of another pilot: the attacker gets weapons and PvP timers, and both pilots get PvP timers. Any crime consequences (<see cref="Aggressed"/>) run before the clients are told.
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
                result[engagement.SecondCharacterID] = _ongoingEngagements.ContainsKey(Pair(characterId, engagement.SecondCharacterID))
                    ? LimitedEngagementTimeoutOngoing
                    : engagement.ExpiresAtUnixMs;
            }
            else if (engagement.SecondCharacterID == characterId)
            {
                result[engagement.FirstCharacterID] = _ongoingEngagements.ContainsKey(Pair(characterId, engagement.FirstCharacterID))
                    ? LimitedEngagementTimeoutOngoing
                    : engagement.ExpiresAtUnixMs;
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
        await _engagementGate.WaitAsync(cancellationToken);
        try
        {
            ScheduleLimitedEngagementExpiry(engagement);
            TellEngagement(engagement, created: true);
            if (HasModuleCombatActivityForPair(first, second))
            {
                MarkEngagementOngoing(first, second);
            }
        }
        finally
        {
            _engagementGate.Release();
        }
        return true;
    }

    private async Task RefreshLimitedEngagementAsync(
        long firstCharacterId,
        long secondCharacterId,
        long nowUnixMs,
        CancellationToken cancellationToken)
    {
        var (first, second) = Pair(firstCharacterId, secondCharacterId);
        await _engagementGate.WaitAsync(cancellationToken);
        try
        {
            var exists = (await persistence.LimitedEngagementsAsync(nowUnixMs, first, cancellationToken))
                .Any(row => row.FirstCharacterID == first && row.SecondCharacterID == second && row.ExpiresAtUnixMs > nowUnixMs);
            var engagement = new LimitedEngagement(first, second, nowUnixMs + LimitedEngagementDurationMs);
            await persistence.UpsertLimitedEngagementAsync(first, second, engagement.ExpiresAtUnixMs, cancellationToken);
            ScheduleLimitedEngagementExpiry(engagement);
            if (!exists)
            {
                TellEngagement(engagement, created: true);
            }
            else if (!_ongoingEngagements.ContainsKey((first, second)))
            {
                TellEngagement(engagement, created: false);
            }
            if (HasModuleCombatActivityForPair(first, second))
            {
                MarkEngagementOngoing(first, second);
            }
        }
        finally
        {
            _engagementGate.Release();
        }
    }

    private void ScheduleLimitedEngagementExpiry(LimitedEngagement engagement)
    {
        var (first, second) = Pair(engagement.FirstCharacterID, engagement.SecondCharacterID);
        if (_ongoingEngagements.ContainsKey((first, second)))
        {
            _timers.Cancel(EngagementKey(first, second));
            return;
        }
        var afterMs = Math.Max(5, engagement.ExpiresAtUnixMs - nowUnixMs() + 5);
        _timers.Set(EngagementKey(first, second), TimeSpan.FromMilliseconds(afterMs),
            () => ExpireLimitedEngagementAsync(first, second, engagement.ExpiresAtUnixMs));
    }

    private async Task ExpireLimitedEngagementAsync(long firstCharacterId, long secondCharacterId, long expectedExpiresAtUnixMs)
    {
        var now = nowUnixMs();
        var (first, second) = Pair(firstCharacterId, secondCharacterId);
        await _engagementGate.WaitAsync(CancellationToken.None);
        try
        {
            if (_ongoingEngagements.ContainsKey((first, second)))
            {
                await persistence.UpsertLimitedEngagementAsync(
                    first, second, now + LimitedEngagementDurationMs, CancellationToken.None);
                return;
            }

            if (await persistence.ExpireLimitedEngagementAsync(
                    first, second, expectedExpiresAtUnixMs, now, CancellationToken.None))
            {
                TellEngagementEnded(first, second);
                return;
            }

            foreach (var active in await persistence.LimitedEngagementsAsync(now, first, CancellationToken.None))
            {
                if (active.FirstCharacterID == first && active.SecondCharacterID == second && active.ExpiresAtUnixMs > now)
                {
                    ScheduleLimitedEngagementExpiry(active);
                    return;
                }
            }
        }
        finally
        {
            _engagementGate.Release();
        }
    }

    private void TellEngagement(LimitedEngagement engagement, bool created)
    {
        var method = created ? "OnCrimewatchEngagementCreated" : "OnCrimewatchEngagementStartTimeout";
        var timeout = new PyLong(Py.FileTimeFromUnixMs(engagement.ExpiresAtUnixMs));
        sessions.Of(engagement.FirstCharacterID)?.SendNotification(method, "charid", InventoryWire.Id(engagement.SecondCharacterID), timeout);
        sessions.Of(engagement.SecondCharacterID)?.SendNotification(method, "charid", InventoryWire.Id(engagement.FirstCharacterID), timeout);
    }

    private void MarkEngagementOngoing(long firstCharacterId, long secondCharacterId)
    {
        var (first, second) = Pair(firstCharacterId, secondCharacterId);
        if (!_ongoingEngagements.TryAdd((first, second), 0))
        {
            return;
        }
        _timers.Cancel(EngagementKey(first, second));
        sessions.Of(first)?.SendNotification("OnCrimewatchEngagementStopTimeout", "charid", InventoryWire.Id(second));
        sessions.Of(second)?.SendNotification("OnCrimewatchEngagementStopTimeout", "charid", InventoryWire.Id(first));
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

    private const string ModuleCombatKey = "crimewatch.module";

    public Task<AggressionResult> RecordAggressionAsync(AggressionRequest request, CancellationToken cancellationToken = default) =>
        RecordAggressionAsync(request, moduleId: null, cancellationToken);

    /// <summary>Records an attack from an accepted offensive module and keeps its combat timers active until the simulation ends that module.</summary>
    public Task<AggressionResult> RecordModuleAggressionAsync(
        AggressionRequest request,
        long moduleId,
        CancellationToken cancellationToken = default) =>
        RecordAggressionAsync(request, moduleId > 0 ? moduleId : null, cancellationToken);

    private async Task<AggressionResult> RecordAggressionAsync(
        AggressionRequest request,
        long? moduleId,
        CancellationToken cancellationToken)
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
        if (moduleId is { } acceptedModuleId && canPair)
        {
            await _engagementGate.WaitAsync(cancellationToken);
            try
            {
                if (_moduleCombatActivities.TryAdd((request.Attacker, acceptedModuleId),
                    new ModuleCombatActivity(request.Attacker, request.Target, request.SolarSystemId, result.Outcome.Reason)))
                {
                    ScheduleModuleCombatActivityRefresh();
                }
            }
            finally
            {
                _engagementGate.Release();
            }
        }
        var receiverTimerCommitted = false;
        try
        {
            if (canPair && result.Outcome.Target is { } targetAfter)
            {
                await store.UpdateAsync(request.Target,
                    state => state with { PvpUntilMs = Math.Max(state.PvpUntilMs, targetAfter.PvpUntilMs) },
                    now,
                    cancellationToken);
                receiverTimerCommitted = true;
            }
            if (startsOrRenewsEngagement)
            {
                await RefreshLimitedEngagementAsync(request.Attacker, request.Target, now, cancellationToken);
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
            try
            {
                await SyncAsync(request.Attacker, cancellationToken);
            }
            finally
            {
                if (receiverTimerCommitted)
                {
                    await SyncAsync(request.Target, cancellationToken);
                }
            }
        }
        return result;
    }

    /// <summary>Ends the combat activity when the simulation reports that its module has completed its final cycle.</summary>
    public async Task EndModuleCombatActivityAsync(
        long characterId,
        long moduleId,
        CancellationToken cancellationToken = default)
    {
        if (characterId <= 0 || moduleId <= 0)
        {
            return;
        }

        ModuleCombatActivity activity;
        await _engagementGate.WaitAsync(cancellationToken);
        try
        {
            if (!_moduleCombatActivities.TryRemove((characterId, moduleId), out var endedActivity))
            {
                return;
            }
            activity = endedActivity;

            var now = nowUnixMs();
            await UpdateModuleCombatTimersAsync([activity], now, cancellationToken);

            var (first, second) = Pair(activity.AttackerCharacterId, activity.TargetCharacterId);
            if (_ongoingEngagements.ContainsKey((first, second)))
            {
                var exists = (await persistence.LimitedEngagementsAsync(now, first, cancellationToken))
                    .Any(row => row.FirstCharacterID == first && row.SecondCharacterID == second && row.ExpiresAtUnixMs > now);
                if (exists)
                {
                    var engagement = new LimitedEngagement(first, second, now + LimitedEngagementDurationMs);
                    await persistence.UpsertLimitedEngagementAsync(first, second, engagement.ExpiresAtUnixMs, cancellationToken);
                    if (HasModuleCombatActivityForPair(first, second))
                    {
                        MarkEngagementOngoing(first, second);
                        ScheduleLimitedEngagementExpiry(engagement);
                    }
                    else
                    {
                        _ongoingEngagements.TryRemove((first, second), out _);
                        ScheduleLimitedEngagementExpiry(engagement);
                        TellEngagement(engagement, created: false);
                    }
                }
                else
                {
                    _ongoingEngagements.TryRemove((first, second), out _);
                }
            }

            if (_moduleCombatActivities.IsEmpty)
            {
                _moduleCombatRefreshScheduled = false;
                _timers.Cancel(ModuleCombatKey);
            }
            else
            {
                ScheduleModuleCombatActivityRefresh();
            }
        }
        finally
        {
            _engagementGate.Release();
        }

        await SyncAsync(activity.AttackerCharacterId, cancellationToken);
        if (activity.TargetCharacterId != activity.AttackerCharacterId)
        {
            await SyncAsync(activity.TargetCharacterId, cancellationToken);
        }
    }

    private void ScheduleModuleCombatActivityRefresh()
    {
        if (_moduleCombatRefreshScheduled || _moduleCombatActivities.IsEmpty)
        {
            return;
        }
        _moduleCombatRefreshScheduled = true;
        _timers.Set(ModuleCombatKey, TimeSpan.FromSeconds(1), RefreshModuleCombatActivitiesAsync);
    }

    private async Task RefreshModuleCombatActivitiesAsync()
    {
        await _engagementGate.WaitAsync(CancellationToken.None);
        try
        {
            _moduleCombatRefreshScheduled = false;
            var activities = _moduleCombatActivities.Values.ToArray();
            if (activities.Length == 0)
            {
                return;
            }

            var now = nowUnixMs();
            await UpdateModuleCombatTimersAsync(activities, now, CancellationToken.None);
            var activePairs = activities
                .Select(activity => Pair(activity.AttackerCharacterId, activity.TargetCharacterId))
                .Distinct()
                .Where(pair => _ongoingEngagements.ContainsKey(pair))
                .OrderBy(pair => pair.First)
                .ThenBy(pair => pair.Second);
            foreach (var (first, second) in activePairs)
            {
                await persistence.UpsertLimitedEngagementAsync(
                    first, second, now + LimitedEngagementDurationMs, CancellationToken.None);
            }
        }
        finally
        {
            if (!_moduleCombatActivities.IsEmpty)
            {
                ScheduleModuleCombatActivityRefresh();
            }
            _engagementGate.Release();
        }
    }

    private async Task UpdateModuleCombatTimersAsync(
        IReadOnlyCollection<ModuleCombatActivity> activities,
        long now,
        CancellationToken cancellationToken)
    {
        var participants = new Dictionary<long, (List<ModuleCombatActivity> All, List<ModuleCombatActivity> Attacks)>();
        foreach (var activity in activities)
        {
            if (!participants.TryGetValue(activity.AttackerCharacterId, out var attacker))
            {
                attacker = ([], []);
                participants.Add(activity.AttackerCharacterId, attacker);
            }
            attacker.All.Add(activity);
            attacker.Attacks.Add(activity);

            if (!participants.TryGetValue(activity.TargetCharacterId, out var target))
            {
                target = ([], []);
                participants.Add(activity.TargetCharacterId, target);
            }
            target.All.Add(activity);
        }

        foreach (var (characterId, (all, attacks)) in participants.OrderBy(entry => entry.Key))
        {
            var criminal = attacks.Any(activity => activity.Reason == AggressionReason.Criminal);
            var suspect = attacks.Any(activity => activity.Reason == AggressionReason.Suspect);
            var systemId = attacks.FirstOrDefault()?.SolarSystemId ?? all[0].SolarSystemId;
            await store.UpdateAsync(characterId, state =>
            {
                var flag = criminal || (suspect && state.FlagAt(now) != Flag.Criminal)
                    ? criminal ? Flag.Criminal : Flag.Suspect
                    : state.Flag;
                return state with
                {
                    WeaponsUntilMs = attacks.Count > 0
                        ? Math.Max(state.WeaponsUntilMs, now + CombatState.WeaponsDurationMs)
                        : state.WeaponsUntilMs,
                    PvpUntilMs = Math.Max(state.PvpUntilMs, now + CombatState.PvpDurationMs),
                    Flag = flag,
                    FlagUntilMs = criminal || suspect
                        ? Math.Max(state.FlagUntilMs, now + CombatState.FlagDurationMs)
                        : state.FlagUntilMs,
                    SolarSystemId = systemId,
                };
            }, now, cancellationToken);
        }
    }

    private static bool SamePair(ModuleCombatActivity activity, long firstCharacterId, long secondCharacterId) =>
        Pair(activity.AttackerCharacterId, activity.TargetCharacterId) == (firstCharacterId, secondCharacterId);

    private bool HasModuleCombatActivityForPair(long firstCharacterId, long secondCharacterId) =>
        _moduleCombatActivities.Values.Any(activity => SamePair(activity, firstCharacterId, secondCharacterId));

    /// <summary>Applies the persisted kill-right activation to the target and tells the activator.</summary>
    public async Task ApplyKillRightActivationAsync(
        KillRightActivation activation,
        long solarSystemId,
        CancellationToken cancellationToken = default)
    {
        if (activation.TargetCharacterId <= 0 || activation.ActivatorCharacterId <= 0)
        {
            return;
        }
        await UpdateTimerAsync(
            activation.TargetCharacterId,
            (state, now) => state.FlagAsSuspect(now) with
            {
                SolarSystemId = solarSystemId > 0 ? solarSystemId : state.SolarSystemId,
            },
            cancellationToken);
        sessions.Of(activation.ActivatorCharacterId)?.SendNotification("OnKillRightActivated", "clientID");
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
        var result = new AggressionResult(request, outcome!, PolicyAction.RemoteAssistance);
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

    /// <summary>
    /// </summary>
    public async Task<long?> GrantKillRightAsync(long ownerCharacterId, long targetCharacterId, long solarSystemId, long now, CancellationToken cancellationToken)
    {
        if (CreateKillRight is not { } createKillRight) return null;
        if (await createKillRight(new KillRightGrantRequest(ownerCharacterId, targetCharacterId, solarSystemId, now), cancellationToken) is not { } killRightId)
            return null;
        var created = new KillRightCreated(killRightId, ownerCharacterId, targetCharacterId, solarSystemId, now);
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
        return killRightId;
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
    public async Task OnSystemEnteredAsync(
        long characterId,
        long solarSystemId,
        double security,
        CancellationToken cancellationToken = default)
    {
        if (characterId <= 0 || solarSystemId <= 0)
        {
            return;
        }

        var now = nowUnixMs();
        var state = await store.StateOfAsync(characterId, cancellationToken);
        if (state.FlagAt(now) != Flag.None)
        {
            var (before, after) = await store.UpdateAsync(
                characterId,
                current => current.FlagAt(now) == Flag.None ? current : current with { SolarSystemId = solarSystemId },
                now,
                cancellationToken);
            state = after;
            if (before.FlagAt(now) != Flag.None && after.FlagAt(now) != Flag.None && before.SolarSystemId != after.SolarSystemId)
            {
                await TellFlagsAsync(before.SolarSystemId, now, cancellationToken);
                await TellFlagsAsync(after.SolarSystemId, now, cancellationToken);
            }
        }

        var securityClass = Security.ClassFromLevel(SecurityPenalty.PseudoSecurity(security));
        if (!Security.IsHighSecOrHigher(securityClass) || !state.IsCriminal(now)) return;
        var attackerShipId = sessions.Of(characterId)?.GetInt("shipid") ?? 0;
        ScheduleConcordResponse(
            new ConcordResponseRequest(characterId, attackerShipId, solarSystemId, security, now),
            now + ConcordResponseDelayMs(security),
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
        try
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
        finally
        {
            _concordResponses.TryRemove((response.AttackerCharacterId, response.SolarSystemId), out _);
        }
    }

    internal static long ConcordResponseDelayMs(double security)
    {
        var displayedSecurity = Math.Round(SecurityPenalty.PseudoSecurity(security), 1, MidpointRounding.AwayFromZero);
        return displayedSecurity >= 0.9 ? 6_000
            : displayedSecurity >= 0.8 ? 7_000
            : displayedSecurity >= 0.7 ? 10_000
            : displayedSecurity >= 0.6 ? 14_000
            : displayedSecurity >= 0.5 ? 19_000
            : 0;
    }

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

    /// <summary>The timers and active-module states returned by the bound client Crimewatch object.</summary>
    public async Task<CombatTimers> ClientTimersOfAsync(long characterId, CancellationToken cancellationToken = default)
    {
        var now = nowUnixMs();
        var state = characterId > 0
            ? await store.StateOfAsync(characterId, cancellationToken)
            : CombatState.Idle;
        return TimersFor(characterId, state, now);
    }

    private CombatTimers TimersFor(long characterId, CombatState state, long now)
    {
        var timers = state.TimersAt(now);
        if (characterId <= 0)
        {
            return timers;
        }

        var activities = _moduleCombatActivities.Values
            .Where(activity => activity.AttackerCharacterId == characterId || activity.TargetCharacterId == characterId)
            .ToArray();
        if (activities.Length == 0)
        {
            return timers;
        }

        var attacking = activities.Where(activity => activity.AttackerCharacterId == characterId).ToArray();
        var weapons = attacking.Length > 0
            ? new TimerView(CombatState.WeaponsActive, null)
            : timers.Weapons;
        var pvp = new TimerView(CombatState.PvpActive, null);
        var activeOffense = attacking.Any(activity => activity.Reason is AggressionReason.Criminal or AggressionReason.Suspect);
        var flag = state.FlagAt(now);
        var criminal = activeOffense
            ? flag switch
            {
                Flag.Criminal => new TimerView(CombatState.CriminalActive, null),
                Flag.Suspect => new TimerView(CombatState.SuspectActive, null),
                _ => timers.Criminal,
            }
            : timers.Criminal;
        return timers with { Weapons = weapons, Pvp = pvp, Criminal = criminal };
    }

      internal Task<bool> TryRememberSecurityStatusPenaltyAsync(
          long attackerCharacterId,
          long targetItemId,
          bool targetIsCapsule,
          long expiresAtUnixMs,
          long nowUnixMs,
            Func<NpgsqlConnection, NpgsqlTransaction, CancellationToken, Task> applyPenalty,
          CancellationToken cancellationToken = default) =>
            store.TryRememberSecurityStatusPenaltyAsync(attackerCharacterId, targetItemId, targetIsCapsule, expiresAtUnixMs, nowUnixMs, applyPenalty, cancellationToken);

    /// <summary>Tells the pilot's client the timers that are not what it was told, tells the system the flags that changed, and waits for the next end.</summary>
    public async Task SyncAsync(long characterId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var now = nowUnixMs();
            var state = await store.StateOfAsync(characterId, cancellationToken);
            var timers = TimersFor(characterId, state, now);
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
        _timers.Cancel(ModuleCombatKey);
        _moduleCombatRefreshScheduled = false;
        _moduleCombatActivities.Clear();
        _ongoingEngagements.Clear();
        _concordResponses.Clear();
    }
}
