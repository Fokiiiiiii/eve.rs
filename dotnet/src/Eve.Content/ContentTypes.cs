using System.Text.Json;
using Npgsql;

namespace Eve.Content;

/// <summary>Whether the objectives of a piece of content are met one after the other or all at once.</summary>
public enum ObjectiveOrder
{
    /// <summary>In order: the next objective only starts when the one before is done.</summary>
    Sequential,

    /// <summary>All active from the start; the content completes when every required one is done (<see cref="ObjectiveDef.Required"/> is false for the optional ones).</summary>
    Parallel,
}

/// <summary>One thing to do. <see cref="Type"/> names an <see cref="ObjectiveType"/>; <see cref="Params"/> is what that type needs (anything System.Text.Json can write).</summary>
public sealed record ObjectiveDef(string Id, string Type, object? Params = null, double Target = 1, bool Required = true);

/// <summary>An action to run when content ends, by the name it was registered under, with its arguments.</summary>
public sealed record ActionRef(string Name, object? Args = null);

/// <summary>A template of content: its objectives, how long it lasts and what happens when it ends.</summary>
public sealed record ContentDefinition(string Kind, string Id, IReadOnlyList<ObjectiveDef> Objectives)
{
    public ObjectiveOrder Order { get; init; } = ObjectiveOrder.Sequential;

    /// <summary>How long it lasts from its start; null: until it is completed, failed or abandoned.</summary>
    public long? DurationMs { get; init; }

    public IReadOnlyList<ActionRef> OnComplete { get; init; } = [];

    /// <summary>Runs when the content fails, expires or is abandoned.</summary>
    public IReadOnlyList<ActionRef> OnFail { get; init; } = [];
}

public sealed record ContentInstance(long Id, string Kind, string Definition, long Owner, string State, string DataJson, long CreatedMs, long? ExpiresMs, long? FinishedMs, string? Result)
{
    public T? Data<T>() => JsonSerializer.Deserialize<T>(DataJson, ContentRuntime.Json);
}

public sealed record ObjectiveInfo(int Index, string Id, string Type, double Progress, double Target, string State, bool Required);

/// <summary>What <see cref="ContentRuntime.StartAsync"/> did: the instance, and whether it was made (false: an active one with the same unique key already existed).</summary>
public sealed record StartResult(long Id, bool Created);

/// <summary>What an action gets when content ends; it runs in the transaction that ends it.</summary>
public sealed class ActionContext
{
    internal ActionContext(ContentInstance instance, NpgsqlConnection connection, NpgsqlTransaction transaction, string outcome, ContentRuntime runtime)
    {
        Instance = instance;
        Connection = connection;
        Transaction = transaction;
        Outcome = outcome;
        Runtime = runtime;
    }

    public ContentInstance Instance { get; }
    public NpgsqlConnection Connection { get; }
    public NpgsqlTransaction Transaction { get; }

    /// <summary><c>completed</c>, <c>failed</c>, <c>expired</c> or <c>abandoned</c>.</summary>
    public string Outcome { get; }

    public ContentRuntime Runtime { get; }
}

/// <summary>An action: do something when content ends (open a follow-up, pay out, publish). Throwing rolls the ending back, so it is tried again.</summary>
public delegate Task ContentAction(ActionContext context, JsonElement args, CancellationToken cancellationToken);
