namespace Eve.GameStore;

/// <summary>
/// Proof of holding a persistence role. Every durable mutation is validated
/// against the stored (role, epoch, instance) inside its own transaction, so a
/// process that lost its lease can no longer publish.
/// </summary>
public sealed record OwnerFence(string OwnerRole, long Epoch, string InstanceId, long? LeaseExpiresAt = null);

/// <param name="Exclusive">Conflicts with every other active owner (implied for the maintenance role).</param>
/// <param name="ForceTakeover">Take the role even though another instance holds a live lease.</param>
/// <param name="ForceExclusive">Deactivate conflicting owners instead of failing. For a proven-offline maintenance transition only.</param>
/// <param name="LeaseMs">Lease length; at least <see cref="PersistenceStore.MinLeaseMs"/>.</param>
public sealed record AcquireOptions(
    bool Exclusive = false,
    bool ForceTakeover = false,
    bool ForceExclusive = false,
    long? LeaseMs = null);

/// <summary>One row change of an upsert: the row key and its JSON document text.</summary>
public readonly record struct RowUpsert(string Key, string Json);

public enum OperationState
{
    Pending,
    Applied,
}

/// <summary>A journaled batch of row changes for one table (the outbox).</summary>
public sealed record PersistenceOperation(
    long OperationId,
    string Table,
    string OwnerRole,
    long OwnerEpoch,
    string OwnerInstanceId,
    IReadOnlyList<RowUpsert> Upserts,
    IReadOnlyList<string> Deletes,
    OperationState State,
    DateTimeOffset CreatedAt,
    DateTimeOffset? AppliedAt);

public readonly record struct StoredRow(string Key, string Json);
