using System.Buffers.Binary;
using System.Text;

namespace Eve.Space;

public static partial class SimCodec
{
    private static SimSceneKey ReadSceneKey(ref Reader r)
    {
        var scene = new SimSceneKey(r.I32(), r.I64(), r.I32());
        if (!scene.IsValid) throw new SimLinkException(SimLinkError.BadValue, "bad scene key");
        return scene;
    }

    private static int ReadAbyssalCount(ref Reader r, int maximum, string field)
    {
        var count = r.U32();
        if (count > maximum) throw new SimLinkException(SimLinkError.BadValue, $"too many Abyssal {field}");
        return (int)count;
    }

    private static SimAbyssalAttributeModifier[] ReadAbyssalModifiers(ref Reader r)
    {
        var count = ReadAbyssalCount(ref r, MaxAbyssalModifiers, "modifiers");
        var modifiers = new SimAbyssalAttributeModifier[count];
        for (var index = 0; index < count; index++)
        {
            var attributeId = r.I32();
            var operation = r.U8() switch
            {
                0 => SimAbyssalModifierOperation.Add,
                1 => SimAbyssalModifierOperation.Multiply,
                _ => throw new SimLinkException(SimLinkError.BadValue, "bad Abyssal modifier operation"),
            };
            var value = r.F64();
            if (attributeId <= 0 || !double.IsFinite(value))
                throw new SimLinkException(SimLinkError.BadValue, "bad Abyssal modifier");
            modifiers[index] = new SimAbyssalAttributeModifier(attributeId, operation, value);
        }
        return modifiers;
    }

    private static SimAbyssalSceneSpec ReadAbyssalSceneSpec(ref Reader r)
    {
        var scene = ReadSceneKey(ref r);
        var global = ReadAbyssalModifiers(ref r);
        var fieldCount = ReadAbyssalCount(ref r, MaxAbyssalFields, "environment fields");
        var fields = new SimAbyssalEnvironmentField[fieldCount];
        for (var index = 0; index < fieldCount; index++)
        {
            fields[index] = new SimAbyssalEnvironmentField(ReadVector(ref r), r.F64(), ReadAbyssalModifiers(ref r));
        }
        var center = ReadVector(ref r);
        var radius = r.F64();
        var tickInterval = r.I64();
        var pointCount = ReadAbyssalCount(ref r, MaxAbyssalBoundaryPoints, "boundary points");
        var curve = new SimAbyssalBoundaryDamagePoint[pointCount];
        for (var index = 0; index < pointCount; index++)
        {
            curve[index] = new SimAbyssalBoundaryDamagePoint(r.F64(), r.F64());
        }
        var terminalExposureMs = r.I64();
        var expiryFileTime = r.I64();
        var spec = new SimAbyssalSceneSpec(
            scene,
            new SimAbyssalEnvironmentProfile(global, fields),
            new SimAbyssalBoundaryProfile(center, radius, tickInterval, curve, terminalExposureMs),
            expiryFileTime);
        if (!IsAbyssalSceneSpec(spec)) throw new SimLinkException(SimLinkError.BadValue, "bad Abyssal scene specification");
        return spec;
    }

    private static int[] ReadAbyssalTypeIds(ref Reader r)
    {
        var count = r.U32();
        if (count > 4096) throw new SimLinkException(SimLinkError.BadValue, "too many Abyssal type ids");
        var ids = new int[count];
        for (var index = 0; index < ids.Length; index++) ids[index] = r.I32();
        return ids;
    }

    private static SimAbyssalPointDefense? ReadAbyssalPointDefense(ref Reader r)
    {
        var flag = r.U8();
        if (flag == 0) return null;
        if (flag != 1) throw new SimLinkException(SimLinkError.BadValue, "bad Abyssal point defense flag");
        return new SimAbyssalPointDefense(r.I64(), [r.F64(), r.F64(), r.F64(), r.F64()], r.I32(),
            ReadAbyssalTypeIds(ref r), ReadAbyssalTypeIds(ref r), ReadAbyssalTypeIds(ref r),
            ReadAbyssalTypeIds(ref r), ReadAbyssalTypeIds(ref r), ReadAbyssalTypeIds(ref r), r.Text());
    }

    private static SimAbyssalSceneObject ReadAbyssalSceneObject(ref Reader r, bool conduit = true)
    {
        var sceneObject = new SimAbyssalSceneObject(
            ReadSceneKey(ref r), r.I64(), r.I32(), r.I32(), r.I32(), r.I64(), r.F64(), r.I32(),
            r.Text(), ReadVector(ref r), ReadVector(ref r), ReadFlag(ref r, "Abyssal conduit open"));
        if (conduit && !IsAbyssalSceneObject(sceneObject))
            throw new SimLinkException(SimLinkError.BadValue, "bad Abyssal scene object");
        return sceneObject;
    }

    private static (ulong RequestId, string? Error) ReadAbyssalResult(ref Reader r)
    {
        var requestId = r.U64();
        var status = r.U8();
        var reason = r.Text();
        if (requestId == 0 || status > 1) throw new SimLinkException(SimLinkError.BadValue, "bad Abyssal result");
        return (requestId, status == 0 ? null : reason);
    }

