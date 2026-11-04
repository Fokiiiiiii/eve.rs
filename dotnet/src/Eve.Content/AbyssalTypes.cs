namespace Eve.Content;

public enum AbyssalGameMode
{
    SoloCruiser = 1,
    ThreeFrigates = 2,
    TwoDestroyers = 3,
}

public enum AbyssalContentType
{
    SoloCruiser = 1,
    FrigateFleet = 2,
    CruiserPvp = 3,
    TwoPlayerDestroyers = 4,
}

public enum AbyssalOperationStatus
{
    Applied,
    AlreadyApplied,
}

public enum AbyssalParticipantStatus
{
    Active,
    Disconnected,
    Destroyed,
    Returning,
    Returned,
}

public enum AbyssalDeathSettlementCause
{
    ShipDestroyed = 1,
    RunExpired = 2,
}

public enum AbyssalEntityRole
{
    Npc,
    Cloud,
    Gate,
    Container,
}

public enum AbyssalExitMode
{
    None,
    FinalGate,
    ServerRestart,
}

public enum AbyssalRuleCode
{
    InvalidRequest,
    InvalidGameMode,
    TraceExpired,
    RunExpired,
    RunNotActive,
    ParticipantLimitExceeded,
    FleetMismatch,
    InvalidShipClass,
    ParticipantNotFound,
    GateLocked,
    InvalidGate,
    RoomSpawnMismatch,
    EntityNotInRoom,
    ReturnNotRequested,
    AdmissionConflict,
}

public sealed class AbyssalRuleException(AbyssalRuleCode code, string message) : InvalidOperationException(message)
{
    public AbyssalRuleCode Code { get; } = code;
}

public sealed record AbyssalPosition(double X, double Y, double Z);

public sealed record AbyssalParticipantOrigin(
    long SolarSystemId,
    long LocationId,
    AbyssalPosition ShipPosition,
    AbyssalPosition ShipDirection,
    double Radius);

public sealed record AbyssalParticipantAdmission(
    long CharacterId,
    long ShipId,
    int ShipTypeId,
    string ShipClass,
    long? FleetId,
    AbyssalParticipantOrigin Origin);

public sealed record AbyssalStartRequest(
    long TraceItemId,
    long CreatorCharacterId,
    long CreatorShipId,
    long? FleetId,
    AbyssalGameMode GameModeId,
    int FilamentTypeId,
    long SourceSolarSystemId,
    long SourceLocationId,
    AbyssalPosition SourcePosition,
    long TraceExpiresAtMs,
    AbyssalRunPlan Plan);

public sealed record AbyssalRunPlan(
    int Tier,
    string WeatherType,
    int NebulaGraphicId,
    int MaximumParticipants,
    IReadOnlyList<AbyssalRoomPlan> Rooms)
{
    /// <summary>Catalog-backed hidden scene location; room LocationId values remain ordinals 1 through 3.</summary>
    public int SpaceLocationId { get; init; }
}

public sealed record AbyssalRoomPlan(
    int LocationId,
    IReadOnlyList<AbyssalNpcSpawnPlan> NpcSpawns,
    IReadOnlyList<AbyssalLootPlan> Loot,
    IReadOnlyList<AbyssalObjectivePlan> Objectives,
    string EnvironmentProfileId)
{
    public int? NebulaGraphicId { get; init; }

    /// <summary>Catalog-resolved functional objects, persisted with this run. Empty is a valid source result.</summary>
    public IReadOnlyList<AbyssalEnvironmentObjectPlan> EnvironmentObjects { get; init; } = Array.Empty<AbyssalEnvironmentObjectPlan>();

    public IReadOnlyList<AbyssalArtifactPlan> Artifacts { get; init; } = Array.Empty<AbyssalArtifactPlan>();
}

public sealed record AbyssalNpcSpawnPlan(int TypeId, int Count);

public sealed record AbyssalLootPlan(int TypeId, int Count)
{
    /// <summary>Set by the SDE catalog for loot materialized as individual blueprint copies.</summary>
    public bool IsBlueprintCopy { get; init; }

    /// <summary>One immutable run count (1..5) per blueprint copy in <see cref="Count"/>.</summary>
    public IReadOnlyList<int>? BlueprintRunsPerCopy { get; init; }
}

/// <summary>Catalog rotation components, preserved without Domain interpretation.</summary>
public sealed record AbyssalRotation(double X, double Y, double Z);

