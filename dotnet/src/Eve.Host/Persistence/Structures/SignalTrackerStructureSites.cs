using System.Text.Json;
using System.Text.Json.Nodes;
using Eve.Sde;
using Eve.Services.Core;
using Eve.Structures;
using Npgsql;

namespace Eve.Host;

/// <summary>
/// The structures of a system for the signal tracker full state: the structure store's records of the system that are not destroyed, as the JSON rows
/// the service turns into the structures element. Deployed items are not listed: the EVE.js structure overlay reads the structures only.
/// </summary>
internal static class SignalTrackerStructureSites
{
    /// <summary>The SDE category of the Upwell structures (the category the EVE.js overlay falls back to).</summary>
    internal const int StructureCategoryId = 65;

    public static async Task<IReadOnlyList<JsonObject>> ReadAsync(
        NpgsqlDataSource source,
        StructureStore structures,
        SdeReader sde,
        long solarSystemId,
        CancellationToken cancellationToken)
    {
        if (solarSystemId is <= 0 or > int.MaxValue)
            return [];

        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        var records = await structures.ListInSystemAsync(connection, null, (int)solarSystemId, cancellationToken);
        if (records.Count == 0)
            return [];

        // The type and group of each structure come from the SDE: the group names the category, and the category falls back to 65 in the service.
        var typeIds = records.Select(record => (long)record.TypeId).Distinct().ToArray();
        var groupOfType = (await sde.ReadAsync("types", typeIds, cancellationToken))
            .ToDictionary(row => row.Key, row => SdeLong(row.Doc, "groupID"));
        var groupIds = groupOfType.Values.Where(groupId => groupId > 0).Distinct().ToArray();
        var categoryOfGroup = (await sde.ReadAsync("groups", groupIds, cancellationToken))
            .ToDictionary(row => row.Key, row => SdeLong(row.Doc, "categoryID"));

        var rows = new List<JsonObject>(records.Count);
        foreach (var record in records)
        {
            var groupId = groupOfType.GetValueOrDefault((long)record.TypeId);
            var categoryId = categoryOfGroup.GetValueOrDefault(groupId);
            rows.Add(new JsonObject
            {
                ["structureID"] = record.Snapshot.StructureId,
                ["typeID"] = (long)record.TypeId,
                ["groupID"] = groupId,
                ["categoryID"] = categoryId > 0 ? categoryId : StructureCategoryId,
                ["position"] = new JsonArray(JsonValue.Create(record.X), JsonValue.Create(record.Y), JsonValue.Create(record.Z)),
                ["targetID"] = SignalTrackerTargetId.Encode("structure", solarSystemId, record.Snapshot.StructureId),
            });
        }
        return rows;
    }

    private static long SdeLong(JsonElement doc, string name) =>
        doc.TryGetProperty(name, out var value) && value.TryGetInt64(out var number) ? number : 0;
}
