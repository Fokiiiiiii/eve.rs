using System.Text.Json;
using Npgsql;

namespace Eve.Skills;

/// <summary>An expert system type of the catalogue: what it grants while it is installed and how long an installation lasts.</summary>
public sealed record ExpertSystemType(int TypeId, string Name, int DurationDays, bool Hidden, bool Retired, IReadOnlyList<SkillGrant> SkillsGranted);

/// <summary>A skill level an expert system grants.</summary>
public sealed record SkillGrant(int TypeId, int Level);

/// <summary>An expert system installed on a character: the Unix times (milliseconds) it was installed and lapses, and the item it came from.</summary>
public sealed record ExpertSystemEntry(long CharacterId, int TypeId, long InstalledAtMs, long ExpiresAtMs, long? SourceItemId, string GrantReason, long UpdatedAtMs);

/// <summary>
/// (<c>tools/DatabaseCreator/staticTables/expertSystems/data.json</c>, the client's expertsystems.jsonl), embedded as <c>expert-systems.v1.json</c>
/// by <c>tools/dev/gen-expert-system-catalog.py</c>: the pinned SDE has no expert system table.
/// </summary>
public sealed class ExpertSystemCatalog(int maxInstallations, int topUpWindowDays, IEnumerable<ExpertSystemType> types)
{
    private readonly Dictionary<int, ExpertSystemType> _types = types.ToDictionary(type => type.TypeId);

    /// <summary>How many different expert systems a character may have installed at once.</summary>
    public int MaxInstallations { get; } = maxInstallations;

    /// <summary>An installed expert system may be extended by a new one of its type only with this many days or fewer left.</summary>
    public int TopUpWindowDays { get; } = topUpWindowDays;

    public IReadOnlyCollection<ExpertSystemType> Types => _types.Values;

    public ExpertSystemType? Get(int typeId) => _types.GetValueOrDefault(typeId);

    public static ExpertSystemCatalog LoadEmbedded()
    {
        using var stream = typeof(ExpertSystemCatalog).Assembly.GetManifestResourceStream("expert-systems.v1.json")
            ?? throw new InvalidOperationException("expert-systems.v1.json is not embedded in Eve.Skills");
        using var document = JsonDocument.Parse(stream);
        var root = document.RootElement;
        var constants = root.GetProperty("constants");
        return new ExpertSystemCatalog(
            constants.GetProperty("maxCharacterInstallations").GetInt32(),
            constants.GetProperty("maxInstalledDurationToAllowTopUpDays").GetInt32(),
            root.GetProperty("expertSystems").EnumerateArray().Select(row => new ExpertSystemType(
                row.GetProperty("typeID").GetInt32(),
                row.GetProperty("name").GetString() ?? "",
                row.GetProperty("durationDays").GetInt32(),
                row.GetProperty("hidden").GetBoolean(),
                row.GetProperty("retired").GetBoolean(),
                row.GetProperty("skillsGranted").EnumerateArray()
                    .Select(grant => new SkillGrant(grant.GetProperty("typeID").GetInt32(), grant.GetProperty("level").GetInt32()))
                    .ToArray())).ToArray());
    }
}

/// <summary>
/// What installed expert systems do to a character's skills. A skill an expert system grants at a level shows at the higher of its trained level and the
/// </summary>
public static class ExpertSystemRules
{
    public const long Day = 24L * 60 * 60 * 1000;

    /// <summary>The highest level each granted skill has from the entries that are still installed at <paramref name="nowMs"/>.</summary>
    public static Dictionary<int, int> GrantsOf(ExpertSystemCatalog catalog, IEnumerable<ExpertSystemEntry> entries, long nowMs)
    {
        var grants = new Dictionary<int, int>();
        foreach (var entry in entries.Where(entry => entry.ExpiresAtMs > nowMs))
        {
            if (catalog.Get(entry.TypeId) is not { } type)
            {
                continue;
            }
            foreach (var grant in type.SkillsGranted.Where(grant => grant.TypeId > 0 && grant.Level is > 0 and <= 5))
            {
                grants[grant.TypeId] = Math.Max(grant.Level, grants.GetValueOrDefault(grant.TypeId));
            }
        }
        return grants;
    }

