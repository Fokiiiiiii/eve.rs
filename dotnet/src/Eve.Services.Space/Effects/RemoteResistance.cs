using Eve.Space;

namespace Eve.Services.Space.Effects;

/// <summary>
/// What a target's resistance does to a remote effect that lands on it (FP-8): the target's resistance attribute, a multiplier from 0 (fully resisted) to 1, that
/// weakens the effect's strength. EVE.js's rule (<c>hostileModuleRuntime.js</c> <c>resolveTargetResistanceMultiplier</c>, <c>assistanceModuleRuntime.js</c>
/// <c>resolveRemoteAssistanceMultiplier</c>): the attribute clamped to 0..1, 1 when the target does not have it. The attributes are the ones EVE.js names (2112 to
/// 2116, 2045; a remote assistance module names its own in 2138, which defaults to the remote repair impedance 2116). The host resolves the multiplier when the
/// effect starts and keeps it for the effect's life; EVE.js resolves it at each cycle (a target's resistance rarely changes during an effect).
/// </summary>
public static class RemoteResistance
{
    /// <summary>sensorDampenerResistance: a sensor dampener's range and resolution changes.</summary>
    public const int SensorDampenerResistance = 2112;

    /// <summary>weaponDisruptionResistance: a tracking disruptor and a guidance disruptor.</summary>
    public const int WeaponDisruptionResistance = 2113;

    /// <summary>targetPainterResistance: a target painter's signature change.</summary>
    public const int TargetPainterResistance = 2114;

    /// <summary>stasisWebifierResistance: a stasis webifier and a tackle's web.</summary>
    public const int StasisWebifierResistance = 2115;

    /// <summary>remoteRepairImpedance: the default for a remote repair, transfer or mutadaptive repair.</summary>
    public const int RemoteRepairImpedance = 2116;

    /// <summary>energyWarfareResistance: an energy neutralizer and a nosferatu.</summary>
    public const int EnergyWarfareResistance = 2045;

    /// <summary>remoteResistanceID: the attribute of a remote assistance module that names the target resistance it uses.</summary>
    public const int RemoteResistanceId = 2138;

    /// <summary>
    /// The resistance attribute that weakens a remote, or 0 for none (a scramble and a jam keep their own rules: neither is named by EVE.js's resistances).
    /// <paramref name="moduleAttribute"/> reads the attribute of the module that runs the effect.
    /// </summary>
    public static int AttributeOf(SimRemote what, Func<int, double> moduleAttribute) => what switch
    {
        SimRemote.Web or SimRemote.Tackle => StasisWebifierResistance,
        SimRemote.Paint => TargetPainterResistance,
        SimRemote.Damp => SensorDampenerResistance,
        SimRemote.Disrupt or SimRemote.GuidanceDisrupt => WeaponDisruptionResistance,
        SimRemote.Neutralize or SimRemote.Drain => EnergyWarfareResistance,
        SimRemote.Repair or SimRemote.MutadaptiveRepair or SimRemote.Transfer => (int)moduleAttribute(RemoteResistanceId) is var named && named > 0 ? named : RemoteRepairImpedance,
        _ => 0,
    };

    /// <summary>The multiplier the target's attribute gives: the value clamped to 0..1, or 1 when the target does not have that attribute (or it is not finite).</summary>
    public static double MultiplierOf(int attributeId, IReadOnlyDictionary<int, double> targetAttributes) =>
        attributeId <= 0 || !targetAttributes.TryGetValue(attributeId, out var value) || !double.IsFinite(value) ? 1 : Math.Clamp(value, 0, 1);

    /// <summary>The remote with its strength weakened by <paramref name="multiplier"/> (the amount, the percentages or the web part of a tackle); a multiplier of 1 changes nothing.</summary>
    public static SimRemote Weakened(SimRemote what, double multiplier) => multiplier >= 1 ? what : what switch
    {
        SimRemote.Repair repair => repair with { Amount = repair.Amount * multiplier },
        SimRemote.MutadaptiveRepair repair => repair with { Amount = repair.Amount * multiplier },
        SimRemote.Transfer transfer => transfer with { Amount = transfer.Amount * multiplier },
        SimRemote.Neutralize neutralize => neutralize with { Amount = neutralize.Amount * multiplier },
        SimRemote.Drain drain => drain with { Amount = drain.Amount * multiplier },
        SimRemote.Web web => web with { Percent = web.Percent * multiplier },
        SimRemote.Tackle tackle => tackle with { WebPercent = tackle.WebPercent * multiplier },
        SimRemote.Paint paint => paint with { Percent = paint.Percent * multiplier },
        SimRemote.Damp damp => damp with
        {
            RangePercent = damp.RangePercent * multiplier,
            ResolutionPercent = damp.ResolutionPercent * multiplier,
            GravimetricStrengthPercent = damp.GravimetricStrengthPercent * multiplier,
            LadarStrengthPercent = damp.LadarStrengthPercent * multiplier,
            MagnetometricStrengthPercent = damp.MagnetometricStrengthPercent * multiplier,
            RadarStrengthPercent = damp.RadarStrengthPercent * multiplier,
        },
        SimRemote.Disrupt disrupt => disrupt with
        {
            RangePercent = disrupt.RangePercent * multiplier,
            FalloffPercent = disrupt.FalloffPercent * multiplier,
            TrackingPercent = disrupt.TrackingPercent * multiplier,
        },
        SimRemote.GuidanceDisrupt guidance => guidance with
        {
            VelocityPercent = guidance.VelocityPercent * multiplier,
            DelayPercent = guidance.DelayPercent * multiplier,
            AoeVelocityPercent = guidance.AoeVelocityPercent * multiplier,
            AoeCloudPercent = guidance.AoeCloudPercent * multiplier,
        },
        _ => what,
    };
}
