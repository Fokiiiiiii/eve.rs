namespace Eve.Ships;

/// <summary>
/// reference (<c>miningMath.js</c>): randomized rounding and a critical yield.
/// </summary>
public enum MiningSource
{
    /// <summary>A mining drone: floor rounding, no critical yield.</summary>
    Drone,

    /// <summary>A mining module (laser, strip miner): randomized rounding and critical yield.</summary>
    Module,
}

/// <summary>
/// The random draws of one mining cycle, each a uniform value from 0 to 1 that the caller supplies. The yield never draws its own.
/// <c>Waste</c> is compared as a whole percent, <c>floor(Waste * 100)</c>, as the reference does. The other four are thresholds for
/// the randomized rounding (the remainder rounds up when it is greater than the roll) and the critical chance (it fires when the chance
/// is greater than the roll). A roll the source does not use is still checked for range, and then ignored.
/// </summary>
public readonly record struct MiningRolls(double Waste, double NormalRounding, double WasteRounding, double Crit, double CritRounding)
{
    /// <summary>
    /// The rolls of a module cycle from the three draws the simulation sends (the waste, the rounding and the crit). The waste and the critical rounding have no
    /// draw of their own on the wire, so each reuses its branch's draw, rescaled to a fresh uniform value: a draw below a chance is uniform on [0, chance).
    /// </summary>
    public static MiningRolls ForModule(double wasteRoll, double roundingRoll, double critRoll, double wasteProbability, double critProbability) => new(
        wasteRoll, roundingRoll, Share(wasteRoll, wasteProbability / 100d), critRoll, Share(critRoll, critProbability));

    private static double Share(double roll, double chance) => chance > 0 ? Math.Min(1d, roll / chance) : 0d;
}

/// <summary>
/// The inputs of one mining cycle. <c>Volume</c> is what the cycle mines in cubic metres before the cargo clamp and the efficiency (the
/// <c>miningAmount</c> of the module or drone). <c>ClampFactor</c> is the share of it that fits in the hold. <c>UnitVolume</c> is the
/// volume of one unit of the ore, and <c>AsteroidQuantity</c> is the units left in the rock. <c>WasteProbability</c> is a percent (0 to
/// 100), and <c>CritProbability</c> is a chance (0 to 1), as the reference reads them from the SDE attributes.
/// </summary>
public sealed record MiningCycle(
    MiningSource Source,
    double Volume,
    double UnitVolume,
    long AsteroidQuantity,
    double WasteVolumeMultiplier,
    double WasteProbability,
    double CritQuantityMultiplier,
    double CritProbability,
    double ClampFactor = 1,
    double Efficiency = 1);

/// <summary>
/// The yield of one cycle. <c>NormalQuantity</c> is what goes to the hold, and <c>WastedQuantity</c> is what the rock loses and nobody
/// keeps. <c>CriticalQuantity</c> is the bonus of a critical yield: it is extra to the hold and does not come out of the rock.
/// </summary>
public sealed record MiningResult(
    double ClampFactor,
    double UnitVolume,
    double NormalVolume,
    double WastedVolume,
    long NormalQuantity,
    long WastedQuantity,
    bool Critical,
    long CriticalQuantity)
{
    /// <summary>The volume of the critical bonus.</summary>
    public double CriticalVolume => CriticalQuantity * UnitVolume;

    /// <summary>The units the rock loses: the normal yield and the waste.</summary>
    public long DepletedQuantity => NormalQuantity + WastedQuantity;

    /// <summary>The units that reach the hold: the normal yield and the critical bonus.</summary>
    public long TransferredQuantity => NormalQuantity + CriticalQuantity;
}

/// <summary>
/// The yield of a mining cycle as a pure function: no clock, no randomness and no I/O, so the same cycle and rolls always give the same
/// 23-105). The reference's draw order is not kept, because every draw is passed in. Callers apply the rock, the hold and the ledger.
/// </summary>
public static class MiningYield
{
    /// <summary>Computes the yield of one cycle from explicit rolls.</summary>
    /// <exception cref="ArgumentOutOfRangeException">A volume, unit volume, quantity or roll is not finite or is out of range.</exception>
    public static MiningResult ComputeMiningResult(MiningCycle cycle, MiningRolls rolls)
    {
        ArgumentNullException.ThrowIfNull(cycle);
        if (!Enum.IsDefined(cycle.Source))
        {
            throw new ArgumentOutOfRangeException(nameof(cycle), cycle.Source, "The mining source is not known.");
        }
        RequireNonNegative(cycle.Volume, nameof(cycle.Volume));
        RequirePositive(cycle.UnitVolume, nameof(cycle.UnitVolume));
        if (cycle.AsteroidQuantity < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(cycle), cycle.AsteroidQuantity, "The asteroid quantity cannot be negative.");
        }
        RequireFinite(cycle.WasteVolumeMultiplier, nameof(cycle.WasteVolumeMultiplier));
        RequireFinite(cycle.WasteProbability, nameof(cycle.WasteProbability));
        RequireFinite(cycle.CritQuantityMultiplier, nameof(cycle.CritQuantityMultiplier));
        RequireFinite(cycle.CritProbability, nameof(cycle.CritProbability));
        RequireFinite(cycle.ClampFactor, nameof(cycle.ClampFactor));
        RequireFinite(cycle.Efficiency, nameof(cycle.Efficiency));
        RequireRoll(rolls.Waste, nameof(rolls.Waste));
        RequireRoll(rolls.NormalRounding, nameof(rolls.NormalRounding));
        RequireRoll(rolls.WasteRounding, nameof(rolls.WasteRounding));
        RequireRoll(rolls.Crit, nameof(rolls.Crit));
        RequireRoll(rolls.CritRounding, nameof(rolls.CritRounding));

