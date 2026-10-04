using System.Text.Json;

namespace Eve.Relations;

/// <summary>A relation that holds now.</summary>
public sealed record Relation(long Id, string Kind, long Subject, long Object, double Value, string AttrsJson, long SinceMs, long? UntilMs, long UpdatedMs)
{
    public T? Attrs<T>() => JsonSerializer.Deserialize<T>(AttrsJson, RelationStore.Json);
}

/// <summary>What a write changed: the value before (null when the relation is new) and after (null when it ended), and why.</summary>
public sealed record RelationChange(long RelationId, string Kind, long Subject, long Object, double? OldValue, double? NewValue, string? Reason, long AtMs);

/// <summary>One line of the history of a relation.</summary>
public sealed record RelationHistoryEntry(long Id, string Kind, long Subject, long Object, double? OldValue, double? NewValue, string? Reason, long AtMs);

/// <summary>Where a character belongs: its corporation, the corporation's alliance, and the faction (of the corporation, the alliance or the character).</summary>
public sealed record Affiliation(long? Corporation, long? Alliance, long? Faction);
