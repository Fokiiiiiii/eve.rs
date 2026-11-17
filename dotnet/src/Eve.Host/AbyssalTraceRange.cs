namespace Eve.Host;

internal static class AbyssalTraceRange
{
    // EveJS v0.12.9 sets 2,500 m.
    public const double MaximumSurfaceDistanceMeters = 2_500;

    public static bool IsWithin(
        IReadOnlyList<double> shipPosition,
        double shipRadius,
        IReadOnlyList<double> tracePosition,
        double traceRadius)
    {
        ArgumentNullException.ThrowIfNull(shipPosition);
        ArgumentNullException.ThrowIfNull(tracePosition);
        if (shipPosition.Count != 3 || tracePosition.Count != 3 ||
            shipPosition.Concat(tracePosition).Any(value => !double.IsFinite(value)) ||
            !double.IsFinite(shipRadius) || shipRadius < 0 ||
            !double.IsFinite(traceRadius) || traceRadius < 0)
        {
            throw new ArgumentException("Abyssal trace range requires finite SDE radii and authoritative poses.");
        }

        return SurfaceDistance(shipPosition, shipRadius, tracePosition, traceRadius) <= MaximumSurfaceDistanceMeters;
    }

    public static bool IsWithinCommittedFollowApproach(
        IReadOnlyList<double> shipPosition,
        double shipRadius,
        IReadOnlyList<double> gatePosition,
        double gateRadius,
        long gateId,
        Eve.Space.SimAbyssalFollowApproach? following)
    {
        if (following is not { } approach || approach.TargetId != gateId || approach.TargetId <= 0 ||
            !double.IsFinite(approach.FollowRangeMeters) || approach.FollowRangeMeters < 0 ||
            approach.FollowRangeMeters > MaximumSurfaceDistanceMeters ||
            !double.IsFinite(approach.MaxVelocityMetersPerSecond) || approach.MaxVelocityMetersPerSecond <= 0)
        {
            return false;
        }

        var allowance = Math.Min(MaximumSurfaceDistanceMeters,
            Math.Max(500, approach.MaxVelocityMetersPerSecond * 2));
        return SurfaceDistance(shipPosition, shipRadius, gatePosition, gateRadius) <= MaximumSurfaceDistanceMeters + allowance;
    }

    private static double SurfaceDistance(
        IReadOnlyList<double> shipPosition,
        double shipRadius,
        IReadOnlyList<double> gatePosition,
        double gateRadius)
    {
        ArgumentNullException.ThrowIfNull(shipPosition);
        ArgumentNullException.ThrowIfNull(gatePosition);
        if (shipPosition.Count != 3 || gatePosition.Count != 3 ||
            shipPosition.Concat(gatePosition).Any(value => !double.IsFinite(value)) ||
            !double.IsFinite(shipRadius) || shipRadius < 0 || !double.IsFinite(gateRadius) || gateRadius < 0)
        {
            throw new ArgumentException("Abyssal gate range requires finite SDE radii and authoritative poses.");
        }

        var dx = shipPosition[0] - gatePosition[0];
        var dy = shipPosition[1] - gatePosition[1];
        var dz = shipPosition[2] - gatePosition[2];
        return Math.Max(0, Math.Sqrt(dx * dx + dy * dy + dz * dz) - shipRadius - gateRadius);
    }
}