    /// <summary>A structure's health (protocol 60): the points of its shield, armor and hull, the resonances of each layer, the shield's recharge, the
    /// limit each layer takes in a second and the repair pause of each layer.</summary>
    private static SimStructureHealth ReadStructureHealth(ref Reader r)
    {
        var shield = r.F64();
        var armor = r.F64();
        var structure = r.F64();
        var resonances = new double[12];
        for (var i = 0; i < resonances.Length; i++)
        {
            resonances[i] = r.F64();
        }
        var recharge = r.F64();
        var perSecond = new[] { r.F64(), r.F64(), r.F64() };
        var repairPause = new[] { r.F64(), r.F64(), r.F64() };
        return new SimStructureHealth(shield, armor, structure, resonances, recharge, perSecond, repairPause);
    }

    /// <summary>A structure's slim fields, in the order the simulation codec writes them (protocol 59): a flagged timer (start, end and a flagged pause),
    /// a flagged unanchoring time and repairing (0 none, 1 not repairing, 2 repairing).</summary>
    private static SimStructureStatus ReadStructureStatus(ref Reader r)
    {
        var state = r.I32();
        var upkeep = r.I32();
        (long Start, long End, long? Paused)? timer = null;
        if (ReadFlag(ref r, "structure timer"))
        {
            var start = r.I64();
            var end = r.I64();
            long? paused = ReadFlag(ref r, "structure timer pause") ? r.I64() : null;
            timer = (start, end, paused);
        }
        long? unanchoring = ReadFlag(ref r, "structure unanchoring") ? r.I64() : null;
        bool? repairing = r.U8() switch
        {
            0 => null,
            1 => false,
            2 => true,
            _ => throw new SimLinkException(SimLinkError.BadValue, "bad structure repairing"),
        };
        return new SimStructureStatus(state, upkeep, timer, unanchoring, repairing);
    }

    private static double[] ReadVector(ref Reader r) => [r.F64(), r.F64(), r.F64()];

    /// <summary>A dynamic slim-item field value (protocol 76): a tag (0 none, 1 int, 2 real, 3 bool, 4 text, 5 tuple), then its payload.</summary>
    private static SimSlimValue ReadSlimValue(ref Reader r, int depth)
    {
        switch (r.U8())
        {
            case 0:
                return new SimSlimNone();
            case 1:
                return new SimSlimInt(r.I64());
            case 2:
                {
                    var number = r.F64();
                    if (!double.IsFinite(number))
                    {
                        throw new SimLinkException(SimLinkError.BadValue, "bad slim field real");
                    }
                    return new SimSlimReal(number);
                }
            case 3:
                return new SimSlimBool(ReadFlag(ref r, "slim field bool"));
            case 4:
                {
                    var text = r.Text();
                    if (Encoding.UTF8.GetByteCount(text) > SlimFieldTextMax)
                    {
                        throw new SimLinkException(SimLinkError.BadValue, "bad slim field text");
                    }
                    return new SimSlimText(text);
                }
            case 5:
                {
                    var count = r.U16();
                    if (depth >= SlimFieldDepthMax || count > SlimFieldTupleMax)
                    {
                        throw new SimLinkException(SimLinkError.BadValue, "bad slim field tuple");
                    }
                    var items = new List<SimSlimValue>(count);
                    for (var index = 0; index < count; index++)
                    {
                        items.Add(ReadSlimValue(ref r, depth + 1));
                    }
                    return new SimSlimTuple(items);
                }
            default:
                throw new SimLinkException(SimLinkError.BadValue, "bad slim field value");
        }
    }

    /// <summary>A CRAB's state (protocol 61): the activation flag, the FILETIME times, the wait in seconds, the link state (1 to 4) and the linked ship.</summary>
    private static SimAnalysisBeaconState ReadAnalysisBeaconState(ref Reader r)
    {
        var active = ReadFlag(ref r, "analysis beacon");
        var activateAt = r.I64();
        var deployedAt = r.I64();
        var activationSeconds = r.I32();
        var linkState = r.I32();
        var linkCompleteAt = r.I64();
        var linkedShip = r.I64();
        if (activationSeconds < 0 || linkState is < 1 or > 4)
        {
            throw new SimLinkException(SimLinkError.BadValue, "bad analysis beacon state");
        }
        return new SimAnalysisBeaconState(active, activateAt, deployedAt, activationSeconds, linkState, linkCompleteAt, linkedShip);
    }

    /// <summary>A flag byte: 0 or 1; anything else is a bad value of <paramref name="what"/>.</summary>
    private static bool ReadFlag(ref Reader r, string what) => r.U8() switch
    {
        0 => false,
        1 => true,
        _ => throw new SimLinkException(SimLinkError.BadValue, $"bad {what}"),
    };

    private static double[]? ReadOptionalVector(ref Reader r) => r.U8() switch
    {
        0 => null,
        1 => ReadVector(ref r),
        _ => throw new SimLinkException(SimLinkError.BadValue, "bad optional vector"),
    };

    private static SimWreckKind? ReadWreckKind(ref Reader r) => r.U8() switch
    {
        0 => null,
        1 => new SimWreckKind(r.I32(), r.Text(), r.I32()),
        _ => throw new SimLinkException(SimLinkError.BadValue, "bad wreck"),
    };

    private static SimCredit ReadCredit(ref Reader r) => new(r.I64(), r.I64(), r.I64(), r.I64());

    private static SimNpcWeapon ReadWeapon(ref Reader r)
    {
        var moduleId = r.I64();
        var effectId = r.I32();
        var cycleMs = r.F64();
        var capacitorNeed = r.F64();
        var turret = ReadTurret(ref r);
        return new SimNpcWeapon(
            moduleId, effectId, cycleMs, capacitorNeed, turret.WeaponTypeId, turret.Fx, turret.RawDamage, turret.OptimalRange, turret.Falloff, turret.TrackingSpeed,
            turret.SignatureResolution, turret.Charge, turret.Missile, turret.RequiresGunnery);
    }

