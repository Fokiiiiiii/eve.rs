using System.Globalization;
using System.Text.Json.Nodes;
using Eve.Data;
using Npgsql;

namespace Eve.Structures;

/// <summary>A structure as the table holds it: the timeline, the place and the owner, and the remainder of the legacy record (<see cref="Doc"/>).</summary>
public sealed record StructureRecord(
    StructureSnapshot Snapshot, int TypeId, int SolarSystemId, double X, double Y, double Z, double Yaw, long OwnerCorporationId, long AllianceId,
    string Name, string Bio, long ProfileId, JsonObject Doc, long Version);

/// <summary>A structure that is about to be anchored (a fresh row in the Unanchored state).</summary>
public sealed record NewStructure(
    long StructureId, int TypeId, int SolarSystemId, double X, double Y, double Z, double Yaw, long OwnerCorporationId, long AllianceId, string Name, string Bio,
    long ProfileId = 0, int ReinforceWeekday = StructureTimes.DefaultReinforceWeekday, int ReinforceHour = StructureTimes.DefaultReinforceHour);

/// <summary>
/// The structure records in PostgreSQL (schema <c>structures</c>, migration 0001): the one owner of Upwell structures (docs/design/adr/0003). Every method that changes a row takes the
/// caller's connection and transaction, so a change, its event and its deadline job commit together (FOUNDATIONS rule 1). <see cref="ReadLegacyTableAsync"/> gives the directory
/// read models the row shape EVE.js kept in the game store's <c>structures</c> table; <see cref="ImportAsync"/> copies those rows in once.
/// </summary>
public sealed class StructureStore
{
    public const string DefaultSchema = "structures";

    private const string Columns =
        "s.structure_id, s.type_id, s.solar_system_id, s.x, s.y, s.z, s.yaw, s.owner_corporation_id, s.alliance_id, s.name, s.bio, s.profile_id, s.state, s.state_started_ms, " +
        "s.state_ends_ms, s.timer_paused_ms, s.upkeep, s.core_installed, s.reinforce_weekday, s.reinforce_hour, s.fight_weekday, s.fight_hour, s.pending_weekday, " +
        "s.pending_hour, s.pending_effective_ms, s.unanchor_ends_ms, s.low_power_since_ms, s.destroyed_ms, s.doc::text, s.version, " +
        "(SELECT count(*) FROM {schema}.structure_services v WHERE v.structure_id = s.structure_id AND v.state = 1)";

    /// <summary>Fields of the legacy record that a column of the table carries (EVE.js names); the rest of the record is kept in <c>doc</c>.</summary>
    private static readonly string[] ColumnFields =
    [
        "structureID", "itemID", "typeID", "solarSystemID", "ownerCorpID", "corporationID", "allianceID", "itemName", "name", "structureName", "position", "yaw", "profileID",
        "state", "upkeepState", "stateStartedAt", "stateEndsAt", "timerPausedAt", "hasQuantumCore", "reinforceWeekday", "reinforceHour", "nextReinforceWeekday",
        "nextReinforceHour", "nextReinforceApply", "unanchoring", "destroyedAt", "abandonAt", "services", "serviceStates", "description",
    ];

    private readonly NpgsqlDataSource _source;
    private readonly string _schema;

    public StructureStore(NpgsqlDataSource source, string schema = DefaultSchema)
    {
        _source = source;
        _schema = Sql.CheckSchema(schema);
    }

    public string Schema => _schema;

    public static IReadOnlyList<Migration> MigrationSet() => Migrations.Embedded(typeof(StructureStore).Assembly, "Migrations");

    public static Task<IReadOnlyList<string>> MigrateAsync(NpgsqlDataSource source, string schema = DefaultSchema, CancellationToken cancellationToken = default) =>
        Migrations.ApplyAsync(source, schema, MigrationSet(), cancellationToken);

    private string Q(string text) => Sql.In(_schema, text);

    // ---- reading --------------------------------------------------------------------------------------------------------------------------------

