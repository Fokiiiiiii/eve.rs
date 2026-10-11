using System.Globalization;
using System.Text.Json.Nodes;
using Eve.Data;
using Npgsql;

namespace Eve.Structures;

/// <summary>A moon extraction as the table holds it (EVE.js <c>moonExtractions.extractions</c>). Times are milliseconds since 1970.</summary>
public sealed record MoonExtraction(
    long ExtractionId, long StructureId, long MoonId, long SolarSystemId, long OwnerCorporationId, long StartMs, long ChunkAvailableMs, long NaturalDecayMs,
    long DurationSeconds, double YieldMultiplier, long CalendarEventId, string State, long ReadyNotifiedMs, long FractureStartedMs, long FractureCompleteMs,
    IReadOnlyDictionary<long, double> OreVolumeM3, long CreatedMs, long CompletedMs, string CompletedReason, long FieldDecayedMs);

/// <summary>The resource profile of a structure that mines a moon: the moon, its owner, its yield and the share of each ore type (EVE.js <c>resourcesByStructureID</c>).</summary>
public sealed record MoonResourceProfile(long StructureId, long MoonId, long SolarSystemId, long OwnerCorporationId, double YieldMultiplier, IReadOnlyDictionary<long, double> Resources);

/// <summary>
/// The moon extractions and resource profiles (migration 0003, schema of the structures): the owner of what EVE.js kept in the game store's <c>moonExtractions</c> table.
/// <see cref="ReadLegacyAsync"/> gives the moon service the shape it reads; <see cref="ImportAsync"/> copies the table in (rows already there are kept, so a second run adds
/// nothing). Writes take the caller's connection and transaction, so an extraction and its jobs commit together (FOUNDATIONS rule 1).
/// </summary>
public sealed class MoonExtractionStore
{
    /// <summary>The fracture delay of a legacy row that has no natural decay time (EVE.js DEFAULT_AUTO_FRACTURE_DELAY_SECONDS).</summary>
    public const long LegacyAutoFractureDelayMs = 10_800_000;

    private const string ExtractionColumns =
        "extraction_id, structure_id, moon_id, solar_system_id, owner_corporation_id, start_ms, chunk_available_ms, natural_decay_ms, duration_seconds, " +
        "yield_multiplier, calendar_event_id, state, ready_notified_ms, fracture_started_ms, fracture_complete_ms, ore_volume::text, created_ms, completed_ms, completed_reason, " +
        "field_decayed_ms";

    private readonly NpgsqlDataSource _source;
    private readonly string _schema;

    public MoonExtractionStore(NpgsqlDataSource source, string schema = StructureStore.DefaultSchema)
    {
        _source = source;
        _schema = Sql.CheckSchema(schema);
    }

    private string Q(string text) => Sql.In(_schema, text);

