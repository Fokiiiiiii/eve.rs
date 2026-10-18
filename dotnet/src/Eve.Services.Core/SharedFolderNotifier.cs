using Eve.AccessGroups;
using Eve.Bookmarks;
using Eve.Marshal;
using Eve.Services;

namespace Eve.Services.Core;

/// <summary>
/// The shared folder changes the client is not told by one of its own requests: a list a folder uses changed (the character loses the
/// folder, or its level changes), a character's corporation or alliance changed (its lists change with it), and a bookmark of a shared
/// folder expired. Each is planned from the stores, then sent: after the response of the request that caused it, or at once for the
/// upkeep timer. The affiliations come from the character store (<paramref name="affiliationsOf"/>), not from the session, which
/// keeps the values it had at login.
/// </summary>
public sealed class SharedFolderNotifier(BookmarkStore store, Func<Affiliation, CancellationToken, ValueTask<AccessLists>> listsOf,
    CharacterSessions sessions, Func<IReadOnlyList<long>, CancellationToken, Task<IReadOnlyDictionary<long, (long CorporationId, long AllianceId)>>>? affiliationsOf = null)
{
    private readonly object _gate = new();
    private readonly Dictionary<long, (long CorporationId, long AllianceId)> _seen = new();

    /// <summary>
    /// Plans the notices for a changed owner group: each connected character with a folder that uses the group gets
    /// <c>OnSharedBookmarksFoldersAccessLost</c> for the folders it can no longer view, and <c>OnSharedBookmarksFoldersAccessChanged</c> (folder
    /// to level) for the rest. Answers null when nothing is to be sent.
    /// </summary>
    public async Task<Action?> PlanAccessChangesAsync(long groupId, CancellationToken token = default)
    {
        var plans = new List<Plan>();
        foreach (var connected in (await store.ConnectedFoldersUsingAsync(groupId, token)).GroupBy(pair => pair.CharacterId))
        {
            if (sessions.Of(connected.Key) is not { } session) continue;
            if (await PlanAsync(session, connected.Select(pair => pair.FolderId).Distinct().ToArray(), token) is { } plan) plans.Add(plan);
        }
        return Send(plans);
    }

    /// <summary>
    /// Plans the notices for the connected characters whose corporation or alliance changed since the last check: their folders are
    /// checked with their new lists. The first check of a character only records its affiliation. Answers null when nothing is to be sent.
    /// </summary>
    public async Task<Action?> PlanAffiliationChangesAsync(CancellationToken token = default)
    {
        if (affiliationsOf is null) return null;
        var ids = sessions.All().Where(session => (session.CharacterId ?? 0) > 0).Select(session => session.CharacterId!.Value).Distinct().ToArray();
        var current = await affiliationsOf(ids, token);
        var moved = new List<long>();
        lock (_gate)
        {
            foreach (var id in ids)
            {
                if (!current.TryGetValue(id, out var now)) continue;
                if (_seen.TryGetValue(id, out var before) && before != now) moved.Add(id);
                _seen[id] = now;
            }
            foreach (var gone in _seen.Keys.Where(key => !ids.Contains(key)).ToList()) _seen.Remove(gone);
        }
        var plans = new List<Plan>();
        foreach (var id in moved)
        {
            if (sessions.Of(id) is not { } session) continue;
            if (await PlanAsync(session, await store.ConnectedFoldersOfAsync(id, token), token) is { } plan) plans.Add(plan);
        }
        return Send(plans);
    }

    /// <summary>
    /// Plans the removal of expired bookmarks to the connected viewers of their shared folders (the character that read them is not one of
    /// the viewers: its answer already leaves them out). Answers null when no viewer is connected.
    /// </summary>
    public async Task<Action?> PlanRemovalsAsync(IReadOnlyList<(long FolderId, long BookmarkId)> removed, long excludeCharacterId, CancellationToken token = default)
    {
        var sends = new List<Action>();
        foreach (var folder in removed.GroupBy(entry => entry.FolderId))
        {
            var update = AccessGroupBookmarkMgrService.Update(folder.Key, "bookmarksRemoved",
                new PyList([.. folder.Select(entry => InventoryWire.Id(entry.BookmarkId))]));
            foreach (var viewer in await store.ActiveViewersAsync(folder.Key, excludeCharacterId, token))
                if (sessions.Of(viewer) is { } session && await CanViewAsync(session, folder.Key, token))
                    sends.Add(() => session.SendNotification("OnSharedBookmarksFolderUpdated", "clientID", new PyList([update])));
        }
        return sends.Count == 0 ? null : () => { foreach (var send in sends) send(); };
    }

    /// <summary>The upkeep timer's job: removes the expired bookmarks, and sends the notices of the affiliation changes since the last run.</summary>
    public async Task UpkeepAsync(CancellationToken token = default)
    {
        var removed = await store.ExpireAsync(token);
        if (removed.Count > 0) (await PlanRemovalsAsync(removed, 0, token))?.Invoke();
        (await PlanAffiliationChangesAsync(token))?.Invoke();
    }

    /// <summary>Whether the session's character holds a level that views the folder now.</summary>
    public async Task<bool> CanViewAsync(ClientSession session, long folderId, CancellationToken token = default)
    {
        var who = await WhoAsync(session, token);
        return FolderAccess.CanView(await store.LevelAsync(folderId, who.CharacterId, await listsOf(who, token), token));
    }

    private sealed record Plan(ClientSession Session, List<long> Lost, Dictionary<long, int> Levels);

    /// <summary>The character's view of the folders with its lists now: those it lost, and the levels of the rest.</summary>
    private async Task<Plan?> PlanAsync(ClientSession session, IReadOnlyList<long> folders, CancellationToken token)
    {
        var who = await WhoAsync(session, token);
        var lists = await listsOf(who, token);
        var lost = new List<long>();
        var levels = new Dictionary<long, int>();
        foreach (var folder in folders)
        {
            var level = await store.LevelAsync(folder, who.CharacterId, lists, token);
            if (FolderAccess.CanView(level)) levels[folder] = level;
            else lost.Add(folder);
        }
        return lost.Count == 0 && levels.Count == 0 ? null : new Plan(session, lost, levels);
    }

    private static Action? Send(IReadOnlyList<Plan> plans)
    {
        if (plans.Count == 0) return null;
        return () =>
        {
            foreach (var plan in plans)
            {
                if (plan.Lost.Count > 0)
                    plan.Session.SendNotification("OnSharedBookmarksFoldersAccessLost", "clientID", new PyList([.. plan.Lost.Select(id => InventoryWire.Id(id))]));
                if (plan.Levels.Count > 0)
                    plan.Session.SendNotification("OnSharedBookmarksFoldersAccessChanged", "clientID", new PyDict(plan.Levels.Select(level =>
                        new KeyValuePair<PyValue, PyValue>(InventoryWire.Id(level.Key), new PyInt(level.Value))).ToArray()));
            }
        };
    }

    /// <summary>The character with its corporation and alliance now (the character store), or what the session had when there is no store.</summary>
    private async Task<Affiliation> WhoAsync(ClientSession session, CancellationToken token)
    {
        var id = session.CharacterId ?? 0;
        if (affiliationsOf is not null && (await affiliationsOf([id], token)).TryGetValue(id, out var now))
            return new Affiliation(id, now.CorporationId, now.AllianceId);
        return new Affiliation(id, session.GetInt("corpid") ?? 0, session.GetInt("allianceid") ?? 0);
    }
}
