using System.IO.Compression;
using System.Text.Json;

namespace Eve.Services.Core;

/// <summary>
/// The localization IDs of a mission's texts (briefing, journal), from the client's own static export (<c>native/missions.jsonl.gz</c>, in the
/// directory named by <c>EVE_CLIENT_SDE_DIR</c>). The client shows the text itself from these IDs, so the server sends IDs and no text.
/// Nothing from that directory is committed; it is read only by the loader.
/// </summary>
public static class ClientMissionMessages
{
    public const string DirectoryVariable = "EVE_CLIENT_SDE_DIR";
    public const string BriefingKey = "messages.mission.briefing";

    /// <summary>The message IDs by mission ID (message key to localization ID). A row without a <c>messages</c> object is skipped and counted.</summary>
    public static (IReadOnlyDictionary<long, IReadOnlyDictionary<string, long>> Messages, int Skipped) Parse(Stream gzipped)
    {
        using var gzip = new GZipStream(gzipped, CompressionMode.Decompress);
        using var reader = new StreamReader(gzip);
        var messages = new Dictionary<long, IReadOnlyDictionary<string, long>>();
        var skipped = 0;
        while (reader.ReadLine() is { } line)
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (!root.TryGetProperty("_key", out var key) || !key.TryGetInt64(out var missionId)) continue;
            if (!root.TryGetProperty("messages", out var raw) || raw.ValueKind != JsonValueKind.Object)
            {
                skipped++;
                continue;
            }
            var ids = new Dictionary<string, long>();
            foreach (var property in raw.EnumerateObject())
            {
                if (property.Value.TryGetInt64(out var id)) ids[property.Name] = id;
            }
            messages[missionId] = ids;
        }
        return (messages, skipped);
    }
}
