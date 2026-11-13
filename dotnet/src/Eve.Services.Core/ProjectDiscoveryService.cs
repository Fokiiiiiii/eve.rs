using Eve.Macho;
using Eve.Marshal;
using Eve.Services;

namespace Eve.Services.Core;

public sealed record ProjectDiscoveryPlayerState(
    double Rank = 1,
    long Experience = 0,
    double AnalysisKredits = 0,
    bool FinishedTutorial = false,
    double TutorialLevel = 0);

public sealed record ProjectDiscoveryRewardGrant(bool Granted, decimal? WalletBalance, long? ItemId = null, long? ItemLocation = null);

public sealed record ProjectDiscoveryPlayerStatistics(string Message, int Code, string ProjectId, double? Score = null,
    string? LocalMode = null, int? Submissions = null, int? Correct = null, double? AverageScore = null);

public sealed record ProjectDiscoveryRpcCallbacks(
    Func<long, CancellationToken, ValueTask<ProjectDiscoveryPlayerState?>>? ReadPlayerStateAsync = null,
    Func<long, long?, CancellationToken, ValueTask>? FixTaskIdAsync = null,
    Func<long, CancellationToken, ValueTask<ProjectDiscoveryPlayerStatistics?>>? ReadPlayerStatisticsAsync = null,
    Func<long, string, CancellationToken, ValueTask<ProjectDiscoveryPlayerState?>>? TransitionTutorialAsync = null,
    Func<ServiceCall, ValueTask<PyValue>>? GetTaskAsync = null,
    Func<ServiceCall, ValueTask<PyValue>>? SubmitClassificationAsync = null,
    Func<long, CancellationToken, ValueTask<bool>>? ReadRewardEntitlementAsync = null,
    Func<ServiceCall, ValueTask<ProjectDiscoveryRewardGrant>>? GrantRewardAsync = null,
    Action<long, decimal>? AnnounceRewardWallet = null,
    Func<long>? ReadTutorialXpReward = null,
    Func<long, string, CancellationToken, ValueTask<int>>? ReadBonusSamplesAsync = null,
    Func<ServiceCall, ValueTask<PyValue>>? NewTrainingTaskAsync = null,
    Func<ServiceCall, string, ValueTask<ProjectDiscoveryPlayerState?>>? TransitionTutorialCallAsync = null,
    bool NotifyStateAfterSubmit = false,
    Func<int>? ReadProjectId = null,
    Action<ClientSession, long, long>? AnnounceRewardItem = null);

public sealed class ProjectDiscoveryService(ProjectDiscoveryRpcCallbacks? callbacks = null) : IService
{
    private const int PlayerNotInDatabaseErrorCode = 103010;
    private const string ActiveProjectId = "mcgill-polyseg";
    private const int TutorialLevels = 8;
    private const int TutorialXpReward = 100;
    private const string StatisticsUnavailableMessage =
        "Project Discovery API is not backed by live MMOS data in this emulator";

    private readonly ProjectDiscoveryRpcCallbacks _callbacks = callbacks ?? new();

    public string Name => "ProjectDiscovery";

    public bool HasMethod(string method) => method is
        "is_enabled" or
        "fix_task_id" or
        "get_new_task" or
        "get_maximum_bonus_samples" or
        "get_remaining_bonus_samples" or
        "new_training_task" or
        "post_classification" or
        "get_player_analysis_kredits" or
        "get_player_state" or
        "get_player_statistics" or
        "get_tutorial_completion_status" or
        "initialize_tutorial_status" or
        "get_project_id" or
        "get_total_needed_xp" or
        "get_tutorial_level" or
        "get_tutorial_xp_reward" or
        "get_is_player_entitled_to_tutorial_reward" or
        "give_tutorial_rewards" or
        "increase_tutorial_level" or
        "reset_tutorial" or
        "skip_tutorial";

