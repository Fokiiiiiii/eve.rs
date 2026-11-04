using Eve.Dogma;
using Eve.Fitting;
using Eve.Inventory;
using Npgsql;

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

    /// <summary>
    /// Puts recovered probes back (EVE.js <c>restoreRecoveredProbeCharges</c>): into the launcher they came from while it has room for them (its charge of the probe's
    /// type, up to the capacity <see cref="AmmoRules.Capacity"/> gives), the rest into the ship's cargo hold, a stack joining the stack of its type there. Answers
    /// what changed, in order.
    /// </summary>
    public async Task<IReadOnlyList<ItemChanged>> RestoreProbesAsync(long characterId, long shipId, long? launcherId, int probeTypeId, long count, CancellationToken cancellationToken = default)
    {
        var changes = new List<ItemChanged>();
        if (count <= 0)
        {
            return changes;
        }
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockAsync(connection, transaction, characterId, cancellationToken);
        await statics.EnsureTypesAsync([probeTypeId], cancellationToken);
        if (statics.Type(probeTypeId) is not { } probe)
        {
            await transaction.RollbackAsync(cancellationToken);
            return changes;
        }
        var remaining = count;
        if (launcherId is { } launcherItem
            && await items.LockAndGetAsync(connection, transaction, launcherItem, cancellationToken) is { } module
            && module.OwnerId == characterId && module.LocationId == shipId)
        {
            await statics.EnsureTypesAsync([module.TypeId], cancellationToken);
            var capacity = statics.Type(module.TypeId) is { } moduleType ? AmmoRules.Capacity(moduleType, probe) : 0;
            var (_, charges) = await ShipSnapshot.SlotsOfAsync(statics, await items.InLocationAsync(shipId, characterId, cancellationToken), cancellationToken);
            var loaded = charges.FirstOrDefault(c => c.Flag == module.Flag);
            // a launcher that holds another charge takes no probes; one that holds probes takes up to its capacity
            if (capacity > 0 && (loaded is null || loaded.TypeId == probeTypeId))
            {
                var put = Math.Min(remaining, Math.Max(0, capacity - (loaded?.Quantity ?? 0)));
                if (put > 0)
                {
                    changes.Add(await AddProbeStackAsync(connection, transaction, characterId, shipId, module.Flag, probeTypeId, put, loaded, cancellationToken));
                    remaining -= put;
                }
            }
        }
        if (remaining > 0)
        {
            var hold = (await items.InContainerAsync(shipId, cancellationToken))
                .Where(i => i.Flag == ItemFlags.Cargo && i.TypeId == probeTypeId && !i.Singleton)
                .OrderBy(i => i.Id)
                .FirstOrDefault();
            changes.Add(await AddProbeStackAsync(connection, transaction, characterId, shipId, ItemFlags.Cargo, probeTypeId, remaining, hold, cancellationToken));
        }
        await transaction.CommitAsync(cancellationToken);
        return changes;
    }

    /// <summary>Grows the probe stack <paramref name="existing"/> (found before the lock; the lock is taken again and the stack must still be where it was), or makes one.</summary>
    private async Task<ItemChanged> AddProbeStackAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, long characterId, long shipId, int flag, int typeId, long quantity, Item? existing,
        CancellationToken cancellationToken)
    {
        if (existing is not null
            && await items.LockAndGetAsync(connection, transaction, existing.Id, cancellationToken) is { } stack
            && (stack.LocationId, stack.Flag, stack.TypeId, stack.Singleton) == (shipId, flag, typeId, false))
        {
            var grown = stack with { Quantity = stack.Quantity + quantity };
            await items.SetQuantityAsync(connection, transaction, stack.Id, grown.Quantity, cancellationToken);
            return new ItemChanged(grown, stack.LocationId, stack.Flag, stack.Quantity);
        }
        var id = await items.CreateAsync(connection, transaction, new NewItem(typeId, characterId, shipId, flag, quantity), cancellationToken);
        return new ItemChanged(new Item(id, typeId, characterId, shipId, flag, quantity, false, null), 0, 0, quantity);
    }
}
