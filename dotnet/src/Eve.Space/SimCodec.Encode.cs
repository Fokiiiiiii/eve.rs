using System.Buffers.Binary;
using System.Text;

namespace Eve.Space;

public static partial class SimCodec
{
    private static void WriteOptionalWeapon(Writer w, SimNpcWeapon? weapon)
    {
        WriteWeapon(w, weapon ?? new SimNpcWeapon(
            0, 0, 0, 0, 0, string.Empty, [0d, 0d, 0d, 0d], 0, 0, 0, 0));
    }

    private static void WriteWeapon(Writer w, SimNpcWeapon weapon)
    {
        w.I64(weapon.ModuleId);
        w.I32(weapon.EffectId);
        w.F64(weapon.CycleMs);
        w.F64(weapon.CapacitorNeed);
        WriteTurret(w, new SimTurret(
            weapon.WeaponTypeId, weapon.Fx, weapon.RawDamage, weapon.OptimalRange, weapon.Falloff, weapon.TrackingSpeed, weapon.SignatureResolution, weapon.Charge, weapon.Missile,
            RequiresGunnery: weapon.RequiresGunnery));
    }

    /// <summary>A strike's body (tags 13 and 15): the shape, the aim, the numbers and the sweep (see <see cref="ReadStrike"/>).</summary>
    private static void WriteStrike(Writer w, byte tag, SimAction.Strike strike, string parameter)
    {
        if (!strike.IsValid)
        {
            throw new ArgumentException("a strike has a unit aim, four kinds of damage and positive cadence and length", parameter);
        }
        w.U8(tag); w.U8(strike.Shape);
        foreach (var component in strike.Direction) w.F64(component);
        w.F64(strike.Length); w.F64(strike.Radius); w.F64(strike.WarningMs); w.F64(strike.PulseMs); w.I32(strike.Pulses);
        foreach (var number in strike.Damage) w.F64(number);
        w.F64(strike.SignatureRadius); w.F64(strike.ImmobileMs); w.F64(strike.NoJumpMs);
        w.F64(strike.NeutAmount); w.F64(strike.NeutRadius); w.F64(strike.NeutSignatureRadius);
        w.F64(strike.DebuffMs);
        w.U8((byte)(strike.SweepEnd is null ? 0 : 1));
        foreach (var component in strike.SweepEnd ?? [0.0, 0.0, 0.0]) w.F64(component);
        // protocol 37: a lance's beacon, a flag then its offset from the firing ship (zeros without a beacon)
        w.U8((byte)(strike.Beacon is null ? 0 : 1));
        foreach (var component in strike.Beacon ?? [0.0, 0.0, 0.0]) w.F64(component);
    }

    /// <summary>A list of ship ids: a u32 count, then each id as an i64 (the simulation's <c>write_ids</c>).</summary>
    private static void WriteIds(Writer w, IReadOnlyList<long> ids)
    {
        w.U32((uint)ids.Count);
        foreach (var id in ids) w.I64(id);
    }

    private static void WriteVector(Writer w, IReadOnlyList<double> v)
    {
        foreach (var number in v)
        {
            w.F64(number);
        }
    }

    private static void WriteOptionalVector(Writer w, IReadOnlyList<double>? vector)
    {
        if (vector is null)
        {
            w.U8(0);
            return;
        }
        if (vector.Count != 3 || vector.Any(value => !double.IsFinite(value)))
            throw new ArgumentException("A simulation pose needs three finite coordinates.", nameof(vector));
        w.U8(1);
        WriteVector(w, vector);
    }

    private static void WriteIdentity(Writer w, SimNpcIdentity i)
    {
        w.I64(i.HostileResponseThreshold);
        w.I64(i.FriendlyResponseThreshold);
        w.F64(i.AggressionRange);
        w.U32((uint)i.Weapons.Count);
        foreach (var weapon in i.Weapons)
        {
            WriteWeapon(w, weapon);
        }
        WriteWreckKind(w, i.Wreck);
        w.U8((byte)(i.Hull ? 1 : 0));
        w.U8((byte)(i.HostWreck ? 1 : 0));
    }

    private static void WriteWreckKind(Writer w, SimWreckKind? kind)
    {
        if (kind is { } wreck)
        {
            w.U8(1);
            w.I32(wreck.TypeId);
            w.Text(wreck.Name);
            w.I32(wreck.GraphicId);
        }
        else
        {
            w.U8(0);
        }
    }

    private static void WriteCredit(Writer w, SimCredit credit)
    {
        w.I64(credit.CharacterId);
        w.I64(credit.CorporationId);
        w.I64(credit.AllianceId);
        w.I64(credit.WarFactionId);
    }

    private static void WriteNumbers(Writer w, SimNumbers n)
    {
        w.F64(n.Mass);
        w.F64(n.MaxVelocity);
        w.F64(n.SignatureRadius);
    }

    private static void WriteChanges(Writer w, IReadOnlyList<SimChange> changes)
    {
        w.U32((uint)changes.Count);
        foreach (var change in changes)
        {
            w.I64(change.ItemId);
            w.I32(change.AttributeId);
            w.F64(change.Value);
        }
    }

    /// <summary>What a launcher flies (a turret's missile, a defender's): the charge's type and group and name, the ball's radius, mass and agility, the hit points, the speed,
    /// the flight time, and the explosion's radius, velocity, damage reduction factor and sensitivity (<c>SimCodec.Decode</c> reads them in this order).</summary>
    private static void WriteMissile(Writer w, SimMissile missile)
    {
        w.I32(missile.ChargeTypeId);
        w.I32(missile.GroupId);
        w.Text(missile.Name);
        w.F64(missile.Radius);
        w.F64(missile.Mass);
        w.F64(missile.Agility);
        w.F64(missile.HitPoints);
        w.F64(missile.MaxVelocity);
        w.F64(missile.FlightTimeMs);
        w.F64(missile.ExplosionRadius);
        w.F64(missile.ExplosionVelocity);
        w.F64(missile.DamageReductionFactor);
        w.F64(missile.DamageReductionSensitivity);
        // the damage over time of a breacher pod (protocol 42): a flag, then its duration, its cap per tick and its percentage per tick
        if (missile.Dot is { } dot)
        {
            w.U8(1);
            w.F64(dot.DurationMs);
            w.F64(dot.MaxDamagePerTick);
            w.F64(dot.MaxHpPercentPerTick);
        }
        else
        {
            w.U8(0);
        }
    }

