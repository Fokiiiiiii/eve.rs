namespace Eve.Services;

/// <summary>
/// The answer to a module order: done, or refused with why (the reference's reasons, <c>MODULE_ALREADY_ACTIVE</c>, <c>MODULE_NOT_ACTIVE</c>, <c>NOT_ENOUGH_CAPACITOR</c>,
/// <c>MODULE_NOT_ONLINE</c>, <c>TARGET_REQUIRED</c>…) and the type of the module, which the client's message names.
/// </summary>
public sealed record ModuleResult(bool Ok, string? Reason = null, int ModuleType = 0, string ModuleName = "")
{
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
}
