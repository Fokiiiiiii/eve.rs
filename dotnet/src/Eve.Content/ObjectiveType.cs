using System.Text.Json;
using Eve.Events;
using Eve.Scheduler;
using Npgsql;

namespace Eve.Content;

/// <summary>What an objective type is told about the objective it evaluates.</summary>
public sealed record ObjectiveState(long InstanceId, long Owner, int Index, string Id, JsonElement Params, double Progress, double Target, JsonElement InstanceData);

/// <summary>What an event does to an objective: moves it forward by <see cref="Delta"/>, or fails the whole content.</summary>
public readonly record struct ObjectiveEffect(double Delta, bool Fail = false)
{
    public static ObjectiveEffect None => default;
    public static ObjectiveEffect Advance(double delta = 1) => new(delta);
    public static ObjectiveEffect FailContent => new(0, true);
}

/// <summary>
/// A kind of objective: how it listens (a topic and which field of the event names who it concerns), and what an event does to it. Register one per behaviour ("kill", "deliver",
/// "wait"); most objectives need no new type, because the built-in <c>counter</c> counts any event with a filter on its payload.
/// </summary>
public abstract class ObjectiveType
{
    public abstract string Name { get; }

    /// <summary>The topic that advances an objective with these parameters, or null for one that no event advances.</summary>
    public virtual string? TopicOf(JsonElement parameters) => null;

    /// <summary>The field of the event that names the owner it concerns (the objective listens to events whose field equals its scope id). Default <c>characterId</c>.</summary>
    public virtual string ScopeFieldOf(JsonElement parameters) =>
        parameters.ValueKind == JsonValueKind.Object && parameters.TryGetProperty("scopeField", out var field) && field.ValueKind == JsonValueKind.String ? field.GetString()! : "characterId";

    /// <summary>What this event does to this objective. Called only for events of the objective's topic that name its owner.</summary>
    public virtual ObjectiveEffect Evaluate(ObjectiveState objective, DomainEvent @event) => ObjectiveEffect.None;

    /// <summary>The objective has just become the one to do (at the start of the content, or when the one before it was done). A timer schedules its job here.</summary>
    public virtual Task ActivatedAsync(ActivationContext context, CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>What <see cref="ObjectiveType.ActivatedAsync"/> gets; its writes join the transaction that activates the objective.</summary>
public sealed class ActivationContext
{
    internal ActivationContext(ObjectiveState objective, NpgsqlConnection connection, NpgsqlTransaction transaction, DurableScheduler scheduler)
    {
        Objective = objective;
        Connection = connection;
        Transaction = transaction;
        Scheduler = scheduler;
    }

    public ObjectiveState Objective { get; }
    public NpgsqlConnection Connection { get; }
    public NpgsqlTransaction Transaction { get; }
    public DurableScheduler Scheduler { get; }
}

/// <summary>
/// Counts events of one topic: <c>{ "topic": "npc.killed", "where": { "typeId": [23245, 23246], "system": "$owner" }, "amountField": "count", "scopeField": "characterId" }</c>.
/// <c>where</c> maps payload fields to a value or a list of values (any of); <c>"$owner"</c> and <c>"$instance"</c> stand for the content's owner and id. Each matching event adds the
/// value of <c>amountField</c> (1 when it is not given).
/// </summary>
public sealed class CounterObjective : ObjectiveType
{
    public override string Name => "counter";

    public override string? TopicOf(JsonElement parameters) =>
        parameters.ValueKind == JsonValueKind.Object && parameters.TryGetProperty("topic", out var topic) ? topic.GetString() : null;

    public override ObjectiveEffect Evaluate(ObjectiveState objective, DomainEvent @event) =>
        Matches(objective, @event) ? ObjectiveEffect.Advance(Amount(objective, @event)) : ObjectiveEffect.None;

    internal static bool Matches(ObjectiveState objective, DomainEvent @event)
    {
        if (!objective.Params.TryGetProperty("where", out var where) || where.ValueKind != JsonValueKind.Object)
        {
            return true;
        }
        using var document = JsonDocument.Parse(@event.PayloadJson);
        var payload = document.RootElement;
        foreach (var condition in where.EnumerateObject())
        {
            if (payload.ValueKind != JsonValueKind.Object || !payload.TryGetProperty(condition.Name, out var actual))
            {
                return false;
            }
            var wanted = condition.Value.ValueKind == JsonValueKind.Array ? condition.Value.EnumerateArray().ToArray() : [condition.Value];
            if (!wanted.Any(w => Same(Resolve(w, objective), actual)))
            {
                return false;
            }
        }
        return true;
    }

    private static JsonElement Resolve(JsonElement wanted, ObjectiveState objective)
    {
        if (wanted.ValueKind == JsonValueKind.String)
        {
            return wanted.GetString() switch
            {
                "$owner" => JsonDocument.Parse(objective.Owner.ToString()).RootElement,
                "$instance" => JsonDocument.Parse(objective.InstanceId.ToString()).RootElement,
                _ => wanted,
            };
        }
        return wanted;
    }

    private static bool Same(JsonElement a, JsonElement b) =>
        a.ValueKind == JsonValueKind.Number && b.ValueKind == JsonValueKind.Number ? a.GetDouble() == b.GetDouble() : a.ValueKind == b.ValueKind && a.GetRawText() == b.GetRawText();

    private static double Amount(ObjectiveState objective, DomainEvent @event)
    {
        if (objective.Params.TryGetProperty("amountField", out var field) && field.ValueKind == JsonValueKind.String)
        {
            using var document = JsonDocument.Parse(@event.PayloadJson);
            if (document.RootElement.TryGetProperty(field.GetString()!, out var amount) && amount.ValueKind == JsonValueKind.Number)
            {
                return Math.Max(0, amount.GetDouble());
            }
            return 0;
        }
        return 1;
    }
}

/// <summary>
/// Fails the whole content when an event matches (same parameters as <c>counter</c>): "do not lose your ship", "do not leave the system". It never completes by itself; give it a
/// <c>required: false</c> in a parallel definition.
/// </summary>
public sealed class ForbidObjective : ObjectiveType
{
    public override string Name => "forbid";

    public override string? TopicOf(JsonElement parameters) => new CounterObjective().TopicOf(parameters);

    public override ObjectiveEffect Evaluate(ObjectiveState objective, DomainEvent @event) =>
        CounterObjective.Matches(objective, @event) ? ObjectiveEffect.FailContent : ObjectiveEffect.None;
}

/// <summary>An objective that no event advances: the feature calls <see cref="ContentRuntime.ProgressAsync"/> ("the player talked to the agent").</summary>
public sealed class ManualObjective : ObjectiveType
{
    public override string Name => "manual";
}

/// <summary>Done after <c>{ "delayMs": 60000 }</c> from the moment it becomes the objective to do; a scheduler job completes it (so it survives restarts).</summary>
public sealed class TimerObjective : ObjectiveType
{
    public const string JobKind = "content.timer";

    public override string Name => "timer";

    public override async Task ActivatedAsync(ActivationContext context, CancellationToken cancellationToken)
    {
        var delay = context.Objective.Params.TryGetProperty("delayMs", out var value) ? value.GetInt64() : 0;
        await context.Scheduler.ScheduleAsync(context.Connection, context.Transaction,
            new JobSpec(JobKind, context.Scheduler.Now + delay)
            {
                Key = $"{context.Objective.InstanceId}:{context.Objective.Index}",
                Payload = new TimerKey(context.Objective.InstanceId, context.Objective.Index),
            }, cancellationToken);
    }

    internal sealed record TimerKey(long InstanceId, int Index);
}
