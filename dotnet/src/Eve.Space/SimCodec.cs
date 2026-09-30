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
    public const ushort ProtocolVersion = 1;

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
        public const byte Destroy = 24;
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
            var moduleId = r.I64();
            var effectId = r.I32();
            var cycleMs = r.F64();
            var capacitorNeed = r.F64();
            var weaponType = r.I32();
            var fx = r.Text();
            double[] damage = [r.F64(), r.F64(), r.F64(), r.F64()];
            weapons[i] = new SimNpcWeapon(moduleId, effectId, cycleMs, capacitorNeed, weaponType, fx, damage, r.F64(), r.F64(), r.F64(), r.F64());
        }
        SimWreckKind? wreck = r.U8() switch
        {
            0 => null,
            1 => new SimWreckKind(r.I32(), r.Text(), r.I32()),
            _ => throw new SimLinkException(SimLinkError.BadValue, "bad wreck"),
        };
        return new SimNpcIdentity(hostile, friendly, aggression, weapons, wreck);
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
        return new SimShip(shipId, characterId, typeId, groupId, categoryId, name, ownerId, corporationId, allianceId, warFactionId, security, radius, mass, maxVelocity, agility, warpSpeed, shieldRecharge, maxTargetRange, scanResolution, signatureRadius, maxLockedTargets, capacitorCapacity, capacitorRecharge, modules, vitals, condition);
    }

    private static SimCondition ReadCondition(ref Reader r) => new(r.F64(), r.F64(), r.F64(), r.F64());

    private static double[] ReadFour(ref Reader r) => [r.F64(), r.F64(), r.F64(), r.F64()];

    private static SimVitals ReadVitals(ref Reader r)
    {
        var shield = r.F64();
        var armor = r.F64();
        var structure = r.F64();
        return new SimVitals(shield, armor, structure, new SimResonances(ReadFour(ref r), ReadFour(ref r), ReadFour(ref r)));
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
        if (flags > 3)
        {
            throw new SimLinkException(SimLinkError.BadValue, "bad flags");
        }
        var on = (flags & 1) != 0 ? ReadNumbers(ref r) : null;
        var off = (flags & 2) != 0 ? ReadNumbers(ref r) : null;
        var onChanges = ReadChanges(ref r);
        var offChanges = ReadChanges(ref r);
        return new SimEffect(module, moduleType, effectId, fx, duration, capacitor, repeat, target, on, off, onChanges, offChanges);
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
                message = new SimDestroyed(r.U64(), r.I64(), r.I64(), ReadVector(ref r), ReadVector(ref r), r.F64(), r.I64());
                break;
            case Kind.Spawn:
                {
                    var requestId = r.U64();
                    var solarSystemId = r.I32();
                    var ship = ReadShip(ref r);
                    var position = ReadVector(ref r);
                    var velocity = ReadVector(ref r);
                    var direction = ReadVector(ref r);
                    var behavior = r.Text();
                    message = new SimSpawn(requestId, solarSystemId, ship, position, velocity, direction, behavior, ReadIdentity(ref r));
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
            case Kind.Destroy:
                message = new SimDestroy(r.I64());
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
            w.I64(weapon.ModuleId);
            w.I32(weapon.EffectId);
            w.F64(weapon.CycleMs);
            w.F64(weapon.CapacitorNeed);
            w.I32(weapon.WeaponTypeId);
            w.Text(weapon.Fx);
            WriteVector(w, weapon.RawDamage);
            w.F64(weapon.OptimalRange);
            w.F64(weapon.Falloff);
            w.F64(weapon.TrackingSpeed);
            w.F64(weapon.SignatureResolution);
        }
        if (i.Wreck is { } wreck)
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
        w.U8((byte)((e.On is null ? 0 : 1) | (e.Off is null ? 0 : 2)));
        if (e.On is { } on)
        {
            WriteNumbers(w, on);
        }
        if (e.Off is { } off)
        {
            WriteNumbers(w, off);
        }
        WriteChanges(w, e.OnChanges);
        WriteChanges(w, e.OffChanges);
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
                break;
            case SimSpawn m:
                w.U8(Kind.Spawn);
                w.U64(m.RequestId);
                w.I32(m.SolarSystemId);
                WriteShip(w, m.Ship);
                WriteVector(w, m.Position);
                WriteVector(w, m.Velocity);
                WriteVector(w, m.Direction);
                w.Text(m.Behavior);
                WriteIdentity(w, m.Identity);
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
