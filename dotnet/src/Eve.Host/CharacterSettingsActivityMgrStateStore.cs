using Eve.Marshal;
using Eve.Services.CharacterInfo;
using Eve.Services.Core;

namespace Eve.Host;

public sealed class CharacterSettingsActivityMgrStateStore(
    Func<long, CancellationToken, ValueTask<CharMgrPersonalData?>> readPersonalData,
    Func<long, Func<CharMgrPersonalData, CharMgrPersonalData>, CancellationToken, ValueTask<CharMgrPersonalData?>> updatePersonalData)
    : IActivityMgrStateStore
{
    private const string SeenKey = "eve.rs.activityMgr.seenActivityIDs";
    private const string PopStateKey = "eve.rs.activityMgr.popState";

    public async ValueTask<ActivityMgrState?> ReadAsync(long characterId, CancellationToken cancellationToken)
    {
        var data = await readPersonalData(characterId, cancellationToken);
        return data is null ? null : ReadState(data.CharacterSettings);
    }

    public async ValueTask<ActivityMgrState?> UpdateAsync(
        long characterId,
        Func<ActivityMgrState, ActivityMgrState> update,
        CancellationToken cancellationToken)
    {
        var data = await updatePersonalData(characterId, current =>
        {
            var state = update(ReadState(current.CharacterSettings));
            var settings = current.CharacterSettings.ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal);
            settings[SeenKey] = new PyList([.. state.SeenActivityIds.Order().Select(WireId)]);
            settings[PopStateKey] = new PyInt(state.PopState);
            return current with { CharacterSettings = settings };
        }, cancellationToken);
        return data is null ? null : ReadState(data.CharacterSettings);
    }

    private static ActivityMgrState ReadState(IReadOnlyDictionary<string, PyValue> settings)
    {
        var seen = new HashSet<long>();
        if (settings.GetValueOrDefault(SeenKey) is PyList values)
        {
            foreach (var value in values.Items)
            {
                var id = Integer(value, 0);
                if (id > 0)
                    seen.Add(id);
            }
        }

        var popState = settings.TryGetValue(PopStateKey, out var rawPopState)
            ? Integer(rawPopState, 1)
            : 1;
        return new ActivityMgrState(seen, popState == 0 ? 0 : 1);
    }

    private static long Integer(PyValue value, long fallback) => value switch
    {
        PyBool boolean => boolean.Value ? 1 : 0,
        PyInt integer => integer.Value,
        PyLong integer => integer.Value,
        _ => fallback,
    };

    private static PyValue WireId(long value) => value <= int.MaxValue
        ? new PyInt((int)value)
        : new PyLong(value);
}