/// <summary>Stable catalog identity and room-local transform for a functional environmental object.</summary>
public sealed record AbyssalEnvironmentObjectPlan(
    string Key,
    int TypeId,
    AbyssalPosition LocalPosition,
    AbyssalRotation Rotation,
    string Role,
    string SourceAuthority)
{
    /// <summary>Whether this object runs its environmental mechanics; false preserves source-declared decoration.</summary>
    public bool MechanicsEnabled { get; init; } = true;

    /// <summary>Source-provided presentation radius; independent of mechanics and damage boundaries.</summary>
    public double? PresentationRadiusMeters { get; init; } = null;
}

public enum AbyssalArtifactKind
{
    MainCache = 0,
    ExtractionNode = 1,
}

/// <summary>One catalog-owned, retry-stable loot allocation for a room artifact.</summary>
public sealed record AbyssalArtifactPlan(
    string Key,
    int TypeId,
    AbyssalArtifactKind Kind,
    AbyssalPosition LocalPosition,
    IReadOnlyList<AbyssalLootPlan> Loot,
    string QuantityPolicy,
    string SourceAuthority);

public sealed record AbyssalObjectivePlan(string Id, IReadOnlyList<int> NpcTypeIds, bool Required = true);

public sealed record AbyssalSpaceEntity(long EntityId, int TypeId, AbyssalEntityRole Role, AbyssalPosition Position)
{
    /// <summary>Optional stable plan key used to bind a reserved ID to its persisted catalog object or artifact.</summary>
    public string? PlanKey { get; init; }
}

public sealed record AbyssalParticipantData(
    long CharacterId,
    long ShipId,
    int ShipTypeId,
    string ShipClass,
    long? FleetId,
    AbyssalParticipantStatus Status,
    int CurrentRoomIndex,
    AbyssalParticipantOrigin Origin,
    long EnteredAtMs,
    long LastSeenAtMs,
    long? DisconnectedAtMs,
    long? DestroyedAtMs,
    long? ReturnRequestedAtMs,
    string? ReturnReason,
    long? ReturnedAtMs,
    long? ContentFinishedAtMs,
    int LifecycleReplayOrdinal)
{
    public string? DeathSettlementKey { get; init; }

    public AbyssalDeathSettlementCause? DeathSettlementCause { get; init; }

    public long? DeathSettlementRequestedAtMs { get; init; }

    public long? DeathSettlementReceiptAtMs { get; init; }

    public int? DeathSettlementWreckTypeId { get; init; }
}

public sealed record AbyssalRoomState(
    int LocationId,
    int NebulaGraphicId,
    bool SpawnPrepared,
    bool Cleared,
    IReadOnlyList<AbyssalSpaceEntity> Entities,
    IReadOnlyList<long> DestroyedNpcIds);

public sealed record AbyssalParticipantRecipient(long CharacterId, long ShipId, AbyssalParticipantStatus Status);

public sealed record AbyssalGateTransition(
    long GateMarkerId,
    int FromLocationId,
    int ToLocationId,
    IReadOnlyList<AbyssalParticipantRecipient> Recipients);

/// <summary>Persisted run state. SpaceLocationId is the hidden scene system; room LocationId values remain ordinals 1 through 3, while participant Origin.LocationId is their K-space origin.</summary>
public sealed record AbyssalRunData(
    int SchemaVersion,
    string ContentId,
    long TraceItemId,
    long CreatorCharacterId,
    long CreatorShipId,
    long? FleetId,
    AbyssalGameMode GameModeId,
    AbyssalContentType ContentType,
    int FilamentTypeId,
    int Tier,
    string WeatherType,
    long SourceSolarSystemId,
    long SourceLocationId,
    int SpaceLocationId,
    AbyssalPosition SourcePosition,
    long TraceExpiresAtMs,
    AbyssalRunPlan Plan,
    long? RunStartedAtMs,
    long? RunExpiresAtMs,
    int CurrentRoomIndex,
    IReadOnlyList<AbyssalParticipantData> Participants,
    IReadOnlyList<AbyssalRoomState> Rooms,
    IReadOnlyList<AbyssalGateTransition> GateTransitions,
    AbyssalExitMode ExitMode,
    long? ExitGateMarkerId)
{
    /// <summary>Saved private-scene origin; null retains the legacy SDE-centered coordinate frame.</summary>
    public AbyssalPosition? PrivateSceneOrigin { get; init; }
}