    /// <summary>
    /// The records the moon service reads, in the EVE.js shape: the extractions that are not completed (the client sees only those; completed ones are history) and the
    /// resource profiles, each keyed by its id.
    /// </summary>
    public async Task<JsonObject> ReadLegacyAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        var extractions = new JsonObject();
        await using (var command = Sql.Command(connection, null, Q($"SELECT {ExtractionColumns} FROM {{schema}}.structure_moon_extractions WHERE state <> 'completed' ORDER BY extraction_id")))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var extraction = ReadExtraction(reader);
                extractions[extraction.ExtractionId.ToString(CultureInfo.InvariantCulture)] = ToLegacy(extraction);
            }
        }
        var profiles = new JsonObject();
        await using (var command = Sql.Command(connection, null, Q("SELECT structure_id, moon_id, solar_system_id, owner_corporation_id, yield_multiplier, resources::text FROM {schema}.structure_moon_resources")))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var profile = ReadProfile(reader);
                profiles[profile.StructureId.ToString(CultureInfo.InvariantCulture)] = ProfileToLegacy(profile);
            }
        }
        return new JsonObject { ["extractions"] = extractions, ["resourcesByStructureID"] = profiles };
    }

    /// <summary>An extraction by id, or null. With <paramref name="forUpdate"/> the row is locked until the transaction ends.</summary>
    public async Task<MoonExtraction?> GetAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, long extractionId, bool forUpdate, CancellationToken cancellationToken = default)
    {
        var lock_ = forUpdate ? " FOR UPDATE" : "";
        await using var command = Sql.Command(connection, transaction,
            Q($"SELECT {ExtractionColumns} FROM {{schema}}.structure_moon_extractions WHERE extraction_id = $1{lock_}"), extractionId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadExtraction(reader) : null;
    }

    /// <summary>The extractions that are not completed (active or fracturing), all of them or those of one structure; locked until the transaction ends with <paramref name="forUpdate"/>.</summary>
    public async Task<IReadOnlyList<MoonExtraction>> OpenAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, long? structureId = null, bool forUpdate = false, CancellationToken cancellationToken = default)
    {
        var filter = structureId is null ? "" : " AND structure_id = $1";
        var lock_ = forUpdate ? " FOR UPDATE" : "";
        var text = Q($"SELECT {ExtractionColumns} FROM {{schema}}.structure_moon_extractions WHERE state <> 'completed'{filter} ORDER BY extraction_id{lock_}");
        await using var command = structureId is null
            ? Sql.Command(connection, transaction, text)
            : Sql.Command(connection, transaction, text, structureId.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var list = new List<MoonExtraction>();
        while (await reader.ReadAsync(cancellationToken))
        {
            list.Add(ReadExtraction(reader));
        }
        return list;
    }

    /// <summary>Writes the mutable columns of an extraction: its state, the chunk's readiness, the fracture, the completion and the field's decay.</summary>
    public async Task UpdateAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, MoonExtraction e, CancellationToken cancellationToken = default) =>
        await Sql.ExecuteAsync(connection, transaction, Q("""
            UPDATE {schema}.structure_moon_extractions SET state = $2, ready_notified_ms = $3, fracture_started_ms = $4, fracture_complete_ms = $5,
                completed_ms = $6, completed_reason = $7, field_decayed_ms = $8
            WHERE extraction_id = $1
            """), cancellationToken, e.ExtractionId, e.State, e.ReadyNotifiedMs, e.FractureStartedMs, e.FractureCompleteMs, e.CompletedMs, e.CompletedReason, e.FieldDecayedMs);

    /// <summary>The extraction of a structure that is active or fracturing, or null.</summary>
    public async Task<MoonExtraction?> ActiveForStructureAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, long structureId, CancellationToken cancellationToken = default)
    {
        await using var command = Sql.Command(connection, transaction, Q($"SELECT {ExtractionColumns} FROM {{schema}}.structure_moon_extractions WHERE structure_id = $1 AND state <> 'completed' ORDER BY extraction_id LIMIT 1"), structureId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadExtraction(reader) : null;
    }

    /// <summary>The resource profile of a structure, or null.</summary>
    public async Task<MoonResourceProfile?> ProfileAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, long structureId, CancellationToken cancellationToken = default)
    {
        await using var command = Sql.Command(connection, transaction, Q("SELECT structure_id, moon_id, solar_system_id, owner_corporation_id, yield_multiplier, resources::text FROM {schema}.structure_moon_resources WHERE structure_id = $1"), structureId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadProfile(reader) : null;
    }

    /// <summary>
    /// Copies the game store's <c>moonExtractions</c> table (EVE.js records) into the tables: the extractions and profiles that are not there yet. Returns how many rows it
    /// added. A row EVE.js could not have used (no id, no structure, no times) is skipped, as the moon service skipped it.
    /// </summary>
    public async Task<int> ImportAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, JsonObject table, CancellationToken cancellationToken = default)
    {
        var added = 0;
        if (table["extractions"] is JsonObject extractions)
        {
            foreach (var (_, node) in extractions)
            {
                if (node is JsonObject row && ParseExtraction(row) is { } extraction)
                {
                    added += await InsertExtractionAsync(connection, transaction, extraction, cancellationToken);
                }
            }
        }
        if (table["resourcesByStructureID"] is JsonObject profiles)
        {
            foreach (var (key, node) in profiles)
            {
                if (long.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out var structureId) && structureId > 0 && node is JsonObject entry)
                {
                    added += await InsertProfileAsync(connection, transaction, ParseProfile(structureId, entry), cancellationToken);
                }
            }
        }
        return added;
    }

    /// <summary>
    /// The next extraction id and the next calendar event id (one past the highest ones). The advisory lock serialises the starts of every structure until the
    /// caller's transaction ends, so two starts cannot take the same id.
    /// </summary>
    public async Task<(long ExtractionId, long EventId)> NextIdsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken cancellationToken = default)
    {
        await Sql.ScalarAsync(connection, transaction, "SELECT pg_advisory_xact_lock(hashtext('structure_moon_extractions'))", cancellationToken);
        await using var command = Sql.Command(connection, transaction, Q("SELECT COALESCE(MAX(extraction_id), 0) + 1, COALESCE(MAX(calendar_event_id), 0) + 1 FROM {schema}.structure_moon_extractions"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return (reader.GetInt64(0), reader.GetInt64(1));
    }

    /// <summary>Inserts an extraction unless its id is taken. Returns 1 when it was added.</summary>
    public async Task<int> InsertExtractionAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, MoonExtraction e, CancellationToken cancellationToken = default) =>
        await Sql.ExecuteAsync(connection, transaction, Q("""
            INSERT INTO {schema}.structure_moon_extractions (extraction_id, structure_id, moon_id, solar_system_id, owner_corporation_id, start_ms, chunk_available_ms,
                natural_decay_ms, duration_seconds, yield_multiplier, calendar_event_id, state, ready_notified_ms, fracture_started_ms, fracture_complete_ms, ore_volume,
                created_ms, completed_ms, completed_reason)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13, $14, $15, $16::text::jsonb, $17, $18, $19)
            ON CONFLICT (extraction_id) DO NOTHING
            """), cancellationToken, e.ExtractionId, e.StructureId, e.MoonId, e.SolarSystemId, e.OwnerCorporationId, e.StartMs, e.ChunkAvailableMs, e.NaturalDecayMs,
            e.DurationSeconds, e.YieldMultiplier, e.CalendarEventId, e.State, e.ReadyNotifiedMs, e.FractureStartedMs, e.FractureCompleteMs, OreJson(e.OreVolumeM3),
            e.CreatedMs, e.CompletedMs, e.CompletedReason);

    private async Task<int> InsertProfileAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, MoonResourceProfile p, CancellationToken cancellationToken) =>
        await Sql.ExecuteAsync(connection, transaction, Q("""
            INSERT INTO {schema}.structure_moon_resources (structure_id, moon_id, solar_system_id, owner_corporation_id, yield_multiplier, resources)
            VALUES ($1, $2, $3, $4, $5, $6::text::jsonb)
            ON CONFLICT (structure_id) DO NOTHING
            """), cancellationToken, p.StructureId, p.MoonId, p.SolarSystemId, p.OwnerCorporationId, p.YieldMultiplier, OreJson(p.Resources));

    // ---- the EVE.js shape --------------------------------------------------------------------------------------------------------------------------

    private static MoonExtraction ReadExtraction(NpgsqlDataReader r) => new(
        r.GetInt64(0), r.GetInt64(1), r.GetInt64(2), r.GetInt64(3), r.GetInt64(4), r.GetInt64(5), r.GetInt64(6), r.GetInt64(7), r.GetInt64(8), r.GetDouble(9),
        r.GetInt64(10), r.GetString(11), r.GetInt64(12), r.GetInt64(13), r.GetInt64(14), OreOf(r.GetString(15)), r.GetInt64(16), r.GetInt64(17), r.GetString(18), r.GetInt64(19));

    public static JsonObject ToLegacy(MoonExtraction e) => new()
    {
        ["extractionID"] = e.ExtractionId,
        ["structureID"] = e.StructureId,
        ["moonID"] = e.MoonId,
        ["solarSystemID"] = e.SolarSystemId,
        ["ownerID"] = e.OwnerCorporationId,
        ["startMoveTimeMs"] = e.StartMs,
        ["chunkAvailableTimeMs"] = e.ChunkAvailableMs,
        ["naturalDecayTimeMs"] = e.NaturalDecayMs,
        ["durationSeconds"] = e.DurationSeconds,
        ["yieldMultiplier"] = e.YieldMultiplier,
        ["calendarEventID"] = e.CalendarEventId,
        ["state"] = e.State,
    };

    private static MoonResourceProfile ReadProfile(NpgsqlDataReader r) => new(
        r.GetInt64(0), r.GetInt64(1), r.GetInt64(2), r.GetInt64(3), r.GetDouble(4), OreOf(r.GetString(5)));

    private static JsonObject ProfileToLegacy(MoonResourceProfile p)
    {
        var resources = new JsonObject();
        foreach (var (typeId, share) in p.Resources)
        {
            resources[typeId.ToString(CultureInfo.InvariantCulture)] = share;
        }
        return new JsonObject
        {
            ["structureID"] = p.StructureId,
            ["moonID"] = p.MoonId,
            ["solarSystemID"] = p.SolarSystemId,
            ["ownerID"] = p.OwnerCorporationId,
            ["yieldMultiplier"] = p.YieldMultiplier,
            ["resources"] = resources,
        };
    }

    // ---- the import ---------------------------------------------------------------------------------------------------------------------------------

    /// <summary>EVE.js <c>normalizeExtraction</c>: the id, the structure and the two times are required; the rest has the defaults EVE.js gave.</summary>
    internal static MoonExtraction? ParseExtraction(JsonObject row)
    {
        var extractionId = Positive(row["extractionID"]);
        var structureId = Positive(row["structureID"]);
        var chunkAvailable = Positive(row["chunkAvailableTimeMs"]);
        var startMove = Positive(row["startMoveTimeMs"]);
        if (extractionId <= 0 || structureId <= 0 || chunkAvailable <= 0 || startMove <= 0)
        {
            return null;
        }
        var decay = Positive(row["naturalDecayTimeMs"]);
        var yield = Real(row["yieldMultiplier"]);
        var state = row["state"]?.ToString().Trim();
        return new MoonExtraction(
            extractionId, structureId, Positive(row["moonID"]), Positive(row["solarSystemID"]), Positive(row["ownerID"] ?? row["ownerCorpID"]),
            startMove, chunkAvailable, decay > 0 ? decay : chunkAvailable + LegacyAutoFractureDelayMs, Positive(row["durationSeconds"]),
            yield > 0 ? yield : 1, Positive(row["calendarEventID"]), string.IsNullOrEmpty(state) ? "active" : state,
            Positive(row["readyNotificationSentAtMs"] ?? row["readyNotifiedAtMs"]), Positive(row["fractureStartedAtMs"]), Positive(row["fractureCompleteAtMs"]),
            Volumes(row["oreVolumeByType"]), Positive(row["createdAt"]), Positive(row["completedAt"]), row["completedReason"]?.ToString() ?? "", 0);
    }

    /// <summary>EVE.js <c>normalizeResourceEntry</c>: the shares are under <c>resources</c>, or the entry itself holds them.</summary>
    internal static MoonResourceProfile ParseProfile(long structureId, JsonObject entry)
    {
        var resources = entry["resources"] as JsonObject ?? entry;
        var yield = Real(entry["yieldMultiplier"]);
        return new MoonResourceProfile(structureId, Positive(entry["moonID"]), Positive(entry["solarSystemID"]), Positive(entry["ownerID"] ?? entry["ownerCorpID"]),
            yield > 0 ? yield : 1, Shares(resources));
    }

    private static IReadOnlyDictionary<long, double> Shares(JsonObject resources)
    {
        var shares = new Dictionary<long, double>();
        foreach (var (key, value) in resources)
        {
            if (long.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out var typeId) && typeId > 0 && Real(value) is var share && share > 0)
            {
                shares[typeId] = share;
            }
        }
        return shares;
    }

    private static IReadOnlyDictionary<long, double> Volumes(JsonNode? node) => node is JsonObject map ? Shares(map) : new Dictionary<long, double>();

    private static IReadOnlyDictionary<long, double> OreOf(string json) =>
        JsonNode.Parse(json) is JsonObject map ? Shares(map) : new Dictionary<long, double>();

    private static string OreJson(IReadOnlyDictionary<long, double> ore)
    {
        var map = new JsonObject();
        foreach (var (typeId, m3) in ore)
        {
            map[typeId.ToString(CultureInfo.InvariantCulture)] = m3;
        }
        return map.ToJsonString();
    }

    private static long Positive(JsonNode? node) => Math.Max(0, StructureStore.Integer(node));

    private static double Real(JsonNode? node) =>
        double.TryParse(node?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number) ? number : 0;
}