    /// <summary>The effect a drone runs at its target, when it has one (a flag, then the effect and its action).</summary>
    private static SimDroneEffect? ReadOptionalDroneEffect(ref Reader r)
    {
        if (r.U8() == 0)
        {
            return null;
        }
        return new SimDroneEffect(r.I32(), r.Text(), r.F64(), r.F64(), ReadAction(ref r));
    }

    private static SimNpcWeapon? ReadOptionalWeapon(ref Reader r)
    {
        var weapon = ReadWeapon(ref r);
        return weapon.ModuleId == 0 ? null : weapon;
    }

    private static SimNpcIdentity ReadIdentity(ref Reader r)
    {
        var hostile = r.I64();
        var friendly = r.I64();
        var aggression = r.F64();
        var count = r.U32();
        // a weapon takes at least 100 bytes, so a count the frame cannot hold is refused before anything is allocated for it
        if (count > r.Remaining / 100)
        {
            throw new SimLinkException(SimLinkError.Truncated, "the frame ends before the message does");
        }
        var weapons = new SimNpcWeapon[count];
        for (var i = 0; i < weapons.Length; i++)
        {
            weapons[i] = ReadWeapon(ref r);
        }
        var repairCount = r.U32();
        if (repairCount > 2)
            throw new SimLinkException(SimLinkError.BadValue, "too many NPC self-repairs");
        if (repairCount > r.Remaining / 36)
            throw new SimLinkException(SimLinkError.Truncated, "the frame ends before the message does");
        var selfRepairs = new SimNpcSelfRepair[repairCount];
        var repairEffects = new HashSet<int>();
        for (var i = 0; i < selfRepairs.Length; i++)
        {
            var repair = new SimNpcSelfRepair(r.I32(), r.F64(), r.F64(), r.F64(), r.F64());
            if (!SimNpcSelfRepair.IsValid(repair) || !repairEffects.Add(repair.EffectId))
                throw new SimLinkException(SimLinkError.BadValue, "bad NPC self-repair");
            selfRepairs[i] = repair;
        }
        var wreck = ReadWreckKind(ref r);
        var hull = r.U8() switch
        {
            0 => false,
            1 => true,
            _ => throw new SimLinkException(SimLinkError.BadValue, "bad hull"),
        };
        var hostWreck = r.U8() switch
        {
            0 => false,
            1 => true,
            _ => throw new SimLinkException(SimLinkError.BadValue, "bad host wreck"),
        };
        return new SimNpcIdentity(hostile, friendly, aggression, weapons, wreck, hull, hostWreck, selfRepairs);
    }

    private static SimShip ReadShip(ref Reader r)
    {
        var shipId = r.I64();
        var characterId = r.I64();
        var typeId = r.I32();
        var groupId = r.I32();
        var categoryId = r.I32();
        var name = r.Text();
        var ownerId = r.I64();
        var corporationId = r.I32();
        var allianceId = r.I32();
        var warFactionId = r.I32();
        var security = r.F64();
        var radius = r.F64();
        var mass = r.F64();
        var maxVelocity = r.F64();
        var agility = r.F64();
        var warpSpeed = r.F64();
        var shieldRecharge = r.F64();
        var maxTargetRange = r.F64();
        var scanResolution = r.F64();
        var signatureRadius = r.F64();
        var capacitorCapacity = r.F64();
        var capacitorRecharge = r.F64();
        var maxLockedTargets = r.I32();
        var vitals = ReadVitals(ref r);
        var condition = ReadCondition(ref r);
        var count = r.U32();
        // a module takes 16 bytes, so a count the frame cannot hold is refused before anything is allocated for it
        if (count > r.Remaining / 16)
        {
            throw new SimLinkException(SimLinkError.Truncated, "the frame ends before the message does");
        }
        var modules = new SimModule[count];
        for (var i = 0; i < modules.Length; i++)
        {
            modules[i] = new SimModule(r.I64(), r.I32(), r.I32());
        }
        var sensors = new SimSensors(r.F64(), r.F64(), r.F64(), r.F64(), r.F64(), r.F64());
        var droneDamageMultiplier = r.F64();
        if (!double.IsFinite(droneDamageMultiplier) || droneDamageMultiplier < 0)
        {
            throw new SimLinkException(SimLinkError.BadValue, "the drone damage multiplier is not a non-negative number");
        }
        var flag = r.U8();
        if (flag > 1)
        {
            throw new SimLinkException(SimLinkError.BadValue, "the ship stance flag is neither 0 nor 1");
        }
        var stance = flag == 0 ? null : new SimShipStance(r.I32(), r.I64(), r.I32());
        return new SimShip(shipId, characterId, typeId, groupId, categoryId, name, ownerId, corporationId, allianceId, warFactionId, security, radius, mass, maxVelocity, agility, warpSpeed, shieldRecharge, maxTargetRange, scanResolution, signatureRadius, maxLockedTargets, capacitorCapacity, capacitorRecharge, modules, vitals, condition, sensors, droneDamageMultiplier, stance);
    }

    private static SimCondition ReadCondition(ref Reader r) => new(r.F64(), r.F64(), r.F64(), r.F64());

    private static double[] ReadFour(ref Reader r) => [r.F64(), r.F64(), r.F64(), r.F64()];

