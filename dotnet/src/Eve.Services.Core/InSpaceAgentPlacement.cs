namespace Eve.Services.Core;

/// <summary>



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
