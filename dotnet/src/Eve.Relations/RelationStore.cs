using System.Text.Json;
using Eve.Data;
using Eve.Events;
using Eve.Scheduler;
using Npgsql;

namespace Eve.Relations;

/// <summary>
/// The one store of "A stands in some relation to B": standings, wars, security status and affiliation are all relations of different kinds (<see cref="RelationKinds"/>), with
/// the same operations: set, adjust, end, read, list, history. A write is made in the caller's transaction, keeps the history, publishes
/// <c>relation.&lt;kind&gt;.changed</c> or <c>relation.&lt;kind&gt;.ended</c> through the event bus (so others react instead of being called), and a relation that ends by itself is ended by a
/// scheduler job. A feature that needs a new kind of relation defines the kind; it does not make a table, a timer or an event of its own. See <c>docs/FOUNDATIONS.md</c>.
/// </summary>
public sealed class RelationStore
{
    public const string DefaultSchema = "relations";
    public const string ExpireKind = "relations.expire";

    /// <summary>The object of a relation that is about the subject alone (security status).</summary>
    public const long World = 0;

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly NpgsqlDataSource _source;
    private readonly DurableScheduler _scheduler;
    private readonly EventBus _bus;
    private readonly string _schema;
    private readonly Dictionary<string, RelationKind> _kinds = new(StringComparer.Ordinal);

    public RelationStore(NpgsqlDataSource source, DurableScheduler scheduler, EventBus bus, string schema = DefaultSchema)
    {
        _source = source;
        _scheduler = scheduler;
        _bus = bus;
        _schema = Sql.CheckSchema(schema);
        foreach (var kind in RelationKinds.All)
        {
            _kinds[kind.Name] = kind;
        }
        scheduler.Register(ExpireKind, async (context, token) =>
        {
            var key = context.Payload<ExpiryKey>()!;
            await ExpireAsync(context.Connection, context.Transaction, key.Kind, key.Subject, key.Object, context.NowMs, token);
        });
    }

    private sealed record ExpiryKey(string Kind, long Subject, long Object);

    public static IReadOnlyList<Migration> MigrationSet() => Migrations.Embedded(typeof(RelationStore).Assembly, "Migrations");

    public static Task<IReadOnlyList<string>> MigrateAsync(NpgsqlDataSource source, string schema = DefaultSchema, CancellationToken cancellationToken = default) =>
        Migrations.ApplyAsync(source, schema, MigrationSet(), cancellationToken);

    private string Q(string text) => Sql.In(_schema, text);

    /// <summary>Adds a kind that <see cref="RelationKinds"/> does not have. Call it at start-up.</summary>
    public void Define(RelationKind kind)
    {
        if (!_kinds.TryAdd(kind.Name, kind))
        {
            throw new InvalidOperationException($"the relation kind '{kind.Name}' is already defined");
        }
    }

    private RelationKind Kind(string name) =>
        _kinds.TryGetValue(name, out var kind) ? kind : throw new ArgumentException($"unknown relation kind '{name}'; define it first", nameof(name));

    private static Relation ReadRelation(NpgsqlDataReader r) =>
        new(r.GetInt64(0), r.GetString(1), r.GetInt64(2), r.GetInt64(3), r.GetDouble(4), r.GetString(5), r.GetInt64(6), r.IsDBNull(7) ? null : r.GetInt64(7), r.GetInt64(8));

    private const string Columns = "id, kind, subject, object, value, attrs::text, since_ms, until_ms, updated_ms";

