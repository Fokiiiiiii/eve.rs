namespace Eve.Planets;

/// <summary>
/// The constants and the formulas of planetary interaction, ported from the V24.01 client
/// (eve-logic <c>eve/common/script/util/planetCommon.py</c>, <c>ecuPin.py</c>, <c>baseColony.py</c>).
/// Times are FILETIME ticks (100 ns), as the client's <c>const.SEC</c> is.
/// </summary>
public static class PlanetRules
{
    public const long Sec = 10_000_000L;
    public const long Min = 60 * Sec;
    public const long Hour = 60 * Min;

    public const int EcuMaxHeads = 10;
    public const int LinkMaxLevel = 10;
    public const int MaxWaypoints = 5;
    public const int CommandCenterMaxLevel = 5;
    public const double RadiusMin = 0.01;
    public const double RadiusMax = 0.05;
    /// <summary>The client's planetResourceMaxValue (appConst.py:1589).</summary>
    public const double MaxResourceValue = 1.21;
    /// <summary>A command center's launch cycle (spaceportPin.py:28: 60 SEC).</summary>
    public const long LaunchCooldown = 60 * Sec;
    /// <summary>PROGRAM_INSTALLATION_COOLDOWN (ecuPin.py:14): an installed program cannot be changed or stopped before it.</summary>
    public const long ProgramInstallCooldown = 1 * Min;
    public const long ExpeditedMinTime = 5 * Min;
    /// <summary>Uncollected launches are lost when their timer ends (EVE University wiki, Planetary Interaction): 5 days.</summary>
    public const long LaunchLifetime = 5L * 24 * Hour;

    /// <summary>Power output, CPU output and upgrade cost per command center level 0..5 (planetCommon.py:149-154).</summary>
    public static readonly (long Power, long Cpu, long UpgradeCost)[] CommandCenterLevels =
    [
        (6000, 1675, 0),
        (9000, 7057, 580000),
        (12000, 12136, 1510000),
        (15000, 17215, 2710000),
        (17000, 21315, 4210000),
        (19000, 25415, 6310000),
    ];

    public static long CommandCenterPower(int level) => CommandCenterLevels[level].Power;
    public static long CommandCenterCpu(int level) => CommandCenterLevels[level].Cpu;

    /// <summary>GetUpgradeCost (planetCommon.py:168): the price difference between two levels.</summary>
    public static long UpgradeCost(int from, int to) => CommandCenterLevels[to].UpgradeCost - CommandCenterLevels[from].UpgradeCost;

    /// <summary>GetBandwidth (planetCommon.py:110): volume per hour for a volume moved each cycle.</summary>
    public static double Bandwidth(double volume, long cycleTime) => volume * Hour / cycleTime;

    /// <summary>GetExpeditedTransferTime (planetCommon.py:84): ceil(max(5 min, volume / bandwidth hours)).</summary>
    public static long ExpeditedTransferTime(double linkBandwidth, double volume) =>
        (long)Math.Ceiling(Math.Max(ExpeditedMinTime, volume / linkBandwidth * Hour));

    /// <summary>GetProgramLengthFromHeadRadius (planetCommon.py:198).</summary>
    public static double ProgramLength(double headRadius) => (headRadius - RadiusMin) / (RadiusMax - RadiusMin) * 335 + 1;

    /// <summary>GetCycleTimeFromProgramLength (planetCommon.py:202), in hours.</summary>
    public static double CycleHours(double programLength) =>
        0.25 * Math.Pow(2, Math.Max(0, Math.Floor(Math.Log(programLength / 25.0, 2)) + 1));

    /// <summary>GetMaxOutput of an ECU (ecuPin.py:291): the output per cycle with no decay, in the client's integer steps.</summary>
    public static double EcuMaxOutput(long qtyPerCycle, long cycleTime, double noise)
    {
        var scaled = (long)((noise + 1) * qtyPerCycle);
        return (double)(scaled * cycleTime / Sec) / 900.0;
    }

    /// <summary>
    /// GetProgramOutput of an ECU (ecuPin.py:302): the depletion and noise curve of a program, cycle by cycle.
    /// <paramref name="decay"/> is attribute 1683, <paramref name="noise"/> attribute 1687.
    /// </summary>
    public static long EcuOutput(long currentTime, long baseValue, long cycleTime, long startTime, double decay, double noise)
    {
        var timeDiff = currentTime - startTime;
        long cycleNum = cycleTime <= 0 ? 0 : Math.Max((timeDiff + Sec) / cycleTime - 1, 0);
        var barWidth = cycleTime / (double)Sec / 900.0;
        var t = (cycleNum + 0.5) * barWidth;
        var decayValue = baseValue / (1 + t * decay);
        var phaseShift = Math.Pow(baseValue, 0.7);
        var sinA = Math.Cos(phaseShift + t / 12.0);
        var sinB = Math.Cos(phaseShift / 2 + t / 5.0);
        var sinC = Math.Cos(t / 2.0);
        var sinStuff = Math.Max(0, (sinA + sinB + sinC) / 3);
        var barHeight = decayValue * (1 + noise * sinStuff);
        return (long)(barWidth * barHeight);
    }

    /// <summary>Round half away from zero, as Python 2's <c>round</c>.</summary>
    public static double PyRound(double value) => Math.Round(value, MidpointRounding.AwayFromZero);
}

/// <summary>A point on a planet's surface (surfacePoint.py): theta is the longitude, phi the latitude.</summary>
public readonly record struct SurfacePoint(double Radius, double Theta, double Phi)
{
    public (double X, double Y, double Z) Vector()
    {
        var radSinPhi = Radius * Math.Sin(Phi);
        return (radSinPhi * Math.Cos(Theta), Radius * Math.Cos(Phi), radSinPhi * Math.Sin(Theta));
    }

    /// <summary>GetAngleBetween (surfacePoint.py:112): the central angle in radians.</summary>
    public double AngleTo(SurfacePoint other)
    {
        var a = Vector();
        var b = other.Vector();
        var dot = (a.X * b.X + a.Y * b.Y + a.Z * b.Z) / Radius / other.Radius;
        return Math.Acos(Math.Min(1.0, dot));
    }

    /// <summary>GetDistanceToOther (surfacePoint.py:118): the arc length on this radius, in the radius' unit.</summary>
    public double DistanceTo(SurfacePoint other) => Radius * AngleTo(other);
}
