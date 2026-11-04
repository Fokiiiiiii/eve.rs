using System.Collections.Concurrent;
using Eve.Marshal;
using Eve.Services;

namespace Eve.Host;

/// <summary>Tracks live scanMgr listeners and sends the state returned by the bound scan manager.</summary>
public sealed class SignalTrackerRegistrationProvider(CharacterSessions sessions)
{
    private readonly ConcurrentDictionary<long, Registration> _byCharacter = new();
    private readonly ConcurrentDictionary<long, PyValue> _lastFullStateBySystem = new();
    private readonly SemaphoreSlim _refreshGate = new(1, 1);

    public Func<long, CancellationToken, ValueTask<PyValue>>? ReadFullState { get; set; }

    public ValueTask<PyValue> RegisterAsync(ServiceCall call, PyValue fullState)
    {
        ArgumentNullException.ThrowIfNull(call);
        var characterId = call.Session.CharacterId ?? 0;
        var systemId = GetSystemId(call.Session);
        if (characterId <= 0 || systemId <= 0 || fullState is PyNone ||
            !ReferenceEquals(sessions.Of(characterId), call.Session))
            return ValueTask.FromResult<PyValue>(PyNone.Instance);

        var registration = new Registration(systemId, call.Session);
        _byCharacter[characterId] = registration;
        _lastFullStateBySystem.TryAdd(systemId, fullState);
        call.Session.AfterResponse(() =>
        {
            if (IsCurrent(characterId, registration))
                call.Session.SendNotification("OnSignalTrackerFullState", "charid", Py.Number(systemId), fullState);
        });
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
                registration.Session.SendNotification("OnSignalTrackerFullState", "charid", Py.Number(systemId), fullState, PyBool.True);
            else
                registration.Session.SendNotification("OnSignalTrackerFullState", "charid", Py.Number(systemId), fullState);
        }
    }

    public async ValueTask RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (ReadFullState is not { } readFullState)
            return;

        await _refreshGate.WaitAsync(cancellationToken);
        try
        {
            foreach (var entry in _byCharacter.ToArray())
            {
                if (!IsCurrent(entry.Key, entry.Value))
                    _byCharacter.TryRemove(entry);
            }

            foreach (var systemId in _byCharacter.Values.Select(registration => registration.SystemId).Distinct())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var fullState = await readFullState(systemId, cancellationToken);
                if (fullState is PyNone)
                    continue;

                if (_lastFullStateBySystem.TryGetValue(systemId, out var previous))
                {
                    if (Equals(previous, fullState))
                        continue;
                    await PublishStateChangesAsync(systemId, previous, fullState, cancellationToken);
                }
                else
                {
                    await PublishFullStateAsync(systemId, fullState, shouldRemoveOldSites: true, cancellationToken: cancellationToken);
                }
                _lastFullStateBySystem[systemId] = fullState;
            }
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private async ValueTask PublishStateChangesAsync(
        long systemId,
        PyValue previous,
        PyValue current,
        CancellationToken cancellationToken)
    {
        if (previous is not PyList oldState || current is not PyList newState ||
            oldState.Items.Count != 4 || newState.Items.Count != 4)
        {
            await PublishFullStateAsync(systemId, current, shouldRemoveOldSites: true, cancellationToken: cancellationToken);
            return;
        }

        if (oldState.Items[2] is not PyDict oldStaticSites || newState.Items[2] is not PyDict newStaticSites)
        {
            await PublishFullStateAsync(systemId, current, shouldRemoveOldSites: true, cancellationToken: cancellationToken);
            return;
        }

        if (!Equals(oldStaticSites, newStaticSites))
        {
            await PublishFullStateAsync(systemId, current, shouldRemoveOldSites: true, cancellationToken: cancellationToken);
            return;
        }

        foreach (var (index, notification) in new[]
        {
            (0, "OnSignalTrackerAnomalyUpdate"),
            (1, "OnSignalTrackerSignatureUpdate"),
            (3, "OnSignalTrackerStructureUpdate"),
        })
        {
            if (oldState.Items[index] is not PyDict oldSites || newState.Items[index] is not PyDict newSites)
            {
                await PublishFullStateAsync(systemId, current, shouldRemoveOldSites: true, cancellationToken: cancellationToken);
                return;
            }

            var oldById = oldSites.Entries.ToDictionary(entry => entry.Key, entry => entry.Value);
            var newById = newSites.Entries.ToDictionary(entry => entry.Key, entry => entry.Value);
            var added = newSites.Entries
                .Where(entry => !oldById.TryGetValue(entry.Key, out var prior) || !Equals(prior, entry.Value))
                .ToArray();
            var removed = oldSites.Entries
                .Where(entry => !newById.ContainsKey(entry.Key))
                .Select(entry => entry.Key)
                .ToArray();
            if (added.Length == 0 && removed.Length == 0)
                continue;

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

                registration.Session.SendNotification(
                    notification,
                    "charid",
                    Py.Number(systemId),
                    new PyDict(added),
                    new PyList(removed));
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
