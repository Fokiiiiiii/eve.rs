namespace Eve.Services.Core;

/// <summary>
/// Where an agent in space is put in its system. The SDE gives the agent's system and dungeon but no coordinates (agentsInSpace holds only
/// the spawn point ID). The rule: 20 km from the system centre, on the circle of the x-z plane at the angle of the agent ID (degrees).
/// Deterministic, so the same agent is in the same place after a restart.
/// </summary>
public static class InSpaceAgentPlacement
{
    public const double RadiusMetres = 20_000;

    public static double[] Position(long agentId, double centreX, double centreY, double centreZ)
    {
        var angle = (agentId % 360) * Math.PI / 180;
        return [centreX + RadiusMetres * Math.Cos(angle), centreY, centreZ + RadiusMetres * Math.Sin(angle)];
    }
}