    /// <summary>The structure, or null. With <paramref name="forUpdate"/> the row is locked until the transaction ends (the timeline of one structure is changed one event at a time).</summary>
    public async Task<StructureRecord?> GetAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, long structureId, bool forUpdate, CancellationToken cancellationToken = default)
    {
        // the lock is on the structure row only: the service count is a plain subquery
        await using var command = Sql.Command(connection, transaction,
            Q($"SELECT {Columns} FROM {{schema}}.structures s WHERE s.structure_id = $1{(forUpdate ? " FOR UPDATE OF s" : "")}"), structureId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
    }

    public async Task<StructureRecord?> GetAsync(long structureId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        return await GetAsync(connection, null, structureId, false, cancellationToken);
    }

    /// <summary>Every structure that is not gone, by id.</summary>
    public async Task<IReadOnlyList<StructureRecord>> ListAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, bool includeGone, CancellationToken cancellationToken = default)
    {
        await using var command = Sql.Command(connection, transaction,
            Q($"SELECT {Columns} FROM {{schema}}.structures s {(includeGone ? "" : "WHERE s.destroyed_ms = 0 ")}ORDER BY s.structure_id"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var list = new List<StructureRecord>();
        while (await reader.ReadAsync(cancellationToken))
        {
            list.Add(Read(reader));
        }
        return list;
    }

    public async Task<IReadOnlyList<StructureRecord>> ListInSystemAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, int solarSystemId,
        CancellationToken cancellationToken = default)
    {
        await using var command = Sql.Command(connection, transaction,
            Q($"SELECT {Columns} FROM {{schema}}.structures s WHERE s.solar_system_id = $1 AND s.destroyed_ms = 0 ORDER BY s.structure_id"), solarSystemId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var list = new List<StructureRecord>();
        while (await reader.ReadAsync(cancellationToken))
            list.Add(Read(reader));
        return list;
    }

    private static StructureRecord Read(NpgsqlDataReader r) => new(
        new StructureSnapshot
        {
            StructureId = r.GetInt64(0),
            State = r.GetInt16(12),
            StateStartedMs = r.GetInt64(13),
            StateEndsMs = r.GetInt64(14),
            TimerPausedMs = r.GetInt64(15),
            Upkeep = r.GetInt16(16),
            CoreInstalled = r.GetBoolean(17),
            ReinforceWeekday = r.GetInt16(18),
            ReinforceHour = r.GetInt16(19),
            FightWeekday = r.GetInt16(20),
            FightHour = r.GetInt16(21),
            PendingWeekday = r.GetInt16(22),
            PendingHour = r.GetInt16(23),
            PendingEffectiveMs = r.GetInt64(24),
            UnanchorEndsMs = r.GetInt64(25),
            LowPowerSinceMs = r.GetInt64(26),
            DestroyedMs = r.GetInt64(27),
            OnlineServices = (int)r.GetInt64(30),
        },
        r.GetInt32(1), r.GetInt32(2), r.GetDouble(3), r.GetDouble(4), r.GetDouble(5), r.GetDouble(6), r.GetInt64(7), r.GetInt64(8), r.GetString(9), r.GetString(10),
        r.GetInt64(11), JsonNode.Parse(r.GetString(28)) as JsonObject ?? new JsonObject(), r.GetInt64(29));

    public async Task<IReadOnlyDictionary<int, int>> ServicesAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, long structureId, CancellationToken cancellationToken = default)
    {
        await using var command = Sql.Command(connection, transaction, Q("SELECT service_id, state FROM {schema}.structure_services WHERE structure_id = $1 ORDER BY service_id"), structureId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var services = new Dictionary<int, int>();
        while (await reader.ReadAsync(cancellationToken))
        {
            services[reader.GetInt32(0)] = reader.GetInt16(1);
        }
        return services;
    }

    // ---- writing --------------------------------------------------------------------------------------------------------------------------------

    /// <summary>Inserts a fresh structure in the Unanchored state. Returns false when the id is taken.</summary>
    public async Task<bool> CreateAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, NewStructure n, CancellationToken cancellationToken = default) =>
        await Sql.ExecuteAsync(connection, transaction, Q("""
            INSERT INTO {schema}.structures (structure_id, type_id, solar_system_id, x, y, z, yaw, owner_corporation_id, alliance_id, name, bio, profile_id, reinforce_weekday, reinforce_hour)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13, $14)
            ON CONFLICT (structure_id) DO NOTHING
            """), cancellationToken, n.StructureId, n.TypeId, n.SolarSystemId, n.X, n.Y, n.Z, n.Yaw, n.OwnerCorporationId, n.AllianceId, n.Name, n.Bio, n.ProfileId,
            (short)n.ReinforceWeekday, (short)n.ReinforceHour) > 0;

    /// <summary>Writes the timeline columns of a snapshot (the caller holds the row lock) and counts the version up.</summary>
    public async Task SaveAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, StructureSnapshot s, CancellationToken cancellationToken = default) =>
        await Sql.ExecuteAsync(connection, transaction, Q("""
            UPDATE {schema}.structures SET state = $2, state_started_ms = $3, state_ends_ms = $4, timer_paused_ms = $5, upkeep = $6, core_installed = $7,
                reinforce_weekday = $8, reinforce_hour = $9, fight_weekday = $10, fight_hour = $11, pending_weekday = $12, pending_hour = $13, pending_effective_ms = $14,
                unanchor_ends_ms = $15, low_power_since_ms = $16, destroyed_ms = $17, version = version + 1
            WHERE structure_id = $1
            """), cancellationToken, s.StructureId, (short)s.State, s.StateStartedMs, s.StateEndsMs, s.TimerPausedMs, (short)s.Upkeep, s.CoreInstalled,
            (short)s.ReinforceWeekday, (short)s.ReinforceHour, (short)s.FightWeekday, (short)s.FightHour, (short)s.PendingWeekday, (short)s.PendingHour,
            s.PendingEffectiveMs, s.UnanchorEndsMs, s.LowPowerSinceMs, s.DestroyedMs);

    /// <summary>Sets a service's state. An online service records <paramref name="nowMs"/> as the time it came online; an offline one holds 0.</summary>
    public async Task SetServiceAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long structureId, int serviceId, int state, long nowMs, CancellationToken cancellationToken = default) =>
        await Sql.ExecuteAsync(connection, transaction, Q("""
            INSERT INTO {schema}.structure_services (structure_id, service_id, state, online_since_ms) VALUES ($1, $2, $3, CASE WHEN $3 = 1 THEN $4::bigint ELSE 0 END)
            ON CONFLICT (structure_id, service_id) DO UPDATE SET state = EXCLUDED.state, online_since_ms = EXCLUDED.online_since_ms
            """), cancellationToken, structureId, serviceId, (short)state, nowMs);

    /// <summary>
    /// When the first online service of this kind in the system came online: 0 when it was online before the record existed, null when none is online in a
    /// structure that is not gone.
    /// </summary>
    public async Task<long?> OnlineSinceAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, int solarSystemId, int serviceId, CancellationToken cancellationToken = default) =>
        await Sql.ScalarAsync(connection, transaction, Q("""
            SELECT min(v.online_since_ms) FROM {schema}.structure_services v JOIN {schema}.structures s ON s.structure_id = v.structure_id
            WHERE s.solar_system_id = $1 AND s.destroyed_ms = 0 AND v.service_id = $2 AND v.state = 1
            """), cancellationToken, solarSystemId, serviceId) is long since ? since : null;

    /// <summary>Takes every online service of a structure offline (the fuel ran out). Returns how many were online.</summary>
    public async Task<int> TakeServicesOfflineAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long structureId, CancellationToken cancellationToken = default) =>
        await Sql.ExecuteAsync(connection, transaction, Q("UPDATE {schema}.structure_services SET state = 2 WHERE structure_id = $1 AND state = 1"), cancellationToken, structureId);

    public async Task SetDescriptionAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, long structureId, string description, CancellationToken cancellationToken = default) =>
        await Sql.ExecuteAsync(connection, transaction, Q("UPDATE {schema}.structures SET bio = $2, version = version + 1 WHERE structure_id = $1"), cancellationToken, structureId, description);

    /// <summary>Renames a structure that is not gone (EVE.js <c>renameStructureByID</c>: the name is the one the directory and the ball show).</summary>
    public async Task SetNameAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, long structureId, string name, CancellationToken cancellationToken = default) =>
        await Sql.ExecuteAsync(connection, transaction, Q("UPDATE {schema}.structures SET name = $2, version = version + 1 WHERE structure_id = $1 AND destroyed_ms = 0"), cancellationToken, structureId, name);

    public async Task SetProfileAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, IEnumerable<long> structureIds, long profileId, CancellationToken cancellationToken = default) =>
        await Sql.ExecuteAsync(connection, transaction,
            Q("UPDATE {schema}.structures SET profile_id = $2, version = version + 1 WHERE structure_id = ANY($1) AND destroyed_ms = 0"), cancellationToken, structureIds.ToArray(), profileId);

    // ---- the legacy record ------------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The table as EVE.js kept it in the game store: a record per structure id (as text), with the field names of that record (<c>ownerCorpID</c>, <c>position</c>,
    /// <c>serviceStates</c>, <c>stateEndsAt</c> ...). The directory, profile and asset-safety read models read this shape.
    /// </summary>
    public async Task<JsonObject> ReadLegacyTableAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        var table = new JsonObject();
        foreach (var record in await ListAsync(connection, null, true, cancellationToken))
        {
            var services = await ServicesAsync(connection, null, record.Snapshot.StructureId, cancellationToken);
            table[record.Snapshot.StructureId.ToString(CultureInfo.InvariantCulture)] = ToLegacy(record, services);
        }
        return table;
    }

    internal static JsonObject ToLegacy(StructureRecord record, IReadOnlyDictionary<int, int> services)
    {
        var s = record.Snapshot;
        var row = (JsonObject)record.Doc.DeepClone();
        row["structureID"] = s.StructureId;
        row["typeID"] = record.TypeId;
        row["solarSystemID"] = record.SolarSystemId;
        if (record.OwnerCorporationId > 0)
        {
            row["ownerCorpID"] = record.OwnerCorporationId;
        }
        row["allianceID"] = record.AllianceId;
        if (record.Name.Length > 0)
        {
            row["itemName"] = record.Name;
            row["name"] = record.Name;
        }
        if (record.Bio.Length > 0)
        {
            row["description"] = record.Bio;
        }
        row["position"] = new JsonObject { ["x"] = record.X, ["y"] = record.Y, ["z"] = record.Z };
        if (record.Yaw != 0)
        {
            row["yaw"] = record.Yaw;
        }
        if (record.ProfileId != 0)
        {
            row["profileID"] = record.ProfileId;
        }
        row["state"] = s.State;
        row["upkeepState"] = s.Upkeep;
        SetIfPositive(row, "stateStartedAt", s.StateStartedMs);
        SetIfPositive(row, "stateEndsAt", s.StateEndsMs);
        SetIfPositive(row, "timerPausedAt", s.TimerPausedMs);
        row["hasQuantumCore"] = s.CoreInstalled;
        row["reinforceWeekday"] = s.ReinforceWeekday;
        row["reinforceHour"] = s.ReinforceHour;
        if (s.PendingEffectiveMs > 0)
        {
            row["nextReinforceWeekday"] = s.PendingWeekday;
            row["nextReinforceHour"] = s.PendingHour;
            row["nextReinforceApply"] = s.PendingEffectiveMs;
        }
        SetIfPositive(row, "unanchoring", s.UnanchorEndsMs);
        SetIfPositive(row, "destroyedAt", s.DestroyedMs);
        if (services.Count > 0)
        {
            var states = new JsonObject();
            foreach (var (serviceId, state) in services)
            {
                states[serviceId.ToString(CultureInfo.InvariantCulture)] = state;
            }
            row["serviceStates"] = states;
        }
        return row;
    }

    private static void SetIfPositive(JsonObject row, string field, long value)
    {
        if (value > 0)
        {
            row[field] = value;
        }
    }

    /// <summary>
    /// Copies the game store's <c>structures</c> table (EVE.js records, by structure id) into the tables: the structures that are not there yet, with their services. Run at
    /// start-up; a second run adds nothing. Returns the ids it added.
    /// </summary>
    public async Task<IReadOnlyList<long>> ImportAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, JsonObject table, long nowMs, CancellationToken cancellationToken = default)
    {
        var added = new List<long>();
        foreach (var (key, node) in table)
        {
            if (node is not JsonObject row || !long.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id <= 0)
            {
                continue;
            }
            var doc = new JsonObject();
            foreach (var (field, value) in row)
            {
                if (!ColumnFields.Contains(field))
                {
                    doc[field] = value?.DeepClone();
                }
            }
            var position = row["position"] as JsonObject;
            var destroyed = Integer(row["destroyedAt"]);
            if (destroyed == 0 && Truthy(row["destroyedAt"]))
            {
                destroyed = 1;
            }
            var hasState = row["state"] is not null;
            var upkeepState = row["upkeepState"] is not null ? Integer(row["upkeepState"]) : UpkeepState.FullPower;
            var abandonAt = Integer(row["abandonAt"]);
            var inserted = await Sql.ExecuteAsync(connection, transaction, Q("""
                INSERT INTO {schema}.structures (structure_id, type_id, solar_system_id, x, y, z, yaw, owner_corporation_id, alliance_id, name, bio, profile_id, state, state_started_ms,
                    state_ends_ms, timer_paused_ms, upkeep, core_installed, reinforce_weekday, reinforce_hour, pending_weekday, pending_hour, pending_effective_ms,
                    unanchor_ends_ms, low_power_since_ms, destroyed_ms, doc)
                VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13, $14, $15, $16, $17, $18, $19, $20, $21, $22, $23, $24, $25, $26, $27::text::jsonb)
                ON CONFLICT (structure_id) DO NOTHING
                """), cancellationToken,
                id, (int)Integer(row["typeID"]), (int)Integer(row["solarSystemID"]),
                Real(position?["x"]), Real(position?["y"]), Real(position?["z"]), Real(row["yaw"]),
                Integer(row["ownerCorpID"] ?? row["corporationID"]), Math.Max(0, Integer(row["allianceID"])),
                Text(row["itemName"] ?? row["name"] ?? row["structureName"]), Text(row["description"]), Integer(row["profileID"]),
                // a record without a state is one of a structure that already works
                (short)(hasState ? Integer(row["state"]) : StructureState.ShieldVulnerable), Integer(row["stateStartedAt"]), Integer(row["stateEndsAt"]), Integer(row["timerPausedAt"]),
                (short)upkeepState, Truthy(row["hasQuantumCore"]),
                (short)(row["reinforceWeekday"] is not null ? Integer(row["reinforceWeekday"]) : StructureTimes.DefaultReinforceWeekday),
                (short)(row["reinforceHour"] is not null ? Integer(row["reinforceHour"]) : StructureTimes.DefaultReinforceHour),
                (short)(Integer(row["nextReinforceApply"]) > 0 ? Integer(row["nextReinforceWeekday"]) : -1),
                (short)(Integer(row["nextReinforceApply"]) > 0 ? Integer(row["nextReinforceHour"]) : -1), Integer(row["nextReinforceApply"]),
                Integer(row["unanchoring"]),
                // the abandonment clock of a structure in Low Power: EVE.js stored the day it falls due, otherwise it starts at the import
                upkeepState == UpkeepState.LowPower ? (abandonAt > 0 ? abandonAt - StructureTimes.AbandonAfterMs : nowMs) : 0L,
                destroyed, doc.ToJsonString());
            if (inserted == 0)
            {
                continue;
            }
            added.Add(id);
            if ((row["services"] ?? row["serviceStates"]) is JsonObject services)
            {
                foreach (var (serviceKey, state) in services)
                {
                    if (int.TryParse(serviceKey, NumberStyles.None, CultureInfo.InvariantCulture, out var serviceId) && serviceId > 0 && IsNumber(state))
                    {
                        await SetServiceAsync(connection, transaction, id, serviceId, (int)Integer(state), 0, cancellationToken);
                    }
                }
            }
        }
        return added;
    }

    private static bool IsNumber(JsonNode? node) => node is JsonValue && long.TryParse(node.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out _);

    /// <summary>A stored number as EVE.js writes it: a number, or the text of one.</summary>
    internal static long Integer(JsonNode? value)
    {
        var text = value?.ToString();
        if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer))
        {
            return integer;
        }
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number) && number > long.MinValue && number < long.MaxValue
            ? (long)Math.Truncate(number)
            : 0;
    }

    private static double Real(JsonNode? value) =>
        double.TryParse(value?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number) ? number : 0;

    private static string Text(JsonNode? value) => value is JsonValue scalar && scalar.TryGetValue<string>(out var text) ? text : "";

    private static bool Truthy(JsonNode? value) => value switch
    {
        null => false,
        JsonValue scalar when scalar.TryGetValue<bool>(out var flag) => flag,
        JsonValue scalar when scalar.TryGetValue<string>(out var text) => text.Length > 0,
        JsonValue scalar => Integer(scalar) != 0,
        _ => true,
    };
}
