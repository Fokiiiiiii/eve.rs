using Eve.Dogma;
using Eve.Inventory;

namespace Eve.Ships;

/// <summary>
/// What a probe launch finds in its launcher: the probe charge loaded there, how many it holds and how long its probes live, or the refusal the reference gives
/// (<see cref="Refusal"/>: EVE.js <c>_resolveValidatedProbeLaunchContext</c>, the names of its checks).
/// </summary>
public sealed record ProbeLaunchState(string? Refusal, long ModuleId, int ModuleTypeId, long ChargeId, int ProbeTypeId, long Quantity, long LifetimeMs);

public sealed partial class CharacterShips
{
    /// <summary>The scanner probe launchers (SDE group 481) and the scanner probes they fire (group 479, in the charges category).</summary>
    public const int ScanProbeLauncherGroup = 481;
    public const int ScanProbeGroup = 479;

    /// <summary>explosionDelay (attribute 281): a probe lives this many milliseconds; an hour when its type says none (EVE.js <c>DEFAULT_PROBE_EXPIRY_SECONDS</c>).</summary>
    private const int ExplosionDelayAttribute = 281;
    public const long DefaultProbeLifetimeMs = 60 * 60 * 1000;

    /// <summary>What a probe launch from a launcher of the ship finds: the checks come in the reference's order and the first to fail is the refusal.</summary>
    public async Task<ProbeLaunchState> ProbeLaunchStateAsync(long characterId, long shipId, long moduleId, CancellationToken cancellationToken = default)
    {
        ProbeLaunchState Refused(string reason) => new(reason, moduleId, 0, 0, 0, 0, 0);
        if (await items.GetAsync(moduleId, cancellationToken) is not { } module || module.OwnerId != characterId || module.LocationId != shipId)
        {
            return Refused("MODULE_NOT_FOUND");
        }
        await statics.EnsureTypesAsync([module.TypeId], cancellationToken);
        if (statics.Type(module.TypeId)?.GroupId != ScanProbeLauncherGroup)
        {
            return Refused("INVALID_LAUNCHER");
        }
        var states = await modules.StatesAsync([moduleId], cancellationToken);
        if (!states.TryGetValue(moduleId, out var state) || state < ItemState.Online)
        {
            return Refused("MODULE_NOT_ONLINE");
        }
        var (_, charges) = await ShipSnapshot.SlotsOfAsync(statics, await items.InLocationAsync(shipId, characterId, cancellationToken), cancellationToken);
        if (charges.FirstOrDefault(c => c.Flag == module.Flag) is not { } charge)
        {
            return Refused("NO_CHARGES");
        }
        await statics.EnsureTypesAsync([charge.TypeId], cancellationToken);
        if (statics.Type(charge.TypeId) is not { } type || type.GroupId != ScanProbeGroup)
        {
            return Refused("INVALID_CHARGE");
        }
        var lifetimeMs = type.Attributes.TryGetValue(ExplosionDelayAttribute, out var delay) && delay > 0
            ? Math.Max(1, Math.Round(delay / 1000)) * 1000
            : DefaultProbeLifetimeMs;
        return new ProbeLaunchState(null, module.Id, module.TypeId, charge.Id, charge.TypeId, charge.Quantity, (long)lifetimeMs);
    }

    /// <summary>
    /// The probe launcher of the ship that is online (EVE.js <c>resolveActiveProbeLauncherForSession</c>): its module id and type, or null when none is online.
    /// A probe scan takes its duration and its strength bonus from this launcher.
    /// </summary>
    public async Task<(long ModuleId, int TypeId)?> OnlineProbeLauncherAsync(long characterId, long shipId, CancellationToken cancellationToken = default)
    {
        var fitted = await items.InLocationAsync(shipId, characterId, cancellationToken);
        await statics.EnsureTypesAsync(fitted.Select(item => item.TypeId).Distinct().ToArray(), cancellationToken);
        var launchers = fitted.Where(item => statics.Type(item.TypeId)?.GroupId == ScanProbeLauncherGroup).ToList();
        if (launchers.Count == 0)
        {
            return null;
        }
        var states = await modules.StatesAsync(launchers.Select(item => item.Id).ToArray(), cancellationToken);
        return launchers
            .Where(item => states.TryGetValue(item.Id, out var state) && state >= ItemState.Online)
            .Select(item => ((long ModuleId, int TypeId)?)(item.Id, item.TypeId))
            .FirstOrDefault();
    }

    /// <summary>
    /// Spends <paramref name="count"/> probes from the stack loaded in a launcher: what is left stays in the stack, and a stack that is used up is gone. Returns false
    /// (and spends nothing) when the stack holds fewer now, since the decision is made on the row the lock finds.
    /// </summary>
    public async Task<bool> SpendProbesAsync(long characterId, long chargeId, int count, CancellationToken cancellationToken = default)
    {
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockAsync(connection, transaction, characterId, cancellationToken);
        if (await items.LockAndGetAsync(connection, transaction, chargeId, cancellationToken) is not { } charge
            || charge.OwnerId != characterId || count <= 0 || charge.Quantity < count)
        {
            await transaction.RollbackAsync(cancellationToken);
            return false;
        }
        if (charge.Quantity == count)
        {
            await items.DeleteAsync(connection, transaction, chargeId, cancellationToken);
        }
        else
        {
            await items.SetQuantityAsync(connection, transaction, chargeId, charge.Quantity - count, cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return true;
    }
}
