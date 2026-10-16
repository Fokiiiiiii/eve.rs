using System.Globalization;
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
    Func<long, CancellationToken, ValueTask<ProjectDiscoveryPlayerStatistics?>>? ReadPlayerStatisticsAsync = null);

public sealed class ProjectDiscoveryService(ProjectDiscoveryRpcCallbacks? callbacks = null) : IService
{
    private const int PlayerNotInDatabaseErrorCode = 103010;
    private const string ActiveProjectId = "mcgill-polyseg";
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
        "initialize_tutorial_status";

    public async ValueTask<PyValue> CallAsync(ServiceCall call)
    {
        var characterId = call.Session.CharacterId.GetValueOrDefault();
        switch (call.Method)
        {
            case "is_enabled":
                return PyBool.True;

            case "fix_task_id":
                if (characterId > 0 && _callbacks.FixTaskIdAsync is { } fixTaskId)
                    await fixTaskId(characterId, PositiveFiniteNumber(call.Arg(0)), call.Cancellation);
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

            default:
                return PyNone.Instance;
        }
    }

    private async ValueTask<ProjectDiscoveryPlayerState> ReadStateAsync(long characterId, CancellationToken cancellationToken)
    {
        if (characterId > 0 && _callbacks.ReadPlayerStateAsync is { } readState &&
            await readState(characterId, cancellationToken) is { } state)
            return state;
        return new ProjectDiscoveryPlayerState();
    }

    private static KeyValuePair<PyValue, PyValue> Pair(string key, PyValue value) =>
        new(new PyStr(key), value);

    private static double NormalizeRank(double value) =>
        double.IsFinite(value) ? Math.Max(1, Math.Truncate(value)) : 1;

    private static double NonNegativeInteger(double value) =>
        double.IsFinite(value) ? Math.Max(0, Math.Truncate(value)) : 0;

    private static double? PositiveFiniteNumber(PyValue value)
    {
        var number = value switch
        {
            PyInt integer => integer.Value,
            PyLong integer => integer.Value,
            PyReal real => real.Value,
            PyBool boolean => boolean.Value ? 1 : 0,
            PyStr text when string.IsNullOrWhiteSpace(text.Value) => 0,
            PyStr text when double.TryParse(text.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => parsed,
            PyNone => 0,
            _ => double.NaN,
        };
        return double.IsFinite(number) && number > 0 ? number : null;
    }
}
