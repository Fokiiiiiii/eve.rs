using System.Collections.Concurrent;
using Eve.Inventory;
using Eve.Macho;
using Eve.Marshal;
using Eve.Relations;
using Eve.Sde;
using Eve.Skills;
using Eve.Universe;
using Eve.Wallet;
using Npgsql;

namespace Eve.Services.Station;

/// <summary>The static data a reprocessing quote reads, as the SDE holds it.</summary>
public sealed record ReprocessingTypeInfo(int TypeId, int PortionSize, int ReprocessingSkillType, IReadOnlyList<ReprocessingRules.Material> Materials);

public interface IReprocessingData
{
    Task<IReadOnlyDictionary<int, ReprocessingTypeInfo>> TypesAsync(IReadOnlyCollection<int> typeIds, CancellationToken cancellationToken);
    /// <summary>The percentage per level of each skill (its <c>refiningYieldMutator</c>).</summary>
    Task<IReadOnlyDictionary<int, double>> PercentagePerLevelAsync(IReadOnlyCollection<int> skillTypeIds, CancellationToken cancellationToken);
    /// <summary>The adjusted price of each type, where the static data has a market price table; a type without one is not in the result.</summary>
    Task<IReadOnlyDictionary<int, double>> AdjustedPricesAsync(IReadOnlyCollection<int> typeIds, CancellationToken cancellationToken);
    /// <summary>The efficiency of an NPC station (its <c>reprocessingEfficiency</c>).</summary>
    Task<double> StationEfficiencyAsync(long stationId, CancellationToken cancellationToken);
}

/// <summary>The reprocessing data read from the loaded static data (<c>typeMaterials</c>, <c>types</c>, <c>typeDogma</c>, <c>npcStations</c>, <c>marketPrices</c>).</summary>
public sealed class SdeReprocessingData(SdeReader sde) : IReprocessingData
{
    public async Task<IReadOnlyDictionary<int, ReprocessingTypeInfo>> TypesAsync(IReadOnlyCollection<int> typeIds, CancellationToken cancellationToken)
    {
        var keys = typeIds.Distinct().Select(id => (long)id).ToList();
        var materials = (await sde.ReadAsync("typeMaterials", keys, cancellationToken))
            .ToDictionary(row => (int)row.Key, row => row.Doc.TryGetProperty("materials", out var list)
                ? list.EnumerateArray().Select(m => new ReprocessingRules.Material(m.GetProperty("materialTypeID").GetInt32(), m.GetProperty("quantity").GetInt64())).ToList()
                : new List<ReprocessingRules.Material>());
        var portions = (await sde.ReadAsync("types", keys, cancellationToken))
            .ToDictionary(row => (int)row.Key, row => row.Doc.TryGetProperty("portionSize", out var size) && size.ValueKind == System.Text.Json.JsonValueKind.Number ? size.GetInt32() : 1);
        var skills = await DogmaValuesAsync(keys, ReprocessingRules.ReprocessingSkillTypeAttribute, cancellationToken);
        var result = new Dictionary<int, ReprocessingTypeInfo>();
        foreach (var id in typeIds.Distinct())
        {
            var skill = skills.TryGetValue(id, out var value) ? (int)value : 0;
            result[id] = new ReprocessingTypeInfo(id, portions.GetValueOrDefault(id, 1), skill, materials.GetValueOrDefault(id, new List<ReprocessingRules.Material>()));
        }
        return result;
    }

    public async Task<IReadOnlyDictionary<int, double>> PercentagePerLevelAsync(IReadOnlyCollection<int> skillTypeIds, CancellationToken cancellationToken) =>
        await DogmaValuesAsync(skillTypeIds.Distinct().Select(id => (long)id).ToList(), ReprocessingRules.RefiningYieldMutatorAttribute, cancellationToken);

    public async Task<IReadOnlyDictionary<int, double>> AdjustedPricesAsync(IReadOnlyCollection<int> typeIds, CancellationToken cancellationToken)
    {
        var prices = new Dictionary<int, double>();
        try
        {
            foreach (var row in await sde.ReadAsync("marketPrices", typeIds.Distinct().Select(id => (long)id).ToList(), cancellationToken))
            {
                var price = row.Doc.TryGetProperty("adjustedPrice", out var adjusted) && adjusted.ValueKind == System.Text.Json.JsonValueKind.Number ? adjusted.GetDouble() : 0;
                if (price <= 0 && row.Doc.TryGetProperty("averagePrice", out var average) && average.ValueKind == System.Text.Json.JsonValueKind.Number) price = average.GetDouble();
                if (price > 0) prices[(int)row.Key] = price;
            }
        }
        catch (Npgsql.PostgresException error) when (error.SqlState == "42P01")
        {
            // no market price table in this static data: the tax on materials is then zero, as the static data gives no price
        }
        return prices;
    }