    private static void WriteTurret(Writer w, SimTurret t)
    {
        w.I32(t.WeaponTypeId);
        w.Text(t.Fx);
        WriteVector(w, t.RawDamage);
        w.F64(t.OptimalRange);
        w.F64(t.Falloff);
        w.F64(t.TrackingSpeed);
        w.F64(t.SignatureResolution);
        if (t.Charge is { } charge)
        {
            w.U8(1);
            w.I32(charge.TypeId);
            w.I32(charge.Flag);
            w.I64(charge.Rounds);
            w.I64(charge.PerCycle);
            w.U8(charge.Reloads ? (byte)1 : (byte)0);
        }
        else
        {
            w.U8(0);
        }
        if (t.Missile is { } missile)
        {
            w.U8(1);
            WriteMissile(w, missile);
        }
        else
        {
            w.U8(0);
        }
        if (t.Spool is { } spool)
        {
            w.U8(1);
            w.F64(spool.PerCycle);
            w.F64(spool.Max);
        }
        else
        {
            w.U8(0);
        }
        if (t.Aoe is { } aoe)
        {
            w.U8(1);
            w.F64(aoe.ExplosionRadius);
            w.F64(aoe.ExplosionVelocity);
            w.F64(aoe.DamageReductionFactor);
            w.F64(aoe.DamageReductionSensitivity);
        }
        else
        {
            w.U8(0);
        }
        if (t.Arc is { } arc)
        {
            w.U8(1);
            w.F64(arc.Range);
            w.I32(arc.Targets);
        }
        else
        {
            w.U8(0);
        }
        w.U8(t.RequiresGunnery ? (byte)1 : (byte)0);
    }

/// <summary>A deployable's charge: its type, its group, its name and the radius of its ball (a positive number).</summary>
private static void WriteDeployable(Writer w, SimDeployable ball, string parameter)
{
    if (!(double.IsFinite(ball.Radius) && ball.Radius > 0))
    {
        throw new ArgumentException("a deployable's ball has a positive radius", parameter);
    }
    w.I32(ball.ChargeTypeId); w.I32(ball.GroupId); w.Text(ball.Name); w.F64(ball.Radius);
}

private static void WriteAction(Writer w, SimAction action)
    {
        switch (action)
        {
            case SimAction.Repair repair:
                w.U8(0); w.U8((byte)repair.Layer); w.U8(repair.AtCycleEnd ? (byte)1 : (byte)0); w.F64(repair.Amount);
                break;
            case SimAction.EcmBurst burst:
                w.U8(3); w.F64(burst.Range); foreach (var number in burst.Strength) w.F64(number);
                break;
            case SimAction.Charged charged:
                w.U8(4); w.I32(charged.ChargeTypeId); w.I32(charged.ChargeFlag); w.I64(charged.ChargePerCycle); w.I64(charged.Charges);
                WriteAction(w, charged.Action);
                w.U8(charged.Fallback is null ? (byte)0 : (byte)1);
                if (charged.Fallback is { } fallback) WriteAction(w, fallback);
                break;
            case SimAction.Cloak cloak:
                w.U8(5); w.U8(cloak.WarpSafe ? (byte)1 : (byte)0);
                break;
            case SimAction.WarpBubble bubble:
                if (!(double.IsFinite(bubble.RangeMeters) && bubble.RangeMeters > 0))
                {
                    throw new ArgumentException("a warp bubble has a positive range", nameof(action));
                }
                w.U8(16); w.F64(bubble.RangeMeters);
                break;
            case SimAction.WarpImmunity:
                w.U8(17);
                break;
            case SimAction.Defender defender:
                if (defender.Damage.Count != 4 || !defender.Damage.All(d => double.IsFinite(d) && d >= 0))
                {
                    throw new ArgumentException("a defender hurts with four damages, finite and not negative", nameof(action));
                }
                w.U8(22); WriteMissile(w, defender.Missile);
                foreach (var number in defender.Damage) w.F64(number);
                break;
            case SimAction.StasisBurst stasis:
                if (!(double.IsFinite(stasis.RangeMeters) && stasis.RangeMeters > 0 && double.IsFinite(stasis.DurationMs) && stasis.DurationMs > 0 && double.IsFinite(stasis.SpeedFactorPercent)))
                {
                    throw new ArgumentException("a stasis burst has a positive range and duration and a finite speed factor", nameof(action));
                }
                w.U8(21); w.F64(stasis.RangeMeters); w.F64(stasis.DurationMs); w.F64(stasis.SpeedFactorPercent);
                break;
            case SimAction.ScrambleStatus status:
                if (!double.IsFinite(status.Delta))
                {
                    throw new ArgumentException("a scramble status change is a finite number", nameof(action));
                }
                w.U8(18); w.F64(status.Delta);
                break;
            case SimAction.Panic panic:
                if (!(double.IsFinite(panic.RangeMeters) && panic.RangeMeters >= 0))
                {
                    throw new ArgumentException("the invulnerability core has a reach of zero or more metres", nameof(action));
                }
                w.U8(29); w.F64(panic.RangeMeters);
                break;
            case SimAction.CommandBurst burst:
                if (!(double.IsFinite(burst.RangeMeters) && burst.RangeMeters >= 0))
                {
                    throw new ArgumentException("a command burst has a reach of zero or more metres", nameof(action));
                }
                w.U8(28); w.F64(burst.RangeMeters);
                break;
            case SimAction.CynoField cyno:
                if (!(double.IsFinite(cyno.LifetimeMs) && cyno.LifetimeMs > 0))
                {
                    throw new ArgumentException("a cynosural field has a positive lifetime", nameof(action));
                }
                w.U8(27); WriteDeployable(w, cyno.Ball, nameof(action)); w.F64(cyno.LifetimeMs);
                break;
            case SimAction.WarpProbe probe:
                if (!(double.IsFinite(probe.RangeMeters) && probe.RangeMeters > 0 && double.IsFinite(probe.LifetimeMs) && probe.LifetimeMs > 0))
                {
                    throw new ArgumentException("a warp probe has a positive range and lifetime", nameof(action));
                }
                w.U8(19); WriteDeployable(w, probe.Ball, nameof(action)); w.F64(probe.RangeMeters); w.F64(probe.LifetimeMs);
                break;
            case SimAction.Bomb bomb:
                if (bomb.Damage.Count != 4 || bomb.Ecm.Count != 4 || !(double.IsFinite(bomb.LifetimeMs) && bomb.LifetimeMs > 0 && double.IsFinite(bomb.BlastRadius) && bomb.BlastRadius > 0)
                    || bomb.Damage.Concat(bomb.Ecm).Append(bomb.Neutralize).Any(n => !double.IsFinite(n) || n < 0))
                {
                    throw new ArgumentException("a bomb has four damages and four ECM strengths, finite and not negative, and a positive lifetime and blast radius", nameof(action));
                }
                w.U8(20); WriteDeployable(w, bomb.Ball, nameof(action)); w.U8((byte)bomb.Kind); w.F64(bomb.LifetimeMs); w.F64(bomb.BlastRadius);
                foreach (var number in bomb.Damage) w.F64(number);
                foreach (var number in bomb.Ecm) w.F64(number);
                w.F64(bomb.Neutralize);
                break;
            case SimAction.MicroJumpDrive jump:
                w.U8(6); w.F64(jump.DistanceMeters); w.I64(jump.CooldownMs);
                break;
            case SimAction.Mining mining:
                w.U8(7); w.U8(mining.Family); w.F64(mining.MiningAmountM3); w.F64(mining.WasteVolumeMultiplier); w.F64(mining.WasteProbability);
                w.F64(mining.CritProbability); w.F64(mining.CritQuantityMultiplier);
                break;
            case SimAction.Salvage salvage:
                w.U8(8); w.F64(salvage.AccessBonusPercent);
                break;
            case SimAction.Scan scan:
                w.U8(26); w.U8(scan.Kind); w.F64(scan.OptimalMeters); w.F64(scan.FalloffMeters);
                break;
            case SimAction.Tractor tractor:
                w.U8(9); w.F64(tractor.VelocityMetersPerSecond); w.F64(tractor.HoldDistanceMeters); w.F64(tractor.RangeMeters);
                break;
            case SimAction.PassiveLock:
                w.U8(10);
                break;
            case SimAction.AutoTarget autoTarget:
                w.U8(11); w.F64(autoTarget.RangeMeters);
                break;
            case SimAction.Strike strike:
                WriteStrike(w, 13, strike, nameof(action));
                break;
            case SimAction.Field field:
                if (!field.IsValid)
                {
                    throw new ArgumentException("a transport field is a sphere strike with a finite destination and a warp block, and lists of ship ids", nameof(action));
                }
                WriteStrike(w, 15, field.Sphere, nameof(action));
                foreach (var component in field.Destination) w.F64(component);
                w.F64(field.WarpBlockMs);
                WriteIds(w, field.Moved);
                WriteIds(w, field.Held);
                break;
            case SimAction.Doomsday doomsday:
                if (!doomsday.IsValid)
                {
                    throw new ArgumentException("a titan doomsday has finite, non-negative delay, damage and windows", nameof(action));
                }
                w.U8(14); w.F64(doomsday.DelayMs);
                foreach (var number in doomsday.Damage) w.F64(number);
                w.F64(doomsday.ImmobileMs); w.F64(doomsday.NoJumpMs);
                break;
            case SimAction.Kamikaze kamikaze:
                if (!kamikaze.IsValid)
                {
                    throw new ArgumentException("a kamikaze has finite, non-negative damage and range", nameof(action));
                }
                w.U8(23);
                foreach (var number in kamikaze.Damage) w.F64(number);
                w.F64(kamikaze.RangeMeters);
                break;
            case SimAction.Blast blast:
                if (!blast.IsValid)
                {
                    throw new ArgumentException("a fighter blast has a finite centre, a positive radius and four kinds of damage", nameof(action));
                }
                w.U8(25);
                foreach (var number in blast.Centre) w.F64(number);
                w.F64(blast.RadiusMeters);
                foreach (var number in blast.Damage) w.F64(number);
                break;
            case SimAction.FighterMissile missile:
                if (!missile.IsValid)
                {
                    throw new ArgumentException("a fighter missile has finite, non-negative damage and explosion", nameof(action));
                }
                w.U8(24);
                foreach (var number in missile.Damage) w.F64(number);
                w.F64(missile.ReachMeters); w.F64(missile.ExplosionRadius); w.F64(missile.ExplosionVelocity);
                w.F64(missile.ReductionFactor); w.F64(missile.ReductionSensitivity);
                break;
            case SimAction.ResistanceShift shift:
                if (shift.Seed.Count != 4 || shift.AttributeIds.Count != 4)
                {
                    throw new ArgumentException("a reactive armor hardener has four kinds of damage", nameof(action));
                }
                w.U8(12);
                foreach (var resonance in shift.Seed) w.F64(resonance);
                w.F64(shift.Shift);
                foreach (var attribute in shift.AttributeIds) w.I32(attribute);
                break;
            case SimAction.Smartbomb bomb:
                w.U8(2); w.F64(bomb.Range); foreach (var number in bomb.Damage) w.F64(number); w.U8(bomb.HurtsPilots ? (byte)1 : (byte)0);
                break;
            case SimAction.Remote remote:
                w.U8(1); w.U8(remote.AtCycleEnd ? (byte)1 : (byte)0); w.F64(remote.Optimal); w.F64(remote.Falloff); w.Text(remote.Jam);
                switch (remote.What)
                {
                    case SimRemote.Repair mend:
                        w.U8(0); w.U8((byte)mend.Layer); w.F64(mend.Amount); break;
                    case SimRemote.MutadaptiveRepair mend:
                        w.U8(11); w.U8((byte)mend.Layer); w.F64(mend.Amount); w.F64(mend.RepairMultiplierBonusPerCycle); w.F64(mend.RepairMultiplierBonusMax); break;
                    case SimRemote.Transfer transfer:
                        w.U8(1); w.F64(transfer.Amount); break;
                    case SimRemote.Neutralize neutralize:
                        w.U8(2); w.F64(neutralize.Amount); w.F64(neutralize.SignatureResolution); break;
                    case SimRemote.Drain drain:
                        w.U8(3); w.F64(drain.Amount); w.F64(drain.SignatureResolution); break;
                    case SimRemote.Web web:
                        w.U8(4); w.F64(web.Percent); break;
                    case SimRemote.Paint paint:
                        w.U8(5); w.F64(paint.Percent); break;
                    case SimRemote.Damp damp:
                        w.U8(6); w.F64(damp.RangePercent); w.F64(damp.ResolutionPercent); w.F64(damp.GravimetricStrengthPercent); w.F64(damp.LadarStrengthPercent); w.F64(damp.MagnetometricStrengthPercent); w.F64(damp.RadarStrengthPercent); break;
                    case SimRemote.Scramble scramble:
                        w.U8(7); w.F64(scramble.Strength); w.U8(scramble.BlocksMwd ? (byte)1 : (byte)0); break;
                    case SimRemote.Disrupt disrupt:
                        w.U8(9); w.F64(disrupt.RangePercent); w.F64(disrupt.FalloffPercent); w.F64(disrupt.TrackingPercent); break;
                    case SimRemote.GuidanceDisrupt guidance:
                        w.U8(10); w.F64(guidance.VelocityPercent); w.F64(guidance.DelayPercent); w.F64(guidance.AoeVelocityPercent); w.F64(guidance.AoeCloudPercent); break;
                    case SimRemote.Jam jam:
                        w.U8(8); w.F64(jam.Gravimetric); w.F64(jam.Ladar); w.F64(jam.Magnetometric); w.F64(jam.Radar); break;
                    case SimRemote.Tackle tackle:
                        w.U8(12); w.F64(tackle.Strength); w.F64(tackle.WebPercent); break;
                }
                break;
        }
    }

