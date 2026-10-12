using System.Globalization;
using System.Text.RegularExpressions;
using System.Text.Json.Nodes;
using Npgsql;

namespace Eve.Ships;

/// <summary>
/// The mining ledger's table (migration 0003, in the space schema): one entry per granted cycle, and the observers' names. The writer
/// (<see cref="RecordAsync"/>) runs in the subscriber's transaction, so the entry and the subscriber's cursor commit together. The reader is the rowset read
/// side (<c>MiningLedgerQueries</c>). <see cref="ImportAsync"/> copies the rows the game store held before the table existed (EVE.js's shape): run at start-up,
/// it adds only the entries that are not there. Times are FILETIMEs, as the reference keeps them.
/// </summary>
public static partial class MiningLedgerStore
{
    /// <summary>EVE.js keeps 90 days of entries; older ones are pruned.</summary>
    public const long RetentionFileTimeTicks = 90L * 24 * 60 * 60 * 1000 * 10_000;

    /// <summary>miningCharacterLedgerDelayMs (the EVE.js default, 10 minutes): a pilot's own entry is shown this long after its cycle.</summary>
    public const long CharacterDelayFileTimeTicks = 600_000L * 10_000;

    private const long TicksPerMs = 10_000;
    private const long UnixEpochFileTime = 116_444_736_000_000_000L;

    /// <summary>One row of the table.</summary>
    public sealed record Entry(
        long EntryId, string OwnerKind, long OwnerId, long CharacterId, long CorporationId, long SolarSystemId, long EventDate, long VisibleAfter,
        long TypeId, long Quantity, long QuantityWasted, long QuantityCritical, long ShipTypeId, long ModuleTypeId, long ObserverItemId, string YieldKind);

    /// <summary>The FILETIME of a Unix time in milliseconds.</summary>
    public static long FileTimeOf(long unixMs) => UnixEpochFileTime + unixMs * TicksPerMs;

