using System.Text.Json;

namespace Eve.Services.Core;

/// <summary>
/// Whether an official epic arc exists in the client version this server targets (build 3396210):
/// <c>in-client-build</c>, <c>newer-than-client</c>, or <c>unknown</c> when there is no evidence either way.
/// </summary>
public sealed record EpicArcAvailabilityRow(long ArcId, string Availability, string Evidence);

/// <summary>
/// The eve.rs-owned availability table of the epic arcs (<c>Data/epic-arc-availability.v1.json</c>, an embedded resource).
/// Rows carry the arc ID, the availability and the evidence in eve.rs's own words; no CCP string is in the file.
/// An arc with no row is <c>unknown</c>.
/// </summary>
public sealed class EpicArcAvailability
{
    public const string Schema = "eve.rs.epic-arc-availability.v1";
    public const string InClientBuild = "in-client-build";
    public const string NewerThanClient = "newer-than-client";
    public const string Unknown = "unknown";

    /// <summary>The fields a row may carry.</summary>
    public static readonly IReadOnlyList<string> AllowedRowFields = ["arcID", "availability", "evidence"];

    /// <summary>The top-level fields a file may carry.</summary>
    public static readonly IReadOnlyList<string> AllowedTopFields = ["schema", "source", "counts", "arcs"];

    private const string CommittedResource = "epic-arc-availability.v1.json";
    private static readonly string[] AllowedAvailability = [InClientBuild, NewerThanClient, Unknown];
    private static readonly Lazy<EpicArcAvailability> CommittedTable = new(() => FromJson(CommittedJson(), CommittedResource));

    private readonly Dictionary<long, EpicArcAvailabilityRow> _rows;

    private EpicArcAvailability(IEnumerable<EpicArcAvailabilityRow> rows)
    {
        _rows = new Dictionary<long, EpicArcAvailabilityRow>();
        foreach (var row in rows)
        {
            if (_rows.ContainsKey(row.ArcId))
                throw new InvalidDataException($"epic arc {row.ArcId} has two availability rows");
            _rows[row.ArcId] = row;
        }
    }

    /// <summary>The committed table.</summary>
    public static EpicArcAvailability Committed => CommittedTable.Value;

    public IReadOnlyCollection<EpicArcAvailabilityRow> All => _rows.Values;

    /// <summary>The availability of an arc: its row, or <c>unknown</c> when it has none.</summary>
    public string Availability(long arcId) => _rows.TryGetValue(arcId, out var row) ? row.Availability : Unknown;

    /// <summary>The row of an arc, or null.</summary>
    public EpicArcAvailabilityRow? Row(long arcId) => _rows.GetValueOrDefault(arcId);

    public static EpicArcAvailability FromJson(string json, string origin)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        foreach (var property in root.EnumerateObject())
        {
            if (!AllowedTopFields.Contains(property.Name))
                throw new InvalidDataException($"{origin}: top-level field {property.Name} is not allowed in an availability file");
        }
        if (root.GetProperty("schema").GetString() != Schema)
            throw new InvalidDataException($"{origin}: not a {Schema} file");
        var rows = new List<EpicArcAvailabilityRow>();
        foreach (var row in root.GetProperty("arcs").EnumerateArray())
        {
            foreach (var property in row.EnumerateObject())
            {
                if (!AllowedRowFields.Contains(property.Name))
                    throw new InvalidDataException($"{origin}: field {property.Name} is not allowed in an availability row");
            }
            foreach (var required in AllowedRowFields)
            {
                if (!row.TryGetProperty(required, out _))
                    throw new InvalidDataException($"{origin}: an availability row has no {required}");
            }
            if (!row.GetProperty("arcID").TryGetInt64(out var arcId) || arcId <= 0)
                throw new InvalidDataException($"{origin}: an availability row has an arcID that is not a positive integer");
            var availability = row.GetProperty("availability").GetString() ?? "";
            var evidence = row.GetProperty("evidence").GetString() ?? "";
            if (!AllowedAvailability.Contains(availability))
                throw new InvalidDataException($"{origin}: arc {arcId} has availability {availability}; allowed: {string.Join(", ", AllowedAvailability)}");
            if (evidence.Length == 0)
                throw new InvalidDataException($"{origin}: arc {arcId} has no evidence");
            rows.Add(new EpicArcAvailabilityRow(arcId, availability, evidence));
        }
        return new EpicArcAvailability(rows);
    }

    /// <summary>The committed file's raw JSON, for the field test.</summary>
    public static string CommittedJson()
    {
        using var stream = typeof(EpicArcAvailability).Assembly.GetManifestResourceStream(CommittedResource)
            ?? throw new InvalidOperationException($"the committed epic arc availability {CommittedResource} is not embedded");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
