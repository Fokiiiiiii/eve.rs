using System.Globalization;
using System.Text.Json.Nodes;
using Eve.Agents;
using Eve.Marshal;
using Eve.Services;

namespace Eve.Services.Core;

/// <summary>
/// The agents store's missions in the shape the mission journal reads (the game store's <c>missionsByAgentID</c> records, see
/// <see cref="AgentMissionService"/>). Offered, accepted and completed missions are journalled; declined, quit, failed and
/// allocated ones are not, so they are removed from the character's journal view.
/// </summary>
public static class AgentMissionStoreRecords
{
    /// <summary>The character's game-store record with the store's missions laid over its <c>missionsByAgentID</c>.</summary>
    public static JsonObject Overlay(JsonObject? character, IReadOnlyList<AgentMissionRecord> stored)
    {
        var merged = character?.DeepClone() as JsonObject ?? new JsonObject();
        if (merged["missionsByAgentID"] is not JsonObject missions)
        {
            missions = new JsonObject();
            merged["missionsByAgentID"] = missions;
        }

        foreach (var record in stored)
        {
            var key = record.AgentId.ToString(CultureInfo.InvariantCulture);
            if (StatusOf(record.State) is { } status)
            {
                missions[key] = Record(record, status);
            }
            else
            {
                missions.Remove(key);
            }
        }
        return merged;
    }

    /// <summary>The client's runtimeStatus for a journalled state, or null when the journal does not show it.</summary>
    public static string? StatusOf(AgentMissionState state) => state switch
    {
        AgentMissionState.Offered => "offered",
        AgentMissionState.Accepted => "accepted",
        AgentMissionState.Completed => "completed",
        _ => null,
    };

    private static JsonObject Record(AgentMissionRecord record, string status)
    {
        var content = record.Content;
        var json = new JsonObject
        {
            ["runtimeStatus"] = status,
            ["missionID"] = content.MissionId,
            ["contentID"] = content.MissionId.ToString(CultureInfo.InvariantCulture),
            ["missionTypeLabel"] = content.MissionTypeLabel,
            ["importantMission"] = content.Important,
            ["expiresAtFileTime"] = FileTime(record.ExpiresAt),
            ["acceptedAtFileTime"] = FileTime(record.AcceptedAt),
            ["completedAtFileTime"] = FileTime(record.CompletedAt),
        };
        if (content.NameId > 0) json["missionNameID"] = content.NameId;
        if (content.Title.Length > 0) json["missionTitle"] = content.Title;
        return json;
    }

    private static JsonNode? FileTime(DateTimeOffset? at) =>
        at is { } time ? JsonValue.Create(time.UtcDateTime.ToFileTimeUtc()) : null;
}

/// <summary>

/// eve-logic agents.py:688). Only committed changes are announced (the store raises them after the commit).
/// </summary>
public sealed class AgentMissionAnnouncer : IDisposable
{
    private readonly AgentMissionStore _store;
    private readonly CharacterSessions _sessions;

    public AgentMissionAnnouncer(AgentMissionStore store, CharacterSessions sessions)
    {
        _store = store;
        _sessions = sessions;
        store.Committed += Changed;
    }

    private void Changed(AgentMissionChange change) =>
        _sessions.Of(change.CharacterId)?.SendNotification("OnAgentMissionChange", "clientID", new PyStr(change.Action), new PyInt(change.AgentId));

    public void Dispose() => _store.Committed -= Changed;
}