    // ---- writing -----------------------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Makes the relation hold with this value (clamped to the kind's range), in the caller's transaction. Returns what changed, or <c>null</c> when nothing did. For a one-per-subject
    /// kind the subject's relation to any other object ends first. <paramref name="attrs"/> replaces the attributes when given; <paramref name="untilMs"/> is when the relation
    /// ends by itself (null: it does not).
    /// </summary>
    public async Task<RelationChange?> SetAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string kind, long subject, long obj, double value,
        string? reason = null, object? attrs = null, long? untilMs = null, CancellationToken cancellationToken = default)
    {
        var rules = Kind(kind);
        if (double.IsNaN(value))
        {
            throw new ArgumentException("a relation's value is a number", nameof(value));
        }
        value = rules.Clamp(value);
        var now = _scheduler.Now;

        if (rules.Cardinality == Cardinality.OnePerSubject)
        {
            await using var command = Sql.Command(connection, transaction, Q("DELETE FROM {schema}.relations WHERE kind = $1 AND subject = $2 AND object <> $3 RETURNING id, object, value, until_ms"), kind, subject, obj);
            var replaced = new List<(long Id, long Object, double Value, bool Timed)>();
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            {
                while (await reader.ReadAsync(cancellationToken))
                {
                    replaced.Add((reader.GetInt64(0), reader.GetInt64(1), reader.GetDouble(2), !reader.IsDBNull(3)));
                }
            }
            foreach (var old in replaced)
            {
                await EndedAsync(connection, transaction, rules, old.Id, subject, old.Object, old.Value, "replaced", old.Timed, now, cancellationToken);
            }
        }

        double? before = null;
        long id;
        string? beforeAttrs = null;
        long? beforeUntil = null;
        await using (var command = Sql.Command(connection, transaction, Q("SELECT id, value, attrs::text, until_ms FROM {schema}.relations WHERE kind = $1 AND subject = $2 AND object = $3 FOR UPDATE"), kind, subject, obj))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            id = 0;
            if (await reader.ReadAsync(cancellationToken))
            {
                id = reader.GetInt64(0);
                before = reader.GetDouble(1);
                beforeAttrs = reader.GetString(2);
                beforeUntil = reader.IsDBNull(3) ? null : reader.GetInt64(3);
            }
        }
        var attrsJson = attrs is null ? null : JsonSerializer.Serialize(attrs, Json);
        if (before is null)
        {
            id = (long)(await Sql.ScalarAsync(connection, transaction, Q("""
                INSERT INTO {schema}.relations (kind, subject, object, value, attrs, since_ms, until_ms, updated_ms)
                VALUES ($1, $2, $3, $4, COALESCE($5::text::jsonb, '{}'), $6, $7, $6) RETURNING id
                """), cancellationToken, kind, subject, obj, value, attrsJson, now, untilMs))!;
        }
        else
        {
            var attrsChanged = attrsJson is not null && !JsonEquals(attrsJson, beforeAttrs!);
            if (before == value && !attrsChanged && beforeUntil == untilMs)
            {
                return null;
            }
            await Sql.ExecuteAsync(connection, transaction, Q("""
                UPDATE {schema}.relations SET value = $2, attrs = COALESCE($3::text::jsonb, attrs), until_ms = $4, updated_ms = $5 WHERE id = $1
                """), cancellationToken, id, value, attrsJson, untilMs, now);
        }

        if (untilMs is { } until)
        {
            await _scheduler.ScheduleAsync(connection, transaction, new JobSpec(ExpireKind, until) { Key = ExpiryJobKey(kind, subject, obj), Payload = new ExpiryKey(kind, subject, obj) }, cancellationToken);
        }
        else if (beforeUntil is not null)
        {
            await _scheduler.CancelAsync(connection, transaction, ExpireKind, ExpiryJobKey(kind, subject, obj), cancellationToken);
        }

        var change = new RelationChange(id, kind, subject, obj, before, value, reason, now);
        if (rules.KeepHistory && (before != value || before is null))
        {
            await Sql.ExecuteAsync(connection, transaction, Q("""
                INSERT INTO {schema}.relation_history (relation_id, kind, subject, object, old_value, new_value, reason, at_ms, attrs)
                VALUES ($1, $2, $3, $4, $5, $6, $7, $8, COALESCE($9::text::jsonb, '{}'))
                """), cancellationToken, id, kind, subject, obj, before, value, reason, now, attrsJson);
        }
        await _bus.PublishAsync(connection, transaction, $"relation.{kind}.changed", Payload(change), $"{kind}:{subject}", cancellationToken);
        return change;
    }

    /// <summary><see cref="SetAsync(NpgsqlConnection, NpgsqlTransaction, string, long, long, double, string?, object?, long?, CancellationToken)"/> in a transaction of its own (for a caller that has no write of its own to join).</summary>
    public async Task<RelationChange?> SetAsync(string kind, long subject, long obj, double value, string? reason = null, CancellationToken cancellationToken = default)
    {
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var change = await SetAsync(connection, transaction, kind, subject, obj, value, reason, cancellationToken: cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return change;
    }

    /// <summary>
    /// Adds <paramref name="delta"/> to the relation's value (clamped), starting from <paramref name="start"/> when there is none yet; the attributes and the end time are kept. Standing
    /// gains and losses and security status changes are this.
    /// </summary>
    public async Task<RelationChange?> AdjustAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string kind, long subject, long obj, double delta,
        string? reason = null, double start = 0, CancellationToken cancellationToken = default)
    {
        var current = await GetAsync(kind, subject, obj, connection, transaction, cancellationToken, forUpdate: true);
        return await SetAsync(connection, transaction, kind, subject, obj, (current?.Value ?? start) + delta, reason,
            attrs: null, untilMs: current?.UntilMs, cancellationToken);
    }

    /// <summary>Ends the relation (if it holds). Returns what ended, or <c>null</c>.</summary>
    public async Task<RelationChange?> EndAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string kind, long subject, long obj, string? reason = null, CancellationToken cancellationToken = default)
    {
        var rules = Kind(kind);
        await using var command = Sql.Command(connection, transaction,
            Q("DELETE FROM {schema}.relations WHERE kind = $1 AND subject = $2 AND object = $3 RETURNING id, value, until_ms"), kind, subject, obj);
        long id;
        double value;
        bool timed;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken))
            {
                return null;
            }
            id = reader.GetInt64(0);
            value = reader.GetDouble(1);
            timed = !reader.IsDBNull(2);
        }
        return await EndedAsync(connection, transaction, rules, id, subject, obj, value, reason, timed, _scheduler.Now, cancellationToken);
    }

    private async Task<RelationChange> EndedAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, RelationKind rules, long id, long subject, long obj, double value, string? reason, bool timed, long now, CancellationToken cancellationToken)
    {
        if (timed)
        {
            await _scheduler.CancelAsync(connection, transaction, ExpireKind, ExpiryJobKey(rules.Name, subject, obj), cancellationToken);
        }
        var change = new RelationChange(id, rules.Name, subject, obj, value, null, reason, now);
        if (rules.KeepHistory)
        {
            await Sql.ExecuteAsync(connection, transaction, Q("""
                INSERT INTO {schema}.relation_history (relation_id, kind, subject, object, old_value, new_value, reason, at_ms) VALUES ($1, $2, $3, $4, $5, NULL, $6, $7)
                """), cancellationToken, id, rules.Name, subject, obj, value, reason, now);
        }
        await _bus.PublishAsync(connection, transaction, $"relation.{rules.Name}.ended", Payload(change), $"{rules.Name}:{subject}", cancellationToken);
        return change;
    }

    private async Task ExpireAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string kind, long subject, long obj, long now, CancellationToken cancellationToken)
    {
        // a relation whose end was moved later is not due yet (the job for the new time is already scheduled)
        var due = await Sql.ScalarAsync(connection, transaction,
            Q("SELECT until_ms <= $4 FROM {schema}.relations WHERE kind = $1 AND subject = $2 AND object = $3 FOR UPDATE"), cancellationToken, kind, subject, obj, now);
        if (due is true)
        {
            await EndAsync(connection, transaction, kind, subject, obj, "expired", cancellationToken);
        }
    }

    private static string ExpiryJobKey(string kind, long subject, long obj) => $"{kind}|{subject}|{obj}";

    private static object Payload(RelationChange change) => new
    {
        change.RelationId, change.Kind, change.Subject, change.Object, change.OldValue, change.NewValue, change.Reason,
    };

    private static bool JsonEquals(string a, string b)
    {
        using var left = JsonDocument.Parse(a);
        using var right = JsonDocument.Parse(b);
        return JsonElement.DeepEquals(left.RootElement, right.RootElement);
    }

    // ---- reading -----------------------------------------------------------------------------------------------------------------------------------

    private async Task<T> WithConnection<T>(NpgsqlConnection? connection, Func<NpgsqlConnection, Task<T>> work, CancellationToken cancellationToken)
    {
        if (connection is not null)
        {
            return await work(connection);
        }
        await using var own = await _source.OpenConnectionAsync(cancellationToken);
        return await work(own);
    }

    /// <summary>The relation, if it holds. Pass the connection and transaction of a write to read its own changes.</summary>
    public Task<Relation?> GetAsync(
        string kind, long subject, long obj, NpgsqlConnection? connection = null, NpgsqlTransaction? transaction = null, CancellationToken cancellationToken = default, bool forUpdate = false) =>
        WithConnection(connection, async c =>
        {
            await using var command = Sql.Command(c, transaction,
                Q($"SELECT {Columns} FROM {{schema}}.relations WHERE kind = $1 AND subject = $2 AND object = $3{(forUpdate ? " FOR UPDATE" : "")}"), kind, subject, obj);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            return await reader.ReadAsync(cancellationToken) ? ReadRelation(reader) : null;
        }, cancellationToken);


    public Task<Relation?> GetByIdAsync(
        long id,
        NpgsqlConnection? connection = null,
        NpgsqlTransaction? transaction = null,
        CancellationToken cancellationToken = default) =>
        WithConnection(connection, async c =>
        {
            await using var command = Sql.Command(c, transaction,
                Q($"SELECT {Columns} FROM {{schema}}.relations WHERE id = $1"), id);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            return await reader.ReadAsync(cancellationToken) ? ReadRelation(reader) : null;
        }, cancellationToken);


    /// <summary>The value of the relation, or <paramref name="fallback"/> when it does not hold.</summary>
    public async Task<double> ValueAsync(string kind, long subject, long obj, double fallback = 0, NpgsqlConnection? connection = null, NpgsqlTransaction? transaction = null, CancellationToken cancellationToken = default) =>
        (await GetAsync(kind, subject, obj, connection, transaction, cancellationToken))?.Value ?? fallback;

    public async Task<bool> HoldsAsync(string kind, long subject, long obj, NpgsqlConnection? connection = null, NpgsqlTransaction? transaction = null, CancellationToken cancellationToken = default) =>
        await GetAsync(kind, subject, obj, connection, transaction, cancellationToken) is not null;

    /// <summary>The relations of a kind that start at the subject (a character's standings).</summary>
    public Task<IReadOnlyList<Relation>> FromAsync(string kind, long subject, NpgsqlConnection? connection = null, NpgsqlTransaction? transaction = null, CancellationToken cancellationToken = default) =>
        List(Q($"SELECT {Columns} FROM {{schema}}.relations WHERE kind = $1 AND subject = $2 ORDER BY object"), connection, transaction, cancellationToken, kind, subject);

    /// <summary>The relations of a kind that end at the object (the members of a corporation, who has a standing towards an agent).</summary>
    public Task<IReadOnlyList<Relation>> ToAsync(string kind, long obj, NpgsqlConnection? connection = null, NpgsqlTransaction? transaction = null, CancellationToken cancellationToken = default) =>
        List(Q($"SELECT {Columns} FROM {{schema}}.relations WHERE kind = $1 AND object = $2 ORDER BY subject"), connection, transaction, cancellationToken, kind, obj);

    /// <summary>The relations of a kind that involve any of the given ids on either side; the base of "is anyone of A at war with anyone of B".</summary>
    public Task<IReadOnlyList<Relation>> BetweenAsync(string kind, IEnumerable<long> sideA, IEnumerable<long> sideB, CancellationToken cancellationToken = default)
    {
        long[] a = [.. sideA];
        long[] b = [.. sideB];
        return List(Q($"SELECT {Columns} FROM {{schema}}.relations WHERE kind = $1 AND ((subject = ANY($2) AND object = ANY($3)) OR (subject = ANY($3) AND object = ANY($2))) ORDER BY id"),
            null, null, cancellationToken, kind, a, b);
    }


    public const int ReconciliationBatchSize = 256;

    public Task<IReadOnlyList<Relation>> ListAsync(string kind, CancellationToken cancellationToken = default)
    {
        _ = Kind(kind);
        return List(Q($"SELECT {Columns} FROM {{schema}}.relations WHERE kind = $1 ORDER BY subject, object"),
            null, null, cancellationToken, kind);
    }

    public async Task ReconcileOnePerSubjectBatchAsync(
        string kind, IReadOnlyDictionary<long, long?> desiredObjects, string? reason = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(desiredObjects);
        if (Kind(kind).Cardinality != Cardinality.OnePerSubject)
            throw new ArgumentException($"relation kind '{kind}' is not one-per-subject", nameof(kind));
        if (desiredObjects.Count > ReconciliationBatchSize)
            throw new ArgumentOutOfRangeException(nameof(desiredObjects),
                $"a reconciliation batch is limited to {ReconciliationBatchSize} subjects");

        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        foreach (var (subject, desiredObject) in desiredObjects.OrderBy(pair => pair.Key))
        {
            if (desiredObject is { } obj)
            {
                await SetAsync(connection, transaction, kind, subject, obj, 1, reason,
                    cancellationToken: cancellationToken);
                continue;
            }

            foreach (var current in await FromAsync(kind, subject, connection, transaction, cancellationToken))
                await EndAsync(connection, transaction, kind, subject, current.Object, reason, cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    private Task<IReadOnlyList<Relation>> List(string text, NpgsqlConnection? connection, NpgsqlTransaction? transaction, CancellationToken cancellationToken, params object?[] values) =>
        WithConnection(connection, async c =>
        {
            await using var command = Sql.Command(c, transaction, text, values);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var list = new List<Relation>();
            while (await reader.ReadAsync(cancellationToken))
            {
                list.Add(ReadRelation(reader));
            }
            return (IReadOnlyList<Relation>)list;
        }, cancellationToken);

    /// <summary>The changes of the relations of a kind that start at the subject (towards <paramref name="obj"/> when given), newest first.</summary>
    public async Task<IReadOnlyList<RelationHistoryEntry>> HistoryAsync(string kind, long subject, long? obj = null, int limit = 100, CancellationToken cancellationToken = default)
    {
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var command = Sql.Command(connection, null, Q("""
            SELECT id, kind, subject, object, old_value, new_value, reason, at_ms, attrs FROM {schema}.relation_history
            WHERE kind = $1 AND subject = $2 AND ($3::bigint IS NULL OR object = $3) ORDER BY id DESC LIMIT $4
            """), kind, subject, obj, limit);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var list = new List<RelationHistoryEntry>();
        while (await reader.ReadAsync(cancellationToken))
        {
            JsonElement? attrs = null;
            if (!reader.IsDBNull(8))
            {
                using var document = JsonDocument.Parse(reader.GetString(8));
                attrs = document.RootElement.Clone();
            }
            list.Add(new RelationHistoryEntry(reader.GetInt64(0), reader.GetString(1), reader.GetInt64(2), reader.GetInt64(3),
                reader.IsDBNull(4) ? null : reader.GetDouble(4), reader.IsDBNull(5) ? null : reader.GetDouble(5),
                reader.IsDBNull(6) ? null : reader.GetString(6), reader.GetInt64(7))
            {
                Attrs = attrs,
            });
        }
        return list;
    }

    // ---- affiliation and effective values -------------------------------------------------------------------------------------------------------------

    private async Task<long?> OneObjectAsync(string kind, long subject, NpgsqlConnection? connection, NpgsqlTransaction? transaction, CancellationToken cancellationToken) =>
        (await FromAsync(kind, subject, connection, transaction, cancellationToken)).Select(r => (long?)r.Object).FirstOrDefault();

    /// <summary>Where a character belongs.</summary>
    public async Task<Affiliation> AffiliationOfAsync(long character, NpgsqlConnection? connection = null, NpgsqlTransaction? transaction = null, CancellationToken cancellationToken = default)
    {
        var corporation = await OneObjectAsync(RelationKinds.CorporationMember.Name, character, connection, transaction, cancellationToken);
        var alliance = corporation is { } c ? await OneObjectAsync(RelationKinds.AllianceMember.Name, c, connection, transaction, cancellationToken) : null;
        var faction = await OneObjectAsync(RelationKinds.FactionMember.Name, character, connection, transaction, cancellationToken)
            ?? (corporation is { } cc ? await OneObjectAsync(RelationKinds.FactionMember.Name, cc, connection, transaction, cancellationToken) : null)
            ?? (alliance is { } a ? await OneObjectAsync(RelationKinds.FactionMember.Name, a, connection, transaction, cancellationToken) : null);
        return new Affiliation(corporation, alliance, faction);
    }

    /// <summary>The ids a character acts as, narrowest first: the character, its corporation, its alliance, its faction. This is the chain standings and wars are resolved along.</summary>
    public async Task<IReadOnlyList<long>> ChainOfAsync(long character, CancellationToken cancellationToken = default)
    {
        var affiliation = await AffiliationOfAsync(character, cancellationToken: cancellationToken);
        return [.. new long?[] { character, affiliation.Corporation, affiliation.Alliance, affiliation.Faction }.Where(id => id is not null).Select(id => id!.Value).Distinct()];
    }

    /// <summary>The best (highest) value of the kind from any of <paramref name="from"/> to any of <paramref name="to"/>, or <c>null</c> when none holds. How to combine values is the caller's rule; this is the common one.</summary>
    public async Task<double?> BestAsync(string kind, IEnumerable<long> from, IEnumerable<long> to, CancellationToken cancellationToken = default)
    {
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        var best = await Sql.ScalarAsync(connection, null,
            Q("SELECT max(value) FROM {schema}.relations WHERE kind = $1 AND subject = ANY($2) AND object = ANY($3)"), cancellationToken, kind, from.ToArray(), to.ToArray());
        return best is double value ? value : null;
    }

    /// <summary>Whether a war holds between any of the two sides (either as aggressor).</summary>
    public async Task<bool> AtWarAsync(IEnumerable<long> sideA, IEnumerable<long> sideB, CancellationToken cancellationToken = default) =>
        (await BetweenAsync(RelationKinds.War.Name, sideA, sideB, cancellationToken)).Count > 0;
}