    private static void WriteEffect(Writer w, SimEffect e)
    {
        w.I64(e.ModuleId);
        w.I32(e.ModuleType);
        w.I32(e.EffectId);
        w.Text(e.Fx);
        w.F64(e.DurationMs);
        w.F64(e.CapacitorNeed);
        w.I32(e.Repeat);
        w.I64(e.TargetId);
        w.U8((byte)((e.On is null ? 0 : 1) | (e.Off is null ? 0 : 2) | (e.Turret is null ? 0 : 4) | (e.Action is null ? 0 : 8) | (e.BurnsFuel ? 16 : 0) | (e.Rules is null ? 0 : 32)));
        if (e.On is { } on) WriteNumbers(w, on);
        if (e.Off is { } off) WriteNumbers(w, off);
        WriteChanges(w, e.OnChanges);
        WriteChanges(w, e.OffChanges);
        if (e.Turret is { } turret) WriteTurret(w, turret);
        if (e.Action is { } action) WriteAction(w, action);
        if (e.Rules is { } rules) WriteRules(w, rules);
    }

    /// <summary>The module rules of an effect (flag 32, after its action): the group, its limit, the rule bits (1 early deactivation, 2 needs a core, 4 is a core), and the reactivation delay.</summary>
    private static void WriteRules(Writer w, SimModuleRules rules)
    {
        if (rules.MaxGroupActive < 0 || rules.ReactivationDelayMs < 0 || !double.IsFinite(rules.ReactivationDelayMs)
            || rules.MaxVelocityLimitMs < 0 || !double.IsFinite(rules.MaxVelocityLimitMs) || rules.MinVelocityLimitMs < 0 || !double.IsFinite(rules.MinVelocityLimitMs))
            throw new ArgumentException("invalid module rules", nameof(rules));
        w.I32(rules.GroupId);
        w.I32(rules.MaxGroupActive);
        w.U8((byte)((rules.DisallowEarlyDeactivation ? 1 : 0) | (rules.RequiresIndustrialCore ? 2 : 0) | (rules.ProvidesIndustrialCore ? 4 : 0)));
        w.F64(rules.ReactivationDelayMs);
        w.F64(rules.MaxVelocityLimitMs);
        w.F64(rules.MinVelocityLimitMs);
    }

