using System.Globalization;
using System.Text.Json;

namespace Eve.Host;

internal sealed record StarbaseAssemblyModifier(double Time, double Material, double Cost);

internal sealed record StarbaseAssemblyActivityProfile(
    int AssemblyLineId,
    double Time,
    double Material,
    double Cost,
    IReadOnlyList<int> Categories,
    IReadOnlyList<int> Groups,
    IReadOnlyDictionary<int, StarbaseAssemblyModifier> GroupModifiers);

internal sealed record StarbaseAssemblyProfile(
    bool Capital,
    IReadOnlyDictionary<int, StarbaseAssemblyActivityProfile> Activities);

/// <summary>
/// POS installation profiles extracted from the V24.01 client's industry_assembly_lines and industry_installation_types resources.
/// The checked-in JSON records the client build and resource hashes used to generate this subset.
/// </summary>
internal static class StarbaseAssemblyProfiles
{
    private static readonly Lazy<IReadOnlyDictionary<int, StarbaseAssemblyProfile>> Profiles = new(Load);

    public static bool TryGet(int typeId, out StarbaseAssemblyProfile profile) =>
        Profiles.Value.TryGetValue(typeId, out profile!);

    private static IReadOnlyDictionary<int, StarbaseAssemblyProfile> Load()
    {
        using var stream = typeof(StarbaseAssemblyProfiles).Assembly.GetManifestResourceStream(
            "Eve.Host.Data.starbase-assembly-profiles.json")
            ?? throw new InvalidOperationException("The V24 POS assembly profiles resource is missing.");
        using var document = JsonDocument.Parse(stream);
        var profiles = document.RootElement.GetProperty("profiles");
        var result = new Dictionary<int, StarbaseAssemblyProfile>();
        foreach (var type in profiles.EnumerateObject())
        {
            if (!int.TryParse(type.Name, NumberStyles.None, CultureInfo.InvariantCulture, out var typeId))
                continue;
            var root = type.Value;
            var activities = new Dictionary<int, StarbaseAssemblyActivityProfile>();
            if (root.TryGetProperty("activities", out var activityRows))
            {
                foreach (var activity in activityRows.EnumerateObject())
                    if (int.TryParse(activity.Name, NumberStyles.None, CultureInfo.InvariantCulture, out var activityId))
                        activities[activityId] = ReadActivity(activity.Value);
            }
            else
            {
                activities[1] = ReadActivity(root);
            }
            result[typeId] = new(
                root.TryGetProperty("capital", out var capital) && capital.ValueKind == JsonValueKind.True,
                activities);
        }
        return result;
    }

    private static StarbaseAssemblyActivityProfile ReadActivity(JsonElement row)
    {
        var groupModifiers = new Dictionary<int, StarbaseAssemblyModifier>();
        if (row.TryGetProperty("groupModifiers", out var groups))
            foreach (var group in groups.EnumerateObject())
                if (int.TryParse(group.Name, NumberStyles.None, CultureInfo.InvariantCulture, out var groupId))
                    groupModifiers[groupId] = new(Number(group.Value, "time"), Number(group.Value, "material"), Number(group.Value, "cost"));

        return new(
            Integer(row, "assemblyLineID"),
            Number(row, "time"),
            Number(row, "material"),
            Number(row, "cost"),
            Integers(row, "categories"),
            Integers(row, "groups"),
            groupModifiers);
    }

    private static double Number(JsonElement row, string name, double fallback = 1) =>
        row.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : fallback;

    private static int Integer(JsonElement row, string name) =>
        row.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetInt32() : 0;

    private static IReadOnlyList<int> Integers(JsonElement row, string name) =>
        row.TryGetProperty(name, out var values) && values.ValueKind == JsonValueKind.Array
            ? values.EnumerateArray().Where(value => value.ValueKind == JsonValueKind.Number).Select(value => value.GetInt32()).ToArray()
            : [];
}
