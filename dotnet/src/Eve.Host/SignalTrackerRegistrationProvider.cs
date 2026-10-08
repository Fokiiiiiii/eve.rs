using System.Collections.Concurrent;
using Eve.Marshal;
using Eve.Services;

namespace Eve.Host;

/// <summary>Tracks live scanMgr listeners and accepts future full snapshots from their producer.</summary>
public sealed class SignalTrackerRegistrationProvider(CharacterSessions sessions)
{
    private readonly ConcurrentDictionary<long, Registration> _byCharacter = new();

    public ValueTask<PyValue> RegisterAsync(ServiceCall call)
    {
        ArgumentNullException.ThrowIfNull(call);
        var characterId = call.Session.CharacterId ?? 0;
        var systemId = GetSystemId(call.Session);
        if (characterId <= 0 || systemId <= 0 || !ReferenceEquals(sessions.Of(characterId), call.Session))
            return ValueTask.FromResult<PyValue>(PyNone.Instance);

        _byCharacter[characterId] = new Registration(systemId, call.Session);
        return ValueTask.FromResult<PyValue>(PyNone.Instance);
    }

    /// <summary>Publishes a real producer snapshot to registered characters still in that solar system.</summary>
    public async ValueTask PublishFullStateAsync(
        long systemId,
        PyValue fullState,
        bool shouldRemoveOldSites = false,
        CancellationToken cancellationToken = default)
    {
        if (systemId <= 0 || fullState is PyNone)
            return;

        await Task.Yield();
        foreach (var entry in _byCharacter.ToArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (characterId, registration) = entry;
            if (registration.SystemId != systemId)
                continue;
            if (!IsCurrent(characterId, registration))
            {
                _byCharacter.TryRemove(entry);
                continue;
            }

            if (shouldRemoveOldSites)
            {
                registration.Session.SendNotification(
                    "OnSignalTrackerFullState", "charid", Py.Number(systemId), fullState, PyBool.True);
            }
            else
            {
                registration.Session.SendNotification(
                    "OnSignalTrackerFullState", "charid", Py.Number(systemId), fullState);
            }
        }
    }

    private bool IsCurrent(long characterId, Registration registration) =>
        ReferenceEquals(sessions.Of(characterId), registration.Session) &&
        GetSystemId(registration.Session) == registration.SystemId;

    private static long GetSystemId(ClientSession session)
    {
        long? systemId = session.GetInt("solarsystemid2");
        if (systemId is > 0)
            return systemId.GetValueOrDefault();
        long? fallback = session.GetInt("solarsystemid");
        return fallback.GetValueOrDefault() > 0 ? fallback.GetValueOrDefault() : 0;
    }

    private sealed record Registration(long SystemId, ClientSession Session);
}