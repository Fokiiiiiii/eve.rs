using Eve.Space;

namespace Eve.Host;

public sealed record AbyssalTracePoseSnapshot(
    int SchemaVersion,
    IReadOnlyList<double> Position,
    IReadOnlyList<double> Direction)
{
    public Eve.Space.SimAbyssalTrace? Trace { get; init; }
    public AbyssalFilamentConsumedEvent? Consumption { get; init; }
}

/// <summary>
/// Builds the public trace ball from the persisted item identity and an authoritative ship pose.

/// its shared Tranquil profile uses the captured source vector; other profiles use the
/// configured 2,500m directional deployment distance.
/// </summary>
public static class AbyssalTraceFactory
{
    public const int TraceTypeId = 48964;
    public const int TraceGroupId = 1991;
    public const int TraceCategoryId = 2;
    public const long TraceOwnerId = 1;
    public const double DirectionalDeploymentDistanceMeters = 2_500;

    private static readonly HashSet<int> CapturedSharedProfileFilaments =
        [56131, 56132, 56133, 56134, 56136];

    private static readonly double[] CapturedSharedProfileOffset =
        [-96.8582763671875, -166.0657196044922, 141.95849609375];

    public static SimAbyssalTrace Create(
        int solarSystemId,
        long traceItemId,
        int gameModeId,
        int filamentTypeId,
        int difficultyTier,
        long expiresAtMs,
        double radius,
        IReadOnlyList<double> sourceShipPosition,
        IReadOnlyList<double> sourceShipDirection)
    {
        ValidateIdentity(solarSystemId, traceItemId, gameModeId, filamentTypeId, difficultyTier, expiresAtMs, radius);
        var source = RequireVector(sourceShipPosition, nameof(sourceShipPosition));
        var direction = NormalizeDirection(RequireVector(sourceShipDirection, nameof(sourceShipDirection)));
        var offset = CapturedSharedProfileFilaments.Contains(filamentTypeId)
            ? CapturedSharedProfileOffset
            : new[]
            {
                direction[0] * DirectionalDeploymentDistanceMeters,
                direction[1] * DirectionalDeploymentDistanceMeters,
                direction[2] * DirectionalDeploymentDistanceMeters,
            };
        var position = new[]
        {
            source[0] + offset[0],
            source[1] + offset[1],
            source[2] + offset[2],
        };
        return Build(solarSystemId, traceItemId, gameModeId, filamentTypeId, difficultyTier,
            expiresAtMs, radius, position, direction);
    }

    public static SimAbyssalTrace Restore(
        int solarSystemId,
        long traceItemId,
        int gameModeId,
        int filamentTypeId,
        int difficultyTier,
        long expiresAtMs,
        double radius,
        AbyssalTracePoseSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ValidateIdentity(solarSystemId, traceItemId, gameModeId, filamentTypeId, difficultyTier, expiresAtMs, radius);
        if (snapshot.SchemaVersion != 1)
            throw new InvalidDataException($"Unsupported Abyssal trace pose schema version {snapshot.SchemaVersion}.");

        return Build(
            solarSystemId,
            traceItemId,
            gameModeId,
            filamentTypeId,
            difficultyTier,
            expiresAtMs,
            radius,
            RequireVector(snapshot.Position, nameof(snapshot.Position)),
            NormalizeDirection(RequireVector(snapshot.Direction, nameof(snapshot.Direction))));
    }

    private static SimAbyssalTrace Build(
        int solarSystemId,
        long traceItemId,
        int gameModeId,
        int filamentTypeId,
        int difficultyTier,
        long expiresAtMs,
        double radius,
        double[] position,
        double[] direction)
    {
        var frozenPosition = Array.AsReadOnly(position);
        return new SimAbyssalTrace(
            solarSystemId,
            traceItemId,
            TraceTypeId,
            TraceGroupId,
            TraceCategoryId,
            TraceOwnerId,
            radius,
            frozenPosition,
            Array.AsReadOnly(new[] { 0d, 0d, 0d }),
            Array.AsReadOnly(direction),
            frozenPosition,
            0,
            gameModeId,
            filamentTypeId,
            difficultyTier,
            expiresAtMs);
    }

    private static void ValidateIdentity(
        int solarSystemId,
        long traceItemId,
        int gameModeId,
        int filamentTypeId,
        int difficultyTier,
        long expiresAtMs,
        double radius)
    {
        if (solarSystemId <= 0 || traceItemId <= 0 || gameModeId is < 1 or > 3 ||
            filamentTypeId <= 0 || difficultyTier is < 0 or > 6 || expiresAtMs <= 0 ||
            !double.IsFinite(radius) || radius <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(solarSystemId), "Abyssal trace identity or static properties are invalid.");
        }
    }

    private static double[] RequireVector(IReadOnlyList<double>? vector, string parameterName)
    {
        if (vector is null || vector.Count != 3 || vector.Any(value => !double.IsFinite(value)))
            throw new ArgumentException("Abyssal trace pose must contain three finite coordinates.", parameterName);
        return [vector[0], vector[1], vector[2]];
    }

    private static double[] NormalizeDirection(IReadOnlyList<double> direction)
    {
        var length = Math.Sqrt(
            direction[0] * direction[0] +
            direction[1] * direction[1] +
            direction[2] * direction[2]);
        if (!double.IsFinite(length) || length <= 0)
            throw new ArgumentException("Abyssal trace direction must be a finite non-zero vector.", nameof(direction));

        return [direction[0] / length, direction[1] / length, direction[2] / length];
    }
}