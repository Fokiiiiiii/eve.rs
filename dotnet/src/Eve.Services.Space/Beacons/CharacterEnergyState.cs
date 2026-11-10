using System.Globalization;
using System.Text.Json.Nodes;
using Eve.Marshal;

namespace Eve.Services.Space.Beacons;

/// <summary>The character-energy state shared by the CRAB link transaction and the client RPC.</summary>
public sealed record CharacterEnergyState(
    double EnergyLevel,
    long LastRechargeAtUnixMs,
    double EnergyIncreasePerRechargeTick,
    double RechargeTickPeriodSeconds,
    double MinEnergyLevel,
    double QuiescentEnergyLevel)
{
    private const double DefaultEnergyIncreasePerRechargeTick = 1;
    private const double DefaultRechargeTickPeriodSeconds = 792;

    /// <summary>Host-compatible defaults for a character without a persisted CharacterEnergyState row.</summary>
    public static CharacterEnergyState Default(long nowUnixMs) => new(
        100, nowUnixMs, DefaultEnergyIncreasePerRechargeTick, DefaultRechargeTickPeriodSeconds, 0, 100);

    /// <summary>Builds a character's state from the loaded legacy GameStore row.</summary>
    public static CharacterEnergyState FromGameStore(JsonObject? raw, long nowUnixMs)
    {
        var nowFileTime = Py.FileTimeFromUnixMs(nowUnixMs);
        var minimum = Read(raw, "minEnergyLevel", "_minEnergyLevel", 0);
        var capacity = Read(raw, "quiescentEnergyLevel", "_quiescentEnergyLevel", 100);
        if (capacity <= minimum) capacity = minimum + 1;
        var increase = Math.Max(double.Epsilon, Read(raw, "energyIncreasePerRechargeTick", "_energyIncreasePerRechargeTick", DefaultEnergyIncreasePerRechargeTick));
        var period = Math.Max(double.Epsilon, Read(raw, "rechargeTickPeriod", "_rechargeTickPeriod", DefaultRechargeTickPeriodSeconds));
        var energy = Math.Clamp(Read(raw, "energyLevel", "_energyLevel", capacity), minimum, capacity);
        var lastFileTime = ReadLong(raw, "lastRechargeTickTimestamp", "_lastRechargeTickTimestamp", nowFileTime);
        return new CharacterEnergyState(energy, UnixMsFromFileTime(lastFileTime), increase, period, minimum, capacity);
    }

    /// <summary>Calculates the client-visible state at a given wall-clock time.</summary>
    public CharacterEnergyState At(long nowUnixMs)
    {
        if (nowUnixMs < LastRechargeAtUnixMs)
            return this;

        var elapsedSeconds = (nowUnixMs - LastRechargeAtUnixMs) / 1000d;
        var energy = Math.Min(QuiescentEnergyLevel,
            EnergyLevel + elapsedSeconds / RechargeTickPeriodSeconds * EnergyIncreasePerRechargeTick);
        return this with { EnergyLevel = Math.Clamp(energy, MinEnergyLevel, QuiescentEnergyLevel), LastRechargeAtUnixMs = nowUnixMs };
    }

    /// <summary>The six-field ObjectEx1 consumed by CharacterEnergySvc.</summary>
    public PyValue ToClientValue(long nowUnixMs)
    {
        var state = At(nowUnixMs);
        return new PyObjectEx(ObjectExKind.Ex1,
            Py.Tuple(new PyToken("characterenergy.characterEnergyState.CharacterEnergyState"), Py.List(
                new PyReal(state.EnergyLevel),
                new PyLong(Py.FileTimeFromUnixMs(state.LastRechargeAtUnixMs)),
                new PyReal(state.EnergyIncreasePerRechargeTick),
                new PyReal(state.RechargeTickPeriodSeconds),
                new PyReal(state.MinEnergyLevel),
                new PyReal(state.QuiescentEnergyLevel))), [], []);
    }

    public static long UnixMsFromFileTime(long fileTime) =>
        (fileTime - 116_444_736_000_000_000L) / 10_000;

    private static double Read(JsonObject? source, string first, string second, double fallback) =>
        double.TryParse((source?[first] ?? source?[second])?.ToString(), NumberStyles.Float,
            CultureInfo.InvariantCulture, out var value) && double.IsFinite(value) ? value : fallback;

    private static long ReadLong(JsonObject? source, string first, string second, long fallback) =>
        long.TryParse((source?[first] ?? source?[second])?.ToString(), NumberStyles.Integer,
            CultureInfo.InvariantCulture, out var value) ? value : fallback;
}
