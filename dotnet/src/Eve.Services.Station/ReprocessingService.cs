using System.Collections.Concurrent;
using System.Globalization;
using Eve.Inventory;
using Eve.Macho;
using Eve.Marshal;
using Eve.Relations;
using Eve.Sde;
using Eve.Services.Core;
using Eve.Skills;
using Eve.Structures;
using Eve.Universe;
using Eve.Wallet;
using Eve.Characters;
using Eve.Corporations;
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
/// The reprocessing plant of a station or structure (<c>reprocessingSvc</c>): the client binds it for the facility it is docked in, asks for
/// the quotes of accessible items there, and reprocesses them. Station and structure yields and taxes use their authoritative data and the
/// formulas in <see cref="ReprocessingRules"/>. A stack too large for the client is split into chunks the reference way. The consumption,
/// outputs and tax are one PostgreSQL transaction; notices go out after the commit.
/// </summary>
[Service("reprocessingSvc")]
public sealed partial class ReprocessingService(NpgsqlDataSource source, PgItemStore items, ItemTypeCatalog types, StationCatalog stations, IReprocessingData data,
    RelationStore relations, PgSkillStore skills, WalletTransactions wallets, PgWalletStore balances, PgCharacterStore characters, DogmaStatic statics,
    Func<long> nowUnixMs, CorporationStore? corporations = null, CorporationOfficeStore? offices = null, StructureStore? structures = null,
    Func<long, long, CancellationToken, ValueTask<bool>>? structureReprocessingAccess = null, SolarSystemCatalog? solarSystems = null) : IBoundObjectReleaseHandler
{
    private const int IskAccount = 1000;
    private const int ImplantFirstFlag = 89;
    private const int ImplantLastFlag = 98;
    private const int MaxLocationDepth = 8;
    private const int CorporationTransactionTaxRefType = 54;
    private const int StructureRigFirstFlag = 92;
    private const int StructureRigLastFlag = 99;
    private const int StructureRigSizeAttribute = 1547;
    private const int RefiningYieldMultiplierAttribute = 717;
    private const int HighSecurityRigMultiplierAttribute = 2355;
    private const int LowSecurityRigMultiplierAttribute = 2356;
    private const int NullSecurityRigMultiplierAttribute = 2357;
    private const int StructureReprocessingYieldBonusAttribute = 2722;
    private const int AsteroidOreRigEffect = 6674;
    private const int MoonOreRigEffect = 6675;
    private const int IceRigEffect = 6678;

    private sealed record BoundFacility(long Id, bool IsStructure);

    /// <summary>The ESI wallet ref type <c>reprocessing_tax</c> (CCP eve-glue, <c>JournalRefTypeEnumV2</c>).</summary>
    public const int ReprocessingTaxRefType = 127;

    private readonly ConcurrentDictionary<string, BoundFacility> _bound = new(StringComparer.Ordinal);

    public void OnBoundObjectReleased(string oid, ClientSession? session) => _bound.TryRemove(oid, out _);

    /// <summary>The station or structure the call is bound to, after checking its live service and the caller's access.</summary>
    private async Task<BoundFacility> BoundFacilityAsync(ServiceCall call)
    {
        if (call.BoundObjectId is not { } oid || !_bound.TryGetValue(oid, out var facility) || call.Session.DockedLocationId != facility.Id)
            throw Notify("The reprocessing plant is not available from here.");
        if (!facility.IsStructure)
        {
            if (stations.Get(facility.Id) is null)
                throw Notify("The reprocessing plant is not available from here.");
            return facility;
        }

        if (structures is null || structureReprocessingAccess is null)
            throw Notify("Reprocessing is unavailable in this structure.");
        var structure = await structures.GetAsync(facility.Id, call.Cancellation).ConfigureAwait(false);
        var services = structure is null
            ? new Dictionary<int, int>()
            : await ReadStructureServicesAsync(facility.Id, call.Cancellation).ConfigureAwait(false);
        if (structure is null || !structure.Snapshot.CoreInstalled || structure.Snapshot.DestroyedMs != 0
            || services.GetValueOrDefault(StructureServiceIds.Reprocessing) != ServiceState.Online
            || !await structureReprocessingAccess(CharacterOf(call.Session), facility.Id, call.Cancellation).ConfigureAwait(false))
            throw Notify("Reprocessing is unavailable in this structure.");
        return facility;
    }

    /// <summary><c>MachoBindObject(locationID, (method, args, kwargs))</c>: binds the plant of the station or structure the client is docked in.</summary>
    [Method("MachoBindObject")]
    public async ValueTask<PyValue> MachoBindObject(ServiceCall call)
    {
        var parameters = call.Arg(0) switch { PyTuple t when t.Items.Count > 0 => t.Items[0], PyList l when l.Items.Count > 0 => l.Items[0], var other => other };
        if (IdOf(parameters) is not { } facilityId || call.Session.DockedLocationId != facilityId)
            throw Notify("Reprocessing is available in the station or structure you are docked in.");
        var station = stations.Get(facilityId);
        var isStructure = station is null;
        if (isStructure)
        {
            if (structures is null || structureReprocessingAccess is null)
                throw Notify("Reprocessing is unavailable in this structure.");
            var structure = await structures.GetAsync(facilityId, call.Cancellation).ConfigureAwait(false);
            var services = structure is null
                ? new Dictionary<int, int>()
                : await ReadStructureServicesAsync(facilityId, call.Cancellation).ConfigureAwait(false);
            if (structure is null || !structure.Snapshot.CoreInstalled || structure.Snapshot.DestroyedMs != 0
                || services.GetValueOrDefault(StructureServiceIds.Reprocessing) != ServiceState.Online
                || !await structureReprocessingAccess(CharacterOf(call.Session), facilityId, call.Cancellation).ConfigureAwait(false))
                throw Notify("Reprocessing is unavailable in this structure.");
        }
        var oid = $"N={MachoMessage.ProxyNodeId}:{BoundObjectIds.Next()}";
        _bound[oid] = new BoundFacility(facilityId, isStructure);
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
    /// <c>GetQuotes(itemIDs, activeShipID)</c>: the facility tax, the facility's efficiency per type, and the quote of each item. A stack too
    /// large for the client is quoted as the sum of its chunks. The answer is <c>[tax, {typeID: efficiency}, {itemID: quote}]</c>.
    /// </summary>
    [Method("GetQuotes")]
    public async ValueTask<PyValue> GetQuotes(ServiceCall call)
    {
        var facility = await BoundFacilityAsync(call).ConfigureAwait(false);
        await using var connection = await source.OpenConnectionAsync(call.Cancellation).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(call.Cancellation).ConfigureAwait(false);
        var plan = await PlanAsync(connection, transaction, call.Session, facility, ItemIdsOf(call.Arg(0)), call.Cancellation).ConfigureAwait(false);
        var efficiencies = new List<KeyValuePair<PyValue, PyValue>> { new(PyNone.Instance, new PyReal(plan.DefaultEfficiency)) };
        foreach (var typeId in plan.Lines.Select(l => l.Quote.TypeId).Distinct())
            efficiencies.Add(new(new PyInt(typeId), new PyReal(plan.Efficiencies.GetValueOrDefault(typeId, plan.DefaultEfficiency))));
        var quotes = plan.Lines.Select(l => new KeyValuePair<PyValue, PyValue>(InventoryWire.Id(l.Item.Id), QuoteValue(l.Quote)));
        return new PyList([new PyReal(plan.FacilityTax), new PyDict(efficiencies), new PyDict([.. quotes])]);
    }

    [Method("Reprocess")]
    public async ValueTask<PyValue> Reprocess(ServiceCall call)
    {
        var facility = await BoundFacilityAsync(call).ConfigureAwait(false);
        var station = facility.Id;
        var character = CharacterOf(call.Session);
        var requestedOwner = IdOf(call.Arg(2)) ?? 0;
        if (requestedOwner < 0)
            throw Notify("Invalid reprocessing output owner.");
        var requestedSourceLocation = IdOf(call.Arg(1)) ?? 0;
        if (requestedSourceLocation < 0) throw Notify("Invalid reprocessing source location.");
        var requestedLocation = IdOf(call.Arg(3));
        var requestedFlag = IdOf(call.Arg(4));
        if (requestedOwner == 0 || requestedOwner == character)
        {
            if ((requestedLocation is null) != (requestedFlag is null)
                || requestedLocation is { } location && (location != station || requestedFlag != (long)ItemFlags.Hangar))
                throw Notify("Choose a supported personal output location.");
        }

        await using var connection = await source.OpenConnectionAsync(call.Cancellation).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(call.Cancellation).ConfigureAwait(false);
        var plan = await PlanAsync(connection, transaction, call.Session, facility, ItemIdsOf(call.Arg(0)), call.Cancellation, sourceLocationId: requestedSourceLocation).ConfigureAwait(false);
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
            plan = await PlanAsync(connection, transaction, call.Session, facility, ItemIdsOf(call.Arg(0)), call.Cancellation, extraIds: created.Select(c => c.Id), sourceLocationId: requestedSourceLocation).ConfigureAwait(false);
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
        var outputOwner = character;
        if (requestedOwner > 0 && requestedOwner != character)
        {
            if (corporations is null || offices is null)
                throw Notify("You cannot use that corporation hangar.");
            if (outputFlag is < 115 or > 121
                || !await corporations.HasHangarAccessAsync(connection, transaction, character, requestedOwner, station, outputFlag,
                    take: false, token: call.Cancellation).ConfigureAwait(false))
                throw Notify("You cannot use that corporation hangar.");
            var office = await offices.GetAsync(connection, transaction, requestedOwner, station, forUpdate: true,
                cancellationToken: call.Cancellation).ConfigureAwait(false);
            if (office is null || office.OfficeFolderId != outputLocation)
                throw Notify("You cannot use that corporation hangar.");
            outputOwner = requestedOwner;
        }
        else if (outputLocation != station)
        {
            if (await items.LockAndGetAsync(connection, transaction, outputLocation, call.Cancellation).ConfigureAwait(false) is not { } place
                || !await RootAtFacilityAsync(connection, transaction, place, facility.Id, character, 0, null, null, call.Cancellation).ConfigureAwait(false))
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
                if (facility.IsStructure)
                {
                    await wallets.AdjustAsync(connection, transaction, new WalletAccount(plan.TaxRecipient, IskAccount), (decimal)plan.TotalCost,
                        character, CorporationTransactionTaxRefType, "Reprocessing tax income", now, operation + ":owner-credit",
                        "reprocessing-owner-credit", call.Cancellation, facility.Id).ConfigureAwait(false);
                }
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
                var id = await items.CreateAsync(connection, transaction, new NewItem(typeId, outputOwner, outputLocation, outputFlag, stack), call.Cancellation).ConfigureAwait(false);
                yielded.Add(new Item(id, typeId, outputOwner, outputLocation, outputFlag, stack, false, null));
                remaining -= stack;
            }
        }

        await transaction.CommitAsync(call.Cancellation).ConfigureAwait(false);
        if (plan.TotalCost > 0)
        {
            if (await balances.GetAsync(character, call.Cancellation).ConfigureAwait(false) is { } balance)
                wallets.AnnounceCommitted(new WalletBalanceChange(character, IskAccount, balance.Isk));
            if (facility.IsStructure && await wallets.AccountBalanceAsync(new WalletAccount(plan.TaxRecipient, IskAccount), call.Cancellation).ConfigureAwait(false) is { } corporationBalance)
                wallets.AnnounceCommitted(new WalletBalanceChange(plan.TaxRecipient, IskAccount, corporationBalance));
        }
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

    private sealed record Plan(double FacilityTax, double DefaultEfficiency, long TaxRecipient, IReadOnlyDictionary<int, double> Efficiencies,
        IReadOnlyList<(Item Item, ReprocessingRules.Quote Quote)> Lines,
        IReadOnlyList<(Item Item, IReadOnlyList<long> Chunks)> Oversized)
    {
        public double TotalCost => Lines.Sum(l => l.Quote.TotalIskCost);
    }

    /// <summary>
    /// The quotes of items the caller may take and which are rooted at this facility (personal hangars, corporation offices, containers,
    /// and ship cargo), not the ship the caller flies. Read under locks in the caller's transaction, so a quote and the
    /// reprocess that follows see the same stacks.
    /// </summary>
    private async Task<Plan> PlanAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, ClientSession session, BoundFacility facility, IReadOnlyList<long> itemIds,
        CancellationToken cancellationToken, IEnumerable<long>? extraIds = null, long sourceLocationId = 0)
    {
        var character = CharacterOf(session);
        if (character <= 0) throw Notify("Only a character can reprocess.");
        var characterInfo = await characters.GetAsync(character, cancellationToken).ConfigureAwait(false);
        var activeShip = characterInfo?.ShipId ?? 0;
        var corporationId = characterInfo?.CorporationId ?? 0;
        var office = corporationId > 0 && offices is not null
            ? await offices.GetAsync(connection, transaction, corporationId, facility.Id, cancellationToken: cancellationToken).ConfigureAwait(false)
            : null;
        var structure = facility.IsStructure
            ? await structures!.GetAsync(connection, transaction, facility.Id, true, cancellationToken).ConfigureAwait(false)
            : null;
        if (facility.IsStructure && (structure is null || !structure.Snapshot.CoreInstalled || structure.Snapshot.DestroyedMs != 0))
            throw Notify("Reprocessing is unavailable in this structure.");
        if (facility.IsStructure &&
            (await structures!.ServicesAsync(connection, transaction, facility.Id, cancellationToken).ConfigureAwait(false))
                .GetValueOrDefault(StructureServiceIds.Reprocessing) != ServiceState.Online)
            throw Notify("Reprocessing is unavailable in this structure.");
        var station = facility.IsStructure ? null : stations.Get(facility.Id);
        if (!facility.IsStructure && station is null)
            throw Notify("Reprocessing is available in the station you are docked in.");

        var locked = new List<Item>();
        var corporationTakeAccess = new Dictionary<int, bool>();
        foreach (var id in itemIds.Concat(extraIds ?? []).Distinct())
        {
            if (activeShip > 0 && id == activeShip) throw Notify("You cannot reprocess the ship you are flying.");
            if (await items.LockAndGetAsync(connection, transaction, id, cancellationToken).ConfigureAwait(false) is { } item
                && ReprocessingRules.MatchesSourceLocation(sourceLocationId, item.LocationId)
                && await RootAtFacilityAsync(connection, transaction, item, facility.Id, character, corporationId, office, corporationTakeAccess, cancellationToken).ConfigureAwait(false))
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

        var defaultEfficiency = facility.IsStructure ? ReprocessingRules.DefaultStationEfficiency
            : await data.StationEfficiencyAsync(facility.Id, cancellationToken).ConfigureAwait(false);
        var taxRate = facility.IsStructure
            ? StructureTaxRate(structure!)
            : ReprocessingRules.StationTax(station!.ReprocessingStationsTake,
                await StandingForTaxAsync(connection, transaction, character, station, cancellationToken).ConfigureAwait(false));
        var taxRecipient = facility.IsStructure ? structure!.OwnerCorporationId : station!.OwnerId;
        IReadOnlyList<Item> structureRigs = [];
        if (facility.IsStructure)
        {
            structureRigs = await items.LockInLocationFlagRangeAsync(connection, transaction, facility.Id, structure!.OwnerCorporationId,
                StructureRigFirstFlag, StructureRigLastFlag, cancellationToken).ConfigureAwait(false);
            await statics.EnsureTypesAsync([structure.TypeId, .. structureRigs.Select(rig => rig.TypeId)], cancellationToken).ConfigureAwait(false);
        }
        var prices = await data.AdjustedPricesAsync(materialTypes, cancellationToken).ConfigureAwait(false);

        var lines = new List<(Item, ReprocessingRules.Quote)>();
        var efficiencies = new Dictionary<int, double>();
        var oversized = new List<(Item, IReadOnlyList<long>)>();
        foreach (var item in locked)
        {
            if (!typeInfo.TryGetValue(item.TypeId, out var info) || info.Materials.Count == 0) continue;
            var facilityEfficiency = facility.IsStructure
                ? await StructureEfficiencyAsync(structure!, structureRigs, info, cancellationToken).ConfigureAwait(false)
                : defaultEfficiency;
            efficiencies[item.TypeId] = facilityEfficiency;
            var characterMultiplier = info.ReprocessingSkillType > 0
                ? Multiplier(ReprocessingRules.ReprocessingSkill) * Multiplier(ReprocessingRules.ReprocessingEfficiencySkill) * Multiplier(info.ReprocessingSkillType) * implantBonus
                : Multiplier(ReprocessingRules.ScrapmetalProcessingSkill);
            var efficiency = ReprocessingRules.Efficiency(facilityEfficiency, characterMultiplier);
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
        return new Plan(taxRate, defaultEfficiency, taxRecipient, efficiencies, lines, oversized);
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

    /// <summary>True when a personal item is rooted at the facility, or a corporation item is rooted in an office the caller may take from.</summary>
    private async Task<bool> RootAtFacilityAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Item item, long facility, long character,
        long corporationId, CorporationOffice? office, Dictionary<int, bool>? corporationTakeAccess, CancellationToken cancellationToken)
    {
        var root = facility;
        if (item.OwnerId != character)
        {
            if (corporationId <= 0 || item.OwnerId != corporationId || office is null || corporations is null || item.Flag is < 115 or > 121)
                return false;
            if (corporationTakeAccess is not null)
            {
                if (!corporationTakeAccess.TryGetValue(item.Flag, out var mayTake))
                {
                    mayTake = await corporations.HasHangarAccessAsync(connection, transaction, character, corporationId, facility, item.Flag,
                        take: true, token: cancellationToken).ConfigureAwait(false);
                    corporationTakeAccess[item.Flag] = mayTake;
                }
                if (!mayTake) return false;
            }
            else if (!await corporations.HasHangarAccessAsync(connection, transaction, character, corporationId, facility, item.Flag,
                         take: true, token: cancellationToken).ConfigureAwait(false))
            {
                return false;
            }
            root = office.OfficeFolderId;
        }
        var current = item;
        for (var depth = 0; depth < MaxLocationDepth; depth++)
        {
            if (current.LocationId == root) return true;
            var parent = await items.LockAndGetAsync(connection, transaction, current.LocationId, cancellationToken);
            if (parent is null) return false;
            current = parent;
        }
        return false;
    }

    private async Task<double> StructureEfficiencyAsync(StructureRecord structure, IReadOnlyList<Item> rigs, ReprocessingTypeInfo info,
        CancellationToken cancellationToken)
    {
        var family = await ReprocessingFamilyAsync(info, cancellationToken).ConfigureAwait(false);
        var efficiency = ReprocessingRules.DefaultStationEfficiency;
        if (family is not ("ore" or "moon_ore" or "ice"))
            return efficiency;

        var structureType = statics.Type(structure.TypeId);
        var rigSize = structureType?.Attributes.GetValueOrDefault(StructureRigSizeAttribute) ?? 0;
        var security = solarSystems?.Get(structure.SolarSystemId)?.Security ?? 0;
        var securityAttribute = security >= 0.45 ? HighSecurityRigMultiplierAttribute
            : security > 0 ? LowSecurityRigMultiplierAttribute : NullSecurityRigMultiplierAttribute;
        var familyEffect = family switch
        {
            "ore" => AsteroidOreRigEffect,
            "moon_ore" => MoonOreRigEffect,
            "ice" => IceRigEffect,
            _ => 0,
        };
        var bestRig = 0d;
        foreach (var rig in rigs)
        {
            var type = statics.Type(rig.TypeId);
            if (type is null || (rigSize > 0 && type.Attributes.GetValueOrDefault(StructureRigSizeAttribute) != rigSize)
                || !type.EffectIds.Contains(familyEffect))
                continue;
            var baseMultiplier = type.Attributes.GetValueOrDefault(RefiningYieldMultiplierAttribute);
            var securityMultiplier = type.Attributes.GetValueOrDefault(securityAttribute, 1d);
            bestRig = Math.Max(bestRig, baseMultiplier * securityMultiplier);
        }
        if (bestRig > 0)
            efficiency = bestRig;
        if (structureType is not null)
            efficiency *= 1 + structureType.Attributes.GetValueOrDefault(StructureReprocessingYieldBonusAttribute) / 100d;
        return Math.Clamp(efficiency, 0, 1);
    }

    private async Task<string> ReprocessingFamilyAsync(ReprocessingTypeInfo info, CancellationToken cancellationToken)
    {
        if (info.ReprocessingSkillType == 18025) return "ice";
        if (info.ReprocessingSkillType is >= 46152 and <= 46156) return "moon_ore";
        if (info.ReprocessingSkillType is 12189 or 12195 or >= 60377 and <= 60381) return "ore";
        return (await types.GetAsync(info.TypeId, cancellationToken).ConfigureAwait(false))?.CategoryId == 25 ? "ore" : "general";
    }

    private static double StructureTaxRate(StructureRecord structure)
    {
        var node = structure.Doc["industryTaxRate"] ?? structure.Doc["taxRate"];
        if (node is null || !decimal.TryParse(node.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var rate))
            return 0;
        if (rate > 1) rate /= 100m;
        return (double)Math.Clamp(rate, 0m, 1m);
    }

    private async Task<IReadOnlyDictionary<int, int>> ReadStructureServicesAsync(long structureId, CancellationToken cancellationToken)
    {
        if (structures is null) return new Dictionary<int, int>();
        await using var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await structures.ServicesAsync(connection, null, structureId, cancellationToken).ConfigureAwait(false);
    }

    private async Task<double> StandingForTaxAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long character, Eve.Universe.Station station,
        CancellationToken cancellationToken)
    {
        var personal = await relations.GetAsync(RelationKinds.Standing.Name, station.OwnerId, character, connection, transaction, cancellationToken).ConfigureAwait(false);
        var affiliation = await relations.AffiliationOfAsync(character, connection, transaction, cancellationToken).ConfigureAwait(false);
        var corporationStanding = affiliation.Corporation is { } corporation
            ? await relations.ValueAsync(RelationKinds.Standing.Name, station.OwnerId, corporation, connection: connection, transaction: transaction,
                cancellationToken: cancellationToken).ConfigureAwait(false)
            : 0;

        // A missing row is not an eligible zero standing for Connections.
        var personalStanding = personal?.Value ?? 0;
        if (personal is { } relation && station.FactionId is { } factionId
            && AgentAccessRule.BonusSkill(relation.Value, factionId) is { } bonusSkill)
        {
            var level = await skills.LevelAsync(connection, transaction, character, bonusSkill, cancellationToken).ConfigureAwait(false);
            personalStanding = AgentAccessRule.EffectiveStanding(relation.Value, factionId, skill => skill == bonusSkill ? level : 0);
        }
        return Math.Max(personalStanding, corporationStanding);
    }

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