    private static SimVitals ReadVitals(ref Reader r)
    {
        var shield = r.F64();
        var armor = r.F64();
        var structure = r.F64();
        var resonances = new SimResonances(ReadFour(ref r), ReadFour(ref r), ReadFour(ref r));
        var pinned = r.U8();
        if (pinned > 1)
        {
            throw new SimLinkException(SimLinkError.BadValue, "bad armor pinned flag");
        }
        return new SimVitals(shield, armor, structure, resonances, pinned == 1);
    }

    private static SimNumbers ReadNumbers(ref Reader r) => new(r.F64(), r.F64(), r.F64());

    /// <summary>
    /// The ships a command burst reaches (protocol 43): a count, then each ship's id. A count the frame cannot hold is refused before it is read. None (a count of
    /// zero, the case of every other command) is null, as the message is made without it.
    /// </summary>
    private static IReadOnlyList<long>? ReadRecipients(ref Reader r)
    {
        var count = r.U32();
        if (r.Remaining < 4 || count > (r.Remaining - 4) / 8)
        {
            throw new SimLinkException(SimLinkError.Truncated, "recipients count");
        }
        if (count == 0)
        {
            return null;
        }
        var ships = new long[count];
        for (var i = 0; i < ships.Length; i++)
        {
            ships[i] = r.I64();
        }
        return ships;
    }

    private static SimChange[] ReadChanges(ref Reader r)
    {
        var count = r.U32();
        // a change takes 20 bytes, so a count the frame cannot hold is refused before anything is allocated for it
        if (count > r.Remaining / 20)
        {
            throw new SimLinkException(SimLinkError.Truncated, "the frame ends before the message does");
        }
        var changes = new SimChange[count];
        for (var i = 0; i < changes.Length; i++)
        {
            changes[i] = new SimChange(r.I64(), r.I32(), r.F64());
        }
        return changes;
    }

    private static SimEffect ReadEffect(ref Reader r)
    {
        var module = r.I64();
        var moduleType = r.I32();
        var effectId = r.I32();
        var fx = r.Text();
        var duration = r.F64();
        var capacitor = r.F64();
        var repeat = r.I32();
        var target = r.I64();
        var flags = r.U8();
        // Bits 1-32 carry the existing effect fields; bit 64 carries the fallback capacitor need after the module rules.
        if (flags > 127)
        {
            throw new SimLinkException(SimLinkError.BadValue, "bad flags");
        }
        var on = (flags & 1) != 0 ? ReadNumbers(ref r) : null;
        var off = (flags & 2) != 0 ? ReadNumbers(ref r) : null;
        var onChanges = ReadChanges(ref r);
        var offChanges = ReadChanges(ref r);
        var turret = (flags & 4) != 0 ? ReadTurret(ref r) : null;
        var action = (flags & 8) != 0 ? ReadAction(ref r) : null;
        var rules = (flags & 32) != 0 ? ReadRules(ref r) : null;
        var fallbackCapacitor = (flags & 64) != 0 ? r.F64() : 0.0;
        return new SimEffect(module, moduleType, effectId, fx, duration, capacitor, repeat, target, on, off, onChanges, offChanges, turret, action, (flags & 16) != 0, rules, fallbackCapacitor);
    }

    private static SimModuleRules ReadRules(ref Reader r)
    {
        var group = r.I32();
        var maxGroupActive = r.I32();
        var bits = r.U8();
        var reactivation = r.F64();
        var (maxVelocity, minVelocity) = (r.F64(), r.F64());
        if (bits > 7 || maxGroupActive < 0 || reactivation < 0 || !double.IsFinite(reactivation)
            || maxVelocity < 0 || !double.IsFinite(maxVelocity) || minVelocity < 0 || !double.IsFinite(minVelocity))
        {
            throw new SimLinkException(SimLinkError.BadValue, "bad module rules");
        }
        return new SimModuleRules(group, maxGroupActive, (bits & 1) != 0, (bits & 2) != 0, (bits & 4) != 0, reactivation, maxVelocity, minVelocity);
    }

    /// <summary>The list <see cref="WriteIds"/> writes; more than 4096 ids is refused (no fleet is that large in a field).</summary>
    private static IReadOnlyList<long> ReadIds(ref Reader r)
    {
        var count = r.U32();
        if (count > 4096)
        {
            throw new SimLinkException(SimLinkError.BadValue, "too many transport ids");
        }
        var ids = new long[count];
        for (var index = 0; index < count; index++)
        {
            ids[index] = r.I64();
        }
        return ids;
    }

    /// <summary>A strike's body after its tag, as <see cref="WriteStrike"/> writes it.</summary>
    private static SimAction.Strike ReadStrike(ref Reader r)
    {
        var shape = r.U8();
        double[] direction = [r.F64(), r.F64(), r.F64()];
        var length = r.F64();
        var radius = r.F64();
        var warning = r.F64();
        var pulse = r.F64();
        var pulses = r.I32();
        double[] damage = [r.F64(), r.F64(), r.F64(), r.F64()];
        var signature = r.F64();
        var immobile = r.F64();
        var noJump = r.F64();
        var neutAmount = r.F64();
        var neutRadius = r.F64();
        var neutSignature = r.F64();
        var debuff = r.F64();
        var sweepFlag = r.U8();
        double[] sweepEnd = [r.F64(), r.F64(), r.F64()];
        if (sweepFlag > 1)
        {
            throw new SimLinkException(SimLinkError.BadValue, "bad strike sweep flag");
        }
        var beaconFlag = r.U8();
        double[] beacon = [r.F64(), r.F64(), r.F64()];
        if (beaconFlag > 1)
        {
            throw new SimLinkException(SimLinkError.BadValue, "bad strike beacon flag");
        }
        var strike = new SimAction.Strike(shape, direction, length, radius, warning, pulse, pulses, damage, signature, immobile, noJump, neutAmount, neutRadius, neutSignature,
            debuff, sweepFlag == 1 ? sweepEnd : null, beaconFlag == 1 ? beacon : null);
        if (!strike.IsValid)
        {
            throw new SimLinkException(SimLinkError.BadValue, "bad strike action");
        }
        return strike;
    }