    private static void WriteHeatProfile(Writer w, SimHeatProfile p)
    {
        if (p.Capacity.Count != 3 || p.InitialRackHeat.Count != 3 || p.DissipationRate.Count != 3 || p.Attenuation.Count != 3 || p.Modules.Count > 256)
            throw new ArgumentException("invalid heat profile", nameof(p));
        w.F64(p.GenerationMultiplier);
        w.F64(p.DamageMultiplier);
        w.I32(p.ThermodynamicsLevel);
        foreach (var value in p.Capacity.Concat(p.InitialRackHeat).Concat(p.DissipationRate).Concat(p.Attenuation)) w.F64(value);
        w.I32(p.Modules.Count);
        foreach (var m in p.Modules)
        {
            w.I64(m.ModuleId); w.I32(m.Flag); w.F64(m.StructureHp); w.F64(m.DamageHp); w.F64(m.HeatDamageHp); w.F64(m.HeatAbsorptionRate);
            w.U8(m.Overloadable ? (byte)1 : (byte)0); w.U8(m.HeatImmune ? (byte)1 : (byte)0); w.U8(m.Online ? (byte)1 : (byte)0);
        }
    }

    private static void WriteCondition(Writer w, SimCondition c)
    {
        foreach (var number in new[] { c.Damage, c.ArmorDamage, c.ShieldCharge, c.Charge })
        {
            w.F64(number);
        }
    }

