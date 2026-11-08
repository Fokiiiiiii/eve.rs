using System.Text.Json.Nodes;
using Eve.Agents;
using Eve.Services.Core;

namespace Eve.Host;

/// <summary>Projects durable research projects into the seven-column rows read by the V24.01 research browser.</summary>
internal static class AgentResearchJournalRows
{
    public static async ValueTask<JsonArray?> ReadAsync(
        ResearchProjectStore projects,
        IReadOnlyDictionary<long, AgentMissionAgentData> agentsById,
        long characterId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var projectsForCharacter = await projects.ReadAllAsync(characterId, cancellationToken);
        var rows = new JsonArray();
        foreach (var project in projectsForCharacter)
        {
            if (!agentsById.TryGetValue(project.AgentId, out var agent)) continue;
            rows.Add(new JsonArray(
                JsonValue.Create(agent.AgentId),
                JsonValue.Create(project.SkillTypeId),
                JsonValue.Create(project.PointsPerDay),
                JsonValue.Create(ResearchProjectStore.AccruedPoints(project, now)),
                JsonValue.Create(agent.Level),
                JsonValue.Create(0),
                agent.StationId is > 0 ? JsonValue.Create(agent.StationId.Value) : null));
        }
        return rows;
    }
}
