using Eve.AccessGroups;
using Eve.Bookmarks;
using Eve.Marshal;
using Eve.Services;

namespace Eve.Services.Core;

/// <summary>
/// The shared folder changes the client is not told by one of its own requests: a list a folder uses changed (the character loses the
/// folder, or its level changes), and a bookmark of a shared folder expired. Each is planned from the stores, then sent: after the response
/// of the request that caused it, or at once for the expiry timer.
/// </summary>
public sealed class SharedFolderNotifier(BookmarkStore store, Func<Affiliation, CancellationToken, ValueTask<AccessLists>> listsOf, CharacterSessions sessions)
{
    /// <summary>
    /// Plans the notices for a changed owner group: each connected character with a folder that uses the group gets
    /// <c>OnSharedBookmarksFoldersAccessLost</c> for the folders it can no longer view, and <c>OnSharedBookmarksFoldersAccessChanged</c> (folder
    /// to level) for the rest. Answers null when nothing is to be sent.
    /// </summary>
    public async Task<Action?> PlanAccessChangesAsync(long groupId, CancellationToken token = default)
    {
        var plans = new List<(ClientSession Session, List<long> Lost, Dictionary<long, int> Levels)>();
        foreach (var connected in (await store.ConnectedFoldersUsingAsync(groupId, token)).GroupBy(pair => pair.CharacterId))
        {
            if (sessions.Of(connected.Key) is not { } session) continue;
            var lists = await listsOf(Who(session), token);
            var lost = new List<long>();
            var levels = new Dictionary<long, int>();
            foreach (var folder in connected.Select(pair => pair.FolderId))
            {
                var level = await store.LevelAsync(folder, connected.Key, lists, token);
                if (FolderAccess.CanView(level)) levels[folder] = level;
                else lost.Add(folder);
            }
            plans.Add((session, lost, levels));
        }
        if (plans.Count == 0) return null;
        return () =>
        {
            foreach (var (session, lost, levels) in plans)
            {
                if (lost.Count > 0)
                    session.SendNotification("OnSharedBookmarksFoldersAccessLost", "clientID", new PyList([.. lost.Select(id => InventoryWire.Id(id))]));
                if (levels.Count > 0)
                    session.SendNotification("OnSharedBookmarksFoldersAccessChanged", "clientID", new PyDict(levels.Select(level =>
                        new KeyValuePair<PyValue, PyValue>(InventoryWire.Id(level.Key), new PyInt(level.Value))).ToArray()));
            }
        };
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

    /// <summary>Removes the expired bookmarks and tells the connected viewers of their folders (the expiry timer's job).</summary>
    public async Task ExpireAsync(CancellationToken token = default)
    {
        var removed = await store.ExpireAsync(token);
        if (removed.Count > 0) (await PlanRemovalsAsync(removed, 0, token))?.Invoke();
    }

    /// <summary>Whether the session's character holds a level that views the folder now.</summary>
    public async Task<bool> CanViewAsync(ClientSession session, long folderId, CancellationToken token = default)
    {
        var who = Who(session);
        return FolderAccess.CanView(await store.LevelAsync(folderId, who.CharacterId, await listsOf(who, token), token));
    }

    private static Affiliation Who(ClientSession session) =>
        new(session.CharacterId ?? 0, session.GetInt("corpid") ?? 0, session.GetInt("allianceid") ?? 0);
}
