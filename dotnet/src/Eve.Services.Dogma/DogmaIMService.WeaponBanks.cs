using Eve.Fitting;
using Eve.Dogma;
using Eve.Macho;
using Eve.Marshal;
using Eve.Ships;

namespace Eve.Services.Dogma;

public sealed partial class DogmaIMService
{
    private static readonly HashSet<int> GroupableWeaponGroups =
    [
        53, 55, 74, 506, 507, 508, 509, 510, 511, 512, 524, 771, 1245, 1327, 1328, 1562, 1673, 1674,
    ];

    private static PyValue WeaponBanksWire(IReadOnlyDictionary<long, IReadOnlyList<long>> banks) =>
        new PyDict(banks.OrderBy(bank => bank.Key).Select(bank => new KeyValuePair<PyValue, PyValue>(
            InventoryWire.Id(bank.Key), new PyList([.. bank.Value.Select(InventoryWire.Id)]))).ToArray());

    private async ValueTask<ShipSnapshot> WeaponBankShipAsync(ServiceCall call)
    {
        if (BoundContextOf(call) is null || call.Session.CharacterId is not { } character
            || await ReadSnapshotAsync(call.Session, call.Cancellation) is not { } snapshot
            || IdOf(call.Arg(0)) is { } ship && ship != snapshot.Ship.Id
            || call.Session.GetInt("shipid") != snapshot.Ship.Id)
            throw MachoErrors.ThrowUserError("DeniedShipChanged");
        return snapshot;
    }

    private static long MasterOf(IReadOnlyDictionary<long, IReadOnlyList<long>> banks, long moduleId) =>
        banks.TryGetValue(moduleId, out _) ? moduleId : banks.FirstOrDefault(bank => bank.Value.Contains(moduleId)).Key;

    private bool Groupable(ShipSnapshot snapshot, Eve.Inventory.Item module) =>
        statics.Type(module.TypeId) is { CategoryId: 7 } type
        && GroupableWeaponGroups.Contains(type.GroupId)
        && Slots.FamilyOfFlag(module.Flag) == SlotFamily.High
        && snapshot.StateOf(module.Id) >= ItemState.Online;

    private static bool Compatible(ShipSnapshot snapshot, Eve.Inventory.Item left, Eve.Inventory.Item right)
    {
        var leftCharge = snapshot.ChargeOf(left);
        var rightCharge = snapshot.ChargeOf(right);
        return left.TypeId == right.TypeId && (leftCharge is null && rightCharge is null
            || leftCharge is not null && rightCharge is not null
                && leftCharge.TypeId == rightCharge.TypeId && leftCharge.Quantity == rightCharge.Quantity);
    }

    private static IReadOnlyDictionary<long, IReadOnlyList<long>> ReplaceBank(
        IReadOnlyDictionary<long, IReadOnlyList<long>> current, long master, IReadOnlyList<long> slaves)
    {
        var next = current.ToDictionary(pair => pair.Key, pair => pair.Value);
        if (slaves.Count == 0) next.Remove(master);
        else next[master] = slaves.Distinct().Where(id => id != master).ToArray();
        return next;
    }

    private async ValueTask<IReadOnlyDictionary<long, IReadOnlyList<long>>> UpdateWeaponBanksAsync(
        ServiceCall call, ShipSnapshot snapshot,
        Func<IReadOnlyDictionary<long, IReadOnlyList<long>>, IReadOnlyDictionary<long, IReadOnlyList<long>>> update)
    {
        return await (weaponBankStore ?? ships).UpdateWeaponBanksAsync(snapshot.Ship.Id, update, call.Cancellation);
    }

    private Eve.Inventory.Item? Module(ShipSnapshot snapshot, long id) => snapshot.Fitted.FirstOrDefault(item => item.Id == id);

    private void RequireCompatibleBank(ShipSnapshot snapshot, IEnumerable<long> modules)
    {
        var rows = modules.Select(id => Module(snapshot, id)).ToArray();
        if (rows.Length is < 2 or > Slots.FlagsPerFamily
            || rows.Any(row => row is null || !Groupable(snapshot, row))
            || rows.Skip(1).Any(row => !Compatible(snapshot, rows[0]!, row!)))
            throw MachoErrors.ThrowUserError("CantLinkModuleNotOnline");
    }

