using Eve.Macho;
using Eve.Marshal;
using Eve.Space;

namespace Eve.Services.Space;

internal static class FleetWarpFormation
{
internal const long FormationSkillTypeId = 57317;
    internal const long FormationDistanceSkillTypeId = 57318;

    internal static FleetFormationSettings? Parse(PyValue? value)
    {
        var fields = value switch
        {
            PyDict dictionary => dictionary.Entries,
            PyObjectEx objectEx => objectEx.Dict,
            _ => null,
        };
        double? Field(string name)
        {
            if (fields is null) return null;
            foreach (var entry in fields)
            {
                if (JsSemantics.PlainText(entry.Key) == name)
                {
                    return entry.Value switch
                    {
                        PyInt integer => integer.Value,
                        PyLong integer => integer.Value,
                        PyReal real => real.Value,
                        _ => null,
                    };
                }
            }
            return null;
        }

        var rawType = Field("formationType");
        if (fields is null || rawType is not { } type || !double.IsFinite(type)
            || type != Math.Truncate(type) || type is < 0 or > 5)
        {
            return null;
        }

        var rawSpacing = Field("formationSpacing");
        var rawSize = Field("formationSize");
        return new FleetFormationSettings(
            (int)type,
            Spacing(rawSpacing),
            Size(rawSize),
            SpacingSkillLevel(rawSpacing),
            SizeSkillLevel(rawSize));
    }

    internal static WarpCoordinates? Destination(
        FleetFormationSettings settings,
        int followerIndex,
        int followerCount,
        WarpCoordinates commanderStart,
        WarpCoordinates landingPoint,
        WarpCoordinates? followerStart)
    {
        if (settings.Type == 5)
        {
            if (followerStart is not { } current) return null;
            var relativeOffset = new Vector(
                current.X - commanderStart.X,
                current.Y - commanderStart.Y,
                current.Z - commanderStart.Z);
            if (Length(relativeOffset) > 200_000) return null;
            return Add(landingPoint, relativeOffset);
        }
        if (followerCount <= 0 || followerIndex < 0 || followerIndex >= followerCount)
        {
            return null;
        }

        var forward = Unit(new Vector(
            landingPoint.X - commanderStart.X,
            landingPoint.Y - commanderStart.Y,
            landingPoint.Z - commanderStart.Z)) ?? new Vector(1, 0, 0);
        var referenceUp = Math.Abs(forward.Y) < 0.9 ? new Vector(0, 1, 0) : new Vector(0, 0, 1);
        var right = Unit(Cross(forward, referenceUp)) ?? new Vector(1, 0, 0);
        var up = Cross(right, forward);
        var spacing = settings.SpacingMeters;
        var radius = settings.SizeMeters / 2;
        Vector layoutOffset = settings.Type switch
        {
            0 => SphereOffset(followerIndex, followerCount, Math.Min(2_000, radius)),
            1 => SphereOffset(followerIndex, followerCount, radius),
            2 => GridOffset(followerIndex, followerCount, spacing, radius, wall: false),
            3 => GridOffset(followerIndex, followerCount, spacing, radius, wall: true),
            4 => ArrowOffset(followerIndex, spacing, radius),
            _ => default,
        };
        var worldOffset = Add(Add(Scale(right, layoutOffset.X), Scale(up, layoutOffset.Y)), Scale(forward, layoutOffset.Z));
        return Add(landingPoint, worldOffset);
    }

    internal static WarpCoordinates LandingPoint(WarpCoordinates target, WarpCoordinates origin, double range)
    {
        var toward = Unit(new Vector(target.X - origin.X, target.Y - origin.Y, target.Z - origin.Z))
            ?? new Vector(1, 0, 0);
        return Add(target, Scale(toward, -Math.Max(0, range)));
    }

    private static double Spacing(double? raw) => raw switch
    {
        100 => 1_000,
        101 => 2_000,
        102 => 3_000,
        103 => 4_000,
        104 => 5_000,
        20 => 10_000,
        { } metres when metres is 1 or 2 or 3 or 4 or 5 or 10 => metres * 1_000,
        _ => 1_000,
    };

