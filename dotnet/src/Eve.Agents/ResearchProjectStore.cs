using Eve.Data;
using Npgsql;

namespace Eve.Agents;

/// <summary>One research project: a character researching a skill with a research agent, at a rate of points per day.</summary>
public sealed record ResearchProject(
    long CharacterId,
    long AgentId,
    long SkillTypeId,
    DateTimeOffset StartedAt,
    double PointsPerDay,
    double RemainderPoints);

/// <summary>A start the rules refuse (the character has too many projects, or already researches with this agent). Nothing is written.</summary>
public sealed class ResearchRefusedException(string reason, string message) : Exception(message)
{
    /// <summary>A stable name for the refusal: too_many_projects or already_researching.</summary>
    public string Reason { get; } = reason;
}

/// <summary>
/// Research projects in PostgreSQL (migration 0003, the agents schema). A start counts the character's projects under a
/// per-character advisory lock, so two starts cannot both pass the limit. Points are not ticked: the total is read as the
/// banked remainder plus the rate times the days since the start (<see cref="AccruedPoints"/>), so downtime neither loses nor
/// invents points.
/// </summary>
public sealed class ResearchProjectStore
{
    public const string DefaultSchema = "agents";

    private static readonly double MillisecondsPerDay = TimeSpan.FromDays(1).TotalMilliseconds;

    private readonly NpgsqlDataSource _source;
    private readonly string _schema;