    private static void WriteVitals(Writer w, SimVitals v)
    {
        foreach (var number in new[] { v.ShieldCapacity, v.ArmorHp, v.StructureHp })
        {
            w.F64(number);
        }
        foreach (var layer in new[] { v.Resonances.Shield, v.Resonances.Armor, v.Resonances.Structure })
        {
            if (layer.Count != 4)
            {
                throw new ArgumentException("a layer resists four kinds of damage", nameof(v));
            }
            foreach (var resonance in layer)
            {
                w.F64(resonance);
            }
        }
        w.U8(v.ArmorPinned ? (byte)1 : (byte)0);
    }

    private static void WriteShip(Writer w, SimShip s)
    {
        w.I64(s.ShipId);
        w.I64(s.CharacterId);
        w.I32(s.TypeId);
        w.I32(s.GroupId);
        w.I32(s.CategoryId);
        w.Text(s.Name);
        w.I64(s.OwnerId);
        w.I32(s.CorporationId);
        w.I32(s.AllianceId);
        w.I32(s.WarFactionId);
        foreach (var number in new[] { s.SecurityStatus, s.Radius, s.Mass, s.MaxVelocity, s.Agility, s.WarpSpeedAu, s.ShieldRechargeMs, s.MaxTargetRange, s.ScanResolution, s.SignatureRadius, s.CapacitorCapacity, s.CapacitorRechargeSeconds })
        {
            w.F64(number);
        }
        w.I32(s.MaxLockedTargets);
        WriteVitals(w, s.Vitals ?? SimVitals.Unhurtable);
        WriteCondition(w, s.Condition ?? SimCondition.Full);
        w.U32((uint)s.Modules.Count);
        foreach (var module in s.Modules)
        {
            w.I64(module.ItemId);
            w.I32(module.TypeId);
            w.I32(module.Flag);
        }
        var sensors = s.Sensors ?? SimSensors.Plain;
        foreach (var number in new[] { sensors.Gravimetric, sensors.Ladar, sensors.Magnetometric, sensors.Radar, sensors.EcmResistance, sensors.EnergyWarfareResistance })
        {
            w.F64(number);
        }
    }

