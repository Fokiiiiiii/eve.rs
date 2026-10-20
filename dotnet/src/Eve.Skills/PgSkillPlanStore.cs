using System.Text.RegularExpressions;
using Npgsql;

namespace Eve.Skills;

public enum SkillPlanError
{
    NotFound,
    InvalidName,
    NameTooLong,
    DescriptionTooLong,
    TooManyPlans,
    TooManySkills,
    TooManyMilestones,
    InvalidMilestone,
}

public sealed class SkillPlanException(SkillPlanError error, string message) : Exception(message)
{
    public SkillPlanError Error { get; } = error;
}

/// <summary>One skill requirement of a plan: a skill type and the level it must reach (0 to 5).</summary>
public sealed record SkillPlanRequirement(int SkillTypeId, int Level);

public sealed record SkillPlanRecord(Guid PlanId, string Name, string Description, IReadOnlyList<SkillPlanRequirement> Requirements);

/// <summary>A milestone of a plan: a type to train to, or a skill at a level (exactly one of the two).</summary>
public sealed record SkillPlanMilestone(Guid MilestoneId, Guid PlanId, string Description, int? TrainToTypeId, int? SkillTypeId, int Level);

/// <summary>
/// The personal skill plans of each character (tables <c>skill_plans</c>, <c>skill_plan_requirements</c>, <c>skill_plan_milestones</c>
/// and <c>skill_plan_active</c>, migration 0009). Plans do not train skills. The limits are the reference's: 10 plans, 150 skill entries,
/// 5 milestones, names of 50 characters and descriptions of 1000, levels from 0 to 5. Every write of a character takes its lock.
/// </summary>
public sealed partial class PgSkillPlanStore(NpgsqlDataSource source, string schema)
{
    public const int MaxPlans = 10;
    public const int MaxSkills = 150;
    public const int MaxMilestones = 5;
    public const int MaxNameLength = 50;
    public const int MaxDescriptionLength = 1000;

    private static readonly Regex SchemaName = SchemaPattern();

    private readonly NpgsqlDataSource _source = source;
    private readonly string _schema = SchemaName.IsMatch(schema) ? schema : throw new ArgumentException("the skill plan schema name is not valid", nameof(schema));

    /// <summary>The plan ids of the character, oldest first.</summary>
    public async Task<IReadOnlyList<Guid>> ListAsync(long characterId, CancellationToken cancellationToken = default)
    {
        await using var command = _source.CreateCommand($"SELECT plan_id FROM {_schema}.skill_plans WHERE character_id = $1 ORDER BY created_seq");
        command.Parameters.Add(new NpgsqlParameter { Value = characterId });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var ids = new List<Guid>();
        while (await reader.ReadAsync(cancellationToken))
            ids.Add(reader.GetGuid(0));
        return ids;
    }

    /// <summary>The character's plan, or null when it has no such plan.</summary>
    public async Task<SkillPlanRecord?> GetAsync(long characterId, Guid planId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        return await ReadPlanAsync(connection, null, characterId, planId, cancellationToken);
    }

