using System.Buffers.Binary;
using System.Globalization;

namespace Eve.Host.PublicGateway;

/// <summary>Read-only client APIs whose authoritative data is empty in the Host's current feature set.</summary>
internal static class ClientReadGateway
{
    private const string CampaignGetAllRequest = "eve_public.campaign.api.GetAllRequest";
    private const string CampaignGetAllResponse = "eve_public.campaign.api.GetAllResponse";
    private const string DailyGoalGetAllWithRewardsRequest = "eve_public.dailygoal.api.GetAllWithRewardsRequest";
    private const string DailyGoalGetAllWithRewardsResponse = "eve_public.dailygoal.api.GetAllWithRewardsResponse";
    private const string StructurePaintworkGetAllRequest = "eve_public.cosmetic.structure.paintwork.api.GetAllInSolarSystemRequest";
    private const string StructurePaintworkGetAllResponse = "eve_public.cosmetic.structure.paintwork.api.GetAllInSolarSystemResponse";
    private const string ShipEntitlementGetAllRequest = "eve_public.entitlement.character.GetAllRequest";
    private const string ShipEntitlementGetAllResponse = "eve_public.entitlement.character.GetAllResponse";

    public static IReadOnlyDictionary<string, GatewayHandler> Handlers(long clientBuild)
    {
        var handlers = new Dictionary<string, GatewayHandler>(StringComparer.Ordinal)
        {
            [CampaignGetAllRequest] = (_, _) => Empty(CampaignGetAllResponse),
            [DailyGoalGetAllWithRewardsRequest] = CharacterEmpty(DailyGoalGetAllWithRewardsResponse),
            [StructurePaintworkGetAllRequest] = CharacterEmpty(StructurePaintworkGetAllResponse),
            [ShipEntitlementGetAllRequest] = CharacterEmpty(ShipEntitlementGetAllResponse),
        };

        foreach (var (name, handler) in MapTagsGateway.Handlers(clientBuild))
            handlers[name] = handler;
        return handlers;
    }

    private static GatewayHandler CharacterEmpty(string responseType) => (request, _) => request.CharacterId is null
        ? Task.FromResult(new GatewayAnswer(400, "no active character", responseType, []))
        : Empty(responseType);

    private static Task<GatewayAnswer> Empty(string responseType) =>
        Task.FromResult(new GatewayAnswer(200, "", responseType, []));
}

/// <summary>
/// EveJS v0.12.9's mapTagsAuthority is explicitly bootstrapped empty because no public SDE map-tag source exists. The
/// client still needs its semantic-version checkpoint and a real, CRC-checked FlatBuffer containing three empty vectors.
/// </summary>
internal static class MapTagsGateway
{
    public const string AssetPath = "/elysian-eve/map-tags/latest.maptags";

    private static readonly byte[] EmptyAsset = EmptyUniverseBuilder.Build();
    private static readonly uint EmptyAssetCrc = Crc32(EmptyAsset);

    public static byte[] Asset => EmptyAsset;

    public static IReadOnlyDictionary<string, GatewayHandler> Handlers(long clientBuild)
    {
        GatewayHandler handler = (request, _) =>
        {
            var responseType = "eve_public.space.api.cdn.GetUpdateResponse";
            if (string.IsNullOrWhiteSpace(request.PublicHost) || Uri.CheckHostName(request.PublicHost) == UriHostNameType.Unknown)
                return Task.FromResult(new GatewayAnswer(400, "request host is unavailable", responseType, []));

            var url = new UriBuilder(Uri.UriSchemeHttps, request.PublicHost) { Path = AssetPath }.Uri.AbsoluteUri;
            var version = new ProtoWriter();
            version.Uint(1, 1);
            version.String(5, clientBuild.ToString(CultureInfo.InvariantCulture));
            var checksum = new ProtoWriter();
            checksum.Uint(1, EmptyAssetCrc);
            var checkpoint = new ProtoWriter();
            checkpoint.String(1, url);
            checkpoint.Message(2, version.ToArray());
            checkpoint.Message(3, checksum.ToArray());
            var payload = new ProtoWriter();
            payload.Message(1, checkpoint.ToArray());
            return Task.FromResult(new GatewayAnswer(200, "", responseType, payload.ToArray()));
        };

        return new Dictionary<string, GatewayHandler>(StringComparer.Ordinal)
        {
            ["eve_public.space.api.cdn.GetUpdateRequest"] = handler,
            ["eve_public.space.api.cdn.requests_pb2.GetUpdateRequest"] = handler,
        };
    }