    private static double Size(double? raw) => raw switch
    {
        20 => 10_000,
        21 => 20_000,
        22 => 30_000,
        105 => 40_000,
        106 => 50_000,
        107 => 100_000,
        { } metres when metres is 10 or 20 or 30 or 40 or 50 or 100 => metres * 1_000,
        _ => 10_000,
    };

    private static int SpacingSkillLevel(double? raw) => raw switch
    {
        100 or 1 => 0,
        101 or 2 => 1,
        102 or 3 => 2,
        103 or 4 => 3,
        104 or 5 => 4,
        20 or 10 => 5,
        _ => 0,
    };

    private static int SizeSkillLevel(double? raw) => raw switch
    {
        20 or 10 => 0,
        21 => 1,
        22 or 30 => 2,
        105 or 40 => 3,
        106 or 50 => 4,
        107 or 100 => 5,
        _ => 0,
    };

    internal static bool Available(
        FleetFormationSettings settings,
        int formationSkillLevel,
        int distanceSkillLevel)
    {
        formationSkillLevel = Math.Clamp(formationSkillLevel, 0, 5);
        distanceSkillLevel = Math.Clamp(distanceSkillLevel, 0, 5);
        return settings.Type is >= 0 and <= 5
            && settings.Type <= formationSkillLevel
            && (settings.Type is not (>= 1 and <= 4) || settings.SizeSkillLevel <= distanceSkillLevel)
            && (settings.Type is not (>= 2 and <= 4) || settings.SpacingSkillLevel <= distanceSkillLevel);
    }

    private static Vector SphereOffset(int index, int count, double radius)
    {
        var zUnit = count == 1 ? 0 : 1 - 2 * (index + 0.5) / count;
        var radial = Math.Sqrt(Math.Max(0, 1 - zUnit * zUnit));
        var angle = index * 2.399963229728653;
        var fill = Math.Cbrt((index + 1.0) / count);
        var distance = radius * fill;
        return new Vector(Math.Cos(angle) * radial * distance, Math.Sin(angle) * radial * distance, zUnit * distance);
    }

    private static Vector GridOffset(
        int index,
        int count,
        double spacing,
        double radius,
        bool wall)
    {
        var columns = (int)Math.Ceiling(Math.Sqrt(count));
        var rows = (int)Math.Ceiling((double)count / columns);
        var column = index % columns;
        var row = index / columns;
        var x = (column - (columns - 1) / 2.0) * spacing;
        var y = (row - (rows - 1) / 2.0) * spacing;
        var extent = Math.Max(Math.Abs(x), Math.Abs(y));
        var scale = extent <= radius || extent == 0 ? 1 : radius / extent;
        x *= scale;
        y *= scale;
        return wall ? new Vector(x, y, 0) : new Vector(x, 0, y);
    }

    private static Vector ArrowOffset(int index, double spacing, double radius)
    {
        var row = index / 2 + 1;
        var side = index % 2 == 0 ? -1 : 1;
        var lateral = Math.Min(radius, row * spacing * 0.5);
        var depth = Math.Min(radius, row * spacing);
        return new Vector(side * lateral, 0, -depth);
    }

    private static WarpCoordinates Add(WarpCoordinates point, Vector offset) =>
        point with { X = point.X + offset.X, Y = point.Y + offset.Y, Z = point.Z + offset.Z };

    private static Vector Add(Vector left, Vector right) =>
        new(left.X + right.X, left.Y + right.Y, left.Z + right.Z);

    private static double Length(Vector value) =>
        Math.Sqrt(value.X * value.X + value.Y * value.Y + value.Z * value.Z);

    private static Vector? Unit(Vector value)
    {
        var length = Length(value);
        return length == 0 || !double.IsFinite(length)
            ? null
            : new Vector(value.X / length, value.Y / length, value.Z / length);
    }

    private static Vector Cross(Vector a, Vector b) =>
        new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);

    private static Vector Scale(Vector value, double factor) =>
        new(value.X * factor, value.Y * factor, value.Z * factor);

    private readonly record struct Vector(double X, double Y, double Z);
}
