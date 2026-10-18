using System.Collections.Concurrent;
using Eve.Inventory;
using Eve.Macho;
using Eve.Marshal;
using Eve.Relations;
using Eve.Sde;
using Eve.Skills;
using Eve.Universe;
using Eve.Wallet;
using Eve.Characters;
using Eve.Dogma;
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

/// <summary>The reprocessing data: the static data (<c>typeMaterials</c>, <c>types</c>, <c>typeDogma</c>, <c>npcStations</c>) and the ESI adjusted prices.</summary>
public sealed class SdeReprocessingData(SdeReader sde, Eve.Market.AdjustedPriceCache prices) : IReprocessingData
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

    public async Task<IReadOnlyDictionary<int, double>> AdjustedPricesAsync(IReadOnlyCollection<int> typeIds, CancellationToken cancellationToken) =>
        await prices.GetAsync(typeIds, cancellationToken);

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
/// quotes of the items it holds there (in its hangar, its containers or its ship's cargo), and reprocesses them. The yields and the tax are
/// <see cref="ReprocessingRules"/>. A stack too large for the client is split into chunks the reference way. The consumption, the outputs and
/// the tax are one PostgreSQL transaction; the notices go out after the commit.
/// </summary>
[Service("reprocessingSvc")]
public sealed partial class ReprocessingService(NpgsqlDataSource source, PgItemStore items, ItemTypeCatalog types, StationCatalog stations, IReprocessingData data,
    RelationStore relations, PgSkillStore skills, WalletTransactions wallets, PgWalletStore balances, PgCharacterStore characters, DogmaStatic statics,
    Func<long> nowUnixMs) : IBoundObjectReleaseHandler
{
    private const int IskAccount = 1000;
    private const int ImplantFirstFlag = 89;
    private const int ImplantLastFlag = 98;
    private const int MaxLocationDepth = 8;

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
    /// <c>GetQuotes(itemIDs, activeShipID)</c>: the station's tax, the station's efficiency per type, and the quote of each item. A stack too
    /// large for the client is quoted as the sum of its chunks. The answer is <c>[tax, {typeID: efficiency}, {itemID: quote}]</c>.
    /// </summary>
    [Method("GetQuotes")]
    public async ValueTask<PyValue> GetQuotes(ServiceCall call)
    {
        var station = BoundStation(call);
        await using var connection = await source.OpenConnectionAsync(call.Cancellation).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(call.Cancellation).ConfigureAwait(false);
        var plan = await PlanAsync(connection, transaction, call.Session, station, ItemIdsOf(call.Arg(0)), call.Cancellation).ConfigureAwait(false);
        var efficiencies = new List<KeyValuePair<PyValue, PyValue>> { new(PyNone.Instance, new PyReal(plan.StationEfficiency)) };
        foreach (var typeId in plan.Lines.Select(l => l.Quote.TypeId).Distinct())
            efficiencies.Add(new(new PyInt(typeId), new PyReal(plan.StationEfficiency)));
        var quotes = plan.Lines.Select(l => new KeyValuePair<PyValue, PyValue>(InventoryWire.Id(l.Item.Id), QuoteValue(l.Quote)));
        return new PyList([new PyReal(plan.StationTax), new PyDict(efficiencies), new PyDict([.. quotes])]);
    }

    /// <summary>
    /// <c>Reprocess(itemIDs, fromLocationID, ownerID, outputLocationID, outputFlagID)</c>: splits the stacks too large for the client, consumes the
    /// whole portions of each item (its leftovers stay), creates the yields where the caller asked (the sample item's place by default), and
    /// charges the tax. Answers <c>[processedItemIDs, {typeID: quantity}]</c>.
    /// </summary>
    [Method("Reprocess")]
    public async ValueTask<PyValue> Reprocess(ServiceCall call)
    {
        var station = BoundStation(call);
        var character = CharacterOf(call.Session);
        var requestedOwner = IdOf(call.Arg(2)) ?? 0;
        if (requestedOwner != 0 && requestedOwner != character)
            throw Notify("Reprocessing outputs go to your own items.");
        var requestedLocation = IdOf(call.Arg(3));
        var requestedFlag = IdOf(call.Arg(4));

        await using var connection = await source.OpenConnectionAsync(call.Cancellation).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(call.Cancellation).ConfigureAwait(false);
        var plan = await PlanAsync(connection, transaction, call.Session, station, ItemIdsOf(call.Arg(0)), call.Cancellation).ConfigureAwait(false);
        var created = new List<Item>();
        var splitRoots = new List<(Item Before, long Root)>();
        if (plan.Oversized.Count > 0)
        {
            foreach (var (item, chunks) in plan.Oversized)
            {
                splitRoots.Add((item, chunks[0]));
                await items.SetQuantityAsync(connection, transaction, item.Id, chunks[0], call.Cancellation).ConfigureAwait(false);
                for (var k = 1; k < chunks.Count; k++)
                {
                    var id = await items.CreateAsync(connection, transaction, new NewItem(item.TypeId, item.OwnerId, item.LocationId, item.Flag, chunks[k]), call.Cancellation).ConfigureAwait(false);
                    created.Add(new Item(id, item.TypeId, item.OwnerId, item.LocationId, item.Flag, chunks[k], false, null));
                }
            }
            plan = await PlanAsync(connection, transaction, call.Session, station, ItemIdsOf(call.Arg(0)), call.Cancellation, extraIds: created.Select(c => c.Id)).ConfigureAwait(false);
        }
        if (plan.Lines.Count == 0)
        {
            await transaction.CommitAsync(call.Cancellation).ConfigureAwait(false);
            return new PyList([new PyList([]), new PyDict([])]);
        }

        // where the yield goes: the sample item's place by default, else a place of the caller's in this station
        var sample = plan.Lines[0].Item;
        var outputLocation = requestedLocation ?? sample.LocationId;
        var outputFlag = (int)(requestedFlag ?? (requestedLocation is null ? sample.Flag : ItemFlags.Hangar));
        if (outputLocation != station)
        {
            if (await items.LockAndGetAsync(connection, transaction, outputLocation, call.Cancellation).ConfigureAwait(false) is not { } place
                || !await RootAtStationAsync(connection, transaction, place, station, character, call.Cancellation).ConfigureAwait(false))
                throw Notify("Choose an output place in this station.");
        }

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
        var yielded = new List<Item>();
        foreach (var (typeId, quantity) in outputs)
        {
            // a yield above the client's stack limit is held as several stacks, each within it
            for (var remaining = quantity; remaining > 0;)
            {
                var stack = Math.Min(remaining, ReprocessingRules.ClientInventoryStackLimit);
                var id = await items.CreateAsync(connection, transaction, new NewItem(typeId, character, outputLocation, outputFlag, stack), call.Cancellation).ConfigureAwait(false);
                yielded.Add(new Item(id, typeId, character, outputLocation, outputFlag, stack, false, null));
                remaining -= stack;
            }
        }

        await transaction.CommitAsync(call.Cancellation).ConfigureAwait(false);
        if (plan.TotalCost > 0 && await balances.GetAsync(character, call.Cancellation).ConfigureAwait(false) is { } balance)
            wallets.AnnounceCommitted(new WalletBalanceChange(character, IskAccount, balance.Isk));
        foreach (var item in created)
            await NotifyItemAsync(call.Session, item with { Quantity = 0 }, item, call.Cancellation).ConfigureAwait(false);
        foreach (var (beforeRoot, root) in splitRoots)
            await NotifyItemAsync(call.Session, beforeRoot, beforeRoot with { Quantity = root }, call.Cancellation).ConfigureAwait(false);
        foreach (var (beforeItem, afterItem) in notices)
            await NotifyItemAsync(call.Session, beforeItem, afterItem, call.Cancellation).ConfigureAwait(false);
        foreach (var item in yielded)
            await NotifyItemAsync(call.Session, item with { Quantity = 0 }, item, call.Cancellation).ConfigureAwait(false);

        var outputByType = outputs.Select(pair => new KeyValuePair<PyValue, PyValue>(new PyInt(pair.Key), new PyLong(pair.Value)));
        return new PyList([new PyList([.. processed.Select(id => (PyValue)new PyLong(id))]), new PyDict([.. outputByType])]);
    }

    // ── planning ─────────────────────────────────────────────────────────────

    private sealed record Plan(double StationTax, double StationEfficiency, long TaxRecipient, IReadOnlyList<(Item Item, ReprocessingRules.Quote Quote)> Lines,
        IReadOnlyList<(Item Item, IReadOnlyList<long> Chunks)> Oversized)
    {
        public double TotalCost => Lines.Sum(l => l.Quote.TotalIskCost);
    }

    /// <summary>
    /// The quotes of the caller's items that the station can reprocess: each item owned by the caller and rooted at this station (its hangar,
    /// a container, the cargo of a ship), not the ship the caller flies. Read under locks in the caller's transaction, so a quote and the
    /// reprocess that follows see the same stacks.
    /// </summary>
    private async Task<Plan> PlanAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, ClientSession session, long stationId, IReadOnlyList<long> itemIds,
        CancellationToken cancellationToken, IEnumerable<long>? extraIds = null)
    {
        var character = CharacterOf(session);
        if (character <= 0) throw Notify("Only a character can reprocess.");
        var station = stations.Get(stationId) ?? throw Notify("Reprocessing is available in NPC stations.");
        var activeShip = (await characters.GetAsync(character, cancellationToken).ConfigureAwait(false))?.ShipId ?? 0;

        var locked = new List<Item>();
        foreach (var id in itemIds.Concat(extraIds ?? []).Distinct())
        {
            if (activeShip > 0 && id == activeShip) throw Notify("You cannot reprocess the ship you are flying.");
            if (await items.LockAndGetAsync(connection, transaction, id, cancellationToken).ConfigureAwait(false) is { } item
                && await RootAtStationAsync(connection, transaction, item, stationId, character, cancellationToken).ConfigureAwait(false))
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
        var implantBonus = await ImplantMultiplierAsync(connection, transaction, character, cancellationToken).ConfigureAwait(false);

        var stationEfficiency = await data.StationEfficiencyAsync(stationId, cancellationToken).ConfigureAwait(false);
        var standing = await StandingTowardAsync(character, station.OwnerId, cancellationToken).ConfigureAwait(false);
        var taxRate = ReprocessingRules.StationTax(station.ReprocessingStationsTake, standing);
        var prices = await data.AdjustedPricesAsync(materialTypes, cancellationToken).ConfigureAwait(false);

        var lines = new List<(Item, ReprocessingRules.Quote)>();
        var oversized = new List<(Item, IReadOnlyList<long>)>();
        foreach (var item in locked)
        {
            if (!typeInfo.TryGetValue(item.TypeId, out var info) || info.Materials.Count == 0) continue;
            var characterMultiplier = info.ReprocessingSkillType > 0
                ? Multiplier(ReprocessingRules.ReprocessingSkill) * Multiplier(ReprocessingRules.ReprocessingEfficiencySkill) * Multiplier(info.ReprocessingSkillType) * implantBonus
                : Multiplier(ReprocessingRules.ScrapmetalProcessingSkill);
            var efficiency = ReprocessingRules.Efficiency(stationEfficiency, characterMultiplier);
            var quote = ReprocessingRules.Build(item.Id, item.TypeId, item.Quantity, info.PortionSize, efficiency, taxRate, info.Materials, prices);
            if (quote.Portions <= 0) continue;
            if (ReprocessingRules.IsClientSafe(quote))
            {
                lines.Add((item, quote));
                continue;
            }
            var chunks = ReprocessingRules.SplitChunks(item.Quantity, info.PortionSize,
                q => ReprocessingRules.IsClientSafe(ReprocessingRules.Build(item.Id, item.TypeId, q, info.PortionSize, efficiency, taxRate, info.Materials, prices)))
                ?? throw Notify("That stack is too large to reprocess in one go.");
            oversized.Add((item, chunks));
            var parts = chunks.Select(q => ReprocessingRules.Build(item.Id, item.TypeId, q, info.PortionSize, efficiency, taxRate, info.Materials, prices)).ToList();
            lines.Add((item, ReprocessingRules.Sum(item.Id, item.TypeId, parts)));
        }
        return new Plan(taxRate, stationEfficiency, station.OwnerId, lines, oversized);
    }

    /// <summary>The bonus of the implants plugged into the character (the reference's legacy path: each implant's refining yield mutator).</summary>
    private async Task<double> ImplantMultiplierAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long character, CancellationToken cancellationToken)
    {
        var multiplier = 1.0;
        var attribute = statics.AttributeId("refiningYieldMutator");
        if (attribute is not { } attributeId) return multiplier;
        foreach (var implant in await items.InLocationAsync(character, character, cancellationToken).ConfigureAwait(false))
        {
            if (implant.Flag < ImplantFirstFlag || implant.Flag > ImplantLastFlag) continue;
            await statics.EnsureTypesAsync([implant.TypeId], cancellationToken).ConfigureAwait(false);
            if (statics.Type(implant.TypeId)?.Attributes.TryGetValue(attributeId, out var bonus) == true && bonus > 0)
                multiplier *= 1 + bonus / 100.0;
        }
        return multiplier;
    }

    /// <summary>True when the item is the caller's and sits, through its containers, at this station.</summary>
    private async Task<bool> RootAtStationAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Item item, long station, long character, CancellationToken cancellationToken)
    {
        if (item.OwnerId != character) return false;
        var current = item;
        for (var depth = 0; depth < MaxLocationDepth; depth++)
        {
            if (current.LocationId == station) return true;
            var parent = await items.LockAndGetAsync(connection, transaction, current.LocationId, cancellationToken);
            if (parent is null) return false;
            current = parent;
        }
        return false;
    }

    /// <summary>The standing toward the owner: the higher of the character's own and its corporation's (the wiki's rule).</summary>
    private async Task<double> StandingTowardAsync(long character, long owner, CancellationToken cancellationToken)
        => await relations.StandingTowardAsync(character, owner, cancellationToken).ConfigureAwait(false);

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
