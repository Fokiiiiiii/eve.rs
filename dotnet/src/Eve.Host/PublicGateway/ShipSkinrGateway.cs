using System.Text;
using Eve.Host.Persistence;
using Eve.Skills;
using Npgsql;

namespace Eve.Host.PublicGateway;

/// <summary>The SKINR draft methods called by V24.01's shipSkinDesignSvc.</summary>
internal static class ShipSkinrGateway
{
    private const string DraftApi = "eve_public.cosmetic.ship.skin.thirdparty.draft.api.";
    private static readonly int[] AdvancedStudioCapacity = [0, 10, 20, 40, 65, 95];

    public static IReadOnlyDictionary<string, GatewayHandler> DraftHandlers(
        NpgsqlDataSource source,
        PgShipSkinrStore store,
        ShipSkinrCatalog catalog,
        PgSkillStore skills,
        IReadOnlyDictionary<string, int> skillTypeIds)
    {
        var handlers = new Dictionary<string, GatewayHandler>(StringComparer.Ordinal)
        {
            [DraftApi + "GetSaveCapacityRequest"] = async (request, cancellationToken) =>
            {
                var character = Character(request);
                if (character <= 0) return Error(400, "INVALID_CHARACTER", "GetSaveCapacityResponse");
                var capacity = await SaveCapacityAsync(source, skills, skillTypeIds, character, cancellationToken);
                var payload = new ProtoWriter();
                payload.Uint(1, (ulong)capacity);
                return Success("GetSaveCapacityResponse", payload.ToArray());
            },
            [DraftApi + "GetAllSavedRequest"] = async (request, cancellationToken) =>
            {
                var character = Character(request);
                if (character <= 0) return Error(400, "INVALID_CHARACTER", "GetAllSavedResponse");
                var payload = new ProtoWriter();
                foreach (var draft in await store.SavedDesignsAsync(character, cancellationToken))
                    payload.Message(1, EncodeSavedDraft(draft));
                return Success("GetAllSavedResponse", payload.ToArray());
            },
            [DraftApi + "GetRequest"] = async (request, cancellationToken) =>
            {
                var character = Character(request);
                var id = DraftId(request.Payload, 1);
                if (character <= 0 || id == Guid.Empty) return Error(400, "INVALID_DRAFT", "GetResponse");
                var draft = await store.SavedDesignAsync(character, id, cancellationToken);
                return draft is null
                    ? Error(404, "DRAFT_NOT_FOUND", "GetResponse")
                    : Success("GetResponse", EncodeAttributesResponse(draft, 1));
            },
            [DraftApi + "GetSharedRequest"] = async (request, cancellationToken) =>
            {
                var character = Character(request);
                var sharedBy = (int)Identifier(request.Payload, 1);
                var id = DraftId(request.Payload, 2);
                if (character <= 0 || sharedBy <= 0 || id == Guid.Empty)
                    return Error(400, "INVALID_DRAFT", "GetSharedResponse");
                var draft = await store.SavedDesignAsync(sharedBy, id, cancellationToken);
                return draft is null
                    ? Error(404, "DRAFT_NOT_FOUND", "GetSharedResponse")
                    : Success("GetSharedResponse", EncodeAttributesResponse(draft, 1));
            },
            [DraftApi + "SaveRequest"] = async (request, cancellationToken) =>
            {
                var character = Character(request);
                var design = ParseDesign(request.Payload, 1, 2, 3, 4);
                if (character <= 0 || design is null || !ValidName(design.Name, design.LineName) ||
                    !catalog.TryValidateLayout(design.Layout, out var shipTypeId, out var tier, out _) ||
                    !ValidEditorConfigurations(design.EditorConfigurations, design.Layout))
                    return Error(400, "INVALID_DESIGN", "SaveResponse");
                var capacity = await SaveCapacityAsync(source, skills, skillTypeIds, character, cancellationToken);
                var id = request.CorrelationUuid.Length == 16
                    ? new Guid(request.CorrelationUuid, bigEndian: true)
                    : Guid.Empty;
                if (id == Guid.Empty)
                    return Error(400, "INVALID_REQUEST_ID", "SaveResponse");
                var hex = ShipSkinrDesignId.Create(character, design.Name, design.LineName, tier, design.Layout.Span);
                var saved = await store.SaveDesignAsync(character, id, design.Name, design.LineName, shipTypeId,
                    design.Layout.ToArray(), EncodeEditorConfigurations(design.EditorConfigurations), hex, capacity,
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), cancellationToken);
                if (saved is not null) return Success("SaveResponse", EncodeSavedDraft(saved));
                return await store.SavedDesignAsync(character, id, cancellationToken) is not null
                    ? Error(409, "REQUEST_CONFLICT", "SaveResponse")
                    : Error(403, "MAX_SAVED_DESIGNS_LIMIT_REACHED", "SaveResponse");
            },
            [DraftApi + "UpdateRequest"] = async (request, cancellationToken) =>
            {
                var character = Character(request);
                var id = DraftId(request.Payload, 1);
                var design = ParseDesign(request.Payload, 2, 3, 4, 5);
                if (character <= 0 || id == Guid.Empty || design is null || !ValidName(design.Name, design.LineName) ||
                    !catalog.TryValidateLayout(design.Layout, out var shipTypeId, out var tier, out _) ||
                    !ValidEditorConfigurations(design.EditorConfigurations, design.Layout))
                    return Error(400, "INVALID_DESIGN", "UpdateResponse");
                var hex = ShipSkinrDesignId.Create(character, design.Name, design.LineName, tier, design.Layout.Span);
                var updated = await store.UpdateDesignAsync(character, id, design.Name, design.LineName, shipTypeId,
                    design.Layout.ToArray(), EncodeEditorConfigurations(design.EditorConfigurations), hex,
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), cancellationToken);
                return updated is null
                    ? Error(404, "DRAFT_NOT_FOUND", "UpdateResponse")
                    : Success("UpdateResponse", EncodeSavedDraft(updated));
            },
            [DraftApi + "DeleteRequest"] = async (request, cancellationToken) =>
            {
                var character = Character(request);
                var id = DraftId(request.Payload, 1);
                if (character <= 0 || id == Guid.Empty) return Error(400, "INVALID_DRAFT", "DeleteResponse");
                return await store.DeleteDesignAsync(character, id, cancellationToken)
                    ? Success("DeleteResponse", [])
                    : Error(404, "DRAFT_NOT_FOUND", "DeleteResponse");
            },
        };
        return handlers;
    }

    internal static async Task<int> SaveCapacityAsync(
        NpgsqlDataSource source,
        PgSkillStore skills,
        IReadOnlyDictionary<string, int> skillTypeIds,
        long characterId,
        CancellationToken cancellationToken)
    {
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var studio = await SkillLevelAsync(skills, connection, transaction, skillTypeIds, "Studio Management", characterId, cancellationToken);
        var advanced = await SkillLevelAsync(skills, connection, transaction, skillTypeIds, "Advanced Studio Management", characterId, cancellationToken);
        return Math.Min(125, 5 + 5 * studio + AdvancedStudioCapacity[Math.Clamp(advanced, 0, 5)]);
    }

    private static Task<int> SkillLevelAsync(
        PgSkillStore skills,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        IReadOnlyDictionary<string, int> skillTypeIds,
        string name,
        long characterId,
        CancellationToken cancellationToken) =>
        skillTypeIds.TryGetValue(name, out var typeId)
            ? skills.LevelAsync(connection, transaction, characterId, typeId, cancellationToken)
            : Task.FromResult(0);

    private sealed record Design(string Name, string LineName, ReadOnlyMemory<byte> Layout, IReadOnlyList<byte[]> EditorConfigurations);

    private static Design? ParseDesign(byte[] payload, int nameField, int lineNameField, int layoutField, int configurationsField)
    {
        var name = "";
        var lineName = "";
        ReadOnlyMemory<byte> layout = default;
        var configurations = new List<byte[]>();
        try
        {
            foreach (var field in ProtoReader.Fields(payload))
            {
                if (field.Number == nameField && field.WireType == 2) name = Encoding.UTF8.GetString(field.Bytes.Span);
                else if (field.Number == lineNameField && field.WireType == 2) lineName = Encoding.UTF8.GetString(field.Bytes.Span);
                else if (field.Number == layoutField && field.WireType == 2) layout = field.Bytes;
                else if (field.Number == configurationsField && field.WireType == 2) configurations.Add(field.Bytes.ToArray());
            }
            return layout.IsEmpty ? null : new Design(name, lineName, layout, configurations);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static bool ValidName(string name, string lineName) =>
        name.EnumerateRunes().Count() is > 0 and <= 20 &&
        lineName.EnumerateRunes().Count() <= 50 &&
        !(name + lineName).Any(char.IsControl);

    private static bool ValidEditorConfigurations(IReadOnlyList<byte[]> configurations, ReadOnlyMemory<byte> layout)
    {
        var slotIds = new HashSet<int>();
        foreach (var field in ProtoReader.Fields(layout))
        {
            if (field.Number != 1 || field.WireType != 2) continue;
            foreach (var slot in ProtoReader.Fields(field.Bytes))
            {
                if (slot.Number != 1 || slot.WireType != 2) continue;
                var id = Identifier(slot.Bytes, 1);
                if (id > 0 && id <= int.MaxValue) slotIds.Add((int)id);
            }
        }
        var seen = new HashSet<int>();
        foreach (var configuration in configurations)
        {
            var slotId = 0;
            ReadOnlyMemory<byte>? config = null;
            foreach (var field in ProtoReader.Fields(configuration))
            {
                if (field.Number == 1 && field.WireType == 2) slotId = (int)Identifier(field.Bytes, 1);
                if (field.Number == 2 && field.WireType == 2) config = field.Bytes;
            }
            if (slotId <= 0 || !slotIds.Contains(slotId) || !seen.Add(slotId) || config is not { } value) return false;
            foreach (var field in ProtoReader.Fields(value))
            {
                if (field.Number is < 1 or > 6 || field.WireType != 5) return false;
                var number = BitConverter.Int32BitsToSingle(unchecked((int)field.Scalar));
                if (!float.IsFinite(number) || Math.Abs(number) > 1_000_000) return false;
            }
        }
        return true;
    }

    private static byte[] EncodeEditorConfigurations(IEnumerable<byte[]> configurations)
    {
        var writer = new ProtoWriter();
        foreach (var configuration in configurations) writer.Message(1, configuration);
        return writer.ToArray();
    }

    internal static byte[] EncodeSavedDraft(ShipSkinrDraft draft)
    {
        var payload = new ProtoWriter();
        payload.Message(1, EncodeDraftIdentifier(draft.Id));
        payload.Message(2, EncodeAttributes(draft));
        return payload.ToArray();
    }

    internal static byte[] EncodeAttributesResponse(ShipSkinrDraft draft, int field)
    {
        var payload = new ProtoWriter();
        payload.Message(field, EncodeAttributes(draft));
        return payload.ToArray();
    }

    private static byte[] EncodeAttributes(ShipSkinrDraft draft)
    {
        var attributes = new ProtoWriter();
        var skin = new ProtoWriter();
        skin.String(1, draft.SkinHex);
        attributes.Message(1, skin.ToArray());
        foreach (var field in ProtoReader.Fields(draft.EditorConfigurations))
            if (field.Number == 1 && field.WireType == 2)
                attributes.Message(3, field.Bytes.ToArray());
        return attributes.ToArray();
    }

    private static byte[] EncodeDraftIdentifier(Guid id)
    {
        var identifier = new ProtoWriter();
        identifier.Bytes(1, id.ToByteArray(bigEndian: true));
        return identifier.ToArray();
    }

    private static Guid DraftId(byte[] payload, int field)
    {
        foreach (var fieldValue in ProtoReader.Fields(payload))
        {
            if (fieldValue.Number != field || fieldValue.WireType != 2) continue;
            foreach (var uuid in ProtoReader.Fields(fieldValue.Bytes))
                if (uuid.Number == 1 && uuid.WireType == 2 && uuid.Bytes.Length == 16)
                    return new Guid(uuid.Bytes.Span, bigEndian: true);
        }
        return Guid.Empty;
    }

    private static long Identifier(byte[] payload, int field)
    {
        foreach (var value in ProtoReader.Fields(payload))
            if (value.Number == field && value.WireType == 2)
                return Identifier(value.Bytes, 1);
        return 0;
    }

    private static long Identifier(ReadOnlyMemory<byte> payload, int field)
    {
        foreach (var value in ProtoReader.Fields(payload))
            if (value.Number == field && value.WireType == 0 && value.Scalar <= long.MaxValue)
                return (long)value.Scalar;
        return 0;
    }

    private static long Character(GatewayRequest request) => request.CharacterId is > 0 and <= long.MaxValue ? request.CharacterId.Value : 0;

    private static GatewayAnswer Success(string response, byte[] payload) =>
        new(200, "OK", DraftApi + response, payload);

    private static GatewayAnswer Error(uint status, string message, string response) =>
        new(status, message, DraftApi + response, []);
}
