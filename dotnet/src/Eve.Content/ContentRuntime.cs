using System.Text.Json;
using Eve.Data;
using Eve.Events;
using Eve.Scheduler;
using Npgsql;

namespace Eve.Content;

/// <summary>
/// The shared runtime for missions, anomalies, incursions and everything else that has objectives. A feature defines content (a <see cref="ContentDefinition"/>: objectives, a
/// duration, actions), starts instances of it, and lets domain events advance the objectives; the runtime keeps the state in PostgreSQL, ends the content when its objectives are
/// met or its time is up (a scheduler job), and announces the end as an event (<c>content.&lt;kind&gt;.completed|failed|expired|abandoned</c>) so the feature's own subscribers pay
/// </summary>
public sealed class ContentRuntime
{
    public const string DefaultSchema = "content";
    public const string SubscriberName = "content.runtime";
    public const string ExpireKind = "content.expire";

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly NpgsqlDataSource _source;
    private readonly DurableScheduler _scheduler;
    private readonly EventBus _bus;
    private readonly string _schema;
    private readonly Dictionary<string, ObjectiveType> _types = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ContentAction> _actions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ContentDefinition> _definitions = new(StringComparer.Ordinal);

    public ContentRuntime(NpgsqlDataSource source, DurableScheduler scheduler, EventBus bus, string schema = DefaultSchema)
    {
        _source = source;
        _scheduler = scheduler;
        _bus = bus;
        _schema = Sql.CheckSchema(schema);
        foreach (var type in new ObjectiveType[] { new CounterObjective(), new ForbidObjective(), new ManualObjective(), new TimerObjective() })
        {
            _types[type.Name] = type;
        }
        RegisterAction("emit", async (context, args, token) =>
        {
            var topic = args.GetProperty("topic").GetString()!;
            object? payload = args.TryGetProperty("payload", out var p) ? p.Clone() : null;
            await _bus.PublishAsync(context.Connection, context.Transaction, topic, payload, $"{context.Instance.Kind}:{context.Instance.Id}", token);
        });
        scheduler.Register(ExpireKind, async (context, token) =>
            await ExpireAsync(context.Connection, context.Transaction, context.Payload<InstanceKey>()!.InstanceId, token));
        scheduler.Register(TimerObjective.JobKind, async (context, token) =>
        {
            var key = context.Payload<TimerObjective.TimerKey>()!;
            await ProgressAsync(context.Connection, context.Transaction, key.InstanceId, key.Index, double.MaxValue, token);
        });
    }

    private sealed record InstanceKey(long InstanceId);

    public static IReadOnlyList<Migration> MigrationSet() => Migrations.Embedded(typeof(ContentRuntime).Assembly, "Migrations");

    public static Task<IReadOnlyList<string>> MigrateAsync(NpgsqlDataSource source, string schema = DefaultSchema, CancellationToken cancellationToken = default) =>
        Migrations.ApplyAsync(source, schema, MigrationSet(), cancellationToken);

    private string Q(string text) => Sql.In(_schema, text);

    // ---- registering -------------------------------------------------------------------------------------------------------------------------------

    public void RegisterObjectiveType(ObjectiveType type)
    {
        if (!_types.TryAdd(type.Name, type))
        {
            throw new InvalidOperationException($"an objective type '{type.Name}' is already registered");
        }
    }

    public void RegisterAction(string name, ContentAction action)
    {
        if (!_actions.TryAdd(name, action))
        {
            throw new InvalidOperationException($"a content action '{name}' is already registered");
        }
    }

    /// <summary>Adds a definition. Define all of them before <see cref="AttachAsync"/>.</summary>
    public void Define(ContentDefinition definition)
    {
        if (!_definitions.TryAdd(definition.Id, definition))
        {
            throw new InvalidOperationException($"content '{definition.Id}' is already defined");
        }
    }

