using Eve.Data;
using Eve.Events;
using Eve.Scheduler;
using Npgsql;

namespace Eve.Agents;

public enum AgentMissionState
{
    Allocated = 0,
    Offered = 1,
    Accepted = 2,
    Failed = 3,
    Completed = 4,
    Declined = 5,
    Quit = 6,
    CantReplay = 7,
}

/// <summary>What a mission is, as the offer names it. The title is the text the client shows when there is no name id.</summary>
public sealed record AgentMissionContent(
    long MissionId, long NameId, string Title, string MissionTypeLabel, bool Important, long? ExpirationMinutes = null);

/// <summary>The durable deadline for one offered or accepted mission.</summary>
public sealed record AgentMissionExpiry(long CharacterId, long AgentId, long ExpiresAtMs);

public sealed record AgentMissionRecord(
    long CharacterId,
    long AgentId,
    AgentMissionState State,
    AgentMissionContent Content,
    DateTimeOffset? OfferedAt,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? AcceptedAt,
    DateTimeOffset? CompletedAt,
    DateTimeOffset? DeclineUntil,
    DateTimeOffset? QuitAt,
    bool ObjectiveCompleted = false);

/// <summary>A mission state transition and its identifying reference.</summary>
public sealed record AgentMissionChange(
    long CharacterId,
    long AgentId,
    AgentMissionState From,
    AgentMissionState To,
    string Action,
    string Reference,
    long MissionId);

/// <summary>A transition the state does not allow (for example an accept with no offer). Nothing is changed.</summary>
public sealed class AgentMissionRefusedException(string reason, string message) : Exception(message)
{
    /// <summary>A refused mission transition with a stable reason string.</summary>
    public string Reason { get; } = reason;
}

/// <summary>
/// Each transition locks the mission row and writes its state and journal entry in one transaction. Reusing a journal reference
/// does not add a second transition. Methods that own the transaction announce after commit; caller-transaction methods return
/// a change for the caller to announce after committing.
/// </summary>
public sealed class AgentMissionStore
{
    public const string DefaultSchema = "agents";
    public const string ExpiryJobKind = "agents.mission.expire";
    public const string AcceptedTopic = "agents.mission.accepted";
    public static readonly TimeSpan DefaultExpiry = TimeSpan.FromDays(7);

    public static readonly TimeSpan DeclineReplayDelay = TimeSpan.FromHours(4);

    private readonly NpgsqlDataSource _source;
    private readonly string _schema;
    private readonly DurableScheduler? _scheduler;
    private readonly EventBus? _events;

    /// <summary>The schema the tables live in.</summary>
    public string Schema => _schema;

