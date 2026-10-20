using Eve.Space;

namespace Eve.Services.Space;

public sealed partial class SpaceContainers
{

    public const double AgentInSpaceLifetimeMs = 365L * 24 * 3600 * 1000;

    /// <summary>
    /// The same message restores it after a restart (the simulation puts it in its place again).
    /// </summary>
    public void PutAgentInSpace(int solarSystemId, long agentId, int typeId, int groupId, int corporationId, IReadOnlyList<double> position) =>
        _sim.RestoreDeployed(new SimDeployed(solarSystemId, agentId, "Agent", typeId, groupId, position, 100, 0, corporationId, 0, 0, AgentInSpaceLifetimeMs));
}
