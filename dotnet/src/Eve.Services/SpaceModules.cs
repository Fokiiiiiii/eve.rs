namespace Eve.Services;

/// <summary>
/// The answer to a module order: done, or refused with why (the reference's reasons, <c>MODULE_ALREADY_ACTIVE</c>, <c>MODULE_NOT_ACTIVE</c>, <c>NOT_ENOUGH_CAPACITOR</c>,
/// <c>MODULE_NOT_ONLINE</c>, <c>TARGET_REQUIRED</c>…) and the type of the module, which the client's message names.
/// </summary>
public sealed record ModuleResult(bool Ok, string? Reason = null, int ModuleType = 0, string ModuleName = "")
{
    /// <summary>For <c>TARGET_OUT_OF_RANGE</c>: the type of the target, the type of the ship that runs the module, how far the module reaches and how far the target is (metres, surface to surface).</summary>
    public int TargetType { get; init; }

    /// <summary>The effect the refusal is for (<c>miningLaser</c> is told <c>TargetTooFar</c> when its target is out of reach, as the reference does).</summary>
    public string EffectName { get; init; } = "";

    public IReadOnlyList<long> ModuleIds { get; init; } = [];

    public int ShipType { get; init; }

    public double DesiredRange { get; init; }

    public double ActualDistance { get; init; }

    public static ModuleResult Done { get; } = new(true);

    public static ModuleResult Refusal(string reason, int moduleType = 0, string moduleName = "") => new(false, reason, moduleType, moduleName);
}

/// <summary>What the dogma service asks of space for the modules of the ship a session flies.</summary>
public interface ISpaceModules
{
    /// <summary>Starts an effect of a fitted module (<c>Activate(itemID, effectName, target, repeat)</c>); null when the session has no ship in space.</summary>
    /// <summary>Starts an effect of a module; <paramref name="aim"/> is the point a point-targeted effect is aimed at (an offset from the ship), or null.</summary>
    Task<ModuleResult?> ActivateAsync(ClientSession session, long moduleId, string effectName, long targetId, int repeat, CancellationToken cancellationToken, IReadOnlyList<double>? aim = null, IReadOnlyList<double>? aimEnd = null);

    /// <summary>Asks for an effect to stop at the end of its cycle (<c>Deactivate(itemID, effectName)</c>); null when the session has no ship in space.</summary>
    Task<ModuleResult?> DeactivateAsync(ClientSession session, long moduleId, string effectName, CancellationToken cancellationToken);

    /// <summary>Starts or stops a module's SDE overload effect.</summary>
    Task<ModuleResult?> OverloadAsync(ClientSession session, long moduleId, int effectId, bool active, CancellationToken cancellationToken);

    Task<ModuleResult?> OfflineAsync(ClientSession session, long moduleId, CancellationToken cancellationToken);

    Task<ModuleResult?> OverloadRackAsync(ClientSession session, long moduleId, bool active, CancellationToken cancellationToken);

    /// <summary>
    /// Tells the simulation again what the ship in space is now (its resonances and numbers), after a module of it went online or offline. Nothing
    /// is sent for a session with no ship in space.
    /// </summary>
    Task RefreshShipAsync(ClientSession session, CancellationToken cancellationToken);

    /// <summary>
    /// Starts the nanite paste repair of fitted modules (<c>InitiateModuleRepair</c>, <c>InitiateModuleRepairMany</c>): each module is started, short of
    /// paste, or refused. Null when the session has no ship in space.
    /// </summary>
    Task<ModuleRepairStart?> StartRepairAsync(ClientSession session, IReadOnlyList<long> moduleIds, CancellationToken cancellationToken);

    /// <summary>Takes a module's repair out of the ones under way (<c>StopModuleRepair</c>); null when the module is not being repaired.</summary>
    Task<ModuleRepairRecord?> EndRepairAsync(ClientSession session, long moduleId, CancellationToken cancellationToken);
}

/// <summary>The answer to a repair start: the modules whose repair started, those short of nanite repair paste, and those refused.</summary>
public sealed record ModuleRepairStart(IReadOnlyList<long> Started, IReadOnlyList<long> MissingPaste, IReadOnlyList<long> Failed);

/// <summary>
/// A module repair under way: the ship it is in, when it began (ms since the epoch), the rate it repairs at (hit points a minute), the damage it began with (hit points)
/// and the paste it was priced at (the paste for the whole damage).
/// </summary>
public sealed record ModuleRepairRecord(long ModuleId, long ShipId, long StartedMs, double RateHpPerMinute, double DamageHp, long PasteUnits);
