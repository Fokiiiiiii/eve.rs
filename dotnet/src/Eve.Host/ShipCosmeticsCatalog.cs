using System.Globalization;
using System.Text.Json;
using Eve.Sde;

namespace Eve.Host;

public sealed record SkinDefinition(int SkinId, int SkinMaterialId, IReadOnlySet<int> ShipTypeIds);
public sealed record SkinLicenseDefinition(int LicenseTypeId, int SkinId, int SkinMaterialId, int DurationDays, bool IsSingleUse);

public sealed class ShipCosmeticsCatalog(
    IReadOnlyDictionary<int, SkinDefinition> skins,
    IReadOnlyDictionary<int, SkinLicenseDefinition> licenseTypes)
{
    public IReadOnlyDictionary<int, SkinDefinition> Skins { get; } = skins;
    public IReadOnlyDictionary<int, SkinLicenseDefinition> LicenseTypes { get; } = licenseTypes;

    public static async Task<ShipCosmeticsCatalog> LoadAsync(SdeReader sde, CancellationToken cancellationToken = default)
    {
        var materialTypes = new Dictionary<int, HashSet<int>>();
        foreach (var row in await sde.ReadAsync("skinMaterials", cancellationToken: cancellationToken))
        {
            var materialId = Int(row.Doc, checked((int)row.Key), "skinMaterialID", "materialID");
            var types = Ints(row.Doc, "shipTypeIDs", "typeIDs", "types");
            var singleType = Int(row.Doc, 0, "shipTypeID", "typeID");
            if (singleType > 0) types.Add(singleType);
            if (materialId > 0 && types.Count > 0) materialTypes[materialId] = types;
        }

        var skins = new Dictionary<int, SkinDefinition>();
        foreach (var row in await sde.ReadAsync("skins", cancellationToken: cancellationToken))
        {
            var skinId = Int(row.Doc, checked((int)row.Key), "skinID");
            var materialId = Int(row.Doc, 0, "skinMaterialID", "materialID");
            var types = Ints(row.Doc, "shipTypeIDs", "typeIDs", "types");
            var singleType = Int(row.Doc, 0, "shipTypeID", "typeID");
            if (singleType > 0) types.Add(singleType);
            if (types.Count == 0 && materialTypes.TryGetValue(materialId, out var materialTypeIds)) types.UnionWith(materialTypeIds);
            if (skinId > 0) skins[skinId] = new SkinDefinition(skinId, materialId, types);
        }

        var licenses = new Dictionary<int, SkinLicenseDefinition>();
        foreach (var row in await sde.ReadAsync("skinLicenses", cancellationToken: cancellationToken))
        {
            var licenseTypeId = Int(row.Doc, checked((int)row.Key), "licenseTypeID", "typeID");
            var skinId = Int(row.Doc, 0, "skinID");
            if (licenseTypeId <= 0 || skinId <= 0) continue;
            licenses[licenseTypeId] = new SkinLicenseDefinition(
                licenseTypeId, skinId,
                Int(row.Doc, skins.GetValueOrDefault(skinId)?.SkinMaterialId ?? 0, "skinMaterialID", "materialID"),
                Math.Max(0, Int(row.Doc, 0, "duration", "durationDays")),
                Bool(row.Doc, false, "isSingleUse", "singleUse"));
        }
        return new ShipCosmeticsCatalog(skins, licenses);
    }

    private static int Int(JsonElement doc, int fallback, params string[] names)
    {
        foreach (var property in doc.EnumerateObject())
        {
            if (!names.Contains(property.Name, StringComparer.OrdinalIgnoreCase)) continue;
            if (property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetInt32(out var value)) return value;
            if (property.Value.ValueKind == JsonValueKind.String && int.TryParse(property.Value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value)) return value;
        }
        return fallback;
    }

    private static HashSet<int> Ints(JsonElement doc, params string[] names)
    {
        foreach (var property in doc.EnumerateObject())
        {
            if (!names.Contains(property.Name, StringComparer.OrdinalIgnoreCase) || property.Value.ValueKind != JsonValueKind.Array) continue;
            return [.. property.Value.EnumerateArray().Select(value =>
                value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number :
                value.ValueKind == JsonValueKind.Object ? Int(value, 0, "typeID", "shipTypeID") : 0).Where(id => id > 0)];
        }
        return [];
    }

    private static bool Bool(JsonElement doc, bool fallback, params string[] names)
    {
        foreach (var property in doc.EnumerateObject())
        {
            if (!names.Contains(property.Name, StringComparer.OrdinalIgnoreCase)) continue;
            return property.Value.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Number => property.Value.TryGetInt32(out var number) && number != 0,
                _ => fallback,
            };
        }
        return fallback;
    }
}