    public async Task<double> StationEfficiencyAsync(long stationId, CancellationToken cancellationToken)
    {
        foreach (var row in await sde.ReadAsync("npcStations", [stationId], cancellationToken))
        {
            if (row.Doc.TryGetProperty("reprocessingEfficiency", out var value) && value.ValueKind == System.Text.Json.JsonValueKind.Number)
                return value.GetDouble();
        }
        return ReprocessingRules.DefaultStationEfficiency;
    }

    /// <summary>The value of one dogma attribute of each type, where the type has it.</summary>
    private async Task<Dictionary<int, double>> DogmaValuesAsync(IReadOnlyCollection<long> typeIds, int attributeId, CancellationToken cancellationToken)
    {
        var values = new Dictionary<int, double>();
        foreach (var row in await sde.ReadAsync("typeDogma", typeIds, cancellationToken))
        {
            if (!row.Doc.TryGetProperty("dogmaAttributes", out var attributes)) continue;
            foreach (var attribute in attributes.EnumerateArray())
            {
                if (attribute.GetProperty("attributeID").GetInt32() == attributeId)
                {
                    values[(int)row.Key] = attribute.GetProperty("value").GetDouble();
                    break;
                }
            }
        }
        return values;
    }
}

/// <summary>
/// The reprocessing plant of an NPC station (<c>reprocessingSvc</c>): the client binds it for the station it is docked in, asks for the
/// quotes of the items it holds there, and reprocesses them. The yields and the tax are <see cref="ReprocessingRules"/>; the consumption,
/// the outputs and the tax are one PostgreSQL transaction, and the notices go out after the commit.
/// </summary>
[Service("reprocessingSvc")]
public sealed partial class ReprocessingService(NpgsqlDataSource source, PgItemStore items, ItemTypeCatalog types, StationCatalog stations, IReprocessingData data,
    RelationStore relations, PgSkillStore skills, WalletTransactions wallets, PgWalletStore balances, Func<long> nowUnixMs) : IBoundObjectReleaseHandler
{
    private const int IskAccount = 1000;

    /// <summary>The ESI wallet ref type <c>reprocessing_tax</c> (CCP eve-glue, <c>JournalRefTypeEnumV2</c>).</summary>
    public const int ReprocessingTaxRefType = 127;

    private readonly ConcurrentDictionary<string, long> _bound = new(StringComparer.Ordinal);

    public void OnBoundObjectReleased(string oid, ClientSession? session) => _bound.TryRemove(oid, out _);

    /// <summary>The station the call is bound to, when the caller is docked there (the plant serves only the station it is in).</summary>
    private long BoundStation(ServiceCall call)
    {
        if (call.BoundObjectId is not { } oid || !_bound.TryGetValue(oid, out var station) || call.Session.DockedLocationId != station)
            throw Notify("The reprocessing plant is not available from here.");
        return station;
    }

    /// <summary><c>MachoBindObject(stationID, (method, args, kwargs))</c>: binds the plant of the station the client is docked in, then makes the nested call.</summary>
    [Method("MachoBindObject")]
    public async ValueTask<PyValue> MachoBindObject(ServiceCall call)
    {
        var parameters = call.Arg(0) switch { PyTuple t when t.Items.Count > 0 => t.Items[0], PyList l when l.Items.Count > 0 => l.Items[0], var other => other };
        if (IdOf(parameters) is not { } stationId || call.Session.DockedLocationId != stationId || stations.Get(stationId) is null)
            throw Notify("Reprocessing is available in the NPC station you are docked in.");
        var oid = $"N={MachoMessage.ProxyNodeId}:{BoundObjectIds.Next()}";
        _bound[oid] = stationId;
        var bound = new PySubStruct(new PySubStream(Py.Tuple(new PyStr(oid), new PyLong(Py.FileTimeFromUnixMs(nowUnixMs())))));
        PyValue result = PyNone.Instance;
        if (call.Arg(1) is PyTuple { Items.Count: > 0 } nested && nested.Items[0] is PyStr method)
        {
            result = await ((IService)this).CallAsync(new ServiceCall
            {
                Method = method.Value,
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
    /// <c>GetQuotes(itemIDs, activeShipID)</c>: the station's tax, the station's efficiency per type, and the quote of each item. The
    /// answer is the reference's: <c>[tax, {typeID: efficiency}, {itemID: quote}]</c>.
    /// </summary>
    [Method("GetQuotes")]
    public async ValueTask<PyValue> GetQuotes(ServiceCall call)
    {
        var station = BoundStation(call);
        await using var connection = await source.OpenConnectionAsync(call.Cancellation).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(call.Cancellation).ConfigureAwait(false);
        var plan = await PlanAsync(connection, transaction, call.Session, station, ItemIdsOf(call.Arg(0)), IdOf(call.Arg(1)) ?? 0, call.Cancellation).ConfigureAwait(false);
        var efficiencies = new List<KeyValuePair<PyValue, PyValue>> { new(PyNone.Instance, new PyReal(plan.StationEfficiency)) };
        foreach (var typeId in plan.Lines.Select(l => l.Quote.TypeId).Distinct())
            efficiencies.Add(new(new PyInt(typeId), new PyReal(plan.StationEfficiency)));
        var quotes = plan.Lines.Select(l => new KeyValuePair<PyValue, PyValue>(InventoryWire.Id(l.Item.Id), QuoteValue(l.Quote)));
        return new PyList([new PyReal(plan.StationTax), new PyDict(efficiencies), new PyDict([.. quotes])]);
    }

    /// <summary>
    /// <c>Reprocess(itemIDs, fromLocationID, ownerID, outputLocationID, outputFlagID)</c>: consumes the whole portions of each item (its leftovers stay),
    /// creates the yields where the caller asked (its station hangar by default), charges the tax. Answers <c>[processedItemIDs, {typeID: quantity}]</c>.
    /// </summary>
    [Method("Reprocess")]
    public async ValueTask<PyValue> Reprocess(ServiceCall call)
    {
        var station = BoundStation(call);
        var character = CharacterOf(call.Session);
        var outputLocation = IdOf(call.Arg(3)) ?? station;
        var outputFlag = (int)(IdOf(call.Arg(4)) ?? ItemFlags.Hangar);
        var owner = IdOf(call.Arg(2)) is { } requestedOwner && requestedOwner != 0 ? requestedOwner : character;
        if (outputLocation != station || owner != character)
            throw Notify("Reprocessing outputs go to your hangar in this station.");

        await using var connection = await source.OpenConnectionAsync(call.Cancellation).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(call.Cancellation).ConfigureAwait(false);
        var plan = await PlanAsync(connection, transaction, call.Session, station, ItemIdsOf(call.Arg(0)), IdOf(call.Arg(5)) ?? 0, call.Cancellation).ConfigureAwait(false);
        if (plan.Lines.Count == 0)
            return new PyList([new PyList([]), new PyDict([])]);

        var now = nowUnixMs();
        var operation = $"reprocess:{Guid.NewGuid():N}";
        if (plan.TotalCost > 0)
        {
            try
            {
                await wallets.AdjustAsync(connection, transaction, character, -(decimal)plan.TotalCost, plan.TaxRecipient, ReprocessingTaxRefType,
                    "Reprocessing tax", now, operation + ":tax", call.Cancellation).ConfigureAwait(false);
            }
            catch (InsufficientFundsException)
            {
                throw Notify($"You need {plan.TotalCost:N2} ISK for the reprocessing tax.");
            }
        }

        var processed = new List<long>();
        var notices = new List<(Item Before, Item After)>();
        foreach (var (item, quote) in plan.Lines)
        {
            processed.Add(item.Id);
            if (quote.LeftOvers > 0)
            {
                await items.SetQuantityAsync(connection, transaction, item.Id, quote.LeftOvers, call.Cancellation).ConfigureAwait(false);
                notices.Add((item, item with { Quantity = quote.LeftOvers }));
            }
            else
            {
                await items.DeleteAsync(connection, transaction, item.Id, call.Cancellation).ConfigureAwait(false);
                notices.Add((item, item with { Quantity = 0 }));
            }
        }

        var outputs = new SortedDictionary<int, long>();
        foreach (var (_, quote) in plan.Lines)
            foreach (var recoverable in quote.Recoverables.Where(r => r.Client > 0))
                outputs[recoverable.TypeId] = outputs.GetValueOrDefault(recoverable.TypeId) + recoverable.Client;
        var created = new List<Item>();
        foreach (var (typeId, quantity) in outputs)
        {
            var id = await items.CreateAsync(connection, transaction, new NewItem(typeId, owner, outputLocation, outputFlag, quantity), call.Cancellation).ConfigureAwait(false);
            created.Add(new Item(id, typeId, owner, outputLocation, outputFlag, quantity, false, null));
        }

        await transaction.CommitAsync(call.Cancellation).ConfigureAwait(false);
        if (plan.TotalCost > 0 && await balances.GetAsync(character, call.Cancellation).ConfigureAwait(false) is { } balance)
            wallets.AnnounceCommitted(new WalletBalanceChange(character, IskAccount, balance.Isk));
        foreach (var (before, after) in notices)
            await NotifyItemAsync(call.Session, before, after, call.Cancellation).ConfigureAwait(false);
        foreach (var item in created)
            await NotifyItemAsync(call.Session, item with { Quantity = 0 }, item, call.Cancellation).ConfigureAwait(false);

        var outputByType = outputs.Select(pair => new KeyValuePair<PyValue, PyValue>(new PyInt(pair.Key), new PyLong(pair.Value)));
        return new PyList([new PyList([.. processed.Select(id => (PyValue)new PyLong(id))]), new PyDict([.. outputByType])]);
    }

    // ── planning ─────────────────────────────────────────────────────────────

    private sealed record Plan(double StationTax, double StationEfficiency, long TaxRecipient, IReadOnlyList<(Item Item, ReprocessingRules.Quote Quote)> Lines)
    {
        public double TotalCost => Lines.Sum(l => l.Quote.TotalIskCost);
    }

    /// <summary>
    /// The quotes of the items of the caller that the station can reprocess: items in its hangar, not the ship flown; each read under a lock
    /// in the caller's transaction, so the quote and the reprocess see the same stacks.
    /// </summary>
    private async Task<Plan> PlanAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, ClientSession session, long stationId, IReadOnlyList<long> itemIds, long activeShipId, CancellationToken cancellationToken)
    {
        var character = CharacterOf(session);
        if (character <= 0) throw Notify("Only a character can reprocess.");
        var station = stations.Get(stationId) ?? throw Notify("Reprocessing is available in NPC stations.");

        var locked = new List<Item>();
        foreach (var id in itemIds.Distinct())
        {
            if (id == activeShipId) throw Notify("You cannot reprocess the ship you are flying.");
            if (await items.LockAndGetAsync(connection, transaction, id, cancellationToken) is { } item
                && item.OwnerId == character && item.LocationId == stationId && item.Flag == ItemFlags.Hangar)
                locked.Add(item);
        }

        var typeIds = locked.Select(i => i.TypeId).Distinct().ToList();
        var typeInfo = await data.TypesAsync(typeIds, cancellationToken).ConfigureAwait(false);
        var materialTypes = typeInfo.Values.SelectMany(t => t.Materials.Select(m => m.TypeId)).Distinct().ToList();
        var skillIds = new HashSet<int> { ReprocessingRules.ReprocessingSkill, ReprocessingRules.ReprocessingEfficiencySkill, ReprocessingRules.ScrapmetalProcessingSkill };
        foreach (var info in typeInfo.Values.Where(t => t.ReprocessingSkillType > 0)) skillIds.Add(info.ReprocessingSkillType);
        var percentages = await data.PercentagePerLevelAsync(skillIds.ToList(), cancellationToken).ConfigureAwait(false);
        var levels = new Dictionary<int, int>();
        foreach (var skill in skillIds) levels[skill] = await skills.LevelAsync(connection, transaction, character, skill, cancellationToken).ConfigureAwait(false);
        double Multiplier(int skill) => ReprocessingRules.SkillMultiplier(levels[skill], percentages.GetValueOrDefault(skill));

        var stationEfficiency = await data.StationEfficiencyAsync(stationId, cancellationToken).ConfigureAwait(false);
        var standing = await StandingTowardAsync(character, station.OwnerId, cancellationToken).ConfigureAwait(false);
        var taxRate = ReprocessingRules.StationTax(station.ReprocessingStationsTake, standing);
        var prices = await data.AdjustedPricesAsync(materialTypes, cancellationToken).ConfigureAwait(false);

        var lines = new List<(Item, ReprocessingRules.Quote)>();
        foreach (var item in locked)
        {
            if (!typeInfo.TryGetValue(item.TypeId, out var info) || info.Materials.Count == 0) continue;
            var characterMultiplier = info.ReprocessingSkillType > 0
                ? Multiplier(ReprocessingRules.ReprocessingSkill) * Multiplier(ReprocessingRules.ReprocessingEfficiencySkill) * Multiplier(info.ReprocessingSkillType)
                : Multiplier(ReprocessingRules.ScrapmetalProcessingSkill);
            var efficiency = ReprocessingRules.Efficiency(stationEfficiency, characterMultiplier);
            var quote = ReprocessingRules.Build(item.Id, item.TypeId, item.Quantity, info.PortionSize, efficiency, taxRate, info.Materials, prices);
            if (quote.Portions > 0) lines.Add((item, quote));
        }
        return new Plan(taxRate, stationEfficiency, station.OwnerId, lines);
    }

    /// <summary>The standing toward the owner: the higher of the character's own and its corporation's (the wiki's rule).</summary>
    private async Task<double> StandingTowardAsync(long character, long owner, CancellationToken cancellationToken)
    {
        var personal = await relations.ValueAsync(RelationKinds.Standing.Name, character, owner, 0, cancellationToken: cancellationToken).ConfigureAwait(false);
        var affiliation = await relations.AffiliationOfAsync(character, cancellationToken: cancellationToken).ConfigureAwait(false);
        var corporate = affiliation.Corporation is { } corporation
            ? await relations.ValueAsync(RelationKinds.Standing.Name, corporation, owner, 0, cancellationToken: cancellationToken).ConfigureAwait(false)
            : 0;
        return Math.Max(personal, corporate);
    }

    // ── rows and notices ─────────────────────────────────────────────────────

    private static PyValue QuoteValue(ReprocessingRules.Quote quote) => Py.KeyVal(
        ("itemID", InventoryWire.Id(quote.ItemId)),
        ("typeID", new PyInt(quote.TypeId)),
        ("quantityToProcess", new PyLong(quote.QuantityToProcess)),
        ("leftOvers", new PyLong(quote.LeftOvers)),
        ("portions", new PyLong(quote.Portions)),
        ("numPortions", new PyLong(quote.Portions)),
        ("efficiency", new PyReal(quote.Efficiency)),
        ("recoverables", new PyList([.. quote.Recoverables.Select(r => Py.KeyVal(
            ("typeID", new PyInt(r.TypeId)),
            ("client", new PyLong(r.Client)),
            ("unrecoverable", new PyLong(r.Unrecoverable)),
            ("iskCost", new PyReal(r.IskCost))))])),
        ("totalISKCost", new PyReal(quote.TotalIskCost)));

    private async Task NotifyItemAsync(ClientSession session, Item before, Item after, CancellationToken cancellationToken)
    {
        var type = await types.GetAsync(after.TypeId, cancellationToken).ConfigureAwait(false) ?? throw new InvalidOperationException($"type {after.TypeId} is not in the static data");
        var row = new ItemRow(after.Id, after.TypeId, after.OwnerId, after.LocationId, after.Flag, after.Singleton ? -1 : after.Quantity, type.GroupId, type.CategoryId);
        var differences = new List<KeyValuePair<PyValue, PyValue>>();
        if (after.LocationId != before.LocationId) differences.Add(new(new PyInt(3), InventoryWire.Id(before.LocationId)));
        if (after.Flag != before.Flag) differences.Add(new(new PyInt(4), new PyInt(before.Flag)));
        if (after.Quantity != before.Quantity) differences.Add(new(new PyInt(9), InventoryWire.Id(before.Quantity)));
        session.SendNotification("OnItemChange", "clientID", InventoryWire.PackedRow(row), new PyDict(differences), PyNone.Instance);
    }

    private static long CharacterOf(ClientSession session) => session.CharacterId ?? session.GetInt("charID") ?? session.GetInt("characterID") ?? 0;

    private static long? IdOf(PyValue value) => value switch
    {
        PyInt number => number.Value,
        PyLong number => number.Value,
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