    [Method("LinkWeapons")]
    public async ValueTask<PyValue> LinkWeapons(ServiceCall call)
    {
        var snapshot = await WeaponBankShipAsync(call);
        var master = IdOf(call.Arg(1)) ?? 0;
        var source = IdOf(call.Arg(2)) ?? 0;
        var banks = snapshot.WeaponBanks;
        var targetMaster = MasterOf(banks, master);
        if (targetMaster == 0) targetMaster = master;
        var sourceMaster = MasterOf(banks, source);
        if (sourceMaster != 0 && sourceMaster == targetMaster)
            return WeaponBanksWire(banks);
        var merging = Flag(call.Arg(3), true);
        if (Module(snapshot, targetMaster) is null || Module(snapshot, source) is null)
            throw MachoErrors.ThrowUserError("DeniedShipChanged");

        var current = banks;
        IReadOnlyDictionary<long, IReadOnlyList<long>> next;
        if (sourceMaster == 0)
        {
            RequireCompatibleBank(snapshot, [targetMaster, source]);
            next = await UpdateWeaponBanksAsync(call, snapshot, state =>
            {
                var slaves = state.TryGetValue(targetMaster, out var existing) ? existing.ToList() : [];
                slaves.Add(source);
                return ReplaceBank(state, targetMaster, slaves);
            });
        }
        else if (merging)
        {
            var moved = current[sourceMaster];
            long[] members = [targetMaster, .. (current.TryGetValue(targetMaster, out var old) ? old : []), sourceMaster, .. moved];
            RequireCompatibleBank(snapshot, members);
            next = await UpdateWeaponBanksAsync(call, snapshot, state =>
            {
                var slaves = state.TryGetValue(targetMaster, out var existing) ? existing.ToList() : [];
                slaves.Add(sourceMaster);
                if (state.TryGetValue(sourceMaster, out var sourceSlaves)) slaves.AddRange(sourceSlaves);
                var updated = ReplaceBank(state, targetMaster, slaves);
                return ReplaceBank(updated, sourceMaster, []);
            });
        }
        else
        {
            var sourceSlaves = current[sourceMaster];
            if (sourceSlaves.Count == 0) return WeaponBanksWire(current);
            var peeled = sourceSlaves[0];
            RequireCompatibleBank(snapshot, [targetMaster, peeled]);
            next = await UpdateWeaponBanksAsync(call, snapshot, state =>
            {
                var targetSlaves = state.TryGetValue(targetMaster, out var existing) ? existing.ToList() : [];
                targetSlaves.Add(peeled);
                var updated = ReplaceBank(state, targetMaster, targetSlaves);
                return ReplaceBank(updated, sourceMaster, sourceSlaves.Skip(1).ToArray());
            });
        }
        return WeaponBanksWire(next);
    }

    [Method("MergeModuleGroups")]
    public async ValueTask<PyValue> MergeModuleGroups(ServiceCall call)
    {
        var snapshot = await WeaponBankShipAsync(call);
        var banks = snapshot.WeaponBanks;
        var target = MasterOf(banks, IdOf(call.Arg(1)) ?? 0);
        if (target == 0) target = IdOf(call.Arg(1)) ?? 0;
        var source = MasterOf(banks, IdOf(call.Arg(2)) ?? 0);
        if (source == 0) source = IdOf(call.Arg(2)) ?? 0;
        if (target == source || !banks.TryGetValue(source, out var sourceSlaves))
            return WeaponBanksWire(banks);
        long[] members = [target, .. (banks.TryGetValue(target, out var targetSlaves) ? targetSlaves : []), source, .. sourceSlaves];
        RequireCompatibleBank(snapshot, members);
        var next = await UpdateWeaponBanksAsync(call, snapshot, state =>
        {
            var slaves = state.TryGetValue(target, out var existing) ? existing.ToList() : [];
            slaves.Add(source);
            if (state.TryGetValue(source, out var additions)) slaves.AddRange(additions);
            return ReplaceBank(ReplaceBank(state, target, slaves), source, []);
        });
        return WeaponBanksWire(next);
    }