    /// <summary>What a launcher flies (see <c>WriteMissile</c>).</summary>
    private static SimMissile ReadMissile(ref Reader r)
    {
        var missile = new SimMissile(r.I32(), r.I32(), r.Text(), r.F64(), r.F64(), r.F64(), r.F64(), r.F64(), r.F64(), r.F64(), r.F64(), r.F64(), r.F64());
        // the damage over time of a breacher pod (protocol 42): a flag, then its duration, its cap per tick and its percentage per tick
        var dot = r.U8() switch
        {
            0 => null,
            1 => new SimDot(r.F64(), r.F64(), r.F64()),
            _ => throw new SimLinkException(SimLinkError.BadValue, "bad dot"),
        };
        return missile with { Dot = dot };
    }

    /// <summary>A deployable's charge (see <c>WriteDeployable</c>): the radius of its ball must be a positive number.</summary>
    private static SimDeployable ReadDeployable(ref Reader r)
    {
        var ball = new SimDeployable(r.I32(), r.I32(), r.Text(), r.F64());
        if (!double.IsFinite(ball.Radius) || ball.Radius <= 0)
        {
            throw new SimLinkException(SimLinkError.BadValue, "bad deployable ball");
        }
        return ball;
    }

    private static SimAction ReadAction(ref Reader r)
    {
        switch (r.U8())
        {
            case 0:
                {
                    var layer = r.U8();
                    var when = r.U8();
                    if (layer > 2 || when > 1)
                    {
                        throw new SimLinkException(SimLinkError.BadValue, "bad repair");
                    }
                    return new SimAction.Repair((SimLayer)layer, r.F64(), when == 1);
                }
            case 3:
                {
                    var range = r.F64();
                    double[] strength = [r.F64(), r.F64(), r.F64(), r.F64()];
                    return new SimAction.EcmBurst(range, strength);
                }
            case 5:
                {
                    var warpSafe = r.U8();
                    if (warpSafe > 1)
                    {
                        throw new SimLinkException(SimLinkError.BadValue, "bad warp-safe flag of the cloak");
                    }
                    return new SimAction.Cloak(warpSafe == 1, r.F64());
                }
            case 16:
                {
                    var range = r.F64();
                    if (!double.IsFinite(range) || range <= 0)
                    {
                        throw new SimLinkException(SimLinkError.BadValue, "bad warp bubble action");
                    }
                    return new SimAction.WarpBubble(range);
                }
            case 17:
                return new SimAction.WarpImmunity();
            case 22:
                {
                    var missile = ReadMissile(ref r);
                    double[] damage = [r.F64(), r.F64(), r.F64(), r.F64()];
                    if (missile.MaxVelocity < 0 || damage.Any(d => !double.IsFinite(d) || d < 0))
                    {
                        throw new SimLinkException(SimLinkError.BadValue, "bad defender action");
                    }
                    return new SimAction.Defender(missile, damage);
                }
            case 21:
                {
                    var range = r.F64();
                    var duration = r.F64();
                    var speed = r.F64();
                    if (!(double.IsFinite(range) && range > 0 && double.IsFinite(duration) && duration > 0 && double.IsFinite(speed)))
                    {
                        throw new SimLinkException(SimLinkError.BadValue, "bad stasis burst action");
                    }
                    return new SimAction.StasisBurst(range, duration, speed);
                }
            case 18:
                {
                    var delta = r.F64();
                    if (!double.IsFinite(delta))
                    {
                        throw new SimLinkException(SimLinkError.BadValue, "bad scramble status action");
                    }
                    return new SimAction.ScrambleStatus(delta);
                }
            case 30:
                {
                    var range = r.F64();
                    double[] damage = [r.F64(), r.F64(), r.F64(), r.F64()];
                    if (!(double.IsFinite(range) && range > 0) || damage.Any(amount => !double.IsFinite(amount) || amount < 0))
                        throw new SimLinkException(SimLinkError.BadValue, "bad point-defense action");
                    return new SimAction.PointDefense(range, damage);
                }
            case 29:
                {
                    var range = r.F64();
                    if (!(double.IsFinite(range) && range >= 0))
                    {
                        throw new SimLinkException(SimLinkError.BadValue, "bad invulnerability core action");
                    }
                    return new SimAction.Panic(range);
                }
            case 31:
                {
                    var range = r.F64();
                    if (!(double.IsFinite(range) && range > 0))
                    {
                        throw new SimLinkException(SimLinkError.BadValue, "bad entosis action");
                    }
                    return new SimAction.Entosis(range);
                }
            case 28:
                {
                    var range = r.F64();
                    if (!(double.IsFinite(range) && range >= 0))
                    {
                        throw new SimLinkException(SimLinkError.BadValue, "bad command burst action");
                    }
                    return new SimAction.CommandBurst(range);
                }
            case 27:
                {
                    var ball = ReadDeployable(ref r);
                    var lifetime = r.F64();
                    if (!(double.IsFinite(lifetime) && lifetime > 0))
                    {
                        throw new SimLinkException(SimLinkError.BadValue, "bad cynosural field action");
                    }
                    return new SimAction.CynoField(ball, lifetime);
                }
            case 19:
                {
                    var ball = ReadDeployable(ref r);
                    var range = r.F64();
                    var lifetime = r.F64();
                    if (!(double.IsFinite(range) && range > 0 && double.IsFinite(lifetime) && lifetime > 0))
                    {
                        throw new SimLinkException(SimLinkError.BadValue, "bad warp probe action");
                    }
                    return new SimAction.WarpProbe(ball, range, lifetime);
                }
            case 20:
                {
                    var ball = ReadDeployable(ref r);
                    var kind = r.U8();
                    var lifetime = r.F64();
                    var blast = r.F64();
                    double[] damage = [r.F64(), r.F64(), r.F64(), r.F64()];
                    double[] ecm = [r.F64(), r.F64(), r.F64(), r.F64()];
                    var neutralize = r.F64();
                    if (kind > 2 || !(double.IsFinite(lifetime) && lifetime > 0 && double.IsFinite(blast) && blast > 0)
                        || damage.Concat(ecm).Append(neutralize).Any(n => !double.IsFinite(n) || n < 0))
                    {
                        throw new SimLinkException(SimLinkError.BadValue, "bad bomb action");
                    }
                    return new SimAction.Bomb(ball, (SimBombKind)kind, lifetime, blast, damage, ecm, neutralize);
                }
            case 6:
                return new SimAction.MicroJumpDrive(r.F64(), r.I64());
            case 7:
                {
                    var family = r.U8();
                    var mining = r.F64();
                    var wasteMultiplier = r.F64();
                    var wasteProbability = r.F64();
                    var critProbability = r.F64();
                    var critMultiplier = r.F64();
                    if (family > SimMiningFamilies.Gas || !(mining > 0) || !double.IsFinite(mining)
                        || new[] { wasteMultiplier, critMultiplier }.Any(n => !double.IsFinite(n) || n < 0)
                        || new[] { wasteProbability, critProbability }.Any(p => !double.IsFinite(p) || p < 0 || p > 1))
                    {
                        throw new SimLinkException(SimLinkError.BadValue, "bad mining action");
                    }
                    return new SimAction.Mining(family, mining, wasteMultiplier, wasteProbability, critProbability, critMultiplier);
                }
            case 8:
                {
                    var bonus = r.F64();
                    if (!double.IsFinite(bonus) || bonus < 0)
                    {
                        throw new SimLinkException(SimLinkError.BadValue, "bad salvage action");
                    }
                    return new SimAction.Salvage(bonus);
                }
            case 9:
                {
                    var velocity = r.F64();
                    var hold = r.F64();
                    var range = r.F64();
                    if (!double.IsFinite(velocity) || velocity <= 0 || !double.IsFinite(hold) || hold < 0
                        || !double.IsFinite(range) || range <= 0)
                    {
                        throw new SimLinkException(SimLinkError.BadValue, "bad tractor action");
                    }
                    return new SimAction.Tractor(velocity, hold, range);
                }
            case 10:
                return new SimAction.PassiveLock();
            case 11:
                {
                    var range = r.F64();
                    if (!double.IsFinite(range) || range <= 0)
                    {
                        throw new SimLinkException(SimLinkError.BadValue, "bad auto-target action");
                    }
                    return new SimAction.AutoTarget(range);
                }
            case 13:
                return ReadStrike(ref r);
            case 15:
                {
                    var strike = ReadStrike(ref r);
                    double[] destination = [r.F64(), r.F64(), r.F64()];
                    var warpBlock = r.F64();
                    var moved = ReadIds(ref r);
                    var held = ReadIds(ref r);
                    var field = new SimAction.Field(strike, destination, warpBlock, moved, held);
                    if (!field.IsValid)
                    {
                        throw new SimLinkException(SimLinkError.BadValue, "bad transport field action");
                    }
                    return field;
                }
            case 14:
                {
                    var delay = r.F64();
                    double[] damage = [r.F64(), r.F64(), r.F64(), r.F64()];
                    var immobile = r.F64();
                    var noJump = r.F64();
                    var doomsday = new SimAction.Doomsday(delay, damage, immobile, noJump);
                    if (!doomsday.IsValid)
                    {
                        throw new SimLinkException(SimLinkError.BadValue, "bad doomsday action");
                    }
                    return doomsday;
                }
            case 26:
                {
                    var kind = r.U8();
                    var optimal = r.F64();
                    var falloff = r.F64();
                    var scan = new SimAction.Scan(kind, optimal, falloff);
                    if (!scan.IsValid)
                    {
                        throw new SimLinkException(SimLinkError.BadValue, "bad scan action");
                    }
                    return scan;
                }
            case 23:
                {
                    double[] damage = [r.F64(), r.F64(), r.F64(), r.F64()];
                    var range = r.F64();
                    var kamikaze = new SimAction.Kamikaze(damage, range);
                    if (!kamikaze.IsValid)
                    {
                        throw new SimLinkException(SimLinkError.BadValue, "bad kamikaze action");
                    }
                    return kamikaze;
                }
            case 25:
                {
                    double[] centre = [r.F64(), r.F64(), r.F64()];
                    var radius = r.F64();
                    double[] damage = [r.F64(), r.F64(), r.F64(), r.F64()];
                    var blast = new SimAction.Blast(centre, radius, damage);
                    if (!blast.IsValid)
                    {
                        throw new SimLinkException(SimLinkError.BadValue, "bad fighter blast action");
                    }
                    return blast;
                }
            case 24:
                {
                    double[] damage = [r.F64(), r.F64(), r.F64(), r.F64()];
                    var reach = r.F64();
                    var radius = r.F64();
                    var velocity = r.F64();
                    var factor = r.F64();
                    var sensitivity = r.F64();
                    var missile = new SimAction.FighterMissile(damage, reach, radius, velocity, factor, sensitivity);
                    if (!missile.IsValid)
                    {
                        throw new SimLinkException(SimLinkError.BadValue, "bad fighter missile action");
                    }
                    return missile;
                }
            case 12:
                {
                    double[] seed = [r.F64(), r.F64(), r.F64(), r.F64()];
                    var shift = r.F64();
                    int[] attributes = [r.I32(), r.I32(), r.I32(), r.I32()];
                    if (seed.Any(resonance => !double.IsFinite(resonance) || resonance < 0 || resonance > 1) || !double.IsFinite(shift) || shift <= 0)
                    {
                        throw new SimLinkException(SimLinkError.BadValue, "bad resistance shift");
                    }
                    return new SimAction.ResistanceShift(seed, shift, attributes);
                }
            case 4:
                {
                    var typeId = r.I32();
                    var flag = r.I32();
                    var perCycle = r.I64();
                    var charges = r.I64();
                    var action = ReadAction(ref r);
                    var hasFallback = r.U8();
                    if (hasFallback > 1 || perCycle <= 0 || charges < 0) throw new SimLinkException(SimLinkError.BadValue, "bad charged action");
                    var fallback = hasFallback == 1 ? ReadAction(ref r) : null;
                    return new SimAction.Charged(action, fallback, typeId, flag, checked((int)perCycle), charges);
                }
            case 2:
                {
                    var range = r.F64();
                    double[] damage = [r.F64(), r.F64(), r.F64(), r.F64()];
                    var hurts = r.U8();
                    if (hurts > 1)
                    {
                        throw new SimLinkException(SimLinkError.BadValue, "bad smartbomb");
                    }
                    return new SimAction.Smartbomb(range, damage, hurts == 1);
                }
            case 1:
                {
                    var when = r.U8();
                    if (when > 1)
                    {
                        throw new SimLinkException(SimLinkError.BadValue, "bad remote");
                    }
                    var (optimal, falloff) = (r.F64(), r.F64());
                    var jam = r.Text();
                    SimRemote what = r.U8() switch
                    {
                        0 => ReadRemoteRepair(ref r),
                        11 => ReadMutadaptiveRepair(ref r),
                        1 => new SimRemote.Transfer(r.F64()),
                        2 => new SimRemote.Neutralize(r.F64(), r.F64()),
                        3 => new SimRemote.Drain(r.F64(), r.F64()),
                        4 => new SimRemote.Web(r.F64()),
                        5 => new SimRemote.Paint(r.F64()),
                        6 => new SimRemote.Damp(r.F64(), r.F64(), r.F64(), r.F64(), r.F64(), r.F64()),
                        7 => ReadScramble(ref r),
                        8 => new SimRemote.Jam(r.F64(), r.F64(), r.F64(), r.F64()),
                        9 => new SimRemote.Disrupt(r.F64(), r.F64(), r.F64()),
                        10 => new SimRemote.GuidanceDisrupt(r.F64(), r.F64(), r.F64(), r.F64()),
                        _ => throw new SimLinkException(SimLinkError.BadValue, "bad remote"),
                    };
                    if (what is SimRemote.MutadaptiveRepair && when != 1)
                    {
                        throw new SimLinkException(SimLinkError.BadValue, "bad mutadaptive repair timing");
                    }
                    return new SimAction.Remote(what, optimal, falloff, when == 1, jam);
                }
            case 32:
                {
                    var (range, duration) = (r.F64(), r.F64());
                    var what = ReadBurstWhat(ref r);
                    if (!(double.IsFinite(range) && range > 0 && double.IsFinite(duration) && duration > 0))
                    {
                        throw new SimLinkException(SimLinkError.BadValue, "bad structure burst");
                    }
                    return new SimAction.StructureBurst(range, duration, what);
                }
            default:
                throw new SimLinkException(SimLinkError.BadValue, "bad action");
        }
    }

