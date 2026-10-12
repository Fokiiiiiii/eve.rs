using System.Collections.Concurrent;
using Eve.Dogma;
using Eve.Fitting;
using Eve.Inventory;
using Eve.Macho;
using Eve.Market;
using Eve.Marshal;
using Eve.Universe;
using Eve.Wallet;
using Npgsql;

namespace Eve.Services.Station;

/// <summary>
/// The repair shop of an NPC station (<c>repairSvc</c>): the client binds it for the station it is docked in, asks for the quote of its
/// damaged items (ships, modules, drones, containers and tools), and repairs what it pays for. The damage is the module state store's; the cost
/// is <see cref="RepairRules"/> on the adjusted price; the charge and the repair are one PostgreSQL transaction, the notices follow the commit.
/// </summary>
[Service("repairSvc")]
public sealed partial class RepairService(NpgsqlDataSource source, PgItemStore items, ItemTypeCatalog types, StationCatalog stations, DogmaStatic statics,
    PgModuleStateStore conditions, AdjustedPriceCache prices, WalletTransactions wallets, PgWalletStore balances, Func<long> nowUnixMs) : IBoundObjectReleaseHandler
{
    /// <summary>The ESI wallet ref type <c>repair_bill</c> (CCP eve-glue, <c>JournalRefTypeEnumV2</c>).</summary>
    public const int RepairBillRefType = 15;

    private const int IskAccount = 1000;
    private const int CategoryShip = 6;
    private const int CategoryModule = 7;
    private const int FlagHangar = ItemFlags.Hangar;
    private const int FlagDroneBay = 87;

    private static readonly HashSet<int> RepairableGroups = [12, 332, 340, 448, 649];
    private static readonly HashSet<int> RepairableCategories = [6, 7, 18, 22, 23, 66];

    private readonly ConcurrentDictionary<string, long> _bound = new(StringComparer.Ordinal);

    public void OnBoundObjectReleased(string oid, ClientSession? session) => _bound.TryRemove(oid, out _);

    /// <summary><c>MachoBindObject((stationID, systemID, groupID), (method, args, kwargs))</c>: binds the repair shop of the station the caller is docked in.</summary>
    [Method("MachoBindObject")]
    public async ValueTask<PyValue> MachoBindObject(ServiceCall call)
    {
        var parameters = call.Arg(0) switch { PyTuple t when t.Items.Count > 0 => t.Items[0], PyList l when l.Items.Count > 0 => l.Items[0], var other => other };
        if (IdOf(parameters) is not { } stationId || call.Session.DockedLocationId != stationId || stations.Get(stationId) is null)
            throw Notify("The repair shop is available in the NPC station you are docked in.");
        var oid = $"N={MachoMessage.ProxyNodeId}:{BoundObjectIds.Next()}";
        _bound[oid] = stationId;
        var bound = new PySubStruct(new PySubStream(Py.Tuple(new PyStr(oid), new PyLong(Py.FileTimeFromUnixMs(nowUnixMs())))));
        PyValue result = PyNone.Instance;
        if (call.Arg(1) is PyTuple { Items.Count: > 0 } nested && Eve.Macho.JsSemantics.StringValue(nested.Items[0]) is { } method)
        {
            result = await ((IService)this).CallAsync(new ServiceCall
            {
                Method = method,
                Args = [.. ArgsOf(nested.Items.Count > 1 ? nested.Items[1] : PyNone.Instance)],
                Kwargs = nested.Items.Count > 2 ? nested.Items[2] : PyNone.Instance,
                Session = call.Session,
                CallId = call.CallId,
                BoundObjectId = oid,
                Cancellation = call.Cancellation,
            }).ConfigureAwait(false);
        }
        return Py.Tuple(bound, result);
    }

    /// <summary>
    /// <c>GetRepairQuotes(itemIDs)</c>: for each requested item of the caller here, its quote rows. A ship in the hangar is quoted with its
    /// fitted modules and its repairable drone-bay items; any other item is quoted alone. The answer is <c>{itemID: [quote, ...]}</c>.
    /// </summary>
    [Method("GetRepairQuotes")]
    public async ValueTask<PyValue> GetRepairQuotes(ServiceCall call)
    {
        var station = BoundStation(call);
        var character = CharacterOf(call.Session);
        var entries = new List<KeyValuePair<PyValue, PyValue>>();
        await using var connection = await source.OpenConnectionAsync(call.Cancellation).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(call.Cancellation).ConfigureAwait(false);
        foreach (var id in ItemIdsOf(call.Arg(0)))
        {
            if (await items.LockAndGetAsync(connection, transaction, id, call.Cancellation).ConfigureAwait(false) is not { } item
                || !await AccessibleAsync(connection, transaction, item, character, station, call.Cancellation).ConfigureAwait(false))
                continue;
            var rows = new List<PyValue>();
            foreach (var quoted in await QuoteGroupAsync(connection, transaction, item, character, call.Cancellation).ConfigureAwait(false))
                rows.Add(QuoteValue(quoted));
            if (rows.Count > 0)
                entries.Add(new(new PyLong(id), new PyList(rows)));
        }
        return new PyDict(entries);
    }

    /// <summary>
    /// <c>RepairItemsInStation(itemIDs, payment)</c>: repairs the damage of the requested items as far as the payment buys it (all of it when the
    /// payment is None). The charge and the repair are one transaction; a fitted module is paid in full.
    /// </summary>
    [Method("RepairItemsInStation")]
    public async ValueTask<PyValue> RepairItemsInStation(ServiceCall call)
    {
        var station = BoundStation(call);
        var character = CharacterOf(call.Session);
        var payment = NumberOrNull(call.Arg(1));
        var now = nowUnixMs();
        var operation = $"repair:{Guid.NewGuid():N}";

        await using var connection = await source.OpenConnectionAsync(call.Cancellation).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(call.Cancellation).ConfigureAwait(false);
        var targets = new List<Damaged>();
        foreach (var id in ItemIdsOf(call.Arg(0)))
        {
            if (await items.LockAndGetAsync(connection, transaction, id, call.Cancellation).ConfigureAwait(false) is not { } item
                || !await AccessibleAsync(connection, transaction, item, character, station, call.Cancellation).ConfigureAwait(false))
                continue;
            var quoted = await QuoteRowAsync(connection, transaction, item, call.Cancellation).ConfigureAwait(false);
            if (!(quoted.Health.Damage > 0)) continue;
            if (!(quoted.UnitCost > 0)) throw Notify("No repair price is known for that item.");
            targets.Add(new Damaged(quoted));
        }
        if (targets.Count == 0)
            return PyNone.Instance;

        var rules = targets.Select(t => new RepairRules.Target(t.Row.Item.Id, t.Row.CategoryId == CategoryModule, t.Row.Health.Damage, t.Row.UnitCost)).ToList();
        var full = RepairRules.Round2(rules.Sum(r => r.FullCost));
        IReadOnlyList<RepairRules.Step> steps;
        try
        {
            steps = RepairRules.Plan(rules, payment ?? full);
        }
        catch (InvalidOperationException error) when (error.Message == "MODULE_PARTIAL_REPAIR")
        {
            throw Notify("A fitted module is repaired in full, not in part.");
        }

        var charged = RepairRules.Round2(steps.Sum(s => s.Charge));
        if (charged > 0)
        {
            try
            {
                await wallets.AdjustAsync(connection, transaction, character, -(decimal)charged, stations.Get(station)!.OwnerId, RepairBillRefType,
                    "Repair", now, operation + ":bill", call.Cancellation).ConfigureAwait(false);
            }
            catch (InsufficientFundsException)
            {
                throw Notify($"You need {charged:N2} ISK for the repair.");
            }
        }

        var moduleChanges = new Dictionary<long, ModuleCondition>();
        var repairedItems = new List<Item>();
        foreach (var step in steps)
        {
            var target = targets.First(t => t.Row.Item.Id == step.ItemId);
            var repaired = RepairRules.Apply(target.Row.Health, step.RepairAmount);
            repairedItems.Add(target.Row.Item);
            if (target.Row.ShipCondition is { } before)
            {
                await conditions.SetShipConditionAsync(connection, transaction, target.Row.Item.Id,
                    new ShipCondition(repaired.StructureDamage, before.Charge, repaired.ShieldCharge, repaired.ArmorDamage, before.Incapacitated), call.Cancellation).ConfigureAwait(false);
            }
            else
            {
                moduleChanges[target.Row.Item.Id] = new ModuleCondition(repaired.StructureDamage, repaired.ArmorDamage, repaired.ShieldCharge, false);
            }
        }
        if (moduleChanges.Count > 0)
            await conditions.SetModuleConditionsAsync(connection, transaction, moduleChanges, call.Cancellation).ConfigureAwait(false);

        await transaction.CommitAsync(call.Cancellation).ConfigureAwait(false);
        if (charged > 0 && await balances.GetAsync(character, call.Cancellation).ConfigureAwait(false) is { } balance)
            wallets.AnnounceCommitted(new WalletBalanceChange(character, IskAccount, balance.Isk));
        foreach (var item in repairedItems)
            await NotifyItemAsync(call.Session, item, call.Cancellation).ConfigureAwait(false);
        return PyNone.Instance;
    }

    /// <summary>The repair in structures is not offered by this repair shop (the structure rules are not built).</summary>
    [Method("RepairItemsInStructure")]
    public PyValue RepairItemsInStructure(ServiceCall call) => throw Notify("Repair in structures is not available.");

    // ── the bound station ───────────────────────────────────────────────────

    private long BoundStation(ServiceCall call)
    {
        if (call.BoundObjectId is not { } oid || !_bound.TryGetValue(oid, out var station) || call.Session.DockedLocationId != station)
            throw Notify("The repair shop is not available from here.");
        return station;
    }

    // ── the health and the quote of one item ────────────────────────────────

    /// <summary>What one item is quoted with: the item, its health, the cost of one unit of damage, and its condition as the store keeps it.</summary>
    private sealed record Quoted(Item Item, int GroupId, int CategoryId, RepairRules.Health Health, double UnitCost, ShipCondition? ShipCondition)
    {
        public double Damage => Health.Damage;
    }

    private sealed record Damaged(Quoted Row);

    private async Task<Quoted> QuoteRowAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Item item, CancellationToken cancellationToken)
    {
        var type = await types.GetAsync(item.TypeId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"type {item.TypeId} is not in the static data");
        await statics.EnsureTypesAsync([item.TypeId], cancellationToken).ConfigureAwait(false);
        var attributes = statics.Type(item.TypeId)?.Attributes;
        double Attribute(string name) => statics.AttributeId(name) is { } id && attributes is not null && attributes.TryGetValue(id, out var value) ? value : 0;
        var shieldCapacity = Attribute("shieldCapacity");
        var armorHp = Attribute("armorHP");
        var structureHp = Attribute("hp");

        ShipCondition? ship = null;
        RepairRules.Health health;
        if (type.CategoryId == CategoryShip)
        {
            ship = await conditions.ShipConditionAsync(connection, transaction, item.Id, cancellationToken).ConfigureAwait(false);
            health = new RepairRules.Health(shieldCapacity, armorHp, structureHp, ship.ShieldCharge, ship.ArmorDamage, ship.Damage);
        }
        else
        {
            var module = (await conditions.ModuleConditionsAsync(connection, transaction, [item.Id], cancellationToken).ConfigureAwait(false))
                .GetValueOrDefault(item.Id, ModuleCondition.Untouched);
            health = new RepairRules.Health(shieldCapacity, armorHp, structureHp, module.ShieldCharge, module.ArmorDamage, module.Damage);
        }
        var price = (await prices.GetAsync([item.TypeId], cancellationToken).ConfigureAwait(false)).GetValueOrDefault(item.TypeId);
        return new Quoted(item, type.GroupId, type.CategoryId, health, RepairRules.UnitCost(price, health.MaxHealth), ship);
    }

    /// <summary>The quote rows of a requested item: the item itself, or for a ship in the hangar the ship with its fitted and repairable drone-bay items.</summary>
    private async Task<IReadOnlyList<Quoted>> QuoteGroupAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Item item, long character, CancellationToken cancellationToken)
    {
        var own = await QuoteRowAsync(connection, transaction, item, cancellationToken).ConfigureAwait(false);
        var rows = new List<Quoted> { own };
        if (own.CategoryId == CategoryShip && item.Flag == FlagHangar)
        {
            foreach (var inside in await items.InLocationAsync(item.Id, character, cancellationToken).ConfigureAwait(false))
            {
                if (!(IsFittedFlag(inside.Flag) || inside.Flag == FlagDroneBay)) continue;
                var type = await types.GetAsync(inside.TypeId, cancellationToken).ConfigureAwait(false);
                if (type is null || !IsRepairableType(type.GroupId, type.CategoryId) || !inside.Singleton) continue;
                rows.Add(await QuoteRowAsync(connection, transaction, inside, cancellationToken).ConfigureAwait(false));
            }
        }
        return rows;
    }

    private static bool IsFittedFlag(int flag) => flag is >= 11 and <= 34 or >= 92 and <= 98 or >= 125 and <= 131;

    /// <summary>
    /// An item the caller can repair here: its own, singleton, of a repairable kind, and in this station (through its containers, if any).
    /// Anything else is skipped, as the reference skips it.
    /// </summary>
    private async Task<bool> AccessibleAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Item item, long character, long station, CancellationToken cancellationToken)
    {
        if (item.OwnerId != character || !item.Singleton) return false;
        var type = await types.GetAsync(item.TypeId, cancellationToken).ConfigureAwait(false);
        if (type is null || !IsRepairableType(type.GroupId, type.CategoryId)) return false;
        var current = item;
        for (var depth = 0; depth < 8; depth++)
        {
            if (current.LocationId == station) return true;
            var parent = await items.LockAndGetAsync(connection, transaction, current.LocationId, cancellationToken).ConfigureAwait(false);
            if (parent is null) return false;
            current = parent;
        }
        return false;
    }

    private static bool IsRepairableType(int groupId, int categoryId) => RepairableCategories.Contains(categoryId) || RepairableGroups.Contains(groupId);

    private static PyValue QuoteValue(Quoted quoted) => Py.KeyVal(
        ("itemID", InventoryWire.Id(quoted.Item.Id)),
        ("typeID", new PyInt(quoted.Item.TypeId)),
        ("groupID", new PyInt(quoted.GroupId)),
        ("categoryID", new PyInt(quoted.CategoryId)),
        ("damage", new PyReal(quoted.Health.Damage)),
        ("maxHealth", new PyReal(quoted.Health.MaxHealth)),
        ("costToRepairOneUnitOfDamage", new PyReal(quoted.UnitCost)),
        ("structureTaxRate", new PyReal(0)),
        ("baseCostToRepairOneUnitOfDamage", new PyReal(quoted.UnitCost)));

    private async Task NotifyItemAsync(ClientSession session, Item item, CancellationToken cancellationToken)
    {
        var type = await types.GetAsync(item.TypeId, cancellationToken).ConfigureAwait(false) ?? throw new InvalidOperationException($"type {item.TypeId} is not in the static data");
        var row = new ItemRow(item.Id, item.TypeId, item.OwnerId, item.LocationId, item.Flag, item.Singleton ? -1 : item.Quantity, type.GroupId, type.CategoryId);
        session.SendNotification("OnItemChange", "clientID", InventoryWire.PackedRow(row), new PyDict([]), PyNone.Instance);
    }

    private static long CharacterOf(ClientSession session) => session.CharacterId ?? session.GetInt("charID") ?? session.GetInt("characterID") ?? 0;

    private static long? IdOf(PyValue value) => value switch
    {
        PyInt number => number.Value,
        PyLong number => number.Value,
        _ => null,
    };

    private static double? NumberOrNull(PyValue value) => value switch
    {
        PyInt number => number.Value,
        PyLong number => number.Value,
        PyReal real => real.Value,
        _ => null,
    };

    private static IReadOnlyList<long> ItemIdsOf(PyValue value) => ArgsOf(value).Select(IdOf).Where(id => id is > 0).Select(id => id!.Value).Distinct().ToList();

    private static IEnumerable<PyValue> ArgsOf(PyValue value) => value switch
    {
        PyList list => list.Items,
        PyTuple tuple => tuple.Items,
        PyObjectEx { Header: PyTuple { Items.Count: 2 } header } ex when header.Items[0] is PyToken { Value: "__builtin__.set" } && header.Items[1] is PyTuple { Items.Count: 1 } payload
            => (payload.Items[0] is PyList first ? first.Items : []).Concat(ex.List),
        _ => [],
    };

    private static MachoWrappedException Notify(string message) =>
        MachoErrors.ThrowUserError("CustomNotify", ("notify", new PyStr(message)));
}