        // Ranges are clamped as the reference clamps them; a negative multiplier is zero.
        var source = cycle.Source;
        var clampFactor = Math.Clamp(cycle.ClampFactor, 0d, 1d);
        var efficiency = Math.Clamp(cycle.Efficiency, 0d, 1d);
        var wasteProbability = Math.Clamp(cycle.WasteProbability, 0d, 100d);
        var wasteMultiplier = Math.Max(0d, cycle.WasteVolumeMultiplier);
        var critMultiplier = Math.Max(0d, cycle.CritQuantityMultiplier);
        var critProbability = Math.Clamp(cycle.CritProbability, 0d, 1d);

        var normalVolume = cycle.Volume * clampFactor;
        var wastedVolume = normalVolume > 0 && wasteProbability > WastePercent(rolls.Waste)
            ? normalVolume * wasteMultiplier * efficiency
            : 0d;

        // The normal yield comes first and the waste takes only what the rock still has after it.
        var normalQuantity = ToQuantity(Math.Min(cycle.AsteroidQuantity, Round(normalVolume / cycle.UnitVolume * efficiency, source, rolls.NormalRounding)));
        var remainingAfterNormal = cycle.AsteroidQuantity - normalQuantity;
        var wastedQuantity = ToQuantity(Math.Min(remainingAfterNormal, Round(wastedVolume / cycle.UnitVolume, source, rolls.WasteRounding)));

        // A critical yield exists only for a module, and only when something was mined and the chance fires.
        var critical = source == MiningSource.Module && normalQuantity > 0 && critProbability > rolls.Crit;
        var criticalQuantity = critical ? ToQuantity(Round(normalQuantity * critMultiplier, source, rolls.CritRounding)) : 0L;

        return new MiningResult(clampFactor, cycle.UnitVolume, normalVolume, wastedVolume, normalQuantity, wastedQuantity, critical, criticalQuantity);
    }

    /// <summary>
    /// The whole part of a non-negative amount, plus one when its remainder is greater than the roll (randomized rounding). A drone
    /// (<see cref="MiningSource.Drone"/>) only takes the whole part.
    /// </summary>
    private static double Round(double value, MiningSource source, double roll)
    {
        var numeric = Math.Max(0d, value);
        var whole = Math.Truncate(numeric);
        return source == MiningSource.Module && numeric - whole > roll ? whole + 1d : whole;
    }

    /// <summary>The integer percent the waste roll stands for (the reference clamps the roll below one, so 99 is the top).</summary>
    private static int WastePercent(double roll) => (int)Math.Floor(Math.Clamp(roll, 0d, 0.999999d) * 100d);

    private static long ToQuantity(double whole)
    {
        if (!(whole < long.MaxValue))
        {
            throw new ArgumentOutOfRangeException(nameof(whole), whole, "The yield is too large to count in units.");
        }
        return (long)whole;
    }

    private static void RequireFinite(double value, string name)
    {
        if (!double.IsFinite(value))
        {
            throw new ArgumentOutOfRangeException(name, value, "The value must be a finite number.");
        }
    }

    private static void RequireNonNegative(double value, string name)
    {
        if (!double.IsFinite(value) || value < 0)
        {
            throw new ArgumentOutOfRangeException(name, value, "The value must be a finite number that is not negative.");
        }
    }

    private static void RequirePositive(double value, string name)
    {
        if (!double.IsFinite(value) || value <= 0)
        {
            throw new ArgumentOutOfRangeException(name, value, "The value must be a finite number greater than zero.");
        }
    }

    private static void RequireRoll(double roll, string name)
    {
        if (!(roll >= 0d && roll <= 1d))
        {
            throw new ArgumentOutOfRangeException(name, roll, "A roll is a uniform value from 0 to 1.");
        }
    }
}
