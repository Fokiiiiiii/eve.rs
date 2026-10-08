using System.Buffers.Binary;
using System.Text;

namespace Eve.Space;

public enum SimLinkError
{
    Empty,
    UnknownKind,
    Truncated,
    Trailing,
    Utf8,
    BadValue,
    FrameTooLarge,
}

/// <summary>The peer broke the link protocol; the connection should be closed.</summary>
public sealed class SimLinkException(SimLinkError error, string message) : Exception(message)
{
    public SimLinkError Error { get; } = error;
}

/// <summary>
/// The wire layout of <see cref="SimMessage"/>: <c>u32 length | u8 kind | body</c>, little endian, text as <c>u16 length + UTF-8</c>,
/// byte strings as <c>u32 length + bytes</c>. The Rust simulation and this codec are both checked against
/// oracle/fixtures/simlink/messages.json.
/// </summary>
public static class SimCodec
{
    public const ushort ProtocolVersion = 22;

    /// <summary>The largest frame either side accepts (1 MiB).</summary>
    public const int MaxFrame = 1 << 20;

    private static readonly UTF8Encoding Strict = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private static class Kind
    {
        public const byte Hello = 1;
        public const byte HelloAck = 2;
        public const byte Enter = 3;
        public const byte Leave = 4;
        public const byte Bind = 5;
        public const byte UpdateState = 6;
        public const byte Command = 7;
        public const byte ShipChanged = 8;
        public const byte Notify = 9;
        public const byte Docked = 10;
        public const byte Refused = 11;
        public const byte Accepted = 12;
        public const byte EnterReply = 13;
        public const byte Approaching = 14;
        public const byte Jumping = 15;
        public const byte Jumped = 16;
        public const byte Targets = 17;
        public const byte TargetRefused = 18;
        public const byte Activate = 19;
        public const byte Condition = 20;
        public const byte Destroyed = 21;
        public const byte Spawn = 22;
        public const byte SpawnReply = 23;
        public const byte LaunchDrone = 36;
        public const byte DroneCommand = 37;
        public const byte DroneResults = 38;
        public const byte DroneScooped = 39;
        public const byte DroneReleased = 40;
        public const byte HeatProfile = 41;
        public const byte ModuleOverheat = 42;
        public const byte ModuleOffline = 43;
        public const byte RackOverheat = 44;
        public const byte Destroy = 24;
        public const byte Respawn = 25;
        public const byte PlaceWreck = 26;
        public const byte ChargeSpent = 27;
        public const byte PlaceContainer = 28;
        public const byte ContainerPlaced = 29;
        public const byte ChangeWreck = 30;
        public const byte RemoveWreck = 31;
        public const byte Measured = 32;
        public const byte AdjustCharge = 33;
        public const byte ChargeAdjusted = 34;
        public const byte Arrived = 35;
        public const byte PlaceAsteroid = 45;
        public const byte DroneWorkCycle = 46;
        public const byte DroneWorkResult = 47;
        public const byte RemoteRepairCycle = 48;
        public const byte RemoteRepairCycleResult = 49;
        public const byte Position = 50;
    }

