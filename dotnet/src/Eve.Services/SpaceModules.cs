namespace Eve.Services;

/// <summary>
/// The answer to a module order: done, or refused with why (the reference's reasons, <c>MODULE_ALREADY_ACTIVE</c>, <c>MODULE_NOT_ACTIVE</c>, <c>NOT_ENOUGH_CAPACITOR</c>,
/// <c>MODULE_NOT_ONLINE</c>, <c>TARGET_REQUIRED</c>…) and the type of the module, which the client's message names.
/// </summary>
public sealed record ModuleResult(bool Ok, string? Reason = null, int ModuleType = 0, string ModuleName = "")
{
    /// <summary>For <c>TARGET_OUT_OF_RANGE</c>: the type of the target, the type of the ship that runs the module, how far the module reaches and how far the target is (metres, surface to surface).</summary>
    public int TargetType { get; init; }

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
    Task<ModuleResult?> ActivateAsync(ClientSession session, long moduleId, string effectName, long targetId, int repeat, CancellationToken cancellationToken);

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
}