    /// <summary>The whole frame, length prefix included.</summary>
    public static byte[] Encode(SimMessage message)
    {
        var w = new Writer();
        switch (message)
        {
            case SimHello m:
                w.U8(Kind.Hello);
                w.U16(m.Version);
                break;
            case SimHelloAck m:
                w.U8(Kind.HelloAck);
                w.U16(m.Version);
                w.U32(m.PollHz);
                break;
            case SimSetHeatProfile m:
                w.U8(Kind.HeatProfile);
                w.U64(m.SessionId);
                WriteHeatProfile(w, m.Profile);
                break;
            case SimSetModuleOverheat m:
                w.U8(Kind.ModuleOverheat);
                w.U64(m.SessionId);
                w.I64(m.ModuleId);
                w.U8(m.Active ? (byte)1 : (byte)0);
                break;
            case SimSetModuleOffline m:
                w.U8(Kind.ModuleOffline);
                w.U64(m.SessionId);
                w.I64(m.ModuleId);
                break;
            case SimSetRackOverheat m:
                if (m.Rack is < 0 or > 2) throw new ArgumentOutOfRangeException(nameof(m));
                w.U8(Kind.RackOverheat);
                w.U64(m.SessionId);
                w.U8((byte)m.Rack);
                w.U8(m.Active ? (byte)1 : (byte)0);
                break;
            case SimEnter m:
                w.U8(Kind.Enter);
                w.U64(m.SessionId);
                w.I32(m.SolarSystemId);
                w.U8((byte)m.How);
                w.I64(m.StationId);
                WriteShip(w, m.Ship);
                if (m.How == EnterHow.InSpace)
                {
                    WriteOptionalVector(w, m.Position ?? throw new ArgumentException("In-space entry requires a position."));
                    WriteOptionalVector(w, m.Direction ?? throw new ArgumentException("In-space entry requires a direction."));
                }
                break;
            case SimEnterReply m:
                w.U8(Kind.EnterReply);
                w.U64(m.SessionId);
                w.U8((byte)(m.Error is null ? 0 : 1));
                w.Text(m.Error ?? "");
                break;
            case SimLeave m:
                w.U8(Kind.Leave);
                w.U64(m.SessionId);
                w.U8((byte)m.Why);
                break;
            case SimBind m:
                w.U8(Kind.Bind);
                w.U64(m.SessionId);
                break;
            case SimUpdateState m:
                w.U8(Kind.UpdateState);
                w.U64(m.SessionId);
                break;
            case SimCommand m:
                w.U8(Kind.Command);
                w.U64(m.SessionId);
                w.U8(m.Code);
                w.U8((byte)m.Args.Count);
                foreach (var arg in m.Args)
                {
                    w.F64(arg);
                }
                break;
            case SimShipChanged m:
                w.U8(Kind.ShipChanged);
                w.U64(m.SessionId);
                WriteShip(w, m.Ship);
                break;
            case SimNotify m:
                w.U8(Kind.Notify);
                w.U64(m.SessionId);
                w.Text(m.Name);
                w.Text(m.IdType);
                w.Bytes(m.Args.Span);
                break;
            case SimDocked m:
                w.U8(Kind.Docked);
                w.U64(m.SessionId);
                w.I64(m.StationId);
                WriteCondition(w, m.Condition ?? SimCondition.Full);
                break;
            case SimConditionReply m:
                w.U8(Kind.Condition);
                w.U64(m.SessionId);
                w.U8(m.Command);
                WriteCondition(w, m.Condition);
                break;
            case SimRefused m:
                w.U8(Kind.Refused);
                w.U64(m.SessionId);
                w.U8(m.Command);
                w.Text(m.Reason);
                break;
            case SimAccepted m:
                w.U8(Kind.Accepted);
                w.U64(m.SessionId);
                w.U8(m.Command);
                w.I64(m.At);
                break;
            case SimPosition m:
                w.U8(Kind.Position);
                w.U64(m.SessionId);
                w.U8(m.Command);
                WriteVector(w, m.Position);
                break;
            case SimApproaching m:
                w.U8(Kind.Approaching);
                w.U64(m.SessionId);
                w.U8(m.Command);
                w.F64(m.Distance);
                break;
            case SimJumping m:
                w.U8(Kind.Jumping);
                w.U64(m.SessionId);
                w.U8(m.Command);
                w.I32(m.DestinationSystem);
                w.I64(m.DestinationGate);
                break;
            case SimJumped m:
                w.U8(Kind.Jumped);
                w.U64(m.SessionId);
                w.I32(m.FromSystem);
                w.I64(m.FromGate);
                w.I32(m.ToSystem);
                w.I64(m.ToGate);
                break;
            case SimDestroyed m:
                w.U8(Kind.Destroyed);
                w.U64(m.SessionId);
                w.I64(m.ShipId);
                w.I64(m.WreckId);
                WriteVector(w, m.Position);
                WriteVector(w, m.Direction);
                w.F64(m.Radius);
                w.I64(m.KillerShipId);
                WriteWreckKind(w, m.WreckKind);
                break;
            case SimChargeSpent m:
                w.U8(Kind.ChargeSpent);
                w.U64(m.SessionId);
                w.I64(m.ModuleId);
                w.I64(m.RoundsLeft);
                break;
            case SimBuffState m:
                w.U8(Kind.BuffState);
                w.U64(m.SessionId);
                w.I64(m.ShipId);
                w.U32((uint)m.Entries.Count);
                foreach (var entry in m.Entries)
                {
                    w.I32(entry.CollectionId);
                    w.F64(entry.Value);
                    w.F64(entry.RemainingMs);
                }
                break;
            case SimUpdateEffect m:
                w.U8(Kind.UpdateEffect);
                w.U64(m.SessionId);
                w.I64(m.ModuleId);
                w.F64(m.DurationMs);
                w.F64(m.CapacitorNeed);
                if (m.Turret is { } turret)
                {
                    w.U8(1);
                    WriteTurret(w, turret);
                }
                else
                {
                    w.U8(0);
                }
                WriteChanges(w, m.Changes);
                break;
            case SimAdjustCharge m:
                w.U8(Kind.AdjustCharge);
                w.U64(m.RequestId);
                w.U64(m.SessionId);
                w.I64(m.ModuleId);
                w.I32(m.ChargeType);
                switch (m.Op)
                {
                    case SimChargeOp.Add add:
                        w.U8(0);
                        w.I64(add.Capacity);
                        w.I64(add.Available);
                        break;
                    case SimChargeOp.Remove remove:
                        w.U8(1);
                        w.I64(remove.Quantity);
                        w.I64(0);
                        break;
                    default:
                        throw new ArgumentException($"unknown charge op {m.Op}");
                }
                break;
            case SimChargeAdjusted m:
                w.U8(Kind.ChargeAdjusted);
                w.U64(m.RequestId);
                w.U8(m.Running ? (byte)1 : (byte)0);
                w.I64(m.Before);
                w.I64(m.After);
                break;
            case SimRespawn m:
                w.U8(Kind.Respawn);
                w.U64(m.SessionId);
                WriteShip(w, m.Ship);
                WriteVector(w, m.Position);
                WriteVector(w, m.Direction);
                break;
            case SimPlaceContainer m:
                w.U8(Kind.PlaceContainer);
                w.U64(m.SessionId);
                w.I64(m.ContainerId);
                WriteWreckKind(w, m.Kind);
                w.F64(m.Radius);
                w.I64(m.LauncherId);
                WriteCredit(w, m.Credit);
                w.F64(m.Distance);
                break;
            case SimArrived m:
                w.U8(Kind.Arrived);
                w.U64(m.SessionId);
                w.I64(m.BeltId);
                WriteVector(w, m.Position);
                break;
            case SimContainerPlaced m:
                w.U8(Kind.ContainerPlaced);
                w.U64(m.SessionId);
                w.I64(m.ContainerId);
                WriteVector(w, m.Position);
                WriteVector(w, m.Direction);
                break;
            case SimChangeWreck m:
                w.U8(Kind.ChangeWreck);
                w.U64(m.SessionId);
                w.I64(m.WreckId);
                w.U8((byte)(m.IsEmpty ? 1 : 0));
                break;
            case SimRemoveWreck m:
                w.U8(Kind.RemoveWreck);
                w.I64(m.WreckId);
                w.I32(m.Effect);
                break;
            case SimDeployed m:
                w.U8(Kind.Deployed);
                w.I32(m.SolarSystemId);
                w.I64(m.DeployableId);
                w.Text(m.Name);
                w.I32(m.TypeId);
                w.I32(m.GroupId);
                WriteVector(w, m.Position);
                w.F64(m.Radius);
                w.I64(m.OwnerId);
                w.I32(m.CorporationId);
                w.I32(m.AllianceId);
                w.I64(m.ShipId);
                w.F64(m.LifetimeMs);
                break;
            case SimRemoveDeployed m:
                w.U8(Kind.RemoveDeployed);
                w.I64(m.DeployableId);
                break;
            case SimPlaceProbe m:
                w.U8(Kind.PlaceProbe);
                w.I64(m.ProbeId);
                w.I32(m.SolarSystemId);
                w.Text(m.Name);
                w.I32(m.TypeId);
                w.I32(m.GroupId);
                WriteVector(w, m.Position);
                w.F64(m.Radius);
                w.I64(m.OwnerId);
                w.I32(m.CorporationId);
                w.I32(m.AllianceId);
                w.I64(m.ShipId);
                break;
            case SimRemoveProbe m:
                w.U8(Kind.RemoveProbe);
                w.I64(m.ProbeId);
                break;
            case SimPlaceAsteroid m:
                w.U8(Kind.PlaceAsteroid);
                w.I32(m.SolarSystemId);
                w.I64(m.AsteroidId);
                w.I32(m.TypeId);
                w.I32(m.GroupId);
                w.I32(m.CategoryId);
                w.Text(m.Name);
                w.U8(m.GraphicId is null ? (byte)0 : (byte)1);
                if (m.GraphicId is { } graphicId)
                {
                    w.I32(graphicId);
                }
                WriteVector(w, m.Position);
                WriteVector(w, m.Direction);
                w.F64(m.Radius);
                w.I64(m.UnitsRemaining);
                break;
            case SimDroneWorkCycle m:
                w.U8(Kind.DroneWorkCycle);
                w.U64(m.SessionId);
                w.I64(m.CharacterId);
                w.I64(m.ShipId);
                w.I32(m.SolarSystemId);
                w.I64(m.DroneId);
                w.I64(m.TargetId);
                w.U64(m.CycleId);
                w.U8(m.Command);
                w.F64(m.MiningAmountM3);
                w.F64(m.WasteVolumeMultiplier);
                w.F64(m.WasteProbability);
                w.F64(m.AccessDifficultyBonus);
                w.F64(m.Roll);
                break;
            case SimDroneWorkResult m:
                w.U8(Kind.DroneWorkResult);
                w.U64(m.SessionId);
                w.I64(m.DroneId);
                w.I64(m.TargetId);
                w.U64(m.CycleId);
                w.U8(m.Accepted ? (byte)1 : (byte)0);
                w.Text(m.Reason);
                w.I64(m.UnitsRemaining);
                w.I64(m.DeliveredUnits);
                w.U8(m.Complete ? (byte)1 : (byte)0);
                break;
            case SimRemoteRepairCycle m:
                w.U8(Kind.RemoteRepairCycle);
                w.U64(m.SessionId);
                w.I64(m.ModuleId);
                w.I64(m.TargetId);
                w.U64(m.CycleId);
                w.I32(m.CycleNumber);
                w.I64(m.CycleAtMs);
                w.I64(m.CycleDurationMs);
                w.U8(m.EndsEffectAfterCycle ? (byte)1 : (byte)0);
                w.F64(m.BaseAmount);
                w.F64(m.RepairMultiplierBonusPerCycle);
                w.F64(m.RepairMultiplierBonusMax);
                break;
            case SimModuleWorkCycle m:
                w.U8(Kind.ModuleWorkCycle);
                w.U64(m.SessionId);
                w.I64(m.CharacterId);
                w.I64(m.ShipId);
                w.I32(m.SolarSystemId);
                w.I64(m.ModuleId);
                w.I64(m.TargetId);
                w.U64(m.CycleId);
                w.U8(m.Command);
                w.F64(m.MiningAmountM3);
                w.F64(m.WasteVolumeMultiplier);
                w.F64(m.WasteProbability);
                w.F64(m.CritProbability);
                w.F64(m.CritQuantityMultiplier);
                w.F64(m.AccessDifficultyBonus);
                w.F64(m.WasteRoll);
                w.F64(m.RoundingRoll);
                w.F64(m.CritRoll);
                // protocol 43: the ships a command burst reaches (a count, then each id); only a burst names any
                var recipients = m.Recipients ?? [];
                if (recipients.Count > 0 && m.Command is not (SimModuleWorkCommands.Burst or SimModuleWorkCommands.Panic))
                {
                    throw new ArgumentException("only a command burst's cycle names ships", nameof(message));
                }
                w.U32((uint)recipients.Count);
                foreach (var ship in recipients)
                {
                    w.I64(ship);
                }
                break;
            case SimModuleWorkResult m:
                w.U8(Kind.ModuleWorkResult);
                w.U64(m.SessionId);
                w.I64(m.ModuleId);
                w.I64(m.TargetId);
                w.U64(m.CycleId);
                w.U8(m.Accepted ? (byte)1 : (byte)0);
                w.Text(m.Reason);
                w.I64(m.UnitsRemaining);
                w.I64(m.DeliveredUnits);
                w.U8(m.Complete ? (byte)1 : (byte)0);
                break;
            case SimModuleEnded m:
                w.U8(Kind.ModuleEnded);
                w.U64(m.SessionId);
                w.I64(m.ModuleId);
                w.U8(m.Reason);
                break;
            case SimRemoteRepairCycleResult m:
                w.U8(Kind.RemoteRepairCycleResult);
                w.U64(m.SessionId);
                w.I64(m.ModuleId);
                w.I64(m.TargetId);
                w.U64(m.CycleId);
                w.U8(m.Accepted ? (byte)1 : (byte)0);
                w.Text(m.Reason);
                w.F64(m.Amount);
                break;
            case SimMeasured m:
                w.U8(Kind.Measured);
                w.U64(m.SessionId);
                w.U8(m.Command);
                if (m.Distance is { } distance)
                {
                    w.U8(1);
                    w.F64(distance);
                }
                else
                {
                    w.U8(0);
                }
                break;
            case SimPlaceWreck m:
                w.U8(Kind.PlaceWreck);
                w.I32(m.SolarSystemId);
                w.I64(m.WreckId);
                w.I32(m.GroupId);
                WriteWreckKind(w, m.Kind);
                WriteVector(w, m.Position);
                WriteVector(w, m.Direction);
                w.F64(m.Radius);
                w.I64(m.LauncherId);
                WriteCredit(w, m.Credit);
                w.U8((byte)(m.IsEmpty ? 1 : 0));
                break;
            case SimSpawn m:
                w.U8(Kind.Spawn);
                w.U64(m.RequestId);
                w.I32(m.SolarSystemId);
                WriteShip(w, m.Ship);
                w.I64(m.AnchorShipId);
                WriteVector(w, m.Position);
                WriteVector(w, m.Velocity);
                WriteVector(w, m.Direction);
                w.Text(m.Behavior);
                WriteIdentity(w, m.Identity);
                break;
            case SimLaunchDrone m:
                w.U8(Kind.LaunchDrone);
                w.U64(m.RequestId);
                w.U64(m.SessionId);
                WriteShip(w, m.Ship);
                WriteOptionalWeapon(w, m.Weapon);
                w.I32(m.GraphicId);
                w.F64(m.OrbitDistance);
                w.F64(m.AttackRange);
                w.F64(m.CruiseFraction);
                w.F64(m.Work.MiningAmountM3);
                w.F64(m.Work.CycleTimeMs);
                w.F64(m.Work.MiningWasteVolumeMultiplier);
                w.F64(m.Work.MiningWasteProbability);
                w.F64(m.Work.SalvageAccessDifficultyBonus);
                if (m.Effect is { } effect)
                {
                    w.U8(1);
                    w.I32(effect.EffectId);
                    w.Text(effect.Fx);
                    w.F64(effect.CycleMs);
                    w.F64(effect.LastingMs);
                    WriteAction(w, effect.Action);
                }
                else
                {
                    w.U8(0);
                }
                break;
            case SimDroneCommand m:
                w.U8(Kind.DroneCommand);
                w.U64(m.SessionId);
                w.U8(m.Command);
                w.I64(m.TargetId);
                w.U32((uint)m.DroneIds.Count);
                foreach (var drone in m.DroneIds)
                {
                    w.I64(drone);
                }
                w.F64(m.ControlRange);
                break;
            case SimDroneResults m:
                w.U8(Kind.DroneResults);
                w.U64(m.SessionId);
                w.U8(m.Command);
                w.U32((uint)m.Results.Count);
                foreach (var result in m.Results)
                {
                    w.I64(result.DroneId);
                    w.Text(result.Reason);
                }
                break;
            case SimDroneScooped m:
                w.U8(Kind.DroneScooped);
                w.U64(m.SessionId);
                w.I64(m.DroneId);
                break;
            case SimDroneReleased m:
                w.U8(Kind.DroneReleased);
                w.U64(m.SessionId);
                w.I64(m.DroneId);
                WriteVector(w, m.Position);
                break;
            case SimLaunchFighter m:
                w.U8(Kind.LaunchFighter);
                w.U64(m.RequestId);
                w.U64(m.SessionId);
                WriteShip(w, m.Ship);
                w.I32(m.GraphicId);
                w.F64(m.CruiseFraction);
                w.I32(m.SquadronSize);
                break;
            case SimFighterSize m:
                w.U8(Kind.FighterSize);
                w.U64(m.SessionId);
                w.I64(m.FighterId);
                w.I32(m.Size);
                break;
            case SimFighterOrder m:
                w.U8(Kind.FighterOrder);
                w.U64(m.SessionId);
                w.U8(m.Command);
                w.I64(m.TargetId);
                w.U32((uint)m.FighterIds.Count);
                foreach (var fighter in m.FighterIds)
                {
                    w.I64(fighter);
                }
                w.F64(m.Range);
                break;
            case SimFighterGotoPoint m:
                w.U8(Kind.FighterGotoPoint);
                w.U64(m.SessionId);
                w.U32((uint)m.FighterIds.Count);
                foreach (var fighter in m.FighterIds)
                {
                    w.I64(fighter);
                }
                if (m.Point.Count != 3)
                {
                    throw new ArgumentException("a point has three coordinates", nameof(m));
                }
                foreach (var coordinate in m.Point)
                {
                    w.F64(coordinate);
                }
                break;
            case SimFighterResults m:
                w.U8(Kind.FighterResults);
                w.U64(m.SessionId);
                w.U8(m.Command);
                w.U32((uint)m.Results.Count);
                foreach (var result in m.Results)
                {
                    w.I64(result.FighterId);
                    w.Text(result.Reason);
                }
                break;
            case SimEcmBurst m:
                w.U8(Kind.EcmBurst);
                w.U64(m.SessionId);
                w.I64(m.ShipId);
                w.U32((uint)m.Reached.Count);
                foreach (var reach in m.Reached)
                {
                    w.I64(reach.ShipId);
                    w.U8(reach.Jammed ? (byte)1 : (byte)0);
                }
                break;
            case SimFighterLanded m:
                w.U8(Kind.FighterLanded);
                w.U64(m.SessionId);
                w.I64(m.FighterId);
                break;
            case SimFighterReleased m:
                w.U8(Kind.FighterReleased);
                w.U64(m.SessionId);
                w.I64(m.FighterId);
                WriteVector(w, m.Position);
                break;
            case SimSpawnReply m:
                w.U8(Kind.SpawnReply);
                w.U64(m.RequestId);
                if (m.Session is { } session)
                {
                    w.U8(0);
                    w.U64(session);
                }
                else
                {
                    w.U8(1);
                    w.Text(m.Error ?? "");
                }
                break;
            case SimDestroy m:
                w.U8(Kind.Destroy);
                w.I64(m.ShipId);
                WriteCredit(w, m.Credit);
                break;
            case SimTargets m:
                w.U8(Kind.Targets);
                w.U64(m.SessionId);
                w.U8(m.Command);
                w.U8((byte)(m.Pending ? 1 : 0));
                w.U32((uint)m.Targets.Count);
                foreach (var target in m.Targets)
                {
                    w.I64(target);
                }
                break;
            case SimActivate m:
                w.U8(Kind.Activate);
                w.U64(m.SessionId);
                WriteEffect(w, m.Effect);
                break;
            case SimTargetRefused m:
                w.U8(Kind.TargetRefused);
                w.U64(m.SessionId);
                w.U8(m.Command);
                w.Text(m.Reason);
                w.I32(m.TargetType);
                w.F64(m.DesiredRange);
                w.F64(m.ActualDistance);
                break;
            default:
                throw new ArgumentException($"cannot encode {message.GetType().Name}", nameof(message));
        }
        var payload = w.ToArray();
        var frame = new byte[4 + payload.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(frame, (uint)payload.Length);
        payload.CopyTo(frame, 4);
        return frame;
    }
}