    /// <summary>The remote effect of a structure burst: only the kinds of the burst families (neutralize, paint, damp, web, disrupt, scramble and jam).</summary>
    private static SimRemote ReadBurstWhat(ref Reader r) => r.U8() switch
    {
        2 => new SimRemote.Neutralize(r.F64(), r.F64()),
        4 => new SimRemote.Web(r.F64()),
        5 => new SimRemote.Paint(r.F64()),
        6 => new SimRemote.Damp(r.F64(), r.F64(), r.F64(), r.F64(), r.F64(), r.F64()),
        7 => ReadScramble(ref r),
        8 => new SimRemote.Jam(r.F64(), r.F64(), r.F64(), r.F64()),
        9 => new SimRemote.Disrupt(r.F64(), r.F64(), r.F64()),
        _ => throw new SimLinkException(SimLinkError.BadValue, "bad structure burst effect"),
    };

    private static SimRemote ReadScramble(ref Reader r)
    {
        var strength = r.F64();
        var blocks = r.U8();
        if (blocks > 1)
        {
            throw new SimLinkException(SimLinkError.BadValue, "bad scramble");
        }
        return new SimRemote.Scramble(strength, blocks == 1);
    }

    private static SimRemote ReadRemoteRepair(ref Reader r)
    {
        var layer = r.U8();
        if (layer > 2)
        {
            throw new SimLinkException(SimLinkError.BadValue, "bad remote repair");
        }
        return new SimRemote.Repair((SimLayer)layer, r.F64());
    }