    /// <summary>
    /// Checks the definitions (every objective type and action must be registered) and subscribes to the events they listen to, together with the events of the content that is
    /// running already. Call it once at start-up, after the definitions and types are registered and before <see cref="EventBus.StartAsync"/>.
    /// </summary>
    public async Task AttachAsync(CancellationToken cancellationToken = default)
    {
        var topics = new HashSet<string>(StringComparer.Ordinal);
        foreach (var definition in _definitions.Values)
        {
            foreach (var objective in definition.Objectives)
            {
                if (!_types.TryGetValue(objective.Type, out var type))
                {
                    throw new InvalidOperationException($"content '{definition.Id}': unknown objective type '{objective.Type}'");
                }
                if (type.TopicOf(ToElement(objective.Params)) is { } topic)
                {
                    topics.Add(topic);
                }
            }
            foreach (var action in definition.OnComplete.Concat(definition.OnFail))
            {
                if (!_actions.ContainsKey(action.Name))
                {
                    throw new InvalidOperationException($"content '{definition.Id}': unknown action '{action.Name}'");
                }
            }
        }
        await using (var connection = await _source.OpenConnectionAsync(cancellationToken))
        await using (var command = Sql.Command(connection, null, Q("SELECT DISTINCT topic FROM {schema}.objectives WHERE topic IS NOT NULL AND state IN ('waiting', 'active')")))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                topics.Add(reader.GetString(0));
            }
        }
        if (topics.Count > 0)
        {
            _bus.Subscribe(SubscriberName, topics, OnEventAsync);
        }
    }

    private static JsonElement ToElement(object? value) => JsonDocument.Parse(JsonSerializer.Serialize(value ?? new { }, Json)).RootElement.Clone();

    // ---- starting ----------------------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The newest instance of a kind with this unique key, in any state (a finished one too), or null. <see cref="StartAsync"/> makes a new instance when the one with the
    /// key has finished, so a feature that starts one once a period asks first.
    /// </summary>
    public async Task<long?> FindAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string kind, string uniqueKey, CancellationToken cancellationToken = default) =>
        (long?)await Sql.ScalarAsync(connection, transaction, Q("SELECT id FROM {schema}.instances WHERE kind = $1 AND unique_key = $2 ORDER BY id DESC LIMIT 1"),
            cancellationToken, kind, uniqueKey);

    /// <summary>Loads an instance under the caller's transaction and locks it for a domain update.</summary>
    public async Task<ContentInstance?> GetForUpdateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long instanceId,
        CancellationToken cancellationToken = default) =>
        (await LoadAsync(connection, transaction, instanceId, true, cancellationToken))?.Info;

    /// <summary>Loads an instance in the caller's transaction without locking or changing it.</summary>
    public async Task<ContentInstance?> GetInTransactionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long instanceId,
        CancellationToken cancellationToken = default) =>
        (await LoadAsync(connection, transaction, instanceId, false, cancellationToken))?.Info;

    /// <summary>
    /// Updates serialized domain data in the caller's transaction without changing lifecycle state or deadlines.
    /// This is used to persist participant settlement receipts that arrive after the run has expired or failed.
    /// </summary>
    public async Task<bool> UpdateDataAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long instanceId,
        object? data,
        CancellationToken cancellationToken = default)
    {
        var row = await LoadAsync(connection, transaction, instanceId, true, cancellationToken);
        if (row is null)
        {
            return false;
        }

        var json = JsonSerializer.Serialize(data ?? new { }, Json);
        return await Sql.ExecuteAsync(connection, transaction,
            Q("UPDATE {schema}.instances SET data = $2::text::jsonb WHERE id = $1"),
            cancellationToken, instanceId, json) > 0;
    }

    /// <summary>
    /// Replaces an active instance's data and expiry job atomically in the caller's transaction.
    /// A null expiry removes the deadline job.
    /// </summary>
    public async Task<bool> UpdateDataAndDeadlineAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long instanceId,
        object? data,
        long? expiresMs,
        CancellationToken cancellationToken = default)
    {
        var row = await LoadAsync(connection, transaction, instanceId, true, cancellationToken);
        if (row is null || row.Info.State != "active")
        {
            return false;
        }

        var json = JsonSerializer.Serialize(data ?? new { }, Json);
        var changed = await Sql.ExecuteAsync(connection, transaction,
            Q("UPDATE {schema}.instances SET data = $2::text::jsonb, expires_ms = $3 WHERE id = $1 AND state = 'active'"),
            cancellationToken, instanceId, json, expiresMs);
        if (changed == 0)
        {
            return false;
        }

        if (row.Info.ExpiresMs != expiresMs)
        {
            if (expiresMs is { } deadline)
            {
                await _scheduler.ScheduleAsync(connection, transaction,
                    new JobSpec(ExpireKind, deadline) { Key = instanceId.ToString(), Payload = new InstanceKey(instanceId), OnDuplicate = OnDuplicate.Replace },
                    cancellationToken);
            }
            else
            {
                await _scheduler.CancelAsync(connection, transaction, ExpireKind, instanceId.ToString(), cancellationToken);
            }
        }

        return true;
    }

    /// <summary>Loads the active instances of a kind in the caller's transaction without locking them.</summary>
    public async Task<IReadOnlyList<ContentInstance>> ActiveByKindReadOnlyAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string kind,
        CancellationToken cancellationToken = default)
    {
        var ids = new List<long>();
        await using (var command = Sql.Command(connection, transaction,
            Q("SELECT id FROM {schema}.instances WHERE kind = $1 AND state = 'active' ORDER BY id"), kind))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                ids.Add(reader.GetInt64(0));
            }
        }

        var instances = new List<ContentInstance>(ids.Count);
        foreach (var id in ids)
        {
            if ((await LoadAsync(connection, transaction, id, false, cancellationToken))?.Info is { } instance)
            {
                instances.Add(instance);
            }
        }
        return instances;
    }

    /// <summary>Loads and locks the active instances of a kind in the caller's transaction.</summary>
    public async Task<IReadOnlyList<ContentInstance>> ActiveByKindAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string kind,
        CancellationToken cancellationToken = default)
    {
        var ids = new List<long>();
        await using (var command = Sql.Command(connection, transaction,
            Q("SELECT id FROM {schema}.instances WHERE kind = $1 AND state = 'active' ORDER BY id FOR UPDATE"), kind))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                ids.Add(reader.GetInt64(0));
            }
        }

        var instances = new List<ContentInstance>(ids.Count);
        foreach (var id in ids)
        {
            if ((await LoadAsync(connection, transaction, id, true, cancellationToken))?.Info is { } instance)
            {
                instances.Add(instance);
            }
        }
        return instances;
    }

    /// <summary>Expires an active instance only when its persisted deadline is due.</summary>
    public async Task<bool> ExpireIfDueAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long instanceId,
        CancellationToken cancellationToken = default)
    {
        var row = await LoadAsync(connection, transaction, instanceId, true, cancellationToken);
        if (row is not { Info: { State: "active", ExpiresMs: { } at } } || at > _scheduler.Now)
        {
            return false;
        }

        await FinishAsync(connection, transaction, row, "expired", "time ran out", cancellationToken);
        return true;
    }

    /// <summary>
    /// a system): asking again returns the one that exists. The first objective (every objective, for parallel content) is active at once.
    /// </summary>
    public async Task<StartResult> StartAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string definitionId, long owner, object? data = null, string? uniqueKey = null, long? durationMs = null,
        CancellationToken cancellationToken = default)
    {
        if (!_definitions.TryGetValue(definitionId, out var definition))
        {
            throw new ArgumentException($"unknown content definition '{definitionId}'", nameof(definitionId));
        }
        var now = _scheduler.Now;
        long? expires = (durationMs ?? definition.DurationMs) is { } span ? now + span : null;
        var id = await Sql.ScalarAsync(connection, transaction, Q("""
            INSERT INTO {schema}.instances (kind, definition, owner, order_mode, data, unique_key, on_complete, on_fail, created_ms, expires_ms)
            VALUES ($1, $2, $3, $4, $5::text::jsonb, $6, $7::text::jsonb, $8::text::jsonb, $9, $10)
            ON CONFLICT (kind, unique_key) WHERE state = 'active' AND unique_key IS NOT NULL DO NOTHING
            RETURNING id
            """), cancellationToken, definition.Kind, definition.Id, owner, definition.Order.ToString().ToLowerInvariant(), JsonSerializer.Serialize(data ?? new { }, Json), uniqueKey,
            JsonSerializer.Serialize(definition.OnComplete, Json), JsonSerializer.Serialize(definition.OnFail, Json), now, expires);
        if (id is null)
        {
            var existing = await Sql.ScalarAsync(connection, transaction, Q("SELECT id FROM {schema}.instances WHERE kind = $1 AND unique_key = $2 AND state = 'active'"), cancellationToken, definition.Kind, uniqueKey);
            return new StartResult((long)existing!, false);
        }
        var instanceId = (long)id;
        var instanceData = ToElement(data);
        for (var index = 0; index < definition.Objectives.Count; index++)
        {
            var objective = definition.Objectives[index];
            var type = _types[objective.Type];
            var parameters = ToElement(objective.Params);
            var scopeId = parameters.ValueKind == JsonValueKind.Object && parameters.TryGetProperty("scope", out var scope) && scope.TryGetInt64(out var scoped) ? scoped : owner;
            var active = definition.Order == ObjectiveOrder.Parallel || index == 0;
            await Sql.ExecuteAsync(connection, transaction, Q("""
                INSERT INTO {schema}.objectives (instance_id, idx, id, type, params, topic, scope_field, scope_id, target, required, state, activated_ms)
                VALUES ($1, $2, $3, $4, $5::text::jsonb, $6, $7, $8, $9, $10, $11, $12)
                """), cancellationToken, instanceId, index, objective.Id, objective.Type, parameters.GetRawText(), type.TopicOf(parameters), type.ScopeFieldOf(parameters), scopeId,
                objective.Target, objective.Required, active ? "active" : "waiting", active ? now : null);
            if (active)
            {
                await type.ActivatedAsync(new ActivationContext(new ObjectiveState(instanceId, owner, index, objective.Id, parameters, 0, objective.Target, instanceData), connection, transaction, _scheduler), cancellationToken);
            }
        }
        if (expires is { } at)
        {
            await _scheduler.ScheduleAsync(connection, transaction, new JobSpec(ExpireKind, at) { Key = instanceId.ToString(), Payload = new InstanceKey(instanceId) }, cancellationToken);
        }
        await _bus.PublishAsync(connection, transaction, $"content.{definition.Kind}.started", Announcement(instanceId, definition.Kind, definition.Id, owner, null, instanceData), $"{definition.Kind}:{instanceId}", cancellationToken);
        return new StartResult(instanceId, true);
    }

    private static object Announcement(long id, string kind, string definition, long owner, string? reason, JsonElement data) =>
        new { instanceId = id, kind, definition, owner, reason, data };

    // ---- progress ----------------------------------------------------------------------------------------------------------------------------------

    private sealed record Row(ContentInstance Info, string OrderMode, string OnComplete, string OnFail);

    private async Task<Row?> LoadAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, long id, bool forUpdate, CancellationToken cancellationToken)
    {
        await using var command = Sql.Command(connection, transaction, Q($$"""
            SELECT id, kind, definition, owner, state, data::text, created_ms, expires_ms, finished_ms, result, order_mode, on_complete::text, on_fail::text
            FROM {schema}.instances WHERE id = $1{{(forUpdate ? " FOR UPDATE" : "")}}
            """), id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }
        var info = new ContentInstance(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3), reader.GetString(4), reader.GetString(5), reader.GetInt64(6),
            reader.IsDBNull(7) ? null : reader.GetInt64(7), reader.IsDBNull(8) ? null : reader.GetInt64(8), reader.IsDBNull(9) ? null : reader.GetString(9));
        return new Row(info, reader.GetString(10), reader.GetString(11), reader.GetString(12));
    }

    private sealed record ObjectiveRow(int Index, string Id, string Type, string ParamsJson, string? Topic, string ScopeField, long ScopeId, double Target, bool Required, double Progress, string State);

    private async Task<List<ObjectiveRow>> ObjectivesAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long instanceId, CancellationToken cancellationToken)
    {
        await using var command = Sql.Command(connection, transaction, Q("""
            SELECT idx, id, type, params::text, topic, scope_field, scope_id, target, required, progress, state FROM {schema}.objectives WHERE instance_id = $1 ORDER BY idx
            """), instanceId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var list = new List<ObjectiveRow>();
        while (await reader.ReadAsync(cancellationToken))
        {
            list.Add(new ObjectiveRow(reader.GetInt32(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetString(5),
                reader.GetInt64(6), reader.GetDouble(7), reader.GetBoolean(8), reader.GetDouble(9), reader.GetString(10)));
        }
        return list;
    }

    private ObjectiveState StateOf(Row row, ObjectiveRow objective) =>
        new(row.Info.Id, row.Info.Owner, objective.Index, objective.Id, JsonDocument.Parse(objective.ParamsJson).RootElement.Clone(), objective.Progress, objective.Target, JsonDocument.Parse(row.Info.DataJson).RootElement.Clone());

    private async Task OnEventAsync(EventContext context, CancellationToken cancellationToken)
    {
        var @event = context.Event;
        using var document = JsonDocument.Parse(@event.PayloadJson);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            return;
        }
        var candidates = document.RootElement.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.Number && p.Value.TryGetInt64(out _)).Select(p => p.Value.GetInt64()).Distinct().ToArray();
        if (candidates.Length == 0)
        {
            return;
        }
        var instanceIds = new List<long>();
        await using (var command = Sql.Command(context.Connection, context.Transaction, Q("""
            SELECT DISTINCT instance_id FROM {schema}.objectives WHERE state = 'active' AND topic = $1 AND scope_id = ANY($2) ORDER BY instance_id
            """), @event.Topic, candidates))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                instanceIds.Add(reader.GetInt64(0));
            }
        }
        foreach (var instanceId in instanceIds)
        {
            var row = await LoadAsync(context.Connection, context.Transaction, instanceId, true, cancellationToken);
            if (row is null || row.Info.State != "active")
            {
                continue;
            }
            foreach (var objective in (await ObjectivesAsync(context.Connection, context.Transaction, instanceId, cancellationToken)).Where(o => o.State == "active" && o.Topic == @event.Topic))
            {
                if (!document.RootElement.TryGetProperty(objective.ScopeField, out var scope) || !scope.TryGetInt64(out var scopeId) || scopeId != objective.ScopeId)
                {
                    continue;
                }
                var effect = _types[objective.Type].Evaluate(StateOf(row, objective), @event);
                if (effect.Fail)
                {
                    await FinishAsync(context.Connection, context.Transaction, row, "failed", $"objective:{objective.Id}", cancellationToken);
                    break;
                }
                if (effect.Delta > 0 && await AddProgressAsync(context.Connection, context.Transaction, row, objective, effect.Delta, cancellationToken))
                {
                    break;   // the content ended
                }
            }
        }
    }

    /// <summary>Adds progress to an active objective, completing it (and maybe the content) when it reaches its target. Returns whether the content ended.</summary>
    private async Task<bool> AddProgressAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Row row, ObjectiveRow objective, double delta, CancellationToken cancellationToken)
    {
        var progress = Math.Min(objective.Target, objective.Progress + delta);
        var done = progress >= objective.Target;
        await Sql.ExecuteAsync(connection, transaction, Q("UPDATE {schema}.objectives SET progress = $3, state = $4, done_ms = $5 WHERE instance_id = $1 AND idx = $2"), cancellationToken,
            row.Info.Id, objective.Index, progress, done ? "done" : "active", done ? _scheduler.Now : null);
        if (!done)
        {
            return false;
        }
        await _bus.PublishAsync(connection, transaction, $"content.{row.Info.Kind}.objective_completed",
            new { instanceId = row.Info.Id, kind = row.Info.Kind, definition = row.Info.Definition, owner = row.Info.Owner, objective = objective.Id }, $"{row.Info.Kind}:{row.Info.Id}", cancellationToken);
        return await AdvanceAsync(connection, transaction, row, cancellationToken);
    }

    /// <summary>After an objective is done: start the next one, or complete the content. Returns whether the content ended.</summary>
    private async Task<bool> AdvanceAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Row row, CancellationToken cancellationToken)
    {
        var objectives = await ObjectivesAsync(connection, transaction, row.Info.Id, cancellationToken);
        if (row.OrderMode == "parallel")
        {
            if (objectives.Where(o => o.Required).All(o => o.State == "done"))
            {
                await FinishAsync(connection, transaction, row, "completed", null, cancellationToken);
                return true;
            }
            return false;
        }
        var next = objectives.FirstOrDefault(o => o.State == "waiting");
        if (next is null)
        {
            if (objectives.All(o => o.State == "done"))
            {
                await FinishAsync(connection, transaction, row, "completed", null, cancellationToken);
                return true;
            }
            return false;
        }
        await Sql.ExecuteAsync(connection, transaction, Q("UPDATE {schema}.objectives SET state = 'active', activated_ms = $3 WHERE instance_id = $1 AND idx = $2"), cancellationToken, row.Info.Id, next.Index, _scheduler.Now);
        await _types[next.Type].ActivatedAsync(new ActivationContext(StateOf(row, next), connection, transaction, _scheduler), cancellationToken);
        return false;
    }

    /// <summary>
    /// active and moved.
    /// </summary>
    public async Task<bool> ProgressAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long instanceId, string objectiveId, double delta = 1, CancellationToken cancellationToken = default)
    {
        var objectives = await ObjectivesAsync(connection, transaction, instanceId, cancellationToken);
        var found = objectives.FirstOrDefault(o => o.Id == objectiveId);
        return found is not null && await ProgressAsync(connection, transaction, instanceId, found.Index, delta, cancellationToken);
    }

    private async Task<bool> ProgressAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long instanceId, int index, double delta, CancellationToken cancellationToken)
    {
        var row = await LoadAsync(connection, transaction, instanceId, true, cancellationToken);
        if (row is null || row.Info.State != "active")
        {
            return false;
        }
        var objective = (await ObjectivesAsync(connection, transaction, instanceId, cancellationToken)).FirstOrDefault(o => o.Index == index && o.State == "active");
        if (objective is null)
        {
            return false;
        }
        await AddProgressAsync(connection, transaction, row, objective, delta, cancellationToken);
        return true;
    }

    // ---- ending ------------------------------------------------------------------------------------------------------------------------------------

    /// <summary>Completes the content now, whatever its objectives say (a feature that decides for itself).</summary>
    public Task<bool> CompleteAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long instanceId, string? reason = null, CancellationToken cancellationToken = default) =>
        EndAsync(connection, transaction, instanceId, "completed", reason, cancellationToken);

    /// <summary>Fails the content (the player lost the ship the mission was about).</summary>
    public Task<bool> FailAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long instanceId, string? reason = null, CancellationToken cancellationToken = default) =>
        EndAsync(connection, transaction, instanceId, "failed", reason, cancellationToken);

    /// <summary>The player gives the content up.</summary>
    public Task<bool> AbandonAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long instanceId, string? reason = null, CancellationToken cancellationToken = default) =>
        EndAsync(connection, transaction, instanceId, "abandoned", reason, cancellationToken);

    private async Task<bool> EndAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long instanceId, string outcome, string? reason, CancellationToken cancellationToken)
    {
        var row = await LoadAsync(connection, transaction, instanceId, true, cancellationToken);
        if (row is null || row.Info.State != "active")
        {
            return false;
        }
        await FinishAsync(connection, transaction, row, outcome, reason, cancellationToken);
        return true;
    }

    private async Task ExpireAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long instanceId, CancellationToken cancellationToken)
    {
        var row = await LoadAsync(connection, transaction, instanceId, true, cancellationToken);
        if (row is { Info: { State: "active", ExpiresMs: { } at } } && at <= _scheduler.Now)
        {
            await FinishAsync(connection, transaction, row, "expired", "time ran out", cancellationToken);
        }
    }

    private async Task FinishAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Row row, string outcome, string? reason, CancellationToken cancellationToken)
    {
        var now = _scheduler.Now;
        var changed = await Sql.ExecuteAsync(connection, transaction, Q("UPDATE {schema}.instances SET state = $2, finished_ms = $3, result = $4 WHERE id = $1 AND state = 'active'"), cancellationToken,
            row.Info.Id, outcome, now, reason);
        if (changed == 0)
        {
            return;
        }
        var objectives = await ObjectivesAsync(connection, transaction, row.Info.Id, cancellationToken);
        await Sql.ExecuteAsync(connection, transaction, Q("""
            UPDATE {schema}.objectives SET state = CASE WHEN $2 = 'completed' THEN 'skipped' ELSE 'failed' END WHERE instance_id = $1 AND state IN ('waiting', 'active')
            """), cancellationToken, row.Info.Id, outcome);
        await _scheduler.CancelAsync(connection, transaction, ExpireKind, row.Info.Id.ToString(), cancellationToken);
        foreach (var timer in objectives.Where(o => o.Type == "timer" && o.State is "active" or "waiting"))
        {
            await _scheduler.CancelAsync(connection, transaction, TimerObjective.JobKind, $"{row.Info.Id}:{timer.Index}", cancellationToken);
        }

        var finished = row.Info with { State = outcome, FinishedMs = now, Result = reason };
        var actions = JsonSerializer.Deserialize<List<ActionRef>>(outcome == "completed" ? row.OnComplete : row.OnFail, Json) ?? [];
        var actionContext = new ActionContext(finished, connection, transaction, outcome, this);
        foreach (var action in actions)
        {
            if (!_actions.TryGetValue(action.Name, out var run))
            {
                throw new InvalidOperationException($"content '{row.Info.Definition}': unknown action '{action.Name}'");
            }
            await run(actionContext, ToElement(action.Args), cancellationToken);
        }
        await _bus.PublishAsync(connection, transaction, $"content.{row.Info.Kind}.{outcome}",
            Announcement(row.Info.Id, row.Info.Kind, row.Info.Definition, row.Info.Owner, reason, JsonDocument.Parse(row.Info.DataJson).RootElement.Clone()), $"{row.Info.Kind}:{row.Info.Id}", cancellationToken);
    }

    // ---- looking -----------------------------------------------------------------------------------------------------------------------------------

    public async Task<ContentInstance?> GetAsync(long instanceId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        return (await LoadAsync(connection, null, instanceId, false, cancellationToken))?.Info;
    }

    public async Task<IReadOnlyList<ContentInstance>> ActiveOfAsync(string kind, long owner, CancellationToken cancellationToken = default)
    {
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        var ids = new List<long>();
        await using (var command = Sql.Command(connection, null, Q("SELECT id FROM {schema}.instances WHERE kind = $1 AND owner = $2 AND state = 'active' ORDER BY id"), kind, owner))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                ids.Add(reader.GetInt64(0));
            }
        }
        var list = new List<ContentInstance>();
        foreach (var id in ids)
        {
            if ((await LoadAsync(connection, null, id, false, cancellationToken))?.Info is { } info)
            {
                list.Add(info);
            }
        }
        return list;
    }

    public async Task<IReadOnlyList<ContentInstance>> ActiveByKindAsync(string kind, CancellationToken cancellationToken = default)
    {
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        var ids = new List<long>();
        await using (var command = Sql.Command(connection, null, Q("SELECT id FROM {schema}.instances WHERE kind = $1 AND state = 'active' ORDER BY id"), kind))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
                ids.Add(reader.GetInt64(0));
        }
        var list = new List<ContentInstance>();
        foreach (var id in ids)
        {
            if ((await LoadAsync(connection, null, id, false, cancellationToken))?.Info is { } info)
                list.Add(info);
        }
        return list;
    }

    public async Task<IReadOnlyList<ObjectiveInfo>> ObjectivesOfAsync(long instanceId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        return (await ObjectivesAsync(connection, transaction, instanceId, cancellationToken)).Select(o => new ObjectiveInfo(o.Index, o.Id, o.Type, o.Progress, o.Target, o.State, o.Required)).ToList();
    }
}