    public ResearchProjectStore(NpgsqlDataSource source, string schema = DefaultSchema)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(schema, "^[a-z_][a-z0-9_]{0,62}$"))
        {
            throw new ArgumentException($"not a usable schema name: {schema}", nameof(schema));
        }
        _source = source;
        _schema = schema;
    }

    public static IReadOnlyList<Migration> MigrationSet() => Migrations.Embedded(typeof(ResearchProjectStore).Assembly, "Migrations");

    public static Task<IReadOnlyList<string>> MigrateAsync(NpgsqlDataSource source, string schema = DefaultSchema, CancellationToken cancellationToken = default) =>
        Migrations.ApplyAsync(source, schema, MigrationSet(), cancellationToken);

    /// <summary>The points a project holds at a moment: the banked remainder and the rate since the start.</summary>
    public static double AccruedPoints(ResearchProject project, DateTimeOffset now) =>
        project.RemainderPoints + project.PointsPerDay * (now - project.StartedAt).TotalMilliseconds / MillisecondsPerDay;

    public async Task<ResearchProject?> ReadAsync(long characterId, long agentId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            $"SELECT character_id, agent_id, skill_type_id, started_at_ms, points_per_day, remainder_points FROM {_schema}.research_projects WHERE character_id = $1 AND agent_id = $2",
            connection)
        {
            Parameters = { new() { Value = characterId }, new() { Value = agentId } },
        };
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
    }

    /// <summary>The character's projects, in agent order.</summary>
    public async Task<IReadOnlyList<ResearchProject>> ReadAllAsync(long characterId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            $"SELECT character_id, agent_id, skill_type_id, started_at_ms, points_per_day, remainder_points FROM {_schema}.research_projects WHERE character_id = $1 ORDER BY agent_id",
            connection)
        {
            Parameters = { new() { Value = characterId } },
        };
        var rows = new List<ResearchProject>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) rows.Add(Read(reader));
        return rows;
    }

    /// <summary>Starts a project. Refused, with nothing written, when the character already has <paramref name="maxConcurrent"/> projects or already researches with this agent.</summary>
    public async Task<ResearchProject> StartAsync(ResearchProject project, int maxConcurrent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (project.CharacterId <= 0 || project.AgentId <= 0 || project.SkillTypeId <= 0) throw new ArgumentOutOfRangeException(nameof(project));
        if (!(project.PointsPerDay >= 0) || !(project.RemainderPoints >= 0)) throw new ArgumentOutOfRangeException(nameof(project));

        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockAsync(connection, transaction, project.CharacterId, cancellationToken);
        await using (var existing = new NpgsqlCommand(
            $"SELECT count(*), count(*) FILTER (WHERE agent_id = $2) FROM {_schema}.research_projects WHERE character_id = $1", connection, transaction))
        {
            existing.Parameters.Add(new NpgsqlParameter { Value = project.CharacterId });
            existing.Parameters.Add(new NpgsqlParameter { Value = project.AgentId });
            await using var reader = await existing.ExecuteReaderAsync(cancellationToken);
            await reader.ReadAsync(cancellationToken);
            if (reader.GetInt64(1) > 0) throw new ResearchRefusedException("already_researching", "The character already researches with this agent.");
            if (reader.GetInt64(0) >= maxConcurrent) throw new ResearchRefusedException("too_many_projects", "The character has no free research project slot.");
        }
        await using (var insert = new NpgsqlCommand($"""
            INSERT INTO {_schema}.research_projects (character_id, agent_id, skill_type_id, started_at_ms, points_per_day, remainder_points)
            VALUES ($1, $2, $3, $4, $5, $6)
            """, connection, transaction))
        {
            insert.Parameters.Add(new NpgsqlParameter { Value = project.CharacterId });
            insert.Parameters.Add(new NpgsqlParameter { Value = project.AgentId });
            insert.Parameters.Add(new NpgsqlParameter { Value = project.SkillTypeId });
            insert.Parameters.Add(new NpgsqlParameter { Value = project.StartedAt.ToUnixTimeMilliseconds() });
            insert.Parameters.Add(new NpgsqlParameter { Value = project.PointsPerDay });
            insert.Parameters.Add(new NpgsqlParameter { Value = project.RemainderPoints });
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return project;
    }

    /// <summary>Takes a character's research lock for the caller's transaction: starts, cancels and datacore purchases of one character run in turn.</summary>
    public async Task LockAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long characterId, CancellationToken cancellationToken = default)
    {
        await using var locked = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended($1, 0))", connection, transaction);
        locked.Parameters.Add(new NpgsqlParameter { Value = "research:" + _schema + ":" + characterId });
        await locked.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>A project read on the caller's transaction (after <see cref="LockAsync"/>).</summary>
    public async Task<ResearchProject?> ReadAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long characterId, long agentId, CancellationToken cancellationToken = default)
    {
        await using var command = new NpgsqlCommand(
            $"SELECT character_id, agent_id, skill_type_id, started_at_ms, points_per_day, remainder_points FROM {_schema}.research_projects WHERE character_id = $1 AND agent_id = $2",
            connection, transaction)
        {
            Parameters = { new() { Value = characterId }, new() { Value = agentId } },
        };
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
    }

    /// <summary>Re-anchors a project on the caller's transaction: its banked remainder and its start (the points it holds now).</summary>
    public async Task RebaseAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, ResearchProject project, CancellationToken cancellationToken = default)
    {
        await using var command = new NpgsqlCommand(
            $"UPDATE {_schema}.research_projects SET started_at_ms = $3, remainder_points = $4 WHERE character_id = $1 AND agent_id = $2",
            connection, transaction)
        {
            Parameters =
            {
                new() { Value = project.CharacterId }, new() { Value = project.AgentId },
                new() { Value = project.StartedAt.ToUnixTimeMilliseconds() }, new() { Value = project.RemainderPoints },
            },
        };
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1) throw new InvalidOperationException("the research project is missing");
    }

    /// <summary>Cancels a project: the row is deleted and its points are lost. False when the character has no such project.</summary>
    public async Task<bool> CancelAsync(long characterId, long agentId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            $"DELETE FROM {_schema}.research_projects WHERE character_id = $1 AND agent_id = $2", connection)
        {
            Parameters = { new() { Value = characterId }, new() { Value = agentId } },
        };
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    private static ResearchProject Read(NpgsqlDataReader reader) => new(
        reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2),
        DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(3)), reader.GetDouble(4), reader.GetDouble(5));
}