    public async ValueTask<PyValue> CallAsync(ServiceCall call)
    {
        if (!HasMethod(call.Method)) return PyNone.Instance;
        call.Cancellation.ThrowIfCancellationRequested();

        var validArguments = call.Method switch
        {
            "fix_task_id" => call.Kwargs is PyNone && call.Args.Count == 1,
            "post_classification" => (call.Kwargs is PyNone or PyDict { Entries.Count: 0 } && call.Args.Count is 2 or 3 or 4) ||
                (call.Args.Count == 3 && call.Kwargs is PyDict { Entries.Count: 1 } remark && JsSemantics.StringValue(remark.Entries[0].Key) == "remark" && remark.Entries[0].Value is PyBool),
            "get_new_task" => HasValidTaskArguments(call),
            "new_training_task" => call.Kwargs is PyNone && call.Args.Count == 1 && call.Arg(0) is PyInt or PyLong,
            "get_player_statistics" => HasValidStatisticsArguments(call),
            "get_total_needed_xp" => call.Kwargs is PyNone && call.Args.Count == 1 && call.Arg(0) is PyInt or PyLong or PyReal,
            _ => call.Kwargs is PyNone && call.Args.Count == 0,
        };
        if (!validArguments)
            throw MachoErrors.ThrowUserError("PROJECT_DISCOVERY_ARGUMENTS_INVALID");
        if (call.Method == "fix_task_id" && call.Arg(0) is not (PyInt or PyLong))
            throw MachoErrors.ThrowUserError("PROJECT_DISCOVERY_TASK_ID_INVALID");

        var characterId = call.Session.CharacterId.GetValueOrDefault();
        switch (call.Method)
        {
            case "get_maximum_bonus_samples":
            case "get_remaining_bonus_samples":
                if (characterId <= 0 || _callbacks.ReadBonusSamplesAsync is not { } bonus) throw MachoErrors.ThrowUserError("PROJECT_DISCOVERY_BONUS_UNAVAILABLE");
                return new PyInt(await bonus(characterId, call.Method, call.Cancellation));
            case "new_training_task":
                return _callbacks.NewTrainingTaskAsync is { } training ? await training(call) : throw MachoErrors.ThrowUserError("PROJECT_DISCOVERY_TRAINING_UNAVAILABLE");
            case "get_new_task":
                return _callbacks.GetTaskAsync is { } getTask
                    ? await getTask(call)
                    : throw MachoErrors.ThrowUserError("PROJECT_DISCOVERY_TASK_UNAVAILABLE");

            case "post_classification":
            {
                if (_callbacks.SubmitClassificationAsync is not { } submit) throw MachoErrors.ThrowUserError("PROJECT_DISCOVERY_TASK_UNAVAILABLE");
                var response = await submit(call);
                if (_callbacks.NotifyStateAfterSubmit && characterId > 0)
                {
                    ProjectDiscoveryPlayerState? state = null;
                    try { state = await ReadStateAsync(characterId, call.Cancellation); }
                    catch (Exception error) { Console.Error.WriteLine($"Project Discovery state notification read failed: {error.GetType().Name}"); }
                    call.Session.AfterResponse(() =>
                    {
                        if (state is not null) SafeNotify(() => call.Session.SendNotification("OnPlayerStateUpdated", "clientID", PlayerStateValue(state)));
                        SafeNotify(() => call.Session.SendNotification("OnAnalysisKreditsChange", "charid"));
                    });
                }
                return response;
            }

            case "is_enabled":
                return PyBool.True;

            case "get_project_id":
                return new PyInt(_callbacks.ReadProjectId?.Invoke() ?? 1);

            case "get_total_needed_xp":
            {
                var rank = NormalizeRank(JsSemantics.NumberValue(call.Arg(0)));
                var needed = (rank - 1) * rank * 50;
                if (!double.IsFinite(needed))
                    throw MachoErrors.ThrowUserError("PROJECT_DISCOVERY_RANK_INVALID");
                return Py.Number(needed);
            }

            case "get_tutorial_xp_reward":
                return Py.Number(_callbacks.ReadTutorialXpReward?.Invoke() ?? TutorialXpReward);

            case "get_is_player_entitled_to_tutorial_reward":
                return characterId > 0 && _callbacks.ReadRewardEntitlementAsync is { } entitled && await entitled(characterId, call.Cancellation) ? PyBool.True : PyBool.False;
            case "give_tutorial_rewards":
            {
                if (characterId <= 0 || _callbacks.GrantRewardAsync is not { } grant) return PyBool.False;
                var reward = await grant(call);
                if (reward.Granted)
                {
                    ProjectDiscoveryPlayerState? state = null;
                    try { state = await ReadStateAsync(characterId, call.Cancellation); }
                    catch (Exception error) { Console.Error.WriteLine($"Project Discovery state notification read failed: {error.GetType().Name}"); }
                    call.Session.AfterResponse(() =>
                    {
                        if (reward.WalletBalance is { } balance) SafeNotify(() => _callbacks.AnnounceRewardWallet?.Invoke(characterId, balance));
                        if (reward.ItemId is { } item && reward.ItemLocation is { } location) SafeNotify(() => _callbacks.AnnounceRewardItem?.Invoke(call.Session, item, location));
                        if (state is not null) SafeNotify(() => call.Session.SendNotification("OnPlayerStateUpdated", "clientID", PlayerStateValue(state)));
                        SafeNotify(() => call.Session.SendNotification("OnAnalysisKreditsChange", "charid"));
                    });
                }
                return reward.Granted ? PyBool.True : PyBool.False;
            }

            case "fix_task_id":
                var taskId = FixedTaskId(call.Arg(0));
                if (characterId <= 0)
                    throw MachoErrors.ThrowUserError("PROJECT_DISCOVERY_TASK_FIX_UNAVAILABLE");
                if (!PublicQaToolsServerService.HasStaffAccountRole(call.Session))
                    throw MachoErrors.ThrowUserError("PROJECT_DISCOVERY_TASK_FIX_UNAUTHORIZED");
                if (_callbacks.FixTaskIdAsync is not { } fixTaskId)
                    throw MachoErrors.ThrowUserError("PROJECT_DISCOVERY_TASK_FIX_UNAVAILABLE");
                await fixTaskId(characterId, taskId, call.Cancellation);
                return PyNone.Instance;

            case "get_player_analysis_kredits":
            {
                var state = await ReadStateAsync(characterId, call.Cancellation);
                return Py.Number(NonNegativeInteger(state.AnalysisKredits));
            }

            case "get_player_state":
            {
                var state = await ReadStateAsync(characterId, call.Cancellation);
                return Py.KeyVal(
                    ("rank", Py.Number(NormalizeRank(state.Rank))),
                    ("experience", new PyInt(Math.Max(0L, state.Experience))),
                    ("analysisKredits", Py.Number(NonNegativeInteger(state.AnalysisKredits))),
                    ("finishedTutorial", state.FinishedTutorial ? PyBool.True : PyBool.False),
                    ("tutorialLevel", Py.Number(NonNegativeInteger(state.TutorialLevel))));
            }

            case "get_player_statistics":
            {
                var statistics = characterId > 0 && _callbacks.ReadPlayerStatisticsAsync is { } readStatistics
                    ? await readStatistics(characterId, call.Cancellation)
                    : null;
                statistics ??= new ProjectDiscoveryPlayerStatistics(
                    StatisticsUnavailableMessage,
                    PlayerNotInDatabaseErrorCode,
                    ActiveProjectId);
                if (statistics.Score is { } score)
                {
                    var project = new PyDict([Pair("score", new PyReal(score))]);
                    var entries = new List<KeyValuePair<PyValue, PyValue>>
                    {
                        Pair("project", project),
                        Pair("projects", new PyList([project])),
                    };
                    if (statistics.LocalMode is { } localMode)
                    {
                        entries.Insert(0, Pair("localMode", new PyStr(localMode)));
                        entries.Insert(1, Pair("projectID", new PyStr(statistics.ProjectId)));
                        entries.Add(Pair("local", new PyDict(
                        [
                            Pair("submissions", new PyInt(statistics.Submissions.GetValueOrDefault())),
                            Pair("correct", new PyInt(statistics.Correct.GetValueOrDefault())),
                            Pair("averageScore", new PyReal(statistics.AverageScore.GetValueOrDefault())),
                        ])));
                    }
                    return new PyDict(entries);
                }
                return new PyDict(
                [
                    Pair("message", new PyStr(statistics.Message)),
                    Pair("code", new PyInt(statistics.Code)),
                    Pair("projectID", new PyStr(statistics.ProjectId)),
                ]);
            }

            case "get_tutorial_completion_status":
            case "initialize_tutorial_status":
            {
                var state = await ReadStateAsync(characterId, call.Cancellation);
                return state.FinishedTutorial ? PyBool.True : PyBool.False;
            }

            case "get_tutorial_level":
            {
                var state = await ReadStateAsync(characterId, call.Cancellation);
                return Py.Number(NonNegativeInteger(state.TutorialLevel));
            }

            case "reset_tutorial":
                return PlayerStateValue(await TransitionTutorialAsync(call, "reset"));

            case "skip_tutorial":
            {
                var state = await TransitionTutorialAsync(call, "skip");
                call.Session.AfterResponse(() =>
                {
                    call.Session.SendNotification("OnPlayerStateUpdated", "clientID", PlayerStateValue(state));
                    call.Session.SendNotification("OnAnalysisKreditsChange", "charid");
                });
                return PlayerStateValue(state);
            }

            case "increase_tutorial_level":
                return PlayerStateValue(await TransitionTutorialAsync(call, "increase"));

            default:
                return PyNone.Instance;
        }
    }