    /// <summary>
    /// Writes the entry of a granted cycle for the pilot, in the caller's transaction, and prunes the entries older than the retention as of that cycle. A replay
    /// (the same entry id) writes nothing.
    /// </summary>
    public static async Task RecordAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string schema, long entryId, OreMinedEvent ore,
        long eventDateFileTime, CancellationToken cancellationToken = default)
    {
        var entry = new Entry(entryId, "character", ore.CharacterId, ore.CharacterId, ore.CorporationId, ore.SolarSystemId, eventDateFileTime,
            eventDateFileTime + CharacterDelayFileTimeTicks, ore.OreTypeId, ore.Quantity, ore.QuantityWasted, ore.QuantityCritical, ore.ShipTypeId,
            ore.MinerTypeId, 0, ore.YieldKind);
        await InsertAsync(connection, transaction, schema, entry, cancellationToken);
        await using var prune = new NpgsqlCommand($"DELETE FROM {Table(schema, "mining_ledger_entries")} WHERE event_date < $1", connection, transaction);
        prune.Parameters.AddWithValue(eventDateFileTime - RetentionFileTimeTicks);
        await prune.ExecuteNonQueryAsync(cancellationToken);
    }

    public static async Task RecordObserverAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string schema,
        long entryId,
        OreMinedEvent ore,
        long eventDateFileTime,
        CancellationToken cancellationToken = default)
    {
        if (ore.MoonExtractionId <= 0 || ore.MoonStructureId <= 0 || ore.MoonId <= 0 || ore.MoonOwnerCorporationId <= 0)
        {
            throw new InvalidDataException("moon mining observer metadata is incomplete");
        }

        var visibleAfter = eventDateFileTime + CharacterDelayFileTimeTicks;
        await using (var observer = new NpgsqlCommand(
            $"""
            INSERT INTO {Table(schema, "mining_ledger_observers")}
                (item_id, corporation_id, solar_system_id, item_name, first_visible_after, last_event_date, moon_id)
            VALUES ($1, $2, $3, '', $4, $5, $6)
            ON CONFLICT (item_id) DO UPDATE SET
                corporation_id = EXCLUDED.corporation_id,
                solar_system_id = EXCLUDED.solar_system_id,
                first_visible_after = LEAST(mining_ledger_observers.first_visible_after, EXCLUDED.first_visible_after),
                last_event_date = GREATEST(mining_ledger_observers.last_event_date, EXCLUDED.last_event_date),
                moon_id = EXCLUDED.moon_id
            """,
            connection, transaction))
        {
            observer.Parameters.AddWithValue(ore.MoonStructureId);
            observer.Parameters.AddWithValue(ore.MoonOwnerCorporationId);
            observer.Parameters.AddWithValue(ore.SolarSystemId);
            observer.Parameters.AddWithValue(visibleAfter);
            observer.Parameters.AddWithValue(eventDateFileTime);
            observer.Parameters.AddWithValue(ore.MoonId);
            await observer.ExecuteNonQueryAsync(cancellationToken);
        }

        var entry = new Entry(entryId, "observer", ore.MoonStructureId, ore.CharacterId, ore.MoonOwnerCorporationId,
            ore.SolarSystemId, eventDateFileTime, visibleAfter, ore.OreTypeId, ore.Quantity, ore.QuantityWasted, ore.QuantityCritical,
            ore.ShipTypeId, ore.MinerTypeId, ore.MoonStructureId, ore.YieldKind);
        await InsertAsync(connection, transaction, schema, entry, cancellationToken);
        await using var prune = new NpgsqlCommand(
            $"DELETE FROM {Table(schema, "mining_ledger_entries")} WHERE event_date < $1", connection, transaction);
        prune.Parameters.AddWithValue(eventDateFileTime - RetentionFileTimeTicks);
        await prune.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Copies the EVE.js ledger (the game store's <c>miningLedger</c> table, as <c>PersistenceStore.LoadTableAsync</c> returns it) into the table: the entries
    /// that are not there yet and within the retention as of <paramref name="nowFileTime"/>, and the observers that have one. Returns how many entries it added.
    /// </summary>
    public static async Task<int> ImportAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string schema, JsonObject table, long nowFileTime,
        CancellationToken cancellationToken = default)
    {
        var added = 0;
        var oldest = nowFileTime - RetentionFileTimeTicks;
        if (table["characters"] is JsonObject characters)
        {
            foreach (var (key, node) in characters)
            {
                if (node is not JsonObject bucket || bucket["entries"] is not JsonArray entries)
                {
                    continue;
                }
                var owner = Integer(bucket["characterID"]) is > 0 and var id ? id : Integer(JsonValue.Create(key));
                foreach (var row in entries.OfType<JsonObject>())
                {
                    if (ParseEntry(row, "character", owner, 0, oldest) is { } entry &&
                        await InsertAsync(connection, transaction, schema, entry, cancellationToken))
                    {
                        added++;
                    }
                }
            }
        }
        if (table["observers"] is JsonObject observers)
        {
            foreach (var (key, node) in observers)
            {
                if (node is not JsonObject bucket || bucket["entries"] is not JsonArray entries)
                {
                    continue;
                }
                var itemId = Integer(bucket["itemID"]) is > 0 and var item ? item : Integer(JsonValue.Create(key));
                var corporationId = Integer(bucket["corporationID"]);
                var rows = entries.OfType<JsonObject>().Select(row => ParseEntry(row, "observer", itemId, corporationId, oldest)).OfType<Entry>().ToList();
                if (rows.Count == 0)
                {
                    continue;
                }
                await using (var header = new NpgsqlCommand(
                    $"INSERT INTO {Table(schema, "mining_ledger_observers")} (item_id, corporation_id, solar_system_id, item_name, first_visible_after, last_event_date) " +
                    "VALUES ($1, $2, $3, $4, $5, $6) ON CONFLICT (item_id) DO NOTHING",
                    connection, transaction))
                {
                    header.Parameters.AddWithValue(itemId);
                    header.Parameters.AddWithValue(corporationId);
                    header.Parameters.AddWithValue(Integer(bucket["solarSystemID"]));
                    header.Parameters.AddWithValue(Text(bucket["itemName"]));
                    header.Parameters.AddWithValue(rows.Min(entry => entry.VisibleAfter));
                    header.Parameters.AddWithValue(rows.Max(entry => entry.EventDate));
                    await header.ExecuteNonQueryAsync(cancellationToken);
                }
                foreach (var entry in rows)
                {
                    if (await InsertAsync(connection, transaction, schema, entry, cancellationToken))
                    {
                        added++;
                    }
                }
            }
        }
        return added;
    }

    /// <summary>One observer entry's owner is the observer; a character's entries carry the character's corporation (the EVE.js shape).</summary>
    private static Entry? ParseEntry(JsonObject row, string kind, long owner, long corporationOverride, long oldest)
    {
        var eventDate = Integer(row["eventDate"]);
        if (eventDate < oldest)
        {
            return null;
        }
        var visibleField = kind == "character" ? "characterVisibleAfter" : "observerVisibleAfter";
        var visibleAfter = Integer(row[visibleField]) is > 0 and var visible ? visible : eventDate + CharacterDelayFileTimeTicks;
        var corporation = corporationOverride > 0 ? corporationOverride : Integer(row["corporationID"]);
        var character = Integer(row["characterID"]);
        return new Entry(
            Integer(row["entryID"]), kind, owner, character, corporation,
            Integer(row["solarSystemID"]) is > 0 and var system ? system : Integer(row["solarsystemID"]),
            eventDate, visibleAfter, Integer(row["typeID"]), Integer(row["quantity"]), Integer(row["quantityWasted"]), Integer(row["quantityCritical"]),
            Integer(row["shipTypeID"]), Integer(row["moduleTypeID"]), Integer(row["observerItemID"]), Text(row["yieldKind"]));
    }

    private static async Task<bool> InsertAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string schema, Entry entry,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"INSERT INTO {Table(schema, "mining_ledger_entries")} (entry_id, owner_kind, owner_id, character_id, corporation_id, solar_system_id, event_date, " +
            "visible_after, type_id, quantity, quantity_wasted, quantity_critical, ship_type_id, module_type_id, observer_item_id, yield_kind) " +
            "VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13, $14, $15, $16) ON CONFLICT (entry_id) DO NOTHING",
            connection, transaction);
        command.Parameters.AddWithValue(entry.EntryId);
        command.Parameters.AddWithValue(entry.OwnerKind);
        command.Parameters.AddWithValue(entry.OwnerId);
        command.Parameters.AddWithValue(entry.CharacterId);
        command.Parameters.AddWithValue(entry.CorporationId);
        command.Parameters.AddWithValue(entry.SolarSystemId);
        command.Parameters.AddWithValue(entry.EventDate);
        command.Parameters.AddWithValue(entry.VisibleAfter);
        command.Parameters.AddWithValue(entry.TypeId);
        command.Parameters.AddWithValue(entry.Quantity);
        command.Parameters.AddWithValue(entry.QuantityWasted);
        command.Parameters.AddWithValue(entry.QuantityCritical);
        command.Parameters.AddWithValue(entry.ShipTypeId);
        command.Parameters.AddWithValue(entry.ModuleTypeId);
        command.Parameters.AddWithValue(entry.ObserverItemId);
        command.Parameters.AddWithValue(entry.YieldKind);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    /// <summary>The table of a schema, the name checked (a schema comes from the environment, and is never text of a request).</summary>
    public static string Table(string schema, string table)
    {
        if (!SchemaName().IsMatch(schema))
        {
            throw new ArgumentException($"not a usable schema name: {schema}", nameof(schema));
        }
        return $"{schema}.{table}";
    }

    /// <summary>A stored number, as the reference writes it: a number, or the text of one (a FILETIME is kept as text there).</summary>
    private static long Integer(JsonNode? value)
    {
        var text = value?.ToString();
        if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer))
        {
            return integer;
        }
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number) &&
            number > long.MinValue && number < long.MaxValue)
        {
            return (long)Math.Truncate(number);
        }
        return 0;
    }

    private static string Text(JsonNode? value) => value is JsonValue scalar && scalar.TryGetValue<string>(out var text) ? text : "";

    [GeneratedRegex("^[a-z_][a-z0-9_]{0,62}$")]
    private static partial Regex SchemaName();
}