    /// <summary>Creates a plan for the character and returns its id.</summary>
    public async Task<Guid> CreateAsync(long characterId, string name, string description, IReadOnlyList<SkillPlanRequirement> requirements, CancellationToken cancellationToken = default)
    {
        var cleanName = CleanName(name);
        var cleanDescription = CleanDescription(description);
        CheckRequirements(requirements);
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockCharacterAsync(connection, transaction, characterId, cancellationToken);
        if (await Count(connection, transaction, "skill_plans", "character_id = $1", characterId, cancellationToken) >= MaxPlans)
            throw new SkillPlanException(SkillPlanError.TooManyPlans, $"Character already has {MaxPlans} personal skill plans");
        var planId = Guid.NewGuid();
        await using (var insert = new NpgsqlCommand($"INSERT INTO {_schema}.skill_plans (plan_id, character_id, name, description) VALUES ($1, $2, $3, $4)", connection, transaction))
        {
            insert.Parameters.Add(new NpgsqlParameter { Value = planId });
            insert.Parameters.Add(new NpgsqlParameter { Value = characterId });
            insert.Parameters.Add(new NpgsqlParameter { Value = cleanName });
            insert.Parameters.Add(new NpgsqlParameter { Value = cleanDescription });
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
        await WriteRequirementsAsync(connection, transaction, planId, requirements, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return planId;
    }

    /// <summary>Changes the given parts of a plan the character owns; the requirements, when given, replace the old ones.</summary>
    public async Task UpdateAsync(long characterId, Guid planId, string? name = null, string? description = null,
        IReadOnlyList<SkillPlanRequirement>? requirements = null, CancellationToken cancellationToken = default)
    {
        var cleanName = name is null ? null : CleanName(name);
        var cleanDescription = description is null ? null : CleanDescription(description);
        if (requirements is not null) CheckRequirements(requirements);
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockCharacterAsync(connection, transaction, characterId, cancellationToken);
        if (!await OwnsPlanAsync(connection, transaction, characterId, planId, cancellationToken))
            throw new SkillPlanException(SkillPlanError.NotFound, "Skill plan not found");
        if (cleanName is not null)
            await ExecuteAsync(connection, transaction, $"UPDATE {_schema}.skill_plans SET name = $2 WHERE plan_id = $1", [planId, cleanName], cancellationToken);
        if (cleanDescription is not null)
            await ExecuteAsync(connection, transaction, $"UPDATE {_schema}.skill_plans SET description = $2 WHERE plan_id = $1", [planId, cleanDescription], cancellationToken);
        if (requirements is not null)
        {
            await ExecuteAsync(connection, transaction, $"DELETE FROM {_schema}.skill_plan_requirements WHERE plan_id = $1", [planId], cancellationToken);
            await WriteRequirementsAsync(connection, transaction, planId, requirements, cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>Deletes a plan the character owns; its milestones go with it, and it is no longer the active plan.</summary>
    public async Task DeleteAsync(long characterId, Guid planId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockCharacterAsync(connection, transaction, characterId, cancellationToken);
        if (!await OwnsPlanAsync(connection, transaction, characterId, planId, cancellationToken))
            throw new SkillPlanException(SkillPlanError.NotFound, "Skill plan not found");
        await ExecuteAsync(connection, transaction, $"DELETE FROM {_schema}.skill_plans WHERE plan_id = $1", [planId], cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>The active plan of the character (Guid.Empty when none is active).</summary>
    public async Task<Guid> ActiveAsync(long characterId, CancellationToken cancellationToken = default)
    {
        await using var command = _source.CreateCommand($"SELECT plan_id FROM {_schema}.skill_plan_active WHERE character_id = $1");
        command.Parameters.Add(new NpgsqlParameter { Value = characterId });
        return await command.ExecuteScalarAsync(cancellationToken) is Guid id ? id : Guid.Empty;
    }

    /// <summary>Makes a plan the character owns the active one; Guid.Empty clears it.</summary>
    public async Task SetActiveAsync(long characterId, Guid planId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockCharacterAsync(connection, transaction, characterId, cancellationToken);
        Guid? active = null;
        if (planId != Guid.Empty)
        {
            if (!await OwnsPlanAsync(connection, transaction, characterId, planId, cancellationToken))
                throw new SkillPlanException(SkillPlanError.NotFound, "Skill plan not found");
            active = planId;
        }
        await ExecuteAsync(connection, transaction,
            $"INSERT INTO {_schema}.skill_plan_active (character_id, plan_id) VALUES ($1, $2) ON CONFLICT (character_id) DO UPDATE SET plan_id = EXCLUDED.plan_id",
            [characterId, active], cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>The milestones of a plan the character owns, oldest first.</summary>
    public async Task<IReadOnlyList<SkillPlanMilestone>> MilestonesAsync(long characterId, Guid planId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        if (!await OwnsPlanAsync(connection, transaction, characterId, planId, cancellationToken))
            throw new SkillPlanException(SkillPlanError.NotFound, "Skill plan not found");
        await using var command = new NpgsqlCommand(
            $"SELECT milestone_id, plan_id, description, train_to_type_id, skill_type_id, level FROM {_schema}.skill_plan_milestones WHERE plan_id = $1 ORDER BY created_seq",
            connection, transaction) { Parameters = { new NpgsqlParameter { Value = planId } } };
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var milestones = new List<SkillPlanMilestone>();
        while (await reader.ReadAsync(cancellationToken))
            milestones.Add(new SkillPlanMilestone(reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetInt32(3), reader.IsDBNull(4) ? null : reader.GetInt32(4), reader.IsDBNull(5) ? 0 : reader.GetInt16(5)));
        return milestones;
    }

    /// <summary>Adds a milestone to a plan the character owns: a type to train to, or a skill at a level.</summary>
    public async Task<Guid> CreateMilestoneAsync(long characterId, Guid planId, string description, int? trainToTypeId, int? skillTypeId, int level,
        CancellationToken cancellationToken = default)
    {
        if ((trainToTypeId is null) == (skillTypeId is null))
            throw new SkillPlanException(SkillPlanError.InvalidMilestone, "Milestone data is invalid");
        var cleanDescription = CleanDescription(description);
        var cleanLevel = Math.Clamp(level, 0, 5);
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockCharacterAsync(connection, transaction, characterId, cancellationToken);
        if (!await OwnsPlanAsync(connection, transaction, characterId, planId, cancellationToken))
            throw new SkillPlanException(SkillPlanError.NotFound, "Skill plan not found");
        if (await Count(connection, transaction, "skill_plan_milestones", "plan_id = $1", planId, cancellationToken) >= MaxMilestones)
            throw new SkillPlanException(SkillPlanError.TooManyMilestones, $"Skill plan exceeds {MaxMilestones} milestones");
        var milestoneId = Guid.NewGuid();
        await ExecuteAsync(connection, transaction,
            $"INSERT INTO {_schema}.skill_plan_milestones (milestone_id, plan_id, description, train_to_type_id, skill_type_id, level) VALUES ($1, $2, $3, $4, $5, $6)",
            [milestoneId, planId, cleanDescription, trainToTypeId, skillTypeId, skillTypeId is null ? 0 : cleanLevel], cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return milestoneId;
    }

    /// <summary>Deletes a milestone whose plan the character owns.</summary>
    public async Task DeleteMilestoneAsync(long characterId, Guid milestoneId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockCharacterAsync(connection, transaction, characterId, cancellationToken);
        var deleted = await ExecuteAsync(connection, transaction,
            $"DELETE FROM {_schema}.skill_plan_milestones m USING {_schema}.skill_plans p WHERE m.plan_id = p.plan_id AND m.milestone_id = $1 AND p.character_id = $2",
            [milestoneId, characterId], cancellationToken);
        if (deleted == 0)
            throw new SkillPlanException(SkillPlanError.NotFound, "Skill plan milestone not found");
        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>Changes the description of a milestone whose plan the character owns.</summary>
    public async Task SetMilestoneDescriptionAsync(long characterId, Guid milestoneId, string description, CancellationToken cancellationToken = default)
    {
        var cleanDescription = CleanDescription(description);
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockCharacterAsync(connection, transaction, characterId, cancellationToken);
        var updated = await ExecuteAsync(connection, transaction,
            $"UPDATE {_schema}.skill_plan_milestones m SET description = $3 FROM {_schema}.skill_plans p WHERE m.plan_id = p.plan_id AND m.milestone_id = $1 AND p.character_id = $2",
            [milestoneId, characterId, cleanDescription], cancellationToken);
        if (updated == 0)
            throw new SkillPlanException(SkillPlanError.NotFound, "Skill plan milestone not found");
        await transaction.CommitAsync(cancellationToken);
    }

    private static string CleanName(string name)
    {
        var clean = name.Trim();
        if (clean.Length == 0)
            throw new SkillPlanException(SkillPlanError.InvalidName, "Skill plan name cannot be blank");
        if (clean.Length > MaxNameLength)
            throw new SkillPlanException(SkillPlanError.NameTooLong, $"Skill plan name is too long (max {MaxNameLength})");
        return clean;
    }

    private static string CleanDescription(string description)
    {
        var clean = description.Trim();
        if (clean.Length > MaxDescriptionLength)
            throw new SkillPlanException(SkillPlanError.DescriptionTooLong, $"Skill plan description is too long (max {MaxDescriptionLength})");
        return clean;
    }

    private static void CheckRequirements(IReadOnlyList<SkillPlanRequirement> requirements)
    {
        if (requirements.Count > MaxSkills)
            throw new SkillPlanException(SkillPlanError.TooManySkills, $"Skill plan exceeds {MaxSkills} skill entries");
    }

    private async Task WriteRequirementsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid planId, IReadOnlyList<SkillPlanRequirement> requirements, CancellationToken cancellationToken)
    {
        for (var position = 0; position < requirements.Count; position++)
        {
            var requirement = requirements[position];
            await ExecuteAsync(connection, transaction,
                $"INSERT INTO {_schema}.skill_plan_requirements (plan_id, position, skill_type_id, level) VALUES ($1, $2, $3, $4)",
                [planId, position, requirement.SkillTypeId, (short)Math.Clamp(requirement.Level, 0, 5)], cancellationToken);
        }
    }

    private async Task<SkillPlanRecord?> ReadPlanAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, long characterId, Guid planId, CancellationToken cancellationToken)
    {
        string name, description;
        await using (var head = new NpgsqlCommand($"SELECT name, description FROM {_schema}.skill_plans WHERE plan_id = $1 AND character_id = $2", connection, transaction)
        {
            Parameters = { new NpgsqlParameter { Value = planId }, new NpgsqlParameter { Value = characterId } },
        })
        await using (var reader = await head.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken))
                return null;
            name = reader.GetString(0);
            description = reader.GetString(1);
        }
        var requirements = new List<SkillPlanRequirement>();
        await using (var rows = new NpgsqlCommand($"SELECT skill_type_id, level FROM {_schema}.skill_plan_requirements WHERE plan_id = $1 ORDER BY position", connection, transaction)
        {
            Parameters = { new NpgsqlParameter { Value = planId } },
        })
        await using (var reader = await rows.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
                requirements.Add(new SkillPlanRequirement(reader.GetInt32(0), reader.GetInt16(1)));
        }
        return new SkillPlanRecord(planId, name, description, requirements);
    }

    private async Task<bool> OwnsPlanAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long characterId, Guid planId, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"SELECT 1 FROM {_schema}.skill_plans WHERE plan_id = $1 AND character_id = $2", connection, transaction)
        {
            Parameters = { new NpgsqlParameter { Value = planId }, new NpgsqlParameter { Value = characterId } },
        };
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    /// <summary>Serializes the writes of one character (the plan limits are counted under this lock).</summary>
    private static async Task LockCharacterAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long characterId, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended('skill_plans:' || $1::text, 0))", connection, transaction)
        {
            Parameters = { new NpgsqlParameter { Value = characterId } },
        };
        await command.ExecuteScalarAsync(cancellationToken);
    }

    private async Task<long> Count(NpgsqlConnection connection, NpgsqlTransaction transaction, string table, string where, object value, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"SELECT count(*) FROM {_schema}.{table} WHERE {where}", connection, transaction)
        {
            Parameters = { new NpgsqlParameter { Value = value } },
        };
        return (long)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    private async Task<int> ExecuteAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, object?[] values, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        foreach (var value in values)
            command.Parameters.Add(new NpgsqlParameter { Value = value ?? DBNull.Value });
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    [GeneratedRegex("^[a-z_][a-z0-9_]{0,62}$")]
    private static partial Regex SchemaPattern();
}