    private static void SafeNotify(Action action)
    {
        try { action(); }
        catch (Exception error) { Console.Error.WriteLine($"Project Discovery notification failed: {error.GetType().Name}"); }
    }

    private static bool HasValidStatisticsArguments(ServiceCall call) =>
        call.Args.Count switch
        {
            0 when call.Kwargs is PyNone => true,
            0 when call.Kwargs is PyDict { Entries.Count: 1 } kwargs =>
                JsSemantics.StringValue(kwargs.Entries[0].Key) == "get_history" &&
                kwargs.Entries[0].Value is PyBool,
            1 when call.Kwargs is PyNone => call.Arg(0) is PyBool,
            _ => false,
        };

    private static bool HasValidTaskArguments(ServiceCall call)
    {
        if (call.Args.Count != 0) return false;
        if (call.Kwargs is PyNone or PyDict { Entries.Count: 0 }) return true;
        if (call.Kwargs is not PyDict { Entries.Count: 2 } kwargs) return false;
        string? code = null;
        bool isTutorial = false;
        foreach (var (key, value) in kwargs.Entries)
        {
            switch (JsSemantics.StringValue(key))
            {
                case "original_code" when value is PyStr or PyWString or PyBytes:
                    code = JsSemantics.StringValue(value);
                    break;
                case "is_tutorial_task" when value is PyBool flag:
                    isTutorial = flag.Value;
                    break;
                default:
                    return false;
            }
        }
        return !string.IsNullOrWhiteSpace(code) && isTutorial;
    }

