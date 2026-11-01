using Eve.Macho;
using Eve.Marshal;
using Eve.Services;

namespace Eve.Services.Core;

public sealed record ProjectDiscoveryPlayerState(
    double Rank = 1,
    double Experience = 0,
    double AnalysisKredits = 0,
    bool FinishedTutorial = false,
    double TutorialLevel = 0);

public sealed record ProjectDiscoveryPlayerStatistics(string Message, int Code, string ProjectId);

public sealed record ProjectDiscoveryRpcCallbacks(
    Func<long, CancellationToken, ValueTask<ProjectDiscoveryPlayerState?>>? ReadPlayerStateAsync = null,
    Func<long, double?, CancellationToken, ValueTask>? FixTaskIdAsync = null,
    Func<long, CancellationToken, ValueTask<ProjectDiscoveryPlayerStatistics?>>? ReadPlayerStatisticsAsync = null,
    Func<long, string, CancellationToken, ValueTask<ProjectDiscoveryPlayerState?>>? TransitionTutorialAsync = null);

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
            case "is_enabled":
                return PyBool.True;

            case "get_project_id":
                return new PyInt(1);

            case "get_total_needed_xp":
            {
                var rank = NormalizeRank(JsSemantics.NumberValue(call.Arg(0)));
                var needed = (rank - 1) * rank * 50;
                if (!double.IsFinite(needed))
                    throw MachoErrors.ThrowUserError("PROJECT_DISCOVERY_RANK_INVALID");
                return Py.Number(needed);
            }

            case "get_tutorial_xp_reward":
                return new PyInt(TutorialXpReward);

            case "get_is_player_entitled_to_tutorial_reward":
            case "give_tutorial_rewards":
                return PyBool.False;

            case "fix_task_id":
                var taskId = FixedTaskId(call.Arg(0));
                if (characterId <= 0 || _callbacks.FixTaskIdAsync is not { } fixTaskId)
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
                    ("experience", Py.Number(NonNegativeInteger(state.Experience))),
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
                return PlayerStateValue(await TransitionTutorialAsync(characterId, "reset", call.Cancellation));

            case "skip_tutorial":
            {
                var state = await TransitionTutorialAsync(characterId, "skip", call.Cancellation);
                call.Session.AfterResponse(() =>
                {
                    call.Session.SendNotification("OnPlayerStateUpdated", "charid", PlayerStateValue(state));
                    call.Session.SendNotification("OnAnalysisKreditsChange", "charid");
                });
                return PlayerStateValue(state);
            }

            case "increase_tutorial_level":
                return PlayerStateValue(await TransitionTutorialAsync(characterId, "increase", call.Cancellation));

            default:
                return PyNone.Instance;
        }
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

    private async ValueTask<ProjectDiscoveryPlayerState> ReadStateAsync(long characterId, CancellationToken cancellationToken)
    {
        if (characterId > 0 && _callbacks.ReadPlayerStateAsync is { } readState &&
            await readState(characterId, cancellationToken) is { } state)
            return state;
        return new ProjectDiscoveryPlayerState();
    }

    private async ValueTask<ProjectDiscoveryPlayerState> TransitionTutorialAsync(long characterId, string action, CancellationToken cancellationToken)
    {
        if (characterId <= 0 || _callbacks.TransitionTutorialAsync is not { } transition)
            throw MachoErrors.ThrowUserError("PROJECT_DISCOVERY_TUTORIAL_UNAVAILABLE");
        if (await transition(characterId, action, cancellationToken) is not { } state)
            throw MachoErrors.ThrowUserError("PROJECT_DISCOVERY_TUTORIAL_UNAVAILABLE");
        return state;
    }

    private static PyValue PlayerStateValue(ProjectDiscoveryPlayerState state) => Py.KeyVal(
        ("rank", Py.Number(NormalizeRank(state.Rank))),
        ("experience", Py.Number(NonNegativeInteger(state.Experience))),
        ("analysisKredits", Py.Number(NonNegativeInteger(state.AnalysisKredits))),
        ("finishedTutorial", state.FinishedTutorial ? PyBool.True : PyBool.False),
        ("tutorialLevel", Py.Number(NonNegativeInteger(state.TutorialLevel))));

    private static KeyValuePair<PyValue, PyValue> Pair(string key, PyValue value) =>
        new(new PyStr(key), value);

    private static double NormalizeRank(double value) =>
        double.IsFinite(value) ? Math.Max(1, Math.Truncate(value)) : 1;

    private static double NonNegativeInteger(double value) =>
        double.IsFinite(value) ? Math.Max(0, Math.Truncate(value)) : 0;

    private static double? FixedTaskId(PyValue value) => value switch
    {
        PyInt integer when integer.Value > 0 => integer.Value,
        PyLong integer when integer.Value > 0 => integer.Value,
        _ => null,
    };
}