    [Method("PeelAndLink")]
    public async ValueTask<PyValue> PeelAndLink(ServiceCall call)
    {
        var snapshot = await WeaponBankShipAsync(call);
        var banks = snapshot.WeaponBanks;
        var target = MasterOf(banks, IdOf(call.Arg(1)) ?? 0);
        if (target == 0) target = IdOf(call.Arg(1)) ?? 0;
        var source = MasterOf(banks, IdOf(call.Arg(2)) ?? 0);
        if (source == 0) source = IdOf(call.Arg(2)) ?? 0;
        if (target == source || !banks.TryGetValue(source, out var sourceSlaves) || sourceSlaves.Count == 0)
            return WeaponBanksWire(banks);
        var peeled = sourceSlaves[0];
        RequireCompatibleBank(snapshot, [target, peeled]);
        var next = await UpdateWeaponBanksAsync(call, snapshot, state =>
        {
            var slaves = state.TryGetValue(target, out var existing) ? existing.ToList() : [];
            slaves.Add(peeled);
            return ReplaceBank(ReplaceBank(state, target, slaves), source, sourceSlaves.Skip(1).ToArray());
        });
        return WeaponBanksWire(next);
    }

    [Method("UnlinkModule")]
    public async ValueTask<PyValue> UnlinkModule(ServiceCall call)
    {
        var snapshot = await WeaponBankShipAsync(call);
        var requested = IdOf(call.Arg(1)) ?? 0;
        var master = MasterOf(snapshot.WeaponBanks, requested);
        if (master == 0 || !snapshot.WeaponBanks.TryGetValue(master, out var slaves) || slaves.Count == 0)
            return new PyLong(0);
        var peeled = slaves[0];
        await UpdateWeaponBanksAsync(call, snapshot, state => ReplaceBank(state, master, slaves.Skip(1).ToArray()));
        return InventoryWire.Id(peeled);
    }

    [Method("LinkAllWeapons")]
    public async ValueTask<PyValue> LinkAllWeapons(ServiceCall call)
    {
        var snapshot = await WeaponBankShipAsync(call);
        var groups = snapshot.Fitted.Where(item => Groupable(snapshot, item))
            .GroupBy(item => (item.TypeId, ChargeType: snapshot.ChargeOf(item)?.TypeId ?? 0, ChargeQuantity: snapshot.ChargeOf(item)?.Quantity ?? 0))
            .Where(group => group.Count() > 1)
            .ToDictionary(group => group.Key, group => group.OrderBy(item => item.Flag).ThenBy(item => item.Id).ToArray());
        foreach (var group in groups.Values) RequireCompatibleBank(snapshot, group.Select(item => item.Id));
        var next = await UpdateWeaponBanksAsync(call, snapshot, _ => groups.Values.ToDictionary(
            group => group[0].Id, group => (IReadOnlyList<long>)group.Skip(1).Select(item => item.Id).ToArray()));
        return WeaponBanksWire(next);
    }

    [Method("UnlinkAllModules")]
    public async ValueTask<PyValue> UnlinkAllModules(ServiceCall call)
    {
        var snapshot = await WeaponBankShipAsync(call);
        await UpdateWeaponBanksAsync(call, snapshot, _ => new Dictionary<long, IReadOnlyList<long>>());
        return WeaponBanksWire(new Dictionary<long, IReadOnlyList<long>>());
    }

    [Method("DestroyWeaponBank")]
    public async ValueTask<PyValue> DestroyWeaponBank(ServiceCall call)
    {
        var snapshot = await WeaponBankShipAsync(call);
        var master = MasterOf(snapshot.WeaponBanks, IdOf(call.Arg(1)) ?? 0);
        if (master != 0)
        {
            await UpdateWeaponBanksAsync(call, snapshot, state => ReplaceBank(state, master, []));
        }
        return None;
    }
}
