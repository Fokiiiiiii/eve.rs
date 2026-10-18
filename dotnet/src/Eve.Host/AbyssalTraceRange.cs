namespace Eve.Host;

internal static class AbyssalTraceRange
{
    // EveJS v0.12.9 sets 2,500 m. shortcut: no FOLLOW reconciliation without Space command-state data; upgrade when target, follow range and velocity are exposed.
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

        var dx = shipPosition[0] - tracePosition[0];
        var dy = shipPosition[1] - tracePosition[1];
        var dz = shipPosition[2] - tracePosition[2];
        var surfaceDistance = Math.Max(0, Math.Sqrt(dx * dx + dy * dy + dz * dz) - shipRadius - traceRadius);
        return surfaceDistance <= MaximumSurfaceDistanceMeters;
    }
}
