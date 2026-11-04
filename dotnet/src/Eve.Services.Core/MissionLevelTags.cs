using System.Text.Json;

namespace Eve.Services.Core;


public sealed record MissionLevelTag(long MissionId, int Level, string Provenance, string Confidence);

/// <summary>


/// A mission is never given a default level.
/// </summary>
public sealed record MissionLevelAnswer(long MissionId, int? Level, string Provenance, string? Confidence, string? Reason);

/// <summary>
/// The level tags of basic missions, keyed by the official mission ID. The committed file

/// row and nothing else: <c>missionID</c>, <c>level</c>, <c>provenance</c> and <c>confidence</c>. Client-derived and


/// </summary>
public sealed class MissionLevelTags
{
    /// <summary>The schema every tag file carries.</summary>
    public const string Schema = "eve.rs.mission-level-tags.v1";


    public const string OptionalFileVariable = "EVE_MISSION_LEVEL_TAGS";

    /// <summary>The fields a tag row may carry. Any other field is refused; the committed file is held to this set by a test.</summary>
    public static readonly IReadOnlyList<string> AllowedRowFields = ["missionID", "level", "provenance", "confidence"];

    /// <summary>The top-level fields a tag file may carry.</summary>
    public static readonly IReadOnlyList<string> AllowedTopFields = ["schema", "source", "provenanceClasses", "counts", "tags"];

    private const string CommittedResource = "mission-level-tags.v1.json";
    private static readonly string[] AllowedProvenance = ["client", "tq-log", "wiki", "rule", "owner"];
    private static readonly string[] AllowedConfidence = ["high", "medium", "low"];
    private static readonly Lazy<MissionLevelTags> CommittedTags = new(ReadCommitted);

    private readonly Dictionary<long, MissionLevelTag> _tags;

    private MissionLevelTags(IEnumerable<MissionLevelTag> tags)
    {
        _tags = new Dictionary<long, MissionLevelTag>();
        foreach (var tag in tags)
        {
            if (_tags.ContainsKey(tag.MissionId))
                throw new InvalidDataException($"mission {tag.MissionId} has two level tags");
            _tags[tag.MissionId] = tag;
        }
    }

    /// <summary>The tags committed in the server.</summary>
    public static MissionLevelTags Committed => CommittedTags.Value;

    public int Count => _tags.Count;

    public IReadOnlyCollection<MissionLevelTag> All => _tags.Values;


    public static MissionLevelTags LoadDefault()
    {
        var path = Environment.GetEnvironmentVariable(OptionalFileVariable);
        if (string.IsNullOrWhiteSpace(path))
            return Committed;
        return Committed.With(FromJson(File.ReadAllText(path), path));
    }

    /// <summary>The tags of <paramref name="json"/> (a tag file of <see cref="Schema"/>); <paramref name="origin"/> names it in errors.</summary>
    public static MissionLevelTags FromJson(string json, string origin)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        foreach (var property in root.EnumerateObject())
        {
            if (!AllowedTopFields.Contains(property.Name))
                throw new InvalidDataException($"{origin}: top-level field {property.Name} is not allowed in a level tag file");
        }
        if (root.GetProperty("schema").GetString() != Schema)
            throw new InvalidDataException($"{origin}: not a {Schema} file");
        var tags = new List<MissionLevelTag>();
        foreach (var row in root.GetProperty("tags").EnumerateArray())
        {
            foreach (var property in row.EnumerateObject())
            {
                if (!AllowedRowFields.Contains(property.Name))
                    throw new InvalidDataException($"{origin}: field {property.Name} is not allowed in a level tag row");
            }
            foreach (var required in AllowedRowFields)
            {
                if (!row.TryGetProperty(required, out _))
                    throw new InvalidDataException($"{origin}: a level tag row has no {required}");
            }
            if (!row.GetProperty("missionID").TryGetInt64(out var missionId) ||
                !row.GetProperty("level").TryGetInt32(out var level))
                throw new InvalidDataException($"{origin}: a level tag row has a missionID or level that is not an integer");
            var provenance = row.GetProperty("provenance").GetString() ?? "";
            var confidence = row.GetProperty("confidence").GetString() ?? "";
            if (missionId <= 0 || level is < 1 or > 5)
                throw new InvalidDataException($"{origin}: mission {missionId} has level {level}; levels are 1 to 5");
            if (!AllowedProvenance.Contains(provenance))
                throw new InvalidDataException($"{origin}: mission {missionId} has provenance {provenance}; allowed: {string.Join(", ", AllowedProvenance)}");
            if (!AllowedConfidence.Contains(confidence))
                throw new InvalidDataException($"{origin}: mission {missionId} has confidence {confidence}; allowed: high, medium, low");
            tags.Add(new MissionLevelTag(missionId, level, provenance, confidence));
        }
        return new MissionLevelTags(tags);
    }

    /// <summary>This set plus <paramref name="other"/>. A mission tagged in both is refused.</summary>
    public MissionLevelTags With(MissionLevelTags other) => new([.. All, .. other.All]);

    /// <summary>The level answer for a mission: its tag with the provenance, or <c>unknown</c> with the reason.</summary>
    public MissionLevelAnswer Answer(long missionId) =>
        _tags.TryGetValue(missionId, out var tag)
            ? new MissionLevelAnswer(missionId, tag.Level, tag.Provenance, tag.Confidence, null)
            : new MissionLevelAnswer(missionId, null, "unknown", null, MissionDataGaps.LevelUnknown(missionId));

    /// <summary>The committed file's raw JSON, for the field test.</summary>
    public static string CommittedJson()
    {
        using var stream = typeof(MissionLevelTags).Assembly.GetManifestResourceStream(CommittedResource)
            ?? throw new InvalidOperationException($"the committed level tags {CommittedResource} are not embedded");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static MissionLevelTags ReadCommitted() => FromJson(CommittedJson(), CommittedResource);
}