    public AgentMissionStore(NpgsqlDataSource source, string schema = DefaultSchema, DurableScheduler? scheduler = null, EventBus? events = null)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(schema, "^[a-z_][a-z0-9_]{0,62}$"))
        {
            throw new ArgumentException($"not a usable schema name: {schema}", nameof(schema));
        }
        _source = source;
        _schema = schema;
        _scheduler = scheduler;
        _events = events;
    }

    /// <summary>A transition committed by one of this store's methods.</summary>
    public event Action<AgentMissionChange>? Committed;

    public static IReadOnlyList<Migration> MigrationSet() => Migrations.Embedded(typeof(AgentMissionStore).Assembly, "Migrations");

    public static Task<IReadOnlyList<string>> MigrateAsync(NpgsqlDataSource source, string schema = DefaultSchema, CancellationToken cancellationToken = default) =>
        Migrations.ApplyAsync(source, schema, MigrationSet(), cancellationToken);

    public async Task<IReadOnlyList<AgentMissionRecord>> ReadAsync(long characterId, CancellationToken cancellationToken = default)
    {
        if (characterId <= 0) return [];
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            $"SELECT {Columns} FROM {_schema}.missions WHERE character_id = $1 ORDER BY agent_id", connection)
        {
            Parameters = { new() { Value = characterId } },
        };
        var rows = new List<AgentMissionRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(ReadRecord(reader));
        }
        return rows;
    }

    /// <summary>The character's completed mission transitions, with their times, in journal order.</summary>
    public Task<IReadOnlyList<(long MissionId, DateTimeOffset CompletedAt)>> ReadCompletedAsync(long characterId, CancellationToken cancellationToken = default) =>
        ReadJournalAsync(characterId, [(int)AgentMissionState.Completed], cancellationToken);

    /// <summary>The missions the character failed or quit (the transitions into Failed or Quit), with their times, from the journal.</summary>
    public Task<IReadOnlyList<(long MissionId, DateTimeOffset At)>> ReadFailedAsync(long characterId, CancellationToken cancellationToken = default) =>
        ReadJournalAsync(characterId, [(int)AgentMissionState.Failed, (int)AgentMissionState.Quit], cancellationToken);

    private async Task<IReadOnlyList<(long MissionId, DateTimeOffset At)>> ReadJournalAsync(long characterId, int[] toStates, CancellationToken cancellationToken)
    {
        if (characterId <= 0) return [];
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            $"SELECT mission_id, COALESCE(transition_at, created_at) FROM {_schema}.journal WHERE character_id = $1 AND to_state = ANY($2) ORDER BY id", connection)
        {
            Parameters = { new() { Value = characterId }, new() { Value = toStates } },
        };
        var rows = new List<(long MissionId, DateTimeOffset At)>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add((reader.GetInt64(0), reader.GetFieldValue<DateTimeOffset>(1)));
        }
        return rows;
    }

    public async Task<AgentMissionRecord?> ReadAsync(long characterId, long agentId, CancellationToken cancellationToken = default)
    {
        if (characterId <= 0 || agentId <= 0) return null;
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            $"SELECT {Columns} FROM {_schema}.missions WHERE character_id = $1 AND agent_id = $2", connection)
        {
            Parameters = { new() { Value = characterId }, new() { Value = agentId } },
        };
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadRecord(reader) : null;
    }

    /// <summary>Offers the given mission content to the character through this agent.</summary>
    public Task<AgentMissionChange?> OfferAsync(
        long characterId, long agentId, AgentMissionContent content, DateTimeOffset now, string reference, CancellationToken cancellationToken = default) =>
        ChangeAsync(characterId, agentId, reference, "offered", now, cancellationToken, current =>
        {
            if (current?.State == AgentMissionState.Offered) throw Refused("already_offered", "the agent has already offered a mission");
            if (current?.State == AgentMissionState.Accepted) throw Refused("already_accepted", "the mission is already accepted");
            if (current?.DeclineUntil is { } until && until > now)
            {
                throw Refused("replay_wait", "the agent does not offer again yet");
            }
            return new AgentMissionRecord(
                characterId, agentId, AgentMissionState.Offered, content,
                OfferedAt: now, ExpiresAt: ExpiryAt(content, now), AcceptedAt: null, CompletedAt: null, DeclineUntil: null, QuitAt: null);
        });

    /// <summary>The character accepts the offer. An offer past its expiry is refused; <see cref="ExpireOffersAsync"/> removes it.</summary>
    public Task<AgentMissionChange?> AcceptAsync(
        long characterId, long agentId, DateTimeOffset now, string reference, CancellationToken cancellationToken = default) =>
        ChangeAsync(characterId, agentId, reference, "accepted", now, cancellationToken, current =>
        {
            if (current?.State != AgentMissionState.Offered) throw Refused("not_offered", "there is no offer to accept");
            if (current.ExpiresAt is { } expires && expires <= now) throw Refused("offer_expired", "the offer has expired");
            return current with { State = AgentMissionState.Accepted, AcceptedAt = now, ExpiresAt = ExpiryAt(current.Content, now) };
        });

    public Task<AgentMissionChange?> DeclineAsync(
        long characterId, long agentId, DateTimeOffset now, string reference, CancellationToken cancellationToken = default) =>
        ChangeAsync(characterId, agentId, reference, "offer_declined", now, cancellationToken, current =>
        {
            if (current?.State != AgentMissionState.Offered) throw Refused("not_offered", "there is no offer to decline");
            return current with { State = AgentMissionState.Declined, ExpiresAt = null, DeclineUntil = now + DeclineReplayDelay };
        });

    /// <summary>The character quits the accepted mission.</summary>
    public Task<AgentMissionChange?> QuitAsync(
        long characterId, long agentId, DateTimeOffset now, string reference, CancellationToken cancellationToken = default) =>
        ChangeAsync(characterId, agentId, reference, "quit", now, cancellationToken, current =>
        {
            if (current?.State != AgentMissionState.Accepted) throw Refused("not_accepted", "there is no accepted mission to quit");
            return current with { State = AgentMissionState.Quit, QuitAt = now };
        });

    /// <summary>Resets a non-allocated mission to Allocated; refuses when the agent has no mission to reset.</summary>
    public Task<AgentMissionChange?> ResetAsync(long characterId, long agentId, DateTimeOffset now, string reference, CancellationToken cancellationToken = default) =>
        ChangeAsync(characterId, agentId, reference, "reset", now, cancellationToken, current =>
        {
            if (current is null) throw Refused("not_held", "there is no mission with this agent to reset");
            return current with { State = AgentMissionState.Allocated, OfferedAt = null, ExpiresAt = null, AcceptedAt = null, CompletedAt = null, DeclineUntil = null, QuitAt = null };
        });

    public Task<AgentMissionChange?> FailAsync(
        long characterId, long agentId, DateTimeOffset now, string reference, CancellationToken cancellationToken = default) =>
        ChangeAsync(characterId, agentId, reference, "failed", now, cancellationToken, current =>
        {
            if (current?.State != AgentMissionState.Accepted) throw Refused("not_accepted", "there is no accepted mission to fail");
            return current with { State = AgentMissionState.Failed };
        });

    /// <summary>Resets the current offer to Allocated. Repeated requests produce no additional transition or notice.</summary>
    public async Task<AgentMissionChange?> RemoveOfferAsync(
        long characterId, long agentId, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        var current = await ReadAsync(characterId, agentId, cancellationToken);
        if (current is null || current.State == AgentMissionState.Allocated) return null;
        if (current.State != AgentMissionState.Offered)
            throw Refused("not_offered", "there is no offer to remove");

        var offeredAt = current.OfferedAt?.ToUnixTimeMilliseconds() ?? 0;
        var reference = $"offer-removed:{characterId}:{agentId}:{current.Content.MissionId}:{offeredAt}";
        return await ChangeAsync(characterId, agentId, reference, "offer_removed", now, cancellationToken, record =>
        {
            if (record?.State != AgentMissionState.Offered)
                throw Refused("not_offered", "there is no offer to remove");
            return record with
            {
                State = AgentMissionState.Allocated,
                OfferedAt = null,
                ExpiresAt = null,
                AcceptedAt = null,
                CompletedAt = null,
                DeclineUntil = null,
                QuitAt = null,
            };
        });
    }

    public async Task<IReadOnlyList<AgentMissionRecord>> ReadAcceptedAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            $"SELECT {Columns} FROM {_schema}.missions WHERE state = {(int)AgentMissionState.Accepted} ORDER BY character_id, agent_id", connection);
        var rows = new List<AgentMissionRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(ReadRecord(reader));
        }
        return rows;
    }

    /// <summary>Active mission deadlines, used at startup to restore jobs for records written before deadline jobs were enabled.</summary>
    public async Task<IReadOnlyList<AgentMissionRecord>> ReadActiveAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            $"SELECT {Columns} FROM {_schema}.missions WHERE state IN (1, 2) ORDER BY character_id, agent_id", connection);
        var rows = new List<AgentMissionRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) rows.Add(ReadRecord(reader));
        return rows;
    }

    /// <summary>Restores a deadline job and backfills the expiry for active records written before deadlines were persisted.</summary>
    public async Task EnsureExpiryScheduledAsync(
        AgentMissionRecord snapshot,
        long? catalogExpirationMinutes,
        CancellationToken cancellationToken = default)
    {
        if (_scheduler is null) return;
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        AgentMissionRecord? current;
        await using (var read = new NpgsqlCommand(
            $"SELECT {Columns} FROM {_schema}.missions WHERE character_id = $1 AND agent_id = $2 FOR UPDATE", connection, transaction)
        {
            Parameters = { new() { Value = snapshot.CharacterId }, new() { Value = snapshot.AgentId } },
        })
        await using (var reader = await read.ExecuteReaderAsync(cancellationToken))
        {
            current = await reader.ReadAsync(cancellationToken) ? ReadRecord(reader) : null;
        }
        if (current?.State is not (AgentMissionState.Offered or AgentMissionState.Accepted))
        {
            await transaction.CommitAsync(cancellationToken);
            return;
        }

        var expirationMinutes = current.Content.ExpirationMinutes ?? catalogExpirationMinutes;
        var baseAt = current.State == AgentMissionState.Accepted
            ? current.AcceptedAt ?? current.OfferedAt
            : current.OfferedAt;
        var expiresAt = current.ExpiresAt ?? ExpiryAt(expirationMinutes, baseAt);
        if (current.Content.ExpirationMinutes != expirationMinutes || current.ExpiresAt != expiresAt)
        {
            await using var update = new NpgsqlCommand($"""
                UPDATE {_schema}.missions SET expiration_minutes = $3, expires_at = $4
                WHERE character_id = $1 AND agent_id = $2
                """, connection, transaction)
            {
                Parameters =
                {
                    new() { Value = current.CharacterId }, new() { Value = current.AgentId },
                    new() { Value = (object?)expirationMinutes ?? DBNull.Value }, new() { Value = (object?)expiresAt ?? DBNull.Value },
                },
            };
            await update.ExecuteNonQueryAsync(cancellationToken);
        }

        await PersistExpiryJobAsync(connection, transaction, current with
        {
            Content = current.Content with { ExpirationMinutes = expirationMinutes },
            ExpiresAt = expiresAt,
        }, cancellationToken, OnDuplicate.Keep);
        await transaction.CommitAsync(cancellationToken);
        _scheduler.Wake();
    }

    /// <summary>Completes an accepted mission in a store-owned transaction. Use <see cref="CompleteInAsync"/> to join another transaction.</summary>
    public Task<AgentMissionChange?> CompleteAsync(
        long characterId, long agentId, DateTimeOffset now, string reference, CancellationToken cancellationToken = default) =>
        ChangeAsync(characterId, agentId, reference, "completed", now, cancellationToken, current =>
        {
            if (current?.State != AgentMissionState.Accepted) throw Refused("not_accepted", "there is no accepted mission to complete");
            return current with { State = AgentMissionState.Completed, CompletedAt = now };
        });

    /// <summary>Persists completed objectives without paying or completing the mission; the pilot must turn it in.</summary>
    public async Task<AgentMissionChange?> MarkObjectiveCompletedAsync(
        long characterId, long agentId, DateTimeOffset now, string reference, CancellationToken cancellationToken = default)
    {
        Validate(characterId, agentId, reference, "objective_completed");
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        AgentMissionRecord? current;
        await using (var read = new NpgsqlCommand(
            $"SELECT {Columns} FROM {_schema}.missions WHERE character_id = $1 AND agent_id = $2 FOR UPDATE", connection, transaction)
        {
            Parameters = { new() { Value = characterId }, new() { Value = agentId } },
        })
        await using (var reader = await read.ExecuteReaderAsync(cancellationToken))
        {
            current = await reader.ReadAsync(cancellationToken) ? ReadRecord(reader) : null;
        }
        if (current?.State != AgentMissionState.Accepted || current.ObjectiveCompleted)
        {
            await transaction.RollbackAsync(cancellationToken);
            return null;
        }

        await using (var write = new NpgsqlCommand(
            $"UPDATE {_schema}.missions SET objective_completed = true WHERE character_id = $1 AND agent_id = $2", connection, transaction)
        {
            Parameters = { new() { Value = characterId }, new() { Value = agentId } },
        })
        {
            await write.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var journal = new NpgsqlCommand(
            $"INSERT INTO {_schema}.journal (character_id, agent_id, mission_id, from_state, to_state, action, reference, transition_at) VALUES ($1, $2, $3, $4, $4, $5, $6, $7)",
            connection, transaction)
        {
            Parameters =
            {
                new() { Value = characterId }, new() { Value = agentId }, new() { Value = current.Content.MissionId },
                new() { Value = (short)AgentMissionState.Accepted }, new() { Value = "objective_completed" },
                new() { Value = reference }, new() { Value = now },
            },
        })
        {
            await journal.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        var change = new AgentMissionChange(characterId, agentId, AgentMissionState.Accepted,
            AgentMissionState.Accepted, "modified", reference, current.Content.MissionId);
        Committed?.Invoke(change);
        return change;
    }

    /// <summary>Expires every overdue offer or accepted mission (state back to allocated), idempotent per deadline.</summary>
    public async Task<IReadOnlyList<AgentMissionChange>> ExpireDueAsync(DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var find = new NpgsqlCommand(
            $"SELECT character_id, agent_id, expires_at FROM {_schema}.missions WHERE state IN (1, 2) AND expires_at <= $1 ORDER BY character_id, agent_id",
            connection)
        {
            Parameters = { new() { Value = now } },
        };
        var due = new List<(long CharacterId, long AgentId, DateTimeOffset ExpiresAt)>();
        await using (var reader = await find.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                due.Add((reader.GetInt64(0), reader.GetInt64(1), reader.GetFieldValue<DateTimeOffset>(2)));
            }
        }
        var changes = new List<AgentMissionChange>();
        foreach (var offer in due)
        {
            var reference = ExpiryReference(offer.CharacterId, offer.AgentId, offer.ExpiresAt);
            var change = await ChangeAsync(offer.CharacterId, offer.AgentId, reference, "offer_expired", now, cancellationToken, current =>
            {
                if (current?.State is not (AgentMissionState.Offered or AgentMissionState.Accepted) ||
                    current.ExpiresAt is not { } expires || expires > now)
                {
                    throw Refused("not_due", "the mission deadline is no longer due");
                }
                return current with { State = AgentMissionState.Allocated, ExpiresAt = null };
            }).ConfigureAwait(false);
            if (change is not null) changes.Add(change);
        }
        return changes;
    }

    /// <summary>Expires a mission in the scheduler job's transaction after checking its state and exact deadline under a row lock.</summary>
    public async Task<AgentMissionChange?> ExpireInAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        AgentMissionExpiry expiry,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        if (expiry.CharacterId <= 0 || expiry.AgentId <= 0 || expiry.ExpiresAtMs <= 0) return null;
        AgentMissionRecord? current;
        await using (var read = new NpgsqlCommand(
            $"SELECT {Columns} FROM {_schema}.missions WHERE character_id = $1 AND agent_id = $2 FOR UPDATE", connection, transaction)
        {
            Parameters = { new() { Value = expiry.CharacterId }, new() { Value = expiry.AgentId } },
        })
        await using (var reader = await read.ExecuteReaderAsync(cancellationToken))
        {
            current = await reader.ReadAsync(cancellationToken) ? ReadRecord(reader) : null;
        }
        var expectedExpiry = DateTimeOffset.FromUnixTimeMilliseconds(expiry.ExpiresAtMs);
        if (current?.State is not (AgentMissionState.Offered or AgentMissionState.Accepted) ||
            current.ExpiresAt?.ToUnixTimeMilliseconds() != expiry.ExpiresAtMs || expectedExpiry > now)
            return null;

        return await ChangeInAsync(connection, transaction, expiry.CharacterId, expiry.AgentId,
            ExpiryReference(expiry.CharacterId, expiry.AgentId, expectedExpiry), "offer_expired", now, cancellationToken,
            record => record is { State: AgentMissionState.Offered or AgentMissionState.Accepted } &&
                      record.ExpiresAt?.ToUnixTimeMilliseconds() == expiry.ExpiresAtMs && expectedExpiry <= now
                ? record with { State = AgentMissionState.Allocated, ExpiresAt = null }
                : throw Refused("not_due", "the mission deadline is no longer due"));
    }

    private static AgentMissionRefusedException Refused(string reason, string message) => new(reason, message);

    /// <summary>Applies a transition and writes its journal entry in a store-owned transaction; returns null when the reference is already in the journal.</summary>
    private async Task<AgentMissionChange?> ChangeAsync(
        long characterId,
        long agentId,
        string reference,
        string action,
        DateTimeOffset at,
        CancellationToken cancellationToken,
        Func<AgentMissionRecord?, AgentMissionRecord> next)
    {
        Validate(characterId, agentId, reference, action);
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var change = await ChangeInAsync(connection, transaction, characterId, agentId, reference, action, at, cancellationToken, next);
        await transaction.CommitAsync(cancellationToken);
        if (change is not null)
        {
            _scheduler?.Wake();
            if (action == "accepted") _events?.Wake();
            Committed?.Invoke(change);
        }
        return change;
    }

    private static void Validate(long characterId, long agentId, string reference, string action)
    {
        if (characterId <= 0) throw new ArgumentOutOfRangeException(nameof(characterId), "the character id must be positive");
        if (agentId <= 0) throw new ArgumentOutOfRangeException(nameof(agentId), "the agent id must be positive");
        if (string.IsNullOrWhiteSpace(reference) || reference.Length > 128) throw new ArgumentException("a reference has 1 to 128 characters", nameof(reference));
        if (string.IsNullOrWhiteSpace(action) || action.Length > 32) throw new ArgumentException("an action has 1 to 32 characters", nameof(action));
    }

    /// <summary>
    /// The change inside the caller's transaction (no commit and no notice): null when the reference is already in the journal. The caller commits,
    /// and then raises the notice with <see cref="Announce"/> for a change it made.
    /// </summary>
    private async Task<AgentMissionChange?> ChangeInAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long characterId,
        long agentId,
        string reference,
        string action,
        DateTimeOffset at,
        CancellationToken cancellationToken,
        Func<AgentMissionRecord?, AgentMissionRecord> next)
    {
        await using (var create = new NpgsqlCommand(
            $"INSERT INTO {_schema}.missions (character_id, agent_id) VALUES ($1, $2) ON CONFLICT DO NOTHING", connection, transaction)
        {
            Parameters = { new() { Value = characterId }, new() { Value = agentId } },
        })
        {
            await create.ExecuteNonQueryAsync(cancellationToken);
        }

        AgentMissionRecord? current;
        await using (var read = new NpgsqlCommand(
            $"SELECT {Columns} FROM {_schema}.missions WHERE character_id = $1 AND agent_id = $2 FOR UPDATE", connection, transaction)
        {
            Parameters = { new() { Value = characterId }, new() { Value = agentId } },
        })
        await using (var reader = await read.ExecuteReaderAsync(cancellationToken))
        {
            current = await reader.ReadAsync(cancellationToken) ? ReadRecord(reader) : null;
        }
        if (current is not null && current.State == AgentMissionState.Allocated) current = null;

        await using (var seen = new NpgsqlCommand(
            $"SELECT 1 FROM {_schema}.journal WHERE reference = $1 AND character_id = $2 AND agent_id = $3", connection, transaction)
        {
            Parameters = { new() { Value = reference }, new() { Value = characterId }, new() { Value = agentId } },
        })
        {
            if (await seen.ExecuteScalarAsync(cancellationToken) is not null)
            {
                return null;
            }
        }

        var from = current?.State ?? AgentMissionState.Allocated;
        var updated = next(current);
        var to = updated.State;

        await using (var write = new NpgsqlCommand(
            $"""
            INSERT INTO {_schema}.missions (character_id, agent_id, state, mission_id, name_id, title, mission_type_label, important,
                offered_at, expires_at, accepted_at, completed_at, decline_until, quit_at, objective_completed, expiration_minutes)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13, $14, $15, $16)
            ON CONFLICT (character_id, agent_id) DO UPDATE SET state = EXCLUDED.state, mission_id = EXCLUDED.mission_id,
                name_id = EXCLUDED.name_id, title = EXCLUDED.title, mission_type_label = EXCLUDED.mission_type_label,
                important = EXCLUDED.important, offered_at = EXCLUDED.offered_at, expires_at = EXCLUDED.expires_at,
                accepted_at = EXCLUDED.accepted_at, completed_at = EXCLUDED.completed_at, decline_until = EXCLUDED.decline_until,
                quit_at = EXCLUDED.quit_at, objective_completed = EXCLUDED.objective_completed,
                expiration_minutes = EXCLUDED.expiration_minutes
            """,
            connection, transaction)
        {
            Parameters =
            {
                new() { Value = characterId }, new() { Value = agentId }, new() { Value = (short)to },
                new() { Value = updated.Content.MissionId }, new() { Value = updated.Content.NameId },
                new() { Value = updated.Content.Title }, new() { Value = updated.Content.MissionTypeLabel },
                new() { Value = updated.Content.Important },
                new() { Value = (object?)updated.OfferedAt ?? DBNull.Value },
                new() { Value = (object?)updated.ExpiresAt ?? DBNull.Value },
                new() { Value = (object?)updated.AcceptedAt ?? DBNull.Value },
                new() { Value = (object?)updated.CompletedAt ?? DBNull.Value },
                new() { Value = (object?)updated.DeclineUntil ?? DBNull.Value },
                new() { Value = (object?)updated.QuitAt ?? DBNull.Value },
                new() { Value = updated.ObjectiveCompleted },
                new() { Value = (object?)updated.Content.ExpirationMinutes ?? DBNull.Value },
            },
        })
        {
            await write.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var journal = new NpgsqlCommand(
            $"""
            INSERT INTO {_schema}.journal (character_id, agent_id, mission_id, from_state, to_state, action, reference, transition_at)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8)
            """,
            connection, transaction)
        {
            Parameters =
            {
                new() { Value = characterId }, new() { Value = agentId }, new() { Value = updated.Content.MissionId },
                new() { Value = (short)from }, new() { Value = (short)to }, new() { Value = action }, new() { Value = reference },
                new() { Value = at },
            },
        })
        {
            await journal.ExecuteNonQueryAsync(cancellationToken);
        }

        await PersistExpiryJobAsync(connection, transaction, updated, cancellationToken);

        var change = new AgentMissionChange(characterId, agentId, from, to, action, reference, updated.Content.MissionId);
        if (action == "accepted" && _events is not null)
            await _events.PublishAsync(connection, transaction, AcceptedTopic, change,
                $"{characterId.ToString(System.Globalization.CultureInfo.InvariantCulture)}:{agentId.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
                cancellationToken);
        return change;
    }

    private async Task PersistExpiryJobAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        AgentMissionRecord record,
        CancellationToken cancellationToken,
        OnDuplicate onDuplicate = OnDuplicate.Replace)
    {
        if (_scheduler is null) return;
        var key = ExpiryJobKey(record.CharacterId, record.AgentId);
        if ((record.State is AgentMissionState.Offered or AgentMissionState.Accepted) && record.ExpiresAt is { } expiresAt)
        {
            var expiresAtMs = expiresAt.ToUnixTimeMilliseconds();
            await _scheduler.ScheduleAsync(connection, transaction, new JobSpec(ExpiryJobKind, expiresAtMs)
            {
                Key = key,
                Payload = new AgentMissionExpiry(record.CharacterId, record.AgentId, expiresAtMs),
                OnDuplicate = onDuplicate,
            }, cancellationToken);
        }
        else
        {
            await _scheduler.CancelAsync(connection, transaction, ExpiryJobKind, key, cancellationToken);
        }
    }

    public static string ExpiryJobKey(long characterId, long agentId) => $"{characterId}:{agentId}";

    private static string ExpiryReference(long characterId, long agentId, DateTimeOffset expiresAt) =>
        $"mission-expired:{characterId}:{agentId}:{expiresAt.ToUnixTimeMilliseconds()}";

    private static DateTimeOffset? ExpiryAt(AgentMissionContent content, DateTimeOffset now) =>
        ExpiryAt(content.ExpirationMinutes, now);

    private static DateTimeOffset? ExpiryAt(long? expirationMinutes, DateTimeOffset? at) =>
        at is not null && (expirationMinutes ?? (long)DefaultExpiry.TotalMinutes) > 0
            ? at.Value.AddMinutes(expirationMinutes ?? DefaultExpiry.TotalMinutes)
            : null;

    /// <summary>
    /// Completes an accepted mission in the caller's transaction. Announce the returned change after commit; the same refusal as
    /// <see cref="CompleteAsync"/> is raised when the mission is not accepted.
    /// </summary>
    public async Task<AgentMissionChange?> CompleteInAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, long characterId, long agentId, DateTimeOffset now, string reference,
        CancellationToken cancellationToken = default)
    {
        Validate(characterId, agentId, reference, "completed");
        return await ChangeInAsync(connection, transaction, characterId, agentId, reference, "completed", now, cancellationToken, current =>
        {
            if (current?.State != AgentMissionState.Accepted) throw Refused("not_accepted", "there is no accepted mission to complete");
            return current with { State = AgentMissionState.Completed, CompletedAt = now };
        });
    }

    /// <summary>Tells the sessions of a change the caller committed (see <see cref="CompleteInAsync"/>).</summary>
    public void Announce(AgentMissionChange change) => Committed?.Invoke(change);

    private const string Columns =
        "character_id, agent_id, state, mission_id, name_id, title, mission_type_label, important, offered_at, expires_at, accepted_at, completed_at, decline_until, quit_at, objective_completed, expiration_minutes";

    private static AgentMissionRecord ReadRecord(NpgsqlDataReader reader) => new(
        reader.GetInt64(0),
        reader.GetInt64(1),
        (AgentMissionState)reader.GetInt16(2),
        new AgentMissionContent(reader.GetInt64(3), reader.GetInt64(4), reader.GetString(5), reader.GetString(6), reader.GetBoolean(7),
            reader.IsDBNull(15) ? null : reader.GetInt64(15)),
        reader.IsDBNull(8) ? null : reader.GetFieldValue<DateTimeOffset>(8),
        reader.IsDBNull(9) ? null : reader.GetFieldValue<DateTimeOffset>(9),
        reader.IsDBNull(10) ? null : reader.GetFieldValue<DateTimeOffset>(10),
        reader.IsDBNull(11) ? null : reader.GetFieldValue<DateTimeOffset>(11),
        reader.IsDBNull(12) ? null : reader.GetFieldValue<DateTimeOffset>(12),
        reader.IsDBNull(13) ? null : reader.GetFieldValue<DateTimeOffset>(13),
        reader.GetBoolean(14));
}
