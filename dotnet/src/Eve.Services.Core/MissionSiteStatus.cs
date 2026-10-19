using System.Text.Json;

namespace Eve.Services.Core;

/// <summary>

/// coverage table). <see cref="Generic"/>: the site's population is a family rule, not client data; <see cref="Missing"/>: the

/// sites are generic or missing are held back from offers (see <see cref="MissionStaticCatalog.IsHeldBack"/>).
/// </summary>
public static class MissionSiteStatus
{
    public const string Generic = "generic";
    public const string Missing = "missing";
    public const string Specific = "specific";

    private const string CommittedResource = "mission-site-status.v1.json";

    private static readonly Lazy<IReadOnlyDictionary<long, string>> CommittedStatus = new(ReadCommitted);

    /// <summary>The committed statuses, keyed by mission ID.</summary>
    public static IReadOnlyDictionary<long, string> Committed => CommittedStatus.Value;

    /// <summary>Reads the statuses from the file's JSON (schema <c>eve.rs.mission-site-status.v1</c>).</summary>
    public static IReadOnlyDictionary<long, string> FromJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        var result = new Dictionary<long, string>();
        foreach (var step in document.RootElement.GetProperty("steps").EnumerateArray())
        {
            var status = step.GetProperty("siteStatus").GetString()
                ?? throw new InvalidDataException("a site status is not a string");
            if (status is not (Generic or Missing or Specific))
                throw new InvalidDataException($"unknown site status '{status}'");
            result[step.GetProperty("missionID").GetInt64()] = status;
        }
        return result;
    }

    private static IReadOnlyDictionary<long, string> ReadCommitted()
    {
        using var stream = typeof(MissionSiteStatus).Assembly.GetManifestResourceStream(CommittedResource)
            ?? throw new InvalidOperationException($"the committed site statuses {CommittedResource} are not embedded");
        using var reader = new StreamReader(stream);
        return FromJson(reader.ReadToEnd());
    }
}
