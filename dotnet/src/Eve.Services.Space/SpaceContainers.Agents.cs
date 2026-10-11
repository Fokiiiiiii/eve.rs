using Eve.Space;

namespace Eve.Services.Space;

public sealed partial class SpaceContainers
{
    /// <summary>How long an agent in space stays in the scene (a year; a pilot's entry puts it back when it is gone).</summary>
    public const double AgentInSpaceLifetimeMs = 365L * 24 * 3600 * 1000;

    /// <summary>
    /// Puts an agent in space in the scene: its ball is its agent ID, its ship type and group are the agentsInSpace row's, and it belongs to its corporation.
    /// The same message restores it after a restart (the simulation puts it in its place again).
    /// </summary>
    public void PutAgentInSpace(int solarSystemId, long agentId, int typeId, int groupId, int corporationId, IReadOnlyList<double> position) =>
        _sim.RestoreDeployed(new SimDeployed(solarSystemId, agentId, "Agent", typeId, groupId, position, 100, 0, corporationId, 0, 0, AgentInSpaceLifetimeMs));
}