    private async ValueTask<ProjectDiscoveryPlayerState> ReadStateAsync(long characterId, CancellationToken cancellationToken)
    {
        if (characterId > 0 && _callbacks.ReadPlayerStateAsync is { } readState &&
            await readState(characterId, cancellationToken) is { } state)
            return state;
        return new ProjectDiscoveryPlayerState();
    }

    private async ValueTask<ProjectDiscoveryPlayerState> TransitionTutorialAsync(ServiceCall call, string action)
    {
        long characterId = call.Session.CharacterId.GetValueOrDefault();
        var cancellationToken = call.Cancellation;
        if (characterId > 0 && _callbacks.TransitionTutorialCallAsync is { } sessionTransition)
            return await sessionTransition(call, action) ?? throw MachoErrors.ThrowUserError("PROJECT_DISCOVERY_TUTORIAL_UNAVAILABLE");
        if (characterId <= 0 || _callbacks.TransitionTutorialAsync is not { } transition)
            throw MachoErrors.ThrowUserError("PROJECT_DISCOVERY_TUTORIAL_UNAVAILABLE");
        if (await transition(characterId, action, cancellationToken) is not { } state)
            throw MachoErrors.ThrowUserError("PROJECT_DISCOVERY_TUTORIAL_UNAVAILABLE");
        return state;
    }

    private static PyValue PlayerStateValue(ProjectDiscoveryPlayerState state) => Py.KeyVal(
        ("rank", Py.Number(NormalizeRank(state.Rank))),
        ("experience", new PyInt(Math.Max(0L, state.Experience))),
        ("analysisKredits", Py.Number(NonNegativeInteger(state.AnalysisKredits))),
        ("finishedTutorial", state.FinishedTutorial ? PyBool.True : PyBool.False),
        ("tutorialLevel", Py.Number(NonNegativeInteger(state.TutorialLevel))));

    private static KeyValuePair<PyValue, PyValue> Pair(string key, PyValue value) =>
        new(new PyStr(key), value);

    private static double NormalizeRank(double value) =>
        double.IsFinite(value) ? Math.Max(1, Math.Truncate(value)) : 1;

    private static double NonNegativeInteger(double value) =>
        double.IsFinite(value) ? Math.Max(0, Math.Truncate(value)) : 0;

    private static long? FixedTaskId(PyValue value) => value switch
    {
        PyInt integer when integer.Value > 0 => integer.Value,
        PyLong integer when integer.Value > 0 => integer.Value,
        _ => null,
    };
}
