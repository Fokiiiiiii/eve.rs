namespace Eve.GameStore;

/// <summary>
/// A persistence failure with a stable machine-readable <see cref="Code"/>. The
/// codes are the ones the reference server uses (PERSISTENCE_OWNER_STALE, ...).
/// </summary>
public sealed class PersistenceException : Exception
{
    public PersistenceException(string message, string code, IReadOnlyDictionary<string, object?>? details = null, Exception? inner = null)
        : base(message, inner)
    {
        Code = code;
        Details = details ?? EmptyDetails;
    }

    private static readonly IReadOnlyDictionary<string, object?> EmptyDetails = new Dictionary<string, object?>();

    public string Code { get; }

    public IReadOnlyDictionary<string, object?> Details { get; }
}

public static class PersistenceCodes
{
    public const string OwnerRoleInvalid = "PERSISTENCE_OWNER_ROLE_INVALID";
    public const string OwnerInstanceInvalid = "PERSISTENCE_OWNER_INSTANCE_INVALID";
    public const string OwnerFenceRequired = "PERSISTENCE_OWNER_FENCE_REQUIRED";
    public const string OwnerEpochInvalid = "PERSISTENCE_OWNER_EPOCH_INVALID";
    public const string OwnerLeaseInvalid = "PERSISTENCE_OWNER_LEASE_INVALID";
    public const string OwnerStale = "PERSISTENCE_OWNER_STALE";
    public const string OwnerActive = "PERSISTENCE_OWNER_ACTIVE";
    public const string OwnerConflict = "PERSISTENCE_OWNER_CONFLICT";
    public const string OwnerReleaseFailed = "PERSISTENCE_OWNER_RELEASE_FAILED";
    public const string OperationOwnerMismatch = "PERSISTENCE_OPERATION_OWNER_MISMATCH";
    public const string OperationNotFound = "PERSISTENCE_OPERATION_NOT_FOUND";
    public const string OperationPending = "PERSISTENCE_OPERATION_PENDING";
    public const string MaintenanceFenceRequired = "PERSISTENCE_MAINTENANCE_FENCE_REQUIRED";
    public const string ForeignOperationsPending = "PERSISTENCE_FOREIGN_OPERATIONS_PENDING";
    public const string TableNotFound = "PERSISTENCE_TABLE_NOT_FOUND";
    public const string UnsupportedJson = "PERSISTENCE_UNSUPPORTED_JSON";
    public const string ProcessOwnershipViolation = "GAMESTORE_PROCESS_OWNERSHIP_VIOLATION";
    public const string ProcessRoleInvalid = "GAMESTORE_PROCESS_ROLE_INVALID";
}
