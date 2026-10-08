using Eve.Space;
using static Eve.Services.Space.Effects.ModuleAttributes;

namespace Eve.Services.Space.Effects;

/// <summary>
/// The reapers and Catmageddon (<c>doomsdaySlash</c>: 40632, 41442, 41443, 41444, 37474): a two-point slash (<c>ActivateSlashModule</c>, SDE
/// <c>isPointTargeted</c> 2). The client's arc is fixed geometry (V24 ArcPath): its centre (the midpoint of the two points) is <c>maxRange</c> beyond the hull, and
/// its half span is <c>tan(15 degrees) * maxRange</c>, perpendicular to the centre (EveJS <c>superweaponRuntime.js</c>, the slash checks in
/// <c>prepareSuperweaponActivation</c>). The reference runs a cylinder of <c>doomsdayDamageRadius</c> whose axis sweeps from the start point to the end point
/// over the damage window, one pulse per cycle time (<c>resolveSlashDamageVolume</c>, <c>tickLanceEffect</c>). The simulation runs that sweep (protocol 28,
/// <see cref="SimAction.Strike.SweepEnd"/>).
/// </summary>
/// <remarks>
/// The reference's tolerances use the firing hull's radius, which the handler's ship snapshot does not carry. The centre is checked against
/// <c>maxRange</c> with a tolerance of 1 % (enough for a titan's hull), and the half span against its expected length and direction. Shape 2 in the static
/// data is run as a cylinder (the reference treats any shape but 3 as one). The reaper's fuel and immobility/no-jump windows are the shared mechanism's.
/// Lowsec and high security are refused by the static data's empire-space rule (<c>disallowInEmpireSpace</c> 1074), as for the lances.
/// </remarks>
internal sealed class ReaperEffect : ShipEffect
{
    public override IReadOnlyCollection<string> EffectNames { get; } = ["doomsdaySlash"];

    /// <summary>The arc's half span is <c>tan(15 degrees)</c> of its centre distance (the client's fixed ArcPath).</summary>
    private static readonly double HalfSpanPerMaxRange = Math.Tan(15 * Math.PI / 180);

    protected override string? Refusal(ModuleEffectContext module)
    {
        if (module.Aim is not { Count: 3 } start || module.AimEnd is not { Count: 3 } end)
        {
            return "SLASH_POINTS_REQUIRED";
        }
        if (DoomsdayStrike.Unit(start) is null || DoomsdayStrike.Unit(end) is null || Distance(start, end) <= 1e-6)
        {
            return "SLASH_POINTS_INVALID";
        }
        var maxRange = module.Value(MaxRange, 0);
        if (!(maxRange > 0))
        {
            return "SLASH_DATA_MISSING";
        }
        var centre = Mid(start, end);
        var halfSpan = Scale(Sub(end, start), 0.5);
        var expectedHalf = HalfSpanPerMaxRange * maxRange;
        var tolerance = maxRange * 0.01;
        // the endpoints lie farther than the arc's reach: out of range (EveJS SLASH_POINTS_OUT_OF_RANGE)
        var reach = Math.Sqrt((maxRange * maxRange) + (expectedHalf * expectedHalf)) + tolerance;
        if (Length(start) > reach || Length(end) > reach)
        {
            return "SLASH_POINTS_OUT_OF_RANGE";
        }
        var centreDistance = Length(centre);
        var radial = centreDistance > 1e-6 ? Math.Abs(Dot(halfSpan, Scale(centre, 1 / centreDistance))) : double.PositiveInfinity;
        if (Math.Abs(centreDistance - maxRange) > tolerance
            || Math.Abs(Length(halfSpan) - expectedHalf) > maxRange * 0.005
            || radial > Math.Max(1, expectedHalf * 0.005))
        {
            return "SLASH_POINTS_INVALID";
        }
        return DoomsdayStrike.Of(module, start, end, cylinder: true) is { IsValid: true } ? null : "SUPERWEAPON_REAPER_DATA_MISSING";
    }

    protected override SimEffect Build(ModuleEffectContext module)
    {
        var (duration, capacitor, durationAttribute) = module.Cycle();
        SimChange[] told = [new(module.ModuleId, CapacitorNeed, capacitor), new(module.ModuleId, durationAttribute, Three(duration))];
        return new SimEffect(
            module.ModuleId, module.Module.TypeId, module.Effect.Id, module.Effect.Guid, duration, capacitor, Math.Max(1, module.Repeat), 0, null, null, told, told, null,
            DoomsdayStrike.Of(module, module.Aim!, module.AimEnd, cylinder: true));
    }

    private static double Dot(IReadOnlyList<double> a, IReadOnlyList<double> b) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2];
    private static double Length(IReadOnlyList<double> a) => Math.Sqrt(Dot(a, a));
    private static double Distance(IReadOnlyList<double> a, IReadOnlyList<double> b) => Length(Sub(a, b));
    private static double[] Sub(IReadOnlyList<double> a, IReadOnlyList<double> b) => [a[0] - b[0], a[1] - b[1], a[2] - b[2]];
    private static double[] Scale(IReadOnlyList<double> a, double k) => [a[0] * k, a[1] * k, a[2] * k];
    private static double[] Mid(IReadOnlyList<double> a, IReadOnlyList<double> b) => [(a[0] + b[0]) / 2, (a[1] + b[1]) / 2, (a[2] + b[2]) / 2];
}