public sealed record AbyssalRunState(long InstanceId, string ContentState, long? ExpiresAtMs, AbyssalRunData Data);

public sealed record AbyssalParticipantRunBinding(AbyssalRunState Run, AbyssalParticipantData Participant);

public sealed record AbyssalOperationResult(AbyssalOperationStatus Status, AbyssalRunState State);

public sealed record AbyssalEnterRequest(long InstanceId, AbyssalParticipantAdmission Participant);

public sealed record AbyssalRoomSpawnRequest(long InstanceId, int LocationId, IReadOnlyList<AbyssalSpaceEntity> Entities);

public sealed record AbyssalNpcDestroyedRequest(long InstanceId, int LocationId, long EntityId);

public sealed record AbyssalRoomGateActivationRequest(long InstanceId, int LocationId, long GateMarkerId, long ActivatingCharacterId);

public sealed record AbyssalEndGateActivationRequest(long InstanceId, int LocationId, long GateMarkerId, long ActivatingCharacterId);

public sealed record AbyssalParticipantDestroyedRequest(long InstanceId, long CharacterId, long ShipId, string Reason, int? WreckTypeId = null);

public sealed record AbyssalParticipantDeathSettlementRequestedEvent(
    long InstanceId,
    string ContentId,
    string SettlementKey,
    long RecipientCharacterId,
    long ShipId,
    AbyssalDeathSettlementCause Cause,
    AbyssalParticipantOrigin Origin,
    int SpaceLocationId,
    int RoomOrdinal,
    long RequestedAtMs,
    int? WreckTypeId);

public sealed record AbyssalParticipantDestructionSettlementRequest(long InstanceId, long CharacterId, long ShipId, string SettlementKey);

public sealed record AbyssalParticipantReturnRequest(long InstanceId, long CharacterId, string Reason);

public sealed record AbyssalReturnReceiptRequest(long InstanceId, long CharacterId);

/// <summary>Internal event: SpaceLocationId identifies the scene; LocationId is the client room ordinal 1 through 3.</summary>
public sealed record AbyssalLocationEnteredEvent(
    long InstanceId,
    string ContentId,
    int DifficultyTier,
    int SpaceLocationId,
    long RecipientCharacterId,
    int LocationId,
    int NebulaGraphicId,
    string EnvironmentProfileId);

public sealed record AbyssalDeadlineAddedEvent(
    long InstanceId,
    string ContentId,
    long RecipientCharacterId,
    long ExpiresAtMs);

public sealed record AbyssalRoomSpawnRequestedEvent(
    long InstanceId,
    string ContentId,
    int SpaceLocationId,
    int LocationId,
    int Tier,
    string WeatherType,
    string EnvironmentProfileId,
    IReadOnlyList<AbyssalSpaceEntity> Entities,
    IReadOnlyList<AbyssalLootPlan> Loot)
{
    /// <summary>Persisted catalog object snapshot for this room.</summary>
    public IReadOnlyList<AbyssalEnvironmentObjectPlan> EnvironmentObjects { get; init; } = Array.Empty<AbyssalEnvironmentObjectPlan>();

    /// <summary>Persisted per-artifact rewards; the subscriber must not reallocate them.</summary>
    public IReadOnlyList<AbyssalArtifactPlan> Artifacts { get; init; } = Array.Empty<AbyssalArtifactPlan>();
}

public sealed record AbyssalRoomTransitionRequestedEvent(
    long InstanceId,
    string ContentId,
    int SpaceLocationId,
    long GateMarkerId,
    int FromLocationId,
    int ToLocationId,
    string FromEnvironmentProfileId,
    string ToEnvironmentProfileId,
    long ActivatingCharacterId,
    IReadOnlyList<AbyssalParticipantRecipient> Recipients);

public sealed record AbyssalLocationExitedEvent(
    long InstanceId,
    string ContentId,
    long RecipientCharacterId,
    int LocationId,
    string EnvironmentProfileId);

public sealed record AbyssalParticipantReturnRequestedEvent(
    long InstanceId,
    string ContentId,
    long RecipientCharacterId,
    long ShipId,
    AbyssalParticipantOrigin Origin,
    string Reason,
    bool Administrative,
    bool RewardEligible,
    bool DeathApplied);

public sealed record AbyssalParticipantContentFinishedEvent(
    long InstanceId,
    string ContentId,
    long RecipientCharacterId);
