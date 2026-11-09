using System.Globalization;

namespace Eve.Services.Core;

/// <summary>
/// The second-decline fallback standing loss. EveJS v0.12.9 applies the level's raw agent reward as a negative change
/// and one tenth of it to the corporation for every mission outcome, including important and non-basic agents.
/// CCP's exact per-mission decline magnitude is not present in the pinned SDE.
/// </summary>
public static class AgentMissionStandingRules
{
    private static readonly double[] AgentStandingByLevel = [0, 0.01, 0.014, 0.02, 0.028, 0.036];

    /// <summary>Returns the reference's direct agent and corporation raw changes for a supported agent level.</summary>
    public static IReadOnlyList<MissionStandingGain> DeclinePenaltyFor(
        AgentMissionAgentData agent, Eve.Agents.AgentMissionContent content)
    {
        if (agent.Level is < 1 or > 5)
            return [];

        var agentLoss = -AgentStandingByLevel[agent.Level];
        var message = string.IsNullOrWhiteSpace(content.Title)
            ? content.MissionId.ToString(CultureInfo.InvariantCulture)
            : content.Title.Trim();
        return
        [
            new MissionStandingGain(agent.CorporationId, agentLoss / 10, Diminishing: true, message),
            new MissionStandingGain(agent.AgentId, agentLoss, Diminishing: true, message),
        ];
    }
}