    private ref struct Reader(ReadOnlyMemory<byte> data)
    {
        private readonly ReadOnlyMemory<byte> _data = data;
        private int _at = 1; // after the kind byte

        private ReadOnlySpan<byte> Take(int count)
        {
            if (count < 0 || _data.Length - _at < count)
            {
                throw new SimLinkException(SimLinkError.Truncated, "the frame ends before the message does");
            }
            var slice = _data.Span.Slice(_at, count);
            _at += count;
            return slice;
        }

        public byte U8() => Take(1)[0];

        public ushort U16() => BinaryPrimitives.ReadUInt16LittleEndian(Take(2));

        public uint U32() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));

        public int I32() => BinaryPrimitives.ReadInt32LittleEndian(Take(4));

        public ulong U64() => BinaryPrimitives.ReadUInt64LittleEndian(Take(8));

        public long I64() => BinaryPrimitives.ReadInt64LittleEndian(Take(8));

        public double F64() => BinaryPrimitives.ReadDoubleLittleEndian(Take(8));

        public string Text()
        {
            var bytes = Take(U16());
            try
            {
                return Strict.GetString(bytes);
            }
            catch (DecoderFallbackException)
            {
                throw new SimLinkException(SimLinkError.Utf8, "text is not UTF-8");
            }
        }

        public ReadOnlyMemory<byte> Bytes()
        {
            var length = U32();
            if (length > int.MaxValue || _data.Length - _at < (int)length)
            {
                throw new SimLinkException(SimLinkError.Truncated, "the frame ends before the message does");
            }
            var slice = _data.Slice(_at, (int)length);
            _at += (int)length;
            return slice;
        }

        public int Remaining => _data.Length - _at;

        public void Finish()
        {
            if (_at != _data.Length)
            {
                throw new SimLinkException(SimLinkError.Trailing, "bytes follow the end of the message");
            }
        }
    }

    private static double[] ReadVector(ref Reader r) => [r.F64(), r.F64(), r.F64()];

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
            turret.SignatureResolution, turret.Charge, turret.Missile);
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
            weapon.WeaponTypeId, weapon.Fx, weapon.RawDamage, weapon.OptimalRange, weapon.Falloff, weapon.TrackingSpeed, weapon.SignatureResolution, weapon.Charge, weapon.Missile));
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
        return new SimNpcIdentity(hostile, friendly, aggression, weapons, wreck, hull, hostWreck);
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
        var sensors = new SimSensors(r.F64(), r.F64(), r.F64(), r.F64(), r.F64());
        return new SimShip(shipId, characterId, typeId, groupId, categoryId, name, ownerId, corporationId, allianceId, warFactionId, security, radius, mass, maxVelocity, agility, warpSpeed, shieldRecharge, maxTargetRange, scanResolution, signatureRadius, maxLockedTargets, capacitorCapacity, capacitorRecharge, modules, vitals, condition, sensors);
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
        if (flags > 15)
        {
            throw new SimLinkException(SimLinkError.BadValue, "bad flags");
        }
        var on = (flags & 1) != 0 ? ReadNumbers(ref r) : null;
        var off = (flags & 2) != 0 ? ReadNumbers(ref r) : null;
        var onChanges = ReadChanges(ref r);
        var offChanges = ReadChanges(ref r);
        var turret = (flags & 4) != 0 ? ReadTurret(ref r) : null;
        var action = (flags & 8) != 0 ? ReadAction(ref r) : null;
        return new SimEffect(module, moduleType, effectId, fx, duration, capacitor, repeat, target, on, off, onChanges, offChanges, turret, action);
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
                return new SimAction.Cloak();
            case 6:
                return new SimAction.MicroJumpDrive(r.F64(), r.I64());
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
            default:
                throw new SimLinkException(SimLinkError.BadValue, "bad action");
        }
    }

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
            1 => new SimTurretCharge(r.I32(), r.I32(), r.I64(), r.I64()),
            _ => throw new SimLinkException(SimLinkError.BadValue, "bad charge"),
        };
        var missile = r.U8() switch
        {
            0 => null,
            1 => new SimMissile(r.I32(), r.I32(), r.Text(), r.F64(), r.F64(), r.F64(), r.F64(), r.F64(), r.F64(), r.F64(), r.F64(), r.F64(), r.F64()),
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
        return new SimTurret(weaponType, fx, damage, optimal, falloff, tracking, resolution, charge, missile, spool, aoe, arc);
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
        return new SimArc(range, targets);
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

    /// <summary>Decodes a frame's payload: the kind byte and the body (without the length prefix).</summary>
    public static SimMessage Decode(ReadOnlyMemory<byte> payload)
    {
        if (payload.IsEmpty)
        {
            throw new SimLinkException(SimLinkError.Empty, "empty frame");
        }
        var r = new Reader(payload);
        SimMessage message;
        switch (payload.Span[0])
        {
            case Kind.Hello:
                message = new SimHello(r.U16());
                break;
            case Kind.HelloAck:
                message = new SimHelloAck(r.U16(), r.U32());
                break;
            case Kind.HeatProfile:
                message = new SimSetHeatProfile(r.U64(), ReadHeatProfile(ref r));
                break;
            case Kind.ModuleOverheat:
                {
                    var session = r.U64();
                    var module = r.I64();
                    var active = r.U8();
                    if (active > 1) throw new SimLinkException(SimLinkError.BadValue, "bad module overheat state");
                    message = new SimSetModuleOverheat(session, module, active == 1);
                    break;
                }
            case Kind.ModuleOffline:
                message = new SimSetModuleOffline(r.U64(), r.I64());
                break;
            case Kind.RackOverheat:
                {
                    var session = r.U64();
                    var rack = r.U8();
                    var active = r.U8();
                    if (rack > 2 || active > 1) throw new SimLinkException(SimLinkError.BadValue, "bad rack overheat state");
                    message = new SimSetRackOverheat(session, rack, active == 1);
                    break;
                }
            case Kind.Enter:
                {
                    var session = r.U64();
                    var system = r.I32();
                    var how = r.U8() switch
                    {
                        0 => EnterHow.Undock,
                        1 => EnterHow.InSpace,
                        _ => throw new SimLinkException(SimLinkError.BadValue, "bad how"),
                    };
                    var station = r.I64();
                    message = new SimEnter(session, system, how, station, ReadShip(ref r));
                    break;
                }
            case Kind.EnterReply:
                {
                    var session = r.U64();
                    var status = r.U8();
                    var reason = r.Text();
                    message = status switch
                    {
                        0 => new SimEnterReply(session, null),
                        1 => new SimEnterReply(session, reason),
                        _ => throw new SimLinkException(SimLinkError.BadValue, "bad status"),
                    };
                    break;
                }
            case Kind.Leave:
                message = new SimLeave(r.U64(), (LeaveWhy)r.U8());
                break;
            case Kind.Bind:
                message = new SimBind(r.U64());
                break;
            case Kind.UpdateState:
                message = new SimUpdateState(r.U64());
                break;
            case Kind.Command:
                {
                    var session = r.U64();
                    var code = r.U8();
                    var args = new double[r.U8()];
                    for (var i = 0; i < args.Length; i++)
                    {
                        args[i] = r.F64();
                    }
                    message = new SimCommand(session, code, args);
                    break;
                }
            case Kind.ShipChanged:
                message = new SimShipChanged(r.U64(), ReadShip(ref r));
                break;
            case Kind.Notify:
                message = new SimNotify(r.U64(), r.Text(), r.Text(), r.Bytes());
                break;
            case Kind.Docked:
                message = new SimDocked(r.U64(), r.I64(), ReadCondition(ref r));
                break;
            case Kind.Condition:
                message = new SimConditionReply(r.U64(), r.U8(), ReadCondition(ref r));
                break;
            case Kind.Refused:
                message = new SimRefused(r.U64(), r.U8(), r.Text());
                break;
            case Kind.Accepted:
                message = new SimAccepted(r.U64(), r.U8(), r.I64());
                break;
            case Kind.Position:
                message = new SimPosition(r.U64(), r.U8(), ReadVector(ref r));
                break;
            case Kind.Approaching:
                message = new SimApproaching(r.U64(), r.U8(), r.F64());
                break;
            case Kind.Jumping:
                message = new SimJumping(r.U64(), r.U8(), r.I32(), r.I64());
                break;
            case Kind.Jumped:
                message = new SimJumped(r.U64(), r.I32(), r.I64(), r.I32(), r.I64());
                break;
            case Kind.Destroyed:
                message = new SimDestroyed(r.U64(), r.I64(), r.I64(), ReadVector(ref r), ReadVector(ref r), r.F64(), r.I64(), ReadWreckKind(ref r));
                break;
            case Kind.Respawn:
                message = new SimRespawn(r.U64(), ReadShip(ref r), ReadVector(ref r), ReadVector(ref r));
                break;
            case Kind.PlaceWreck:
                {
                    var system = r.I32();
                    var wreckId = r.I64();
                    var group = r.I32();
                    var kind = ReadWreckKind(ref r) ?? throw new SimLinkException(SimLinkError.BadValue, "bad wreck");
                    var position = ReadVector(ref r);
                    var direction = ReadVector(ref r);
                    var radius = r.F64();
                    var launcher = r.I64();
                    var credit = ReadCredit(ref r);
                    var empty = r.U8() switch
                    {
                        0 => false,
                        1 => true,
                        _ => throw new SimLinkException(SimLinkError.BadValue, "bad is_empty"),
                    };
                    message = new SimPlaceWreck(system, wreckId, group, kind, position, direction, radius, launcher, credit, empty);
                    break;
                }
            case Kind.Spawn:
                {
                    var requestId = r.U64();
                    var solarSystemId = r.I32();
                    var ship = ReadShip(ref r);
                    var anchor = r.I64();
                    var position = ReadVector(ref r);
                    var velocity = ReadVector(ref r);
                    var direction = ReadVector(ref r);
                    var behavior = r.Text();
                    message = new SimSpawn(requestId, solarSystemId, ship, anchor, position, velocity, direction, behavior, ReadIdentity(ref r));
                    break;
                }
            case Kind.SpawnReply:
                {
                    var requestId = r.U64();
                    message = r.U8() switch
                    {
                        0 => new SimSpawnReply(requestId, r.U64(), null),
                        1 => new SimSpawnReply(requestId, null, r.Text()),
                        _ => throw new SimLinkException(SimLinkError.BadValue, "bad status"),
                    };
                    break;
                }
            case Kind.LaunchDrone:
                message = new SimLaunchDrone(r.U64(), r.U64(), ReadShip(ref r), ReadOptionalWeapon(ref r), r.I32(), r.F64(), r.F64(), r.F64(), new SimDroneWorkProfile(r.F64(), r.F64(), r.F64(), r.F64(), r.F64()), ReadOptionalDroneEffect(ref r));
                break;
            case Kind.DroneCommand:
                {
                    var session = r.U64();
                    var command = r.U8();
                    var target = r.I64();
                    var count = r.U32();
                    if (r.Remaining < 8 || count > (r.Remaining - 8) / 8)
                    {
                        throw new SimLinkException(SimLinkError.Truncated, "the frame ends before the message does");
                    }
                    var drones = new long[count];
                    for (var i = 0; i < drones.Length; i++)
                    {
                        drones[i] = r.I64();
                    }
                    message = new SimDroneCommand(session, command, target, drones, r.F64());
                    break;
                }
            case Kind.DroneResults:
                {
                    var session = r.U64();
                    var command = r.U8();
                    var count = r.U32();
                    if (count > r.Remaining / 10)
                    {
                        throw new SimLinkException(SimLinkError.Truncated, "the frame ends before the message does");
                    }
                    var results = new SimDroneResult[count];
                    for (var i = 0; i < results.Length; i++)
                    {
                        results[i] = new SimDroneResult(r.I64(), r.Text());
                    }
                    message = new SimDroneResults(session, command, results);
                    break;
                }
            case Kind.DroneScooped:
                message = new SimDroneScooped(r.U64(), r.I64());
                break;
            case Kind.DroneReleased:
                message = new SimDroneReleased(r.U64(), r.I64(), ReadVector(ref r));
                break;
            case Kind.DroneWorkCycle:
                message = new SimDroneWorkCycle(r.U64(), r.I64(), r.I64(), r.I32(), r.I64(), r.I64(), r.U64(), r.U8(), r.F64(), r.F64(), r.F64(), r.F64(), r.F64());
                break;
            case Kind.DroneWorkResult:
                {
                    var session = r.U64();
                    var drone = r.I64();
                    var target = r.I64();
                    var cycleId = r.U64();
                    var accepted = r.U8();
                    var reason = r.Text();
                    var unitsRemaining = r.I64();
                    var deliveredUnits = r.I64();
                    var complete = r.U8();
                    if (cycleId == 0 || accepted > 1 || unitsRemaining < 0 || deliveredUnits < 0 || complete > 1)
                    {
                        throw new SimLinkException(SimLinkError.BadValue, "bad drone work result");
                    }
                    message = new SimDroneWorkResult(
                        session, drone, target, cycleId, accepted == 1, reason, unitsRemaining, deliveredUnits, complete == 1);
                    break;
                }
            case Kind.RemoteRepairCycle:
                {
                    var session = r.U64();
                    var module = r.I64();
                    var target = r.I64();
                    var cycleId = r.U64();
                    var cycleNumber = r.I32();
                    var cycleAtMs = r.I64();
                    var cycleDurationMs = r.I64();
                    var endsAfterCycle = r.U8();
                    var baseAmount = r.F64();
                    var bonusPerCycle = r.F64();
                    var bonusMax = r.F64();
                    if (cycleId == 0 || cycleNumber <= 0 || cycleDurationMs <= 0 || endsAfterCycle > 1 || !double.IsFinite(baseAmount) || baseAmount < 0 || !double.IsFinite(bonusPerCycle) || bonusPerCycle < 0 || !double.IsFinite(bonusMax) || bonusMax < 0)
                    {
                        throw new SimLinkException(SimLinkError.BadValue, "bad remote repair cycle");
                    }
                    message = new SimRemoteRepairCycle(session, module, target, cycleId, cycleNumber, cycleAtMs, cycleDurationMs, endsAfterCycle == 1, baseAmount, bonusPerCycle, bonusMax);
                    break;
                }
            case Kind.RemoteRepairCycleResult:
                {
                    var session = r.U64();
                    var module = r.I64();
                    var target = r.I64();
                    var cycleId = r.U64();
                    var accepted = r.U8();
                    var reason = r.Text();
                    var amount = r.F64();
                    if (cycleId == 0 || accepted > 1 || !double.IsFinite(amount) || amount < 0)
                    {
                        throw new SimLinkException(SimLinkError.BadValue, "bad remote repair cycle result");
                    }
                    message = new SimRemoteRepairCycleResult(session, module, target, cycleId, accepted == 1, reason, amount);
                    break;
                }
            case Kind.Destroy:
                message = new SimDestroy(r.I64(), ReadCredit(ref r));
                break;
            case Kind.Targets:
                {
                    var session = r.U64();
                    var command = r.U8();
                    var pending = r.U8() switch
                    {
                        0 => false,
                        1 => true,
                        _ => throw new SimLinkException(SimLinkError.BadValue, "bad pending"),
                    };
                    var count = r.U32();
                    // a target takes 8 bytes, so a count the frame cannot hold is refused before anything is allocated for it
                    if (count > r.Remaining / 8)
                    {
                        throw new SimLinkException(SimLinkError.Truncated, "the frame ends before the message does");
                    }
                    var targets = new long[count];
                    for (var i = 0; i < targets.Length; i++)
                    {
                        targets[i] = r.I64();
                    }
                    message = new SimTargets(session, command, pending, targets);
                    break;
                }
            case Kind.TargetRefused:
                message = new SimTargetRefused(r.U64(), r.U8(), r.Text(), r.I32(), r.F64(), r.F64());
                break;
            case Kind.Activate:
                message = new SimActivate(r.U64(), ReadEffect(ref r));
                break;
            case Kind.ChargeSpent:
                message = new SimChargeSpent(r.U64(), r.I64(), r.I64());
                break;
            case Kind.AdjustCharge:
                {
                    var request = r.U64();
                    var session = r.U64();
                    var module = r.I64();
                    var type = r.I32();
                    var op = r.U8();
                    var (first, second) = (r.I64(), r.I64());
                    message = new SimAdjustCharge(request, session, module, type, op switch
                    {
                        0 => new SimChargeOp.Add(first, second),
                        1 => new SimChargeOp.Remove(first),
                        _ => throw new SimLinkException(SimLinkError.BadValue, "charge op"),
                    });
                    break;
                }
            case Kind.ChargeAdjusted:
                {
                    var request = r.U64();
                    var running = r.U8() switch
                    {
                        0 => false,
                        1 => true,
                        _ => throw new SimLinkException(SimLinkError.BadValue, "running"),
                    };
                    message = new SimChargeAdjusted(request, running, r.I64(), r.I64());
                    break;
                }
            case Kind.PlaceContainer:
                {
                    var session = r.U64();
                    var container = r.I64();
                    var kind = ReadWreckKind(ref r) ?? throw new SimLinkException(SimLinkError.BadValue, "bad container");
                    message = new SimPlaceContainer(session, container, kind, r.F64(), r.I64(), ReadCredit(ref r), r.F64());
                    break;
                }
            case Kind.Arrived:
                message = new SimArrived(r.U64(), r.I64(), ReadVector(ref r));
                break;
            case Kind.ContainerPlaced:
                message = new SimContainerPlaced(r.U64(), r.I64(), ReadVector(ref r), ReadVector(ref r));
                break;
            case Kind.ChangeWreck:
                {
                    var session = r.U64();
                    var wreck = r.I64();
                    message = new SimChangeWreck(session, wreck, r.U8() switch
                    {
                        0 => false,
                        1 => true,
                        _ => throw new SimLinkException(SimLinkError.BadValue, "bad is_empty"),
                    });
                    break;
                }
            case Kind.RemoveWreck:
                message = new SimRemoveWreck(r.I64(), r.I32());
                break;
            case Kind.Measured:
                {
                    var session = r.U64();
                    var command = r.U8();
                    message = new SimMeasured(session, command, r.U8() switch
                    {
                        0 => null,
                        1 => r.F64(),
                        _ => throw new SimLinkException(SimLinkError.BadValue, "bad distance"),
                    });
                    break;
                }
            case var other:
                throw new SimLinkException(SimLinkError.UnknownKind, $"unknown message kind {other}");
        }
        r.Finish();
        return message;
    }

    private sealed class Writer
    {
        private readonly MemoryStream _out = new();

        public void U8(byte value) => _out.WriteByte(value);

        private void Put(Action<Span<byte>> write, int size)
        {
            Span<byte> buffer = stackalloc byte[size];
            write(buffer);
            _out.Write(buffer);
        }

        public void U16(ushort value) => Put(b => BinaryPrimitives.WriteUInt16LittleEndian(b, value), 2);

        public void U32(uint value) => Put(b => BinaryPrimitives.WriteUInt32LittleEndian(b, value), 4);

        public void I32(int value) => Put(b => BinaryPrimitives.WriteInt32LittleEndian(b, value), 4);

        public void U64(ulong value) => Put(b => BinaryPrimitives.WriteUInt64LittleEndian(b, value), 8);

        public void I64(long value) => Put(b => BinaryPrimitives.WriteInt64LittleEndian(b, value), 8);

        public void F64(double value) => Put(b => BinaryPrimitives.WriteDoubleLittleEndian(b, value), 8);

        public void Text(string text)
        {
            var bytes = Strict.GetBytes(text);
            if (bytes.Length > ushort.MaxValue)
            {
                throw new ArgumentException("text is longer than a u16 length allows", nameof(text));
            }
            U16((ushort)bytes.Length);
            _out.Write(bytes);
        }

        public void Bytes(ReadOnlySpan<byte> bytes)
        {
            U32((uint)bytes.Length);
            _out.Write(bytes);
        }

        public byte[] ToArray() => _out.ToArray();
    }

    private static void WriteVector(Writer w, IReadOnlyList<double> v)
    {
        foreach (var number in v)
        {
            w.F64(number);
        }
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
        }
        else
        {
            w.U8(0);
        }
        if (t.Missile is { } missile)
        {
            w.U8(1);
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
            case SimAction.Cloak:
                w.U8(5);
                break;
            case SimAction.MicroJumpDrive jump:
                w.U8(6); w.F64(jump.DistanceMeters); w.I64(jump.CooldownMs);
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
        w.U8((byte)((e.On is null ? 0 : 1) | (e.Off is null ? 0 : 2) | (e.Turret is null ? 0 : 4) | (e.Action is null ? 0 : 8)));
        if (e.On is { } on) WriteNumbers(w, on);
        if (e.Off is { } off) WriteNumbers(w, off);
        WriteChanges(w, e.OnChanges);
        WriteChanges(w, e.OffChanges);
        if (e.Turret is { } turret) WriteTurret(w, turret);
        if (e.Action is { } action) WriteAction(w, action);
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
        foreach (var number in new[] { sensors.Gravimetric, sensors.Ladar, sensors.Magnetometric, sensors.Radar, sensors.EcmResistance })
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
