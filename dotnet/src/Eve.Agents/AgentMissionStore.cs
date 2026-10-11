using Eve.Data;
using Npgsql;

namespace Eve.Agents;

/// <summary>The client's mission states (eve-logic appConst.py:1617-1624). Allocated means no mission is held with the agent.</summary>
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
public sealed record AgentMissionContent(long MissionId, long NameId, string Title, string MissionTypeLabel, bool Important);

/// <summary>One character's mission with one agent: its state and the times the state started (null when it did not happen).</summary>
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
    DateTimeOffset? QuitAt);

/// <summary>A transition that committed: the states before and after, the action the client is told, and the reference it applied under.</summary>
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
    /// <summary>A stable name for the refusal: not_offered, offer_expired, already_offered, already_accepted, replay_wait, not_accepted.</summary>
    public string Reason { get; } = reason;
}

/// <summary>
/// Agent offers and mission states in PostgreSQL (migration 0001, the agents schema). Every change is one transaction: the row is
/// locked, the transition is checked against the state it leaves, the row and one journal row are written, and then the
/// change is announced through <see cref="Committed"/>. A transition names its reference; a reference that is already in the
/// journal applies nothing and returns null, so a replayed request changes nothing and announces nothing.
/// </summary>
public sealed class AgentMissionStore
{
    public const string DefaultSchema = "agents";

    /// <summary>The offer lasts seven days (EveJS missionRuntimeState.js:27, OFFER_EXPIRY_MS).</summary>
    public static readonly TimeSpan OfferLifetime = TimeSpan.FromDays(7);

    /// <summary>After a decline the same agent does not offer again for four hours (EveJS missionRuntimeState.js:26, REPLAY_DELAY_MS).</summary>
    public static readonly TimeSpan DeclineReplayDelay = TimeSpan.FromHours(4);

    private readonly NpgsqlDataSource _source;
    private readonly string _schema;

    /// <summary>The schema the tables live in.</summary>
    public string Schema => _schema;