    /// <summary>The skills as the server shows them: the trained list, with each granted level applied (a skill not trained has no points).</summary>
    public static IReadOnlyList<CharacterSkill> Effective(IReadOnlyList<CharacterSkill> trained, IReadOnlyDictionary<int, int> grants)
    {
        if (grants.Count == 0)
        {
            return trained;
        }
        var byType = trained.ToDictionary(skill => skill.TypeId);
        foreach (var (typeId, level) in grants)
        {
            byType[typeId] = byType.TryGetValue(typeId, out var skill)
                ? (level > skill.Level ? skill with { Level = level } : skill)
                : new CharacterSkill(typeId, level, 0);
        }
        return [.. byType.Values.OrderBy(skill => skill.TypeId)];
    }

    /// <summary>
    /// Why an expert system of this type cannot be installed now, or null when it can. A type already installed is topped up instead, while it has
    /// </summary>
    public static string? RefusalOf(ExpertSystemCatalog catalog, ExpertSystemType? type, IReadOnlyList<ExpertSystemEntry> entries, long nowMs)
    {
        if (type is null)
        {
            return "Expert System type not found.";
        }
        if (type.Hidden || type.Retired)
        {
            return "That Expert System is hidden or retired and cannot be activated normally.";
        }
        var active = entries.Where(entry => entry.ExpiresAtMs > nowMs).ToArray();
        var existing = active.FirstOrDefault(entry => entry.TypeId == type.TypeId);
        if (existing is null && active.Length >= catalog.MaxInstallations)
        {
            return $"You already have {active.Length}/{catalog.MaxInstallations} Expert Systems installed.";
        }
        if (existing is not null && existing.ExpiresAtMs - nowMs > catalog.TopUpWindowDays * Day)
        {
            return $"That Expert System cannot be extended until it has {catalog.TopUpWindowDays} days or less remaining.";
        }
        return null;
    }

    /// <summary>The entry an install leaves: a new one lasts the type's duration from now; a top-up adds it to the time left.</summary>
    public static ExpertSystemEntry InstalledEntry(ExpertSystemType type, IReadOnlyList<ExpertSystemEntry> entries, long characterId, long nowMs, long? sourceItemId)
    {
        var existing = entries.FirstOrDefault(entry => entry.TypeId == type.TypeId && entry.ExpiresAtMs > nowMs);
        var duration = type.DurationDays * Day;
        return existing is null
            ? new ExpertSystemEntry(characterId, type.TypeId, nowMs, nowMs + duration, sourceItemId, "item", nowMs)
            : existing with { ExpiresAtMs = Math.Max(nowMs, existing.ExpiresAtMs) + duration, SourceItemId = sourceItemId, GrantReason = "item", UpdatedAtMs = nowMs };
    }
}

/// <summary>Installed expert systems in PostgreSQL (one row per character and type, in the skills schema).</summary>
public sealed class PgExpertSystemStore(NpgsqlDataSource source, string schema = PgSkillStore.DefaultSchema)
{
    private readonly string _schema = schema;

    public async Task<IReadOnlyList<ExpertSystemEntry>> ForCharacterAsync(long characterId, CancellationToken cancellationToken = default)
    {
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        return await ReadAsync(connection, null, characterId, cancellationToken);
    }