    private static SimRemote ReadMutadaptiveRepair(ref Reader r)
    {
        var layer = r.U8();
        var amount = r.F64();
        var bonusPerCycle = r.F64();
        var bonusMax = r.F64();
        if (layer > 2 || !double.IsFinite(amount) || amount < 0 || !double.IsFinite(bonusPerCycle) || bonusPerCycle < 0 || !double.IsFinite(bonusMax) || bonusMax < 0)
        {
            throw new SimLinkException(SimLinkError.BadValue, "bad mutadaptive repair");
        }
        return new SimRemote.MutadaptiveRepair((SimLayer)layer, amount, bonusPerCycle, bonusMax);
    }

    private static SimTurret ReadTurret(ref Reader r)
    {
        var weaponType = r.I32();
        var fx = r.Text();
        double[] damage = [r.F64(), r.F64(), r.F64(), r.F64()];
        var (optimal, falloff, tracking, resolution) = (r.F64(), r.F64(), r.F64(), r.F64());
        var charge = r.U8() switch
        {
            0 => null,
            1 => new SimTurretCharge(r.I32(), r.I32(), r.I64(), r.I64(), r.U8() switch
            {
                0 => false,
                1 => true,
                _ => throw new SimLinkException(SimLinkError.BadValue, "bad charge reloads"),
            }),
            _ => throw new SimLinkException(SimLinkError.BadValue, "bad charge"),
        };
        var missile = r.U8() switch
        {
            0 => null,
            1 => ReadMissile(ref r),
            _ => throw new SimLinkException(SimLinkError.BadValue, "bad missile"),
        };
        var spool = r.U8() switch
        {
            0 => null,
            1 => ReadSpool(ref r),
            _ => throw new SimLinkException(SimLinkError.BadValue, "bad spool"),
        };
        var aoe = r.U8() switch
        {
            0 => null,
            1 => ReadAoe(ref r),
            _ => throw new SimLinkException(SimLinkError.BadValue, "bad aoe"),
        };
        var arc = r.U8() switch
        {
            0 => null,
            1 => ReadArc(ref r),
            _ => throw new SimLinkException(SimLinkError.BadValue, "bad arc"),
        };
        var requiresGunnery = r.U8() switch
        {
            0 => false,
            1 => true,
            _ => throw new SimLinkException(SimLinkError.BadValue, "bad requires_gunnery"),
        };
        return new SimTurret(weaponType, fx, damage, optimal, falloff, tracking, resolution, charge, missile, spool, aoe, arc, requiresGunnery);
    }

