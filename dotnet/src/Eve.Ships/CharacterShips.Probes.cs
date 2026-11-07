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

/// <summary>The committed records from one launch, or the validation reason that kept its charges and records unchanged.</summary>
public sealed record ProbeLaunchCommit(string? Refusal, IReadOnlyList<ScanProbeRecord> Records);

/// <summary>The probes and item stacks changed by one atomic recovery.</summary>
public sealed record ProbeRecoveryCommit(IReadOnlyList<ScanProbeRecord> Probes, IReadOnlyList<ItemChanged> Changes);

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
    /// Spends the launcher charge and writes every launched probe row together. The active count is checked while holding the character's ship lock, so concurrent
    /// launches cannot both spend from the same pre-launch count. Scene changes and client notifications are sent by SpaceProbes after this commits.
    /// </summary>
    public async Task<ProbeLaunchCommit> LaunchProbesAndRecordAsync(
        long characterId, long shipId, int solarSystemId, ProbeLaunchState launchState, int count, int maxActiveProbes, PgProbeStore probeStore,
        Func<int, IReadOnlyList<long>, IReadOnlyList<ScanProbeRecord>> recordsOf, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(probeStore);
        ArgumentNullException.ThrowIfNull(recordsOf);
        if (count <= 0)
            throw new ArgumentOutOfRangeException(nameof(count));

        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockAsync(connection, transaction, characterId, cancellationToken);

        var module = await items.LockAndGetAsync(connection, transaction, launchState.ModuleId, cancellationToken);
        if (module is null || module.OwnerId != characterId || module.LocationId != shipId || module.TypeId != launchState.ModuleTypeId)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new ProbeLaunchCommit("MODULE_NOT_FOUND", []);
        }
        if (await modules.StateAsync(connection, transaction, module.Id, cancellationToken) < ItemState.Online)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new ProbeLaunchCommit("MODULE_NOT_ONLINE", []);
        }
        var charge = await items.LockAndGetAsync(connection, transaction, launchState.ChargeId, cancellationToken);
        if (charge is null || charge.OwnerId != characterId || charge.LocationId != shipId || charge.Flag != module.Flag
            || charge.TypeId != launchState.ProbeTypeId || charge.Quantity < count)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new ProbeLaunchCommit("NO_CHARGES", []);
        }

        var active = await probeStore.ActiveCountAsync(connection, transaction, characterId, solarSystemId, cancellationToken);
        if (active + count > maxActiveProbes)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new ProbeLaunchCommit("ACTIVE_PROBE_LIMIT", []);
        }

        var ids = new long[count];
        for (var index = 0; index < ids.Length; index++)
            ids[index] = await probeStore.NextIdAsync(connection, transaction, cancellationToken);
        var records = recordsOf(active, ids);
        if (records.Count != count || records.Select(record => record.ProbeId).Distinct().Count() != count
            || records.Any(record => record.CharacterId != characterId || record.SolarSystemId != solarSystemId || record.ShipId != shipId
                || record.TypeId != launchState.ProbeTypeId || record.LauncherId != launchState.ModuleId))
        {
            throw new InvalidOperationException("The scanner probe launch records do not match their transaction.");
        }

        if (charge.Quantity == count)
            await items.DeleteAsync(connection, transaction, charge.Id, cancellationToken);
        else
            await items.SetQuantityAsync(connection, transaction, charge.Id, charge.Quantity - count, cancellationToken);
        foreach (var record in records)
            await probeStore.SaveAsync(connection, transaction, record, cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return new ProbeLaunchCommit(null, records);
    }

    /// <summary>
    /// Puts recovered probes back (EVE.js <c>restoreRecoveredProbeCharges</c>): into the launcher they came from while it has room for them (its charge of the probe's
    /// type, up to the capacity <see cref="AmmoRules.Capacity"/> gives), the rest into the ship's cargo hold, a stack joining the stack of its type there. Answers
    /// what changed, in order.
    /// </summary>
    public async Task<IReadOnlyList<ItemChanged>> RestoreProbesAsync(long characterId, long shipId, long? launcherId, int probeTypeId, long count, CancellationToken cancellationToken = default)
    {
        if (count <= 0)
            return [];
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockAsync(connection, transaction, characterId, cancellationToken);
        var changes = await RestoreProbeGroupAsync(connection, transaction, characterId, shipId, launcherId, probeTypeId, count, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return changes;
    }

    /// <summary>Removes a character's requested probe rows and restores their charges as one idempotent transaction.</summary>
    public async Task<ProbeRecoveryCommit> RecoverProbesAsync(
        long characterId, long shipId, int solarSystemId, IReadOnlyCollection<long> probeIds, PgProbeStore probeStore,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(probeStore);
        var requested = probeIds.Distinct().ToArray();
        if (requested.Length == 0)
            return new ProbeRecoveryCommit([], []);

        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockAsync(connection, transaction, characterId, cancellationToken);
        var records = (await probeStore.LockOwnedAsync(connection, transaction, requested, characterId, solarSystemId, cancellationToken)).ToList();
        if (records.Count == 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new ProbeRecoveryCommit([], []);
        }
        var position = requested.Select((id, index) => (id, index)).ToDictionary(pair => pair.id, pair => pair.index);
        records.Sort((left, right) => position[left.ProbeId].CompareTo(position[right.ProbeId]));

        var changes = new List<ItemChanged>();
        foreach (var group in records.GroupBy(record => (record.LauncherId, record.TypeId)))
        {
            changes.AddRange(await RestoreProbeGroupAsync(
                connection, transaction, characterId, shipId, group.Key.LauncherId, group.Key.TypeId, group.Count(), cancellationToken));
        }

        var removed = await probeStore.RemoveOwnedAsync(connection, transaction, records.Select(record => record.ProbeId).ToArray(), characterId, solarSystemId, cancellationToken);
        if (removed.Count != records.Count)
            throw new InvalidOperationException("A locked scanner probe was not removed during recovery.");
        await transaction.CommitAsync(cancellationToken);
        return new ProbeRecoveryCommit(records, changes);
    }

    private async Task<IReadOnlyList<ItemChanged>> RestoreProbeGroupAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, long characterId, long shipId, long? launcherId, int probeTypeId, long count,
        CancellationToken cancellationToken)
    {
        await statics.EnsureTypesAsync([probeTypeId], cancellationToken);
        if (statics.Type(probeTypeId) is not { } probe)
        {
            throw new InvalidDataException($"The scanner probe type {probeTypeId} is missing from dogma static data.");
        }
        var contents = await items.LockContainerAsync(connection, transaction, shipId, cancellationToken);
        var ownedContents = contents.Where(item => item.OwnerId == characterId).ToArray();
        var changes = new List<ItemChanged>();
        var remaining = count;
        if (launcherId is { } launcherItem
            && await items.LockAndGetAsync(connection, transaction, launcherItem, cancellationToken) is { } module
            && module.OwnerId == characterId && module.LocationId == shipId)
        {
            await statics.EnsureTypesAsync([module.TypeId], cancellationToken);
            var capacity = statics.Type(module.TypeId) is { } moduleType ? AmmoRules.Capacity(moduleType, probe) : 0;
            var (_, charges) = await ShipSnapshot.SlotsOfAsync(statics, ownedContents, cancellationToken);
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
            var hold = contents
                .Where(i => i.Flag == ItemFlags.Cargo && i.TypeId == probeTypeId && !i.Singleton)
                .OrderBy(i => i.Id)
                .FirstOrDefault();
            changes.Add(await AddProbeStackAsync(connection, transaction, characterId, shipId, ItemFlags.Cargo, probeTypeId, remaining, hold, cancellationToken));
        }
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
