using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace Eve.Host.PublicGateway;

/// <summary>A decoded eve_public.Request: the correlation id, the request type (its full name in the Any type url) and the payload.</summary>
internal sealed record GatewayRequest(byte[] CorrelationUuid, string TypeUrl, byte[] Payload, long? CharacterId)
{
    /// <summary>The request's full message name (the part of the type url after the last slash).</summary>
    public string TypeName => TypeUrl[(TypeUrl.LastIndexOf('/') + 1)..];
}

/// <summary>The envelopes and the gRPC framing of the public gateway (EveJS publicGatewayLocal.js: createGrpcFrame and the response envelope).</summary>
internal static class GatewayEnvelope
{
    /// <summary>The type url prefix of the responses (the client's TYPE_URL, eveProto/__init__.py).</summary>
    public const string TypeUrlPrefix = "type.evetech.net/";
    public const int MaxFrameLength = 16 << 20;

    /// <summary>
    /// Decodes eve_public.Request: correlation_uuid 2, payload Any 3, authoritative_context 6 whose identity 10 names the character
    /// (ActiveIdentity.character 1, an Identifier whose sequential 1 is the id).
    /// </summary>
    public static GatewayRequest DecodeRequest(ReadOnlyMemory<byte> message)
    {
        byte[] correlation = [];
        string typeUrl = "";
        byte[] payload = [];
        long? character = null;
        foreach (var field in ProtoReader.Fields(message))
        {
            if (field.Number == 2 && field.WireType == 2)
                correlation = field.Bytes.ToArray();
            else if (field.Number == 3 && field.WireType == 2)
                (typeUrl, payload) = DecodeAny(field.Bytes);
            else if (field.Number == 6 && field.WireType == 2)
                character = IdentityCharacter(field.Bytes);
        }
        return new GatewayRequest(correlation, typeUrl, payload, character);
    }

    /// <summary>
    /// Encodes eve_public.Response: dispatched 1 (Timestamp), correlation_uuid 2, status_code 3, status_message 4, payload Any 5. The payload
    /// is left out when the answer has no message (an error or an empty answer).
    /// </summary>
    public static byte[] EncodeResponse(byte[] correlationUuid, uint statusCode, string statusMessage, string payloadTypeName, byte[] payload, DateTimeOffset dispatched)
    {
        var writer = new ProtoWriter();
        writer.Message(1, Timestamp(dispatched));
        writer.Bytes(2, correlationUuid);
        writer.Uint(3, statusCode);
        writer.String(4, statusMessage);
        if (payloadTypeName.Length > 0)
        {
            var any = new ProtoWriter();
            any.String(1, TypeUrlPrefix + payloadTypeName);
            any.Bytes(2, payload);
            writer.Message(5, any.ToArray());
        }
        writer.String(6, "eve.rs");
        return writer.ToArray();
    }

    /// <summary>
    /// Encodes eve_public.Notice: dispatched 1, uuid 2, internal_origin 3, payload Any 5 and target_group 6 (character 4 or user 3, the
    /// reference's TargetGroup fields). The character or the account routes it; a notice with neither is not sent by the host.
    /// </summary>
    public static byte[] EncodeNotice(string payloadTypeName, ReadOnlySpan<byte> payload, long? character, long? account, DateTimeOffset dispatched)
    {
        var writer = new ProtoWriter();
        writer.Message(1, Timestamp(dispatched));
        writer.Bytes(2, Guid.NewGuid().ToByteArray());
        writer.String(3, "eve.rs");
        var any = new ProtoWriter();
        any.String(1, TypeUrlPrefix + payloadTypeName);
        any.Bytes(2, payload.ToArray());
        writer.Message(5, any.ToArray());
        var target = new ProtoWriter();
        if (character is { } id)
            target.Uint(4, (ulong)id);
        else
            target.Uint(3, (ulong)(account ?? 0));
        writer.Message(6, target.ToArray());
        return writer.ToArray();
    }

    /// <summary>A google.protobuf.Timestamp: seconds 1 and the fraction of a second in nanoseconds 2 (as the reference writes it).</summary>
    private static byte[] Timestamp(DateTimeOffset at)
    {
        var timestamp = new ProtoWriter();
        var ticks = at.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks;
        timestamp.Uint(1, (ulong)(ticks / TimeSpan.TicksPerSecond));
        timestamp.Uint(2, (ulong)(ticks % TimeSpan.TicksPerSecond * 100));
        return timestamp.ToArray();
    }

    /// <summary>One gRPC message frame: the compression flag (0), the length as a big-endian uint32, then the message.</summary>
    public static byte[] Frame(ReadOnlySpan<byte> message)
    {
        var frame = new byte[5 + message.Length];
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(1, 4), (uint)message.Length);
        message.CopyTo(frame.AsSpan(5));
        return frame;
    }

    /// <summary>
    /// Takes the complete frames off the front of the buffer and leaves the rest in it for the next chunk. A compressed frame is refused:
    /// the gateway uses the identity encoding only (grpc-encoding identity).
    /// </summary>
    public static List<byte[]> TakeFrames(List<byte> buffer)
    {
        var frames = new List<byte[]>();
        var offset = 0;
        while (buffer.Count - offset >= 5)
        {
            var span = CollectionsMarshal.AsSpan(buffer);
            if (span[offset] != 0)
                throw new FormatException("a compressed gRPC frame is not supported");
            var declaredLength = BinaryPrimitives.ReadUInt32BigEndian(span.Slice(offset + 1, 4));
            if (declaredLength > MaxFrameLength)
                throw new FormatException($"gRPC frame length {declaredLength} exceeds {MaxFrameLength}");
            var length = (int)declaredLength;
            if (buffer.Count - offset - 5 < length)
                break;
            frames.Add(buffer.GetRange(offset + 5, length).ToArray());
            offset += 5 + length;
        }
        buffer.RemoveRange(0, offset);
        return frames;
    }

    private static (string TypeUrl, byte[] Value) DecodeAny(ReadOnlyMemory<byte> message)
    {
        var typeUrl = "";
        var value = Array.Empty<byte>();
        foreach (var field in ProtoReader.Fields(message))
        {
            if (field.Number == 1 && field.WireType == 2)
                typeUrl = System.Text.Encoding.UTF8.GetString(field.Bytes.Span);
            else if (field.Number == 2 && field.WireType == 2)
                value = field.Bytes.ToArray();
        }
        return (typeUrl, value);
    }

    /// <summary>The character of an AuthoritativeContext (identity 10 -> character 1 -> sequential 1), or null when it names none.</summary>
    private static long? IdentityCharacter(ReadOnlyMemory<byte> context)
    {
        var identity = SkillPlanWire.Message(context, 10);
        var character = identity is { } active ? SkillPlanWire.Message(active, 1) : null;
        if (character is not { } id)
            return null;
        var sequential = SkillPlanWire.VarintOf(id, 1);
        return sequential > 0 ? sequential : null;
    }
}