    public AgentMissionStore(NpgsqlDataSource source, string schema = DefaultSchema)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(schema, "^[a-z_][a-z0-9_]{0,62}$"))
        {
            throw new ArgumentException($"not a usable schema name: {schema}", nameof(schema));
        }
        _source = source;
        _schema = schema;
    }

    /// <summary>A transition committed by one of this store's methods.</summary>
    public event Action<AgentMissionChange>? Committed;

    public static IReadOnlyList<Migration> MigrationSet() => Migrations.Embedded(typeof(AgentMissionStore).Assembly, "Migrations");

    public static Task<IReadOnlyList<string>> MigrateAsync(NpgsqlDataSource source, string schema = DefaultSchema, CancellationToken cancellationToken = default) =>
        Migrations.ApplyAsync(source, schema, MigrationSet(), cancellationToken);

    /// <summary>Every mission the character holds (offered, accepted, completed, declined, quit), in agent order.</summary>
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

    /// <summary>
    /// The missions the character has completed, with the time of each completion, from the journal (the transitions into
    /// <see cref="AgentMissionState.Completed"/>). The store keeps one row per agent, so the history is read here.
    /// </summary>
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

    /// <summary>The character's mission with one agent, or null when there is none.</summary>
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

    /// <summary>
    /// The agent offers <paramref name="content"/>. Refused while the character holds an offer or an accepted mission with this
    /// agent, and during the decline replay window.
    /// </summary>
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
                OfferedAt: now, ExpiresAt: now + OfferLifetime, AcceptedAt: null, CompletedAt: null, DeclineUntil: null, QuitAt: null);
        });

    /// <summary>The character accepts the offer. An offer past its expiry is refused; <see cref="ExpireOffersAsync"/> removes it.</summary>
    public Task<AgentMissionChange?> AcceptAsync(
        long characterId, long agentId, DateTimeOffset now, string reference, CancellationToken cancellationToken = default) =>
        ChangeAsync(characterId, agentId, reference, "accepted", now, cancellationToken, current =>
        {
            if (current?.State != AgentMissionState.Offered) throw Refused("not_offered", "there is no offer to accept");
            if (current.ExpiresAt is { } expires && expires <= now) throw Refused("offer_expired", "the offer has expired");
            return current with { State = AgentMissionState.Accepted, AcceptedAt = now };
        });

    /// <summary>The character declines the offer; the agent does not offer again for <see cref="DeclineReplayDelay"/>.</summary>
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

    /// <summary>The accepted mission fails (its dungeon failed, was abandoned or expired); the agent offers it again later and no reward is paid.</summary>
    public Task<AgentMissionChange?> FailAsync(
        long characterId, long agentId, DateTimeOffset now, string reference, CancellationToken cancellationToken = default) =>
        ChangeAsync(characterId, agentId, reference, "failed", now, cancellationToken, current =>
        {
            if (current?.State != AgentMissionState.Accepted) throw Refused("not_accepted", "there is no accepted mission to fail");
            return current with { State = AgentMissionState.Failed };
        });

    /// <summary>Every accepted mission of every character, for the mission runtime's sweep (one row per character and agent).</summary>
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

    /// <summary>The accepted mission is completed (the mission runtime calls this when its objectives are done).</summary>
    public Task<AgentMissionChange?> CompleteAsync(
        long characterId, long agentId, DateTimeOffset now, string reference, CancellationToken cancellationToken = default) =>
        ChangeAsync(characterId, agentId, reference, "completed", now, cancellationToken, current =>
        {
            if (current?.State != AgentMissionState.Accepted) throw Refused("not_accepted", "there is no accepted mission to complete");
            return current with { State = AgentMissionState.Completed, CompletedAt = now };
        });

    /// <summary>Removes every offer whose expiry has passed (state back to allocated); one journal row per offer, idempotent per expiry.</summary>
    public async Task<IReadOnlyList<AgentMissionChange>> ExpireOffersAsync(DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var find = new NpgsqlCommand(
            $"SELECT character_id, agent_id, expires_at FROM {_schema}.missions WHERE state = 1 AND expires_at <= $1 ORDER BY character_id, agent_id",
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
            var reference = $"offer-expired:{offer.CharacterId}:{offer.AgentId}:{offer.ExpiresAt.ToUnixTimeMilliseconds()}";
            var change = await ChangeAsync(offer.CharacterId, offer.AgentId, reference, "offer_expired", now, cancellationToken, current =>
            {
                if (current?.State != AgentMissionState.Offered || current.ExpiresAt is not { } expires || expires > now)
                {
                    throw Refused("not_offered", "the offer is no longer due");
                }
                return current with { State = AgentMissionState.Allocated, ExpiresAt = null };
            }).ConfigureAwait(false);
            if (change is not null) changes.Add(change);
        }
        return changes;
    }

    private static AgentMissionRefusedException Refused(string reason, string message) => new(reason, message);

    /// <summary>
    /// Locks the character's row with the agent, applies <paramref name="next"/> to the current record (null when there is none) and writes the
    /// row and the journal row. Returns null when the reference is already in the journal.
    /// </summary>
    private async Task<AgentMissionChange?> ChangeAsync(
        long characterId,
        long agentId,
        string reference,
        string action,
        DateTimeOffset at,
        CancellationToken cancellationToken,
        Func<AgentMissionRecord?, AgentMissionRecord> next)
    {
        if (characterId <= 0) throw new ArgumentOutOfRangeException(nameof(characterId), "the character id must be positive");
        if (agentId <= 0) throw new ArgumentOutOfRangeException(nameof(agentId), "the agent id must be positive");
        if (string.IsNullOrWhiteSpace(reference) || reference.Length > 128) throw new ArgumentException("a reference has 1 to 128 characters", nameof(reference));
        if (string.IsNullOrWhiteSpace(action) || action.Length > 32) throw new ArgumentException("an action has 1 to 32 characters", nameof(action));

        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

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
                await transaction.CommitAsync(cancellationToken);
                return null;
            }
        }

        var from = current?.State ?? AgentMissionState.Allocated;
        var updated = next(current);
        var to = updated.State;

        await using (var write = new NpgsqlCommand(
            $"""
            INSERT INTO {_schema}.missions (character_id, agent_id, state, mission_id, name_id, title, mission_type_label, important,
                offered_at, expires_at, accepted_at, completed_at, decline_until, quit_at)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13, $14)
            ON CONFLICT (character_id, agent_id) DO UPDATE SET state = EXCLUDED.state, mission_id = EXCLUDED.mission_id,
                name_id = EXCLUDED.name_id, title = EXCLUDED.title, mission_type_label = EXCLUDED.mission_type_label,
                important = EXCLUDED.important, offered_at = EXCLUDED.offered_at, expires_at = EXCLUDED.expires_at,
                accepted_at = EXCLUDED.accepted_at, completed_at = EXCLUDED.completed_at, decline_until = EXCLUDED.decline_until,
                quit_at = EXCLUDED.quit_at
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

        await transaction.CommitAsync(cancellationToken);
        var change = new AgentMissionChange(characterId, agentId, from, to, action, reference, updated.Content.MissionId);
        Committed?.Invoke(change);
        return change;
    }

    private const string Columns =
        "character_id, agent_id, state, mission_id, name_id, title, mission_type_label, important, offered_at, expires_at, accepted_at, completed_at, decline_until, quit_at";

    private static AgentMissionRecord ReadRecord(NpgsqlDataReader reader) => new(
        reader.GetInt64(0),
        reader.GetInt64(1),
        (AgentMissionState)reader.GetInt16(2),
        new AgentMissionContent(reader.GetInt64(3), reader.GetInt64(4), reader.GetString(5), reader.GetString(6), reader.GetBoolean(7)),
        reader.IsDBNull(8) ? null : reader.GetFieldValue<DateTimeOffset>(8),
        reader.IsDBNull(9) ? null : reader.GetFieldValue<DateTimeOffset>(9),
        reader.IsDBNull(10) ? null : reader.GetFieldValue<DateTimeOffset>(10),
        reader.IsDBNull(11) ? null : reader.GetFieldValue<DateTimeOffset>(11),
        reader.IsDBNull(12) ? null : reader.GetFieldValue<DateTimeOffset>(12),
        reader.IsDBNull(13) ? null : reader.GetFieldValue<DateTimeOffset>(13));
}
