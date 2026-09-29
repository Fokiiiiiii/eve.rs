using Eve.Data;
using Npgsql;

namespace Eve.Skills;

/// <summary>A skill a character has: its level and the points in it.</summary>
public sealed record CharacterSkill(int TypeId, int Level, long SkillPoints);

/// <summary>Skills in PostgreSQL, in the module's own schema (<c>skills</c> unless told otherwise).</summary>
public sealed class PgSkillStore
{
    public const string DefaultSchema = "skills";

    private readonly NpgsqlDataSource _source;
    private readonly string _schema;

    public PgSkillStore(NpgsqlDataSource source, string schema = DefaultSchema)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(schema, "^[a-z_][a-z0-9_]{0,62}$"))
        {
            throw new ArgumentException($"not a usable schema name: {schema}", nameof(schema));
        }
        _source = source;
        _schema = schema;
    }

    public static IReadOnlyList<Migration> MigrationSet() => Migrations.Embedded(typeof(PgSkillStore).Assembly, "Migrations");

    public static Task<IReadOnlyList<string>> MigrateAsync(NpgsqlDataSource source, string schema = DefaultSchema, CancellationToken cancellationToken = default) =>
        Migrations.ApplyAsync(source, schema, MigrationSet(), cancellationToken);

    /// <summary>Gives a character skills inside the caller's transaction (a skill it already has is replaced).</summary>
    public async Task GrantAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long characterId, IEnumerable<CharacterSkill> skills, CancellationToken cancellationToken = default)
    {
        foreach (var skill in skills)
        {
            await using var command = new NpgsqlCommand(
                $"INSERT INTO {_schema}.character_skills (character_id, type_id, level, skill_points) VALUES ($1, $2, $3, $4) " +
                "ON CONFLICT (character_id, type_id) DO UPDATE SET level = EXCLUDED.level, skill_points = EXCLUDED.skill_points",
                connection, transaction)
            {
                Parameters = { new() { Value = characterId }, new() { Value = skill.TypeId }, new() { Value = (short)skill.Level }, new() { Value = skill.SkillPoints } },
            };
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    /// <summary>The character's skills, by type.</summary>
    public async Task<IReadOnlyList<CharacterSkill>> ForCharacterAsync(long characterId, CancellationToken cancellationToken = default)
    {
        await using var command = _source.CreateCommand($"SELECT type_id, level, skill_points FROM {_schema}.character_skills WHERE character_id = $1 ORDER BY type_id");
        command.Parameters.Add(new NpgsqlParameter { Value = characterId });
        var list = new List<CharacterSkill>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            list.Add(new CharacterSkill(reader.GetInt32(0), reader.GetInt16(1), reader.GetInt64(2)));
        }
        return list;
    }

    /// <summary>The total skill points of each of the characters (0 for one with no skills).</summary>
    public async Task<IReadOnlyDictionary<long, long>> TotalPointsAsync(IEnumerable<long> characterIds, CancellationToken cancellationToken = default)
    {
        var ids = characterIds.ToArray();
        var totals = ids.ToDictionary(id => id, _ => 0L);
        if (ids.Length == 0)
        {
            return totals;
        }
        await using var command = _source.CreateCommand(
            $"SELECT character_id, sum(skill_points)::bigint FROM {_schema}.character_skills WHERE character_id = ANY($1) GROUP BY character_id");
        command.Parameters.Add(new NpgsqlParameter { Value = ids });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            totals[reader.GetInt64(0)] = reader.GetInt64(1);
        }
        return totals;
    }
}
