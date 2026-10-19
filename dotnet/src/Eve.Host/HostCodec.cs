using System.Buffers.Binary;
using System.Text;

namespace Eve.Host;


public abstract record HostMessage;

public sealed record Hello(ushort Version, uint NodeId) : HostMessage;

public sealed record HelloAck(ushort Version) : HostMessage;

public sealed record AuthRequest(uint Corr, string UserName, ReadOnlyMemory<byte> PasswordHash, string Language, string Address) : HostMessage;

public abstract record AuthOutcome;

public sealed record Accepted(uint UserId, ulong Role, string CountryCode) : AuthOutcome;

public sealed record Rejected(string Reason) : AuthOutcome;

public sealed record AuthReply(uint Corr, AuthOutcome Outcome) : HostMessage;

public sealed record SessionOpen(ulong SessionId, ulong ClientId, uint UserId, ulong Role, string Address, string Language, string CountryCode) : HostMessage;

public sealed record SessionClose(ulong SessionId) : HostMessage;

/// <summary>A client packet, as the marshal bytes the client sent.</summary>
public sealed record Packet(ulong SessionId, ReadOnlyMemory<byte> Payload) : HostMessage;

/// <summary>A packet for a client, as marshal bytes.</summary>
public sealed record Send(ulong SessionId, ReadOnlyMemory<byte> Payload) : HostMessage;

public sealed record Disconnect(ulong SessionId, string Reason) : HostMessage;

/// <summary>The global-config dictionary the handshake hands the client, as marshal bytes.</summary>
public sealed record ClusterConfig(ReadOnlyMemory<byte> Payload) : HostMessage;

public enum HostLinkError
{
    Empty,
    UnknownKind,
    Truncated,
    Trailing,
    Utf8,
    BadStatus,
    FrameTooLarge,
}

/// <summary>The peer broke the link protocol; the connection should be closed.</summary>
public sealed class HostLinkException(HostLinkError error, string message) : Exception(message)
{
    public HostLinkError Error { get; } = error;
}

/// <summary>
/// The wire layout of <see cref="HostMessage"/>: <c>u32 length | u8 kind | body</c>, little endian, text as

/// both checked against oracle/fixtures/hostlink/messages.json.
/// </summary>
public static class HostCodec
{
    public const ushort ProtocolVersion = 1;

    /// <summary>The largest frame either side accepts (16 MiB).</summary>
    public const int MaxFrame = 16 << 20;

    private static readonly UTF8Encoding Strict = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private static class Kind
    {
        public const byte Hello = 1;
        public const byte HelloAck = 2;
        public const byte AuthRequest = 3;
        public const byte AuthReply = 4;
        public const byte SessionOpen = 5;
        public const byte SessionClose = 6;
        public const byte Packet = 7;
        public const byte Send = 8;
        public const byte Disconnect = 9;
        public const byte ClusterConfig = 10;
    }

    // ── decoding ────────────────────────────────────────────────────────────

