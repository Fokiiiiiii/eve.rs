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
        return new SimShip(shipId, characterId, typeId, groupId, categoryId, name, ownerId, corporationId, allianceId, warFactionId, security, radius, mass, maxVelocity, agility, warpSpeed, shieldRecharge, modules);
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
                message = new SimDocked(r.U64(), r.I64());
                break;
            case Kind.Refused:
                message = new SimRefused(r.U64(), r.U8(), r.Text());
                break;
            case Kind.Accepted:
                message = new SimAccepted(r.U64(), r.U8(), r.I64());
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
        foreach (var number in new[] { s.SecurityStatus, s.Radius, s.Mass, s.MaxVelocity, s.Agility, s.WarpSpeedAu, s.ShieldRechargeMs })
        {
            w.F64(number);
        }
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
