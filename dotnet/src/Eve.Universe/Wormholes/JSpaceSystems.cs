using System.Reflection;
using System.Text.Json;

namespace Eve.Universe;

/// <summary>A static wormhole of a J-space system: its type code (the SDE type "Wormhole &lt;code&gt;") and type ID.</summary>
public sealed record JSpaceStatic(string Code, int TypeId);

/// <summary>
/// One J-space system from the committed data file (<c>Wormholes/Data/jspace-systems.json</c>, embedded): its class, its effect
/// beacon (the SDE type ID and the beacon's strength class; null when it has none), and its statics. The file's header names the sources.
/// </summary>
public sealed record JSpaceSystem(
    int Id,
    string Name,
    int WormholeClass,
    string? Effect,
    int? EffectClass,
    int? EffectTypeId,
    IReadOnlyList<JSpaceStatic> Statics,
    string StaticsSource);

public static class JSpaceSystems
{
    private const string ResourceName = "Eve.Universe.jspace-systems.json";

    /// <summary>The J-space systems of the data file, in system ID order.</summary>
    public static IReadOnlyList<JSpaceSystem> Load()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"embedded resource {ResourceName} is missing");
        using var document = JsonDocument.Parse(stream);
        var systems = new List<JSpaceSystem>();
        foreach (var row in document.RootElement.GetProperty("systems").EnumerateArray())
        {
            var statics = row.GetProperty("statics").EnumerateArray()
                .Select(s => new JSpaceStatic(s.GetProperty("code").GetString()!, s.GetProperty("typeID").GetInt32()))
                .ToArray();
            systems.Add(new JSpaceSystem(
                row.GetProperty("id").GetInt32(),
                row.GetProperty("name").GetString() ?? string.Empty,
                row.GetProperty("class").GetInt32(),
                row.TryGetProperty("effect", out var effect) && effect.ValueKind == JsonValueKind.String ? effect.GetString() : null,
                row.TryGetProperty("effectClass", out var effectClass) ? effectClass.GetInt32() : null,
                row.TryGetProperty("effectTypeID", out var effectType) ? effectType.GetInt32() : null,
                statics,
                row.GetProperty("staticsSource").GetString() ?? string.Empty));
        }
        return systems;
    }
}