    public Task<IReadOnlyList<ExpertSystemEntry>> ForCharacterAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long characterId, CancellationToken cancellationToken) =>
        ReadAsync(connection, transaction, characterId, cancellationToken);

    /// <summary>Installed expert systems whose time has run out by <paramref name="nowMs"/>, earliest first.</summary>
    public async Task<IReadOnlyList<ExpertSystemEntry>> ExpiredAsync(long nowMs, int limit, CancellationToken cancellationToken = default)
    {
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            $"SELECT character_id, type_id, installed_at_ms, expires_at_ms, source_item_id, grant_reason, updated_at_ms FROM {_schema}.character_expert_systems " +
            "WHERE expires_at_ms <= $1 ORDER BY expires_at_ms, character_id, type_id LIMIT $2", connection);
        command.Parameters.Add(new NpgsqlParameter { Value = nowMs });
        command.Parameters.Add(new NpgsqlParameter { Value = limit });
        return await ReadRowsAsync(command, cancellationToken);
    }

    public async Task UpsertAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, ExpertSystemEntry entry, CancellationToken cancellationToken = default)
    {
        await using var command = new NpgsqlCommand(
            $"INSERT INTO {_schema}.character_expert_systems (character_id, type_id, installed_at_ms, expires_at_ms, source_item_id, grant_reason, updated_at_ms) " +
            "VALUES ($1, $2, $3, $4, $5, $6, $7) ON CONFLICT (character_id, type_id) DO UPDATE SET installed_at_ms = EXCLUDED.installed_at_ms, " +
            "expires_at_ms = EXCLUDED.expires_at_ms, source_item_id = EXCLUDED.source_item_id, grant_reason = EXCLUDED.grant_reason, updated_at_ms = EXCLUDED.updated_at_ms",
            connection, transaction);
        command.Parameters.Add(new NpgsqlParameter { Value = entry.CharacterId });
        command.Parameters.Add(new NpgsqlParameter { Value = entry.TypeId });
        command.Parameters.Add(new NpgsqlParameter { Value = entry.InstalledAtMs });
        command.Parameters.Add(new NpgsqlParameter { Value = entry.ExpiresAtMs });
        command.Parameters.Add(new NpgsqlParameter { Value = (object?)entry.SourceItemId ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter { Value = entry.GrantReason });
        command.Parameters.Add(new NpgsqlParameter { Value = entry.UpdatedAtMs });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>Removes one installed expert system; true when there was one. With <paramref name="expiredBy"/>, only when its time has run out by then.</summary>
    public async Task<bool> RemoveAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long characterId, int typeId, long? expiredBy = null, CancellationToken cancellationToken = default)
    {
        var sql = $"DELETE FROM {_schema}.character_expert_systems WHERE character_id = $1 AND type_id = $2" +
            (expiredBy is null ? "" : " AND expires_at_ms <= $3");
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.Add(new NpgsqlParameter { Value = characterId });
        command.Parameters.Add(new NpgsqlParameter { Value = typeId });
        if (expiredBy is { } now)
        {
            command.Parameters.Add(new NpgsqlParameter { Value = now });
        }
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    private async Task<IReadOnlyList<ExpertSystemEntry>> ReadAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, long characterId, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"SELECT character_id, type_id, installed_at_ms, expires_at_ms, source_item_id, grant_reason, updated_at_ms FROM {_schema}.character_expert_systems " +
            "WHERE character_id = $1 ORDER BY type_id", connection, transaction);
        command.Parameters.Add(new NpgsqlParameter { Value = characterId });
        return await ReadRowsAsync(command, cancellationToken);
    }

    private static async Task<IReadOnlyList<ExpertSystemEntry>> ReadRowsAsync(NpgsqlCommand command, CancellationToken cancellationToken)
    {
        var entries = new List<ExpertSystemEntry>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            entries.Add(new ExpertSystemEntry(
                reader.GetInt64(0), reader.GetInt32(1), reader.GetInt64(2), reader.GetInt64(3),
                reader.IsDBNull(4) ? null : reader.GetInt64(4), reader.GetString(5), reader.GetInt64(6)));
        }
        return entries;
    }
}

/// <summary>The catalogue, the installed expert systems and the clock: what a character's skills are granted now.</summary>
public sealed class ExpertSystems(ExpertSystemCatalog catalog, PgExpertSystemStore store, Func<long> nowUnixMs)
{
    public ExpertSystemCatalog Catalog => catalog;

    public PgExpertSystemStore Store => store;

    public long NowMs => nowUnixMs();

    /// <summary>The skill levels the character's installed expert systems grant now.</summary>
    public async Task<Dictionary<int, int>> GrantsAsync(long characterId, CancellationToken cancellationToken = default) =>
        ExpertSystemRules.GrantsOf(catalog, await store.ForCharacterAsync(characterId, cancellationToken), nowUnixMs());
}