    private static uint Crc32(ReadOnlySpan<byte> data)
    {
        var crc = uint.MaxValue;
        foreach (var value in data)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xedb88320u : crc >> 1;
        }
        return ~crc;
    }

    /// <summary>Builds the empty eve_public.map.Universe root with its regions, constellations and systems vectors.</summary>
    private sealed class EmptyUniverseBuilder
    {
        private readonly List<byte> _bytes = [];
        private int _minimumAlignment = 1;
        private int _vectorLength;
        private int[] _fields = [];
        private int _objectStart;

        private int Offset => _bytes.Count;

        public static byte[] Build()
        {
            var builder = new EmptyUniverseBuilder();
            var systems = builder.EmptyVector();
            var constellations = builder.EmptyVector();
            var regions = builder.EmptyVector();
            builder.StartObject(3);
            builder.OffsetField(2, systems);
            builder.OffsetField(1, constellations);
            builder.OffsetField(0, regions);
            return builder.Finish(builder.EndObject());
        }

        private int EmptyVector()
        {
            Prep(4, 0);
            Prep(4, 0);
            _vectorLength = 0;
            PrependUInt32((uint)_vectorLength);
            return Offset;
        }

        private void StartObject(int fields)
        {
            _fields = new int[fields];
            _objectStart = Offset;
        }

        private void OffsetField(int field, int target)
        {
            PrependOffset(target);
            _fields[field] = Offset;
        }

        private int EndObject()
        {
            PrependInt32(0);
            var objectOffset = Offset;
            for (var field = _fields.Length - 1; field >= 0; field--)
                PrependUInt16(_fields[field] == 0 ? (ushort)0 : checked((ushort)(objectOffset - _fields[field])));
            PrependUInt16(checked((ushort)(objectOffset - _objectStart)));
            PrependUInt16(checked((ushort)((_fields.Length + 2) * 2)));
            var vtableOffset = Offset;
            var tableIndex = _bytes.Count - objectOffset;
            var displacement = new byte[sizeof(int)];
            BinaryPrimitives.WriteInt32LittleEndian(displacement, vtableOffset - objectOffset);
            for (var index = 0; index < displacement.Length; index++)
                _bytes[tableIndex + index] = displacement[index];
            return objectOffset;
        }

        private byte[] Finish(int root)
        {
            Prep(_minimumAlignment, sizeof(uint));
            PrependOffset(root);
            return [.. _bytes];
        }

        private void PrependOffset(int target)
        {
            Prep(sizeof(uint), 0);
            PrependUInt32(checked((uint)(Offset - target + sizeof(uint))));
        }

        private void PrependUInt32(uint value)
        {
            Prep(sizeof(uint), 0);
            var bytes = new byte[sizeof(uint)];
            BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
            _bytes.InsertRange(0, bytes);
        }

        private void PrependInt32(int value)
        {
            Prep(sizeof(int), 0);
            var bytes = new byte[sizeof(int)];
            BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
            _bytes.InsertRange(0, bytes);
        }

        private void PrependUInt16(ushort value)
        {
            var bytes = new byte[sizeof(ushort)];
            BinaryPrimitives.WriteUInt16LittleEndian(bytes, value);
            _bytes.InsertRange(0, bytes);
        }

        private void Prep(int size, int additional)
        {
            _minimumAlignment = Math.Max(_minimumAlignment, size);
            var padding = (-(Offset + additional)) & (size - 1);
            if (padding > 0)
                _bytes.InsertRange(0, new byte[padding]);
        }
    }
}