    private ref struct Reader(ReadOnlyMemory<byte> data)
    {
        private readonly ReadOnlyMemory<byte> _data = data;
        private int _at = 1; // after the kind byte

        private ReadOnlyMemory<byte> Take(int count)
        {
            if (count < 0 || _data.Length - _at < count)
            {
                throw new HostLinkException(HostLinkError.Truncated, "the frame ends before the message does");
            }
            var slice = _data.Slice(_at, count);
            _at += count;
            return slice;
        }

        public byte U8() => Take(1).Span[0];

        public ushort U16() => BinaryPrimitives.ReadUInt16LittleEndian(Take(2).Span);

        public uint U32() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4).Span);

        public ulong U64() => BinaryPrimitives.ReadUInt64LittleEndian(Take(8).Span);

        public string Text()
        {
            var bytes = Take(U16()).Span;
            try
            {
                return Strict.GetString(bytes);
            }
            catch (DecoderFallbackException)
            {
                throw new HostLinkException(HostLinkError.Utf8, "text is not UTF-8");
            }
        }

        public ReadOnlyMemory<byte> Bytes() => Take(checked((int)Math.Min(U32(), int.MaxValue)));

        public ReadOnlyMemory<byte> Rest()
        {
            var rest = _data[_at..];
            _at = _data.Length;
            return rest;
        }

        public void Finish()
        {
            if (_at != _data.Length)
            {
                throw new HostLinkException(HostLinkError.Trailing, "bytes follow the end of the message");
            }
        }
    }

    /// <summary>Decodes a frame's payload: the kind byte and the body (without the length prefix).</summary>
    public static HostMessage Decode(ReadOnlyMemory<byte> payload)
    {
        if (payload.IsEmpty)
        {
            throw new HostLinkException(HostLinkError.Empty, "empty frame");
        }
        var reader = new Reader(payload);
        HostMessage message;
        switch (payload.Span[0])
        {
            case Kind.Hello:
                message = new Hello(reader.U16(), reader.U32());
                break;
            case Kind.HelloAck:
                message = new HelloAck(reader.U16());
                break;
            case Kind.AuthRequest:
                message = new AuthRequest(reader.U32(), reader.Text(), reader.Bytes(), reader.Text(), reader.Text());
                break;
            case Kind.AuthReply:
                {
                    var corr = reader.U32();
                    AuthOutcome outcome;
                    switch (reader.U8())
                    {
                        case 0:
                            outcome = new Accepted(reader.U32(), reader.U64(), reader.Text());
                            break;
                        case 1:
                            outcome = new Rejected(reader.Text());
                            break;
                        case var status:
                            throw new HostLinkException(HostLinkError.BadStatus, $"unknown status {status}");
                    }
                    message = new AuthReply(corr, outcome);
                    break;
                }
            case Kind.SessionOpen:
                message = new SessionOpen(reader.U64(), reader.U64(), reader.U32(), reader.U64(), reader.Text(), reader.Text(), reader.Text());
                break;
            case Kind.SessionClose:
                message = new SessionClose(reader.U64());
                break;
            case Kind.Packet:
                message = new Packet(reader.U64(), reader.Rest());
                break;
            case Kind.Send:
                message = new Send(reader.U64(), reader.Rest());
                break;
            case Kind.Disconnect:
                message = new Disconnect(reader.U64(), reader.Text());
                break;
            case Kind.ClusterConfig:
                message = new ClusterConfig(reader.Rest());
                break;
            case var other:
                throw new HostLinkException(HostLinkError.UnknownKind, $"unknown message kind {other}");
        }
        reader.Finish();
        return message;
    }

    // ── encoding ────────────────────────────────────────────────────────────

    private static int TextSize(string text) => 2 + Strict.GetByteCount(text);

    private static int BodySize(HostMessage message) => message switch
    {
        Hello => 2 + 4,
        HelloAck => 2,
        AuthRequest m => 4 + TextSize(m.UserName) + 4 + m.PasswordHash.Length + TextSize(m.Language) + TextSize(m.Address),
        AuthReply { Outcome: Accepted a } => 4 + 1 + 4 + 8 + TextSize(a.CountryCode),
        AuthReply { Outcome: Rejected r } => 4 + 1 + TextSize(r.Reason),
        SessionOpen m => 8 + 8 + 4 + 8 + TextSize(m.Address) + TextSize(m.Language) + TextSize(m.CountryCode),
        SessionClose => 8,
        Packet m => 8 + m.Payload.Length,
        Send m => 8 + m.Payload.Length,
        Disconnect m => 8 + TextSize(m.Reason),
        ClusterConfig m => m.Payload.Length,
        _ => throw new ArgumentException($"cannot encode {message.GetType().Name}", nameof(message)),
    };

    private ref struct Writer(Span<byte> span)
    {
        private readonly Span<byte> _span = span;
        private int _at;

        public void U8(byte value) => _span[_at++] = value;

        public void U16(ushort value)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(_span[_at..], value);
            _at += 2;
        }

        public void U32(uint value)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(_span[_at..], value);
            _at += 4;
        }

        public void U64(ulong value)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(_span[_at..], value);
            _at += 8;
        }

        public void Text(string text)
        {
            var length = Strict.GetByteCount(text);
            if (length > ushort.MaxValue)
            {
                throw new ArgumentException("text is longer than a u16 length allows", nameof(text));
            }
            U16((ushort)length);
            _at += Strict.GetBytes(text, _span[_at..]);
        }

        public void Bytes(ReadOnlySpan<byte> bytes)
        {
            U32((uint)bytes.Length);
            Raw(bytes);
        }

        public void Raw(ReadOnlySpan<byte> bytes)
        {
            bytes.CopyTo(_span[_at..]);
            _at += bytes.Length;
        }
    }

    /// <summary>The whole frame, length prefix included.</summary>
    public static byte[] Encode(HostMessage message)
    {
        var body = BodySize(message);
        var frame = new byte[4 + 1 + body];
        BinaryPrimitives.WriteUInt32LittleEndian(frame, (uint)(1 + body));
        var w = new Writer(frame.AsSpan(4));
        switch (message)
        {
            case Hello m:
                w.U8(Kind.Hello);
                w.U16(m.Version);
                w.U32(m.NodeId);
                break;
            case HelloAck m:
                w.U8(Kind.HelloAck);
                w.U16(m.Version);
                break;
            case AuthRequest m:
                w.U8(Kind.AuthRequest);
                w.U32(m.Corr);
                w.Text(m.UserName);
                w.Bytes(m.PasswordHash.Span);
                w.Text(m.Language);
                w.Text(m.Address);
                break;
            case AuthReply { Outcome: Accepted a } m:
                w.U8(Kind.AuthReply);
                w.U32(m.Corr);
                w.U8(0);
                w.U32(a.UserId);
                w.U64(a.Role);
                w.Text(a.CountryCode);
                break;
            case AuthReply { Outcome: Rejected r } m:
                w.U8(Kind.AuthReply);
                w.U32(m.Corr);
                w.U8(1);
                w.Text(r.Reason);
                break;
            case SessionOpen m:
                w.U8(Kind.SessionOpen);
                w.U64(m.SessionId);
                w.U64(m.ClientId);
                w.U32(m.UserId);
                w.U64(m.Role);
                w.Text(m.Address);
                w.Text(m.Language);
                w.Text(m.CountryCode);
                break;
            case SessionClose m:
                w.U8(Kind.SessionClose);
                w.U64(m.SessionId);
                break;
            case Packet m:
                w.U8(Kind.Packet);
                w.U64(m.SessionId);
                w.Raw(m.Payload.Span);
                break;
            case Send m:
                w.U8(Kind.Send);
                w.U64(m.SessionId);
                w.Raw(m.Payload.Span);
                break;
            case Disconnect m:
                w.U8(Kind.Disconnect);
                w.U64(m.SessionId);
                w.Text(m.Reason);
                break;
            case ClusterConfig m:
                w.U8(Kind.ClusterConfig);
                w.Raw(m.Payload.Span);
                break;
        }
        return frame;
    }
}