    private static SimAoe ReadAoe(ref Reader r)
    {
        var (radius, velocity) = (r.F64(), r.F64());
        var (factor, sensitivity) = (r.F64(), r.F64());
        if (!double.IsFinite(radius) || radius < 0 || !double.IsFinite(velocity) || velocity < 0
            || !double.IsFinite(factor) || factor < 0 || !double.IsFinite(sensitivity) || sensitivity < 0)
        {
            throw new SimLinkException(SimLinkError.BadValue, "bad aoe");
        }
        return new SimAoe(radius, velocity, factor, sensitivity);
    }

    private static SimArc ReadArc(ref Reader r)
    {
        var (range, targets) = (r.F64(), r.I32());
        if (!double.IsFinite(range) || range < 0 || targets < 0)
        {
            throw new SimLinkException(SimLinkError.BadValue, "bad arc");
        }
        var damageLossTarget = ReadFlag(ref r, "arc damage loss target") ? r.F64() : (double?)null;
        if (damageLossTarget is { } factor && (!double.IsFinite(factor) || factor < 0))
            throw new SimLinkException(SimLinkError.BadValue, "bad arc damage loss target");
        return new SimArc(range, targets, damageLossTarget);
    }

    private static SimSpool ReadSpool(ref Reader r)
    {
        var (perCycle, max) = (r.F64(), r.F64());
        if (!double.IsFinite(perCycle) || perCycle < 0 || !double.IsFinite(max) || max < 0)
        {
            throw new SimLinkException(SimLinkError.BadValue, "bad spool");
        }
        return new SimSpool(perCycle, max);
    }

    private static SimHeatProfile ReadHeatProfile(ref Reader r)
    {
        var generation = r.F64();
        var damage = r.F64();
        var thermodynamics = r.I32();
        double[] capacity = [r.F64(), r.F64(), r.F64()];
        double[] initial = [r.F64(), r.F64(), r.F64()];
        double[] dissipation = [r.F64(), r.F64(), r.F64()];
        double[] attenuation = [r.F64(), r.F64(), r.F64()];
        var count = r.I32();
        if (count is < 0 or > 256 || thermodynamics is < 0 or > 5) throw new SimLinkException(SimLinkError.BadValue, "bad heat profile");
        var modules = new SimHeatModule[count];
        for (var i = 0; i < count; i++)
            modules[i] = new SimHeatModule(r.I64(), r.I32(), r.F64(), r.F64(), r.F64(), r.F64(),
                ReadHeatBool(ref r), ReadHeatBool(ref r), ReadHeatBool(ref r));
        return new SimHeatProfile(generation, damage, thermodynamics, capacity, initial, dissipation, attenuation, modules);
    }

    private static bool ReadHeatBool(ref Reader r)
    {
        var value = r.U8();
        if (value > 1) throw new SimLinkException(SimLinkError.BadValue, "bad heat flag");
        return value == 1;
    }
}
