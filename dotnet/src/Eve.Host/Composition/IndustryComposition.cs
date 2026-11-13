using System.Net;
using System.Net.Sockets;
using Eve.Characters;
using Eve.Data;
using Eve.Host;
using Eve.Identity;
using Eve.Dogma;
using Eve.Fitting;
using Eve.GameStore;
using System.Text.Json;
using System.Text.Json.Nodes;
using Eve.Inventory;
using Eve.Ships;
using Eve.Marshal;
using Eve.Npc;
using Eve.Sde;
using Eve.Skills;
using Eve.Universe;
using Eve.Wallet;
using Npgsql;
using Sentry;
using Eve.Services;
using Eve.Services.CharacterSelect;
using Eve.Services.Core;
using Eve.Services.Dogma;
using Eve.Services.Inventory;
using Eve.Services.CharacterInfo;
using Eve.Services.Skills;
using Eve.Services.Slash;
using Eve.Services.Space;
using Eve.Space;
using Eve.Services.Station;
using Eve.Services.Wallet;
using static Eve.Host.HostConfiguration;

namespace Eve.Host;

internal sealed partial class CharacterServicesComposition
{
    private const int IndustryOfficeTypeId = 27;
    private const int IndustryUnlockedContainerFlagId = 64;
    private static readonly IReadOnlySet<int> IndustryContainerGroupIds = new HashSet<int> { 12, 340, 448, 649 };

    private static readonly IReadOnlyDictionary<int, int> StructureIndustryTaxSettingIds = new Dictionary<int, int>
    {
        [Eve.Structures.StructureServiceIds.ManufacturingBasic] = 29,
        [Eve.Structures.StructureServiceIds.ManufacturingCapital] = 30,
        [Eve.Structures.StructureServiceIds.ManufacturingSupercapital] = 31,
        [Eve.Structures.StructureServiceIds.LaboratoryResearchTime] = 32,
        [Eve.Structures.StructureServiceIds.LaboratoryResearchMaterial] = 32,
        [Eve.Structures.StructureServiceIds.LaboratoryCopying] = 32,
        [Eve.Structures.StructureServiceIds.LaboratoryInvention] = 33,
        [Eve.Structures.StructureServiceIds.ReactionsComposite] = 28,
        [Eve.Structures.StructureServiceIds.ReactionsBiochemical] = 26,
        [Eve.Structures.StructureServiceIds.ReactionsHybrid] = 27,
    };

    static Eve.Marshal.PyValue MailingListJoinValue(
        long listId, JsonObject list, long characterId, long role) =>
        Py.KeyVal([
            ("id", new Eve.Marshal.PyLong(listId)),
            ("name", new Eve.Marshal.PyStr(MailingListJsonText(list["name"]))),
            ("displayName", new Eve.Marshal.PyStr(MailingListJsonText(list["displayName"]))),
            ("isMuted", role == 0 ? Eve.Marshal.PyBool.True : Eve.Marshal.PyBool.False),
            ("isOperator", role == 2 ? Eve.Marshal.PyBool.True : Eve.Marshal.PyBool.False),
            ("isOwner", MailingListJsonLong(list["ownerID"]) == characterId ? Eve.Marshal.PyBool.True : Eve.Marshal.PyBool.False),
        ]);

    async ValueTask<PersistenceIndustryJobOperations.IndustryFacility?> ResolveIndustryFacilityAsync(
        long facilityId, long stationId, long solarSystemId, long characterId, CancellationToken token)
    {
        if (stations.Get(facilityId) is { } station)
        {
            if (station.SolarSystemId != solarSystemId || stationId > 0 && stationId != station.Id)
                return null;
            // An NPC station runs the activities its SDE operation lists; its factors are the job time factors.
            if (await ReadNpcStationOperationAsync(station.Id, token) is not { } operation)
                return null;
            var tax = await ReadNpcStationTaxAsync(station.OwnerId, token);
            // the factional warfare factor of this station for the pilot's war faction (the client's npsStationTaxModifier)
            tax *= (decimal)Eve.FactionWarfare.FwRules.NpcStationTaxModifier(station.Id, await WarFactionOfAsync(characterId, token));
            return new PersistenceIndustryJobOperations.IndustryFacility(
                station.Id, station.SolarSystemId, station.OwnerId, tax, operation.ManufacturingFactor,
                true, operation.ResearchFactor, operation.Services);
        }

        if (stationId > 0)
            return null;
        var persisted = await structureDirectoryReadModel.GetStructureInfoAsync(facilityId, token);
        if (persisted is not { PersistentlyDeployed: true })
            return null;

        var structures = await ReadGameStoreTableAsync("structures", token);
        if (structures[facilityId.ToString(System.Globalization.CultureInfo.InvariantCulture)] is not JsonObject structure)
            return null;
        return await ResolveIndustryStructureFacilityAsync(facilityId, solarSystemId, characterId, structure, token);
    }

    async ValueTask<PersistenceIndustryJobOperations.IndustryFacility?> ResolveIndustryStructureFacilityAsync(
        long facilityId, long solarSystemId, long characterId, JsonObject structure, CancellationToken token)
    {
        if (IndustryLong(structure["solarSystemID"]) != solarSystemId)
            return null;

        // The online service ids of the structure (its service modules) decide the activities it runs (IR.StructureActivities).
        var services = structure["services"] as JsonObject ?? structure["serviceStates"] as JsonObject;
        var online = new List<int>();
        if (services is not null)
            foreach (var (key, value) in services)
                if (int.TryParse(key, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var serviceId) &&
                    IndustryLong(value) == 1)  // STRUCTURE_SERVICE_STATE.ONLINE
                    online.Add(serviceId);
        var activities = Eve.Services.Core.IndustryRules.StructureActivities(online);
        if (activities.Count == 0)
            return null;

        var ownerCorporationId = IndustryLong(structure["ownerCorpID"] ?? structure["corporationID"]);
        if (ownerCorporationId <= 0)
            return null;
        var taxRate = IndustryDecimal(structure["industryTaxRate"] ?? structure["taxRate"]);
        if (taxRate > 1)
            taxRate /= 100m;
        taxRate = Math.Max(0m, taxRate);
        var timeMultiplier = IndustryDouble(structure["industryTimeMultiplier"], 1d);
        if (!double.IsFinite(timeMultiplier) || timeMultiplier <= 0)
            return null;
        var serviceTaxes = await StructureIndustryServiceTaxesAsync(
            facilityId, characterId, services, taxRate, token);
        return new PersistenceIndustryJobOperations.IndustryFacility(
            facilityId, solarSystemId, ownerCorporationId, taxRate, timeMultiplier, true, null, null, activities,
            await StructureBonusesAsync(facilityId, IndustryLong(structure["typeID"]), solarSystemId, token), serviceTaxes);
    }

    async ValueTask<IReadOnlyDictionary<int, decimal>> StructureIndustryServiceTaxesAsync(
        long structureId,
        long characterId,
        JsonObject? serviceStates,
        decimal fallbackRate,
        CancellationToken token)
    {
        var rates = new Dictionary<int, decimal>();
        if (characterId <= 0 || serviceStates is null)
            return rates;
        foreach (var (rawServiceId, state) in serviceStates)
        {
            if (!int.TryParse(rawServiceId, System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out var serviceId) ||
                IndustryLong(state) != Eve.Structures.ServiceState.Online ||
                !StructureIndustryTaxSettingIds.TryGetValue(serviceId, out var settingId) ||
                !await structureDirectoryReadModel.CharacterHasServiceAsync(characterId, structureId, serviceId, token))
                continue;

            var value = await structureDirectoryReadModel.CharacterGetServiceAsync(characterId, structureId, serviceId, token);
            var rate = value switch
            {
                PyReal real when double.IsFinite(real.Value) => (decimal)real.Value / 100m,
                PyInt integer => integer.Value / 100m,
                PyLong integer => integer.Value / 100m,
                _ => fallbackRate,
            };
            rates[serviceId] = Math.Max(0m, rate);
        }
        return rates;
    }

    /// <summary>
    /// The facility tax of an NPC station: the SDE <c>npcCorporations.taxRate</c> of the station's owning corporation

    /// </summary>
    async ValueTask<decimal> ReadNpcStationTaxAsync(long ownerId, CancellationToken token)
    {
        var rows = await sde.ReadAsync("npcCorporations", [ownerId], token);
        if (rows.Count == 0 || !rows[0].Doc.TryGetProperty("taxRate", out var rate) || rate.ValueKind != JsonValueKind.Number)
            return 0m;
        return Math.Clamp(rate.GetDecimal(), 0m, 1m);
    }

    /// <summary>
    /// The facility modifiers of a structure, from the SDE: its own hull (role) bonuses from its type, and the bonuses of the rigs
    /// fitted in its rig slots (the items of its container in flags 92 to 99). The security band is the structure's system's.
    /// </summary>
    async ValueTask<IReadOnlyList<Eve.Services.Core.IndustryBonus>> StructureBonusesAsync(
        long structureId, long typeId, long solarSystemId, CancellationToken token)
    {
        var bonuses = new List<Eve.Services.Core.IndustryBonus>();
        if (typeId > 0)
        {
            var typeRows = await sde.ReadAsync("typeDogma", [typeId], token);
            if (typeRows.Count > 0)
                bonuses.AddRange(Eve.Services.Core.IndustryBonuses.HullBonuses(
                    Eve.Services.Core.IndustryBonuses.ParseAttributes(typeRows[0].Doc)));
        }
        var systemRows = await sde.ReadAsync("mapSolarSystems", [solarSystemId], token);
        var security = systemRows.Count > 0 && systemRows[0].Doc.TryGetProperty("securityStatus", out var status) &&
            status.ValueKind == System.Text.Json.JsonValueKind.Number
            ? status.GetDouble()
            : 0d;
        foreach (var item in await items.InContainerAsync(structureId, token))
        {
            if (!Eve.Services.Core.IndustryBonuses.IsRigSlot(item.Flag) ||
                await ReadRigFitAsync(item.TypeId, token) is not { } rig)
                continue;
            bonuses.AddRange(Eve.Services.Core.IndustryBonuses.RigBonuses(rig, security));
        }
        return bonuses;
    }

    /// <summary>A fitted rig's SDE dogma: its typeDogma row and the dogmaEffects rows of its effects (null for an unknown type).</summary>
    async ValueTask<Eve.Services.Core.RigFit?> ReadRigFitAsync(int typeId, CancellationToken token)
    {
        var typeRows = await sde.ReadAsync("typeDogma", [(long)typeId], token);
        if (typeRows.Count == 0)
            return null;
        var effects = new List<System.Text.Json.JsonElement>();
        foreach (var effectId in Eve.Services.Core.IndustryBonuses.EffectIds(typeRows[0].Doc))
        {
            var rows = await sde.ReadAsync("dogmaEffects", [(long)effectId], token);
            if (rows.Count > 0)
                effects.Add(rows[0].Doc);
        }
        return Eve.Services.Core.IndustryBonuses.ParseRig(typeId, typeRows[0].Doc, effects);
    }

    /// <summary>
    /// The system cost index applied to every system and activity. No source loaded here carries live indices
    /// (the SDE has none), so EVE_INDUSTRY_COST_INDEX (a fraction, default 0) is the project rule.
    /// </summary>
    static readonly decimal ConfiguredIndustryCostIndex = ParseCostIndex(Environment.GetEnvironmentVariable("EVE_INDUSTRY_COST_INDEX"));

    static decimal ParseCostIndex(string? text) =>
        decimal.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value)
            ? Math.Clamp(value, 0m, 1m)
            : 0m;

    private static readonly System.Net.Http.HttpClient industryIndexHttp = new();

    private IIndustryCostIndexSource industryCostIndexes = default!;
    private IndustryReadyJobs industryReady = default!;

    /// <summary>The stored ESI cost indices, refreshed at most daily; EVE_INDUSTRY_INDEX_SEED=skip makes no call out.</summary>
    private IIndustryCostIndexSource IndustryCostIndexSetup()
    {
        var esi = Env("EVE_INDUSTRY_INDEX_SEED", "auto") is "skip" or "off"
            ? null
            : new Eve.Market.EsiIndustrySystems(industryIndexHttp, Eve.Market.EsiIndustrySystems.DefaultBase);
        var cache = new Eve.Market.IndustryCostIndexCache(
            new Eve.Market.IndustryCostIndexStore(source, marketSchema), esi, () => now(),
            message => Console.Error.WriteLine("eve-host: " + message));
        return new StoredIndustryCostIndexes(cache, ConfiguredIndustryCostIndex);
    }

    /// <summary>The factors and services of an NPC station's SDE operation (npcStations, then stationOperations).</summary>
    async ValueTask<(double ManufacturingFactor, double ResearchFactor, IReadOnlySet<int> Services)?> ReadNpcStationOperationAsync(
        long stationId, CancellationToken token)
    {
        var stationRows = await sde.ReadAsync("npcStations", [stationId], token);
        if (stationRows.Count == 0 || !stationRows[0].Doc.TryGetProperty("operationID", out var operationId) ||
            operationId.ValueKind != JsonValueKind.Number)
            return null;
        var operationRows = await sde.ReadAsync("stationOperations", [operationId.GetInt64()], token);
        if (operationRows.Count == 0)
            return null;
        var operation = operationRows[0].Doc;
        var services = new HashSet<int>();
        if (operation.TryGetProperty("services", out var list) && list.ValueKind == JsonValueKind.Array)
            foreach (var service in list.EnumerateArray())
                if (service.ValueKind == JsonValueKind.Number)
                    services.Add(service.GetInt32());
        return (SdeFactor(operation, "manufacturingFactor"), SdeFactor(operation, "researchFactor"), services);
    }

    static double SdeFactor(System.Text.Json.JsonElement row, string field) =>
        row.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.Number && value.GetDouble() > 0
            ? value.GetDouble()
            : 1d;

    static long IndustryLong(JsonNode? value) =>
        long.TryParse(value?.ToString(), System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var result) ? result : 0;

    static decimal IndustryDecimal(JsonNode? value) =>
        decimal.TryParse(value?.ToString(), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var result) ? result : 0;

    static double IndustryDouble(JsonNode? value, double fallback) =>
        double.TryParse(value?.ToString(), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var result) && double.IsFinite(result)
            ? result : fallback;

    static PyValue? FacilityField(PyValue? source, string name)
    {
        IEnumerable<KeyValuePair<PyValue, PyValue>> entries = source switch
        {
            PyDict dict => dict.Entries,
            PyObject { Args: PyDict args } => args.Entries,
            PyObjectEx value => value.Dict,
            _ => Array.Empty<KeyValuePair<PyValue, PyValue>>(),
        };
        foreach (var (key, value) in entries)
            if (Eve.Macho.JsSemantics.StringValue(key) == name) return value;
        return null;
    }

    static long FacilityNumber(PyValue? value) => value switch
    {
        PyInt integer => integer.Value,
        PyLong integer => integer.Value,
        PyReal real when double.IsFinite(real.Value) && real.Value >= long.MinValue &&
            real.Value <= long.MaxValue && Math.Truncate(real.Value) == real.Value => (long)real.Value,
        _ => 0,
    };


    private const int FactoryServiceId = 14;
    private const int LaboratoryServiceId = 15;

    /// <summary>An NPC station operation as the SDE gives it: the services that run activities and the job time factors.</summary>
    private sealed record NpcOperation(double ManufacturingFactor, double ResearchFactor, IReadOnlySet<int> Services);

    private Task<Dictionary<int, NpcOperation>>? npcOperationsCache;
    private Task<Dictionary<long, decimal>>? npcCorporationTaxesCache;

    /// <summary>The SDE station operations, read once: the activities an NPC station runs and its job time factors.</summary>
    private Task<Dictionary<int, NpcOperation>> NpcOperationsAsync() =>
        npcOperationsCache ??= LoadNpcOperationsAsync();

    private async Task<Dictionary<int, NpcOperation>> LoadNpcOperationsAsync()
    {
        var rows = await sde.ReadAsync("stationOperations", cancellationToken: CancellationToken.None);
        var operations = new Dictionary<int, NpcOperation>();
        foreach (var row in rows)
        {
            var services = new HashSet<int>();
            if (row.Doc.TryGetProperty("services", out var list) && list.ValueKind == JsonValueKind.Array)
                foreach (var service in list.EnumerateArray())
                    if (service.ValueKind == JsonValueKind.Number)
                        services.Add(service.GetInt32());
            operations[(int)row.Key] = new NpcOperation(SdeFactor(row.Doc, "manufacturingFactor"), SdeFactor(row.Doc, "researchFactor"), services);
        }
        return operations;
    }

    private async Task<NpcOperation?> NpcOperationAsync(int operationId) =>
        (await NpcOperationsAsync()).TryGetValue(operationId, out var operation) ? operation : null;

    /// <summary>The facility tax of each NPC corporation (npcCorporations.taxRate), read once.</summary>
    private Task<Dictionary<long, decimal>> NpcCorporationTaxesAsync() =>
        npcCorporationTaxesCache ??= LoadNpcCorporationTaxesAsync();

    private async Task<Dictionary<long, decimal>> LoadNpcCorporationTaxesAsync()
    {
        var rows = await sde.ReadAsync("npcCorporations", cancellationToken: CancellationToken.None);
        var taxes = new Dictionary<long, decimal>();
        foreach (var row in rows)
            if (row.Doc.TryGetProperty("taxRate", out var rate) && rate.ValueKind == JsonValueKind.Number)
                taxes[row.Key] = Math.Clamp(rate.GetDecimal(), 0m, 1m);
        return taxes;
    }

    /// <summary>The activities as the client reads them, with the job time factor of manufacturing and of the science activities.</summary>
    static PyValue ActivitiesPayload(
        IEnumerable<int> activityIds, double manufacturingFactor, double researchFactor,
        IReadOnlyList<Eve.Services.Core.IndustryBonus>? bonuses = null)
    {
        var entries = new List<KeyValuePair<PyValue, PyValue>>();
        foreach (var activityId in activityIds)
        {
            var factor = activityId == Eve.Services.Core.IndustryRules.Manufacturing ? manufacturingFactor : researchFactor;
            var time = new List<PyValue>();
            var material = new List<PyValue>();
            var cost = new List<PyValue>();
            if (factor != 1d)
                time.Add(BonusModifierPayload(factor, null, null, 5));
            foreach (var bonus in bonuses ?? [])
            {
                if (bonus.ActivityId != activityId)
                    continue;
                var list = bonus.Kind switch
                {
                    Eve.Services.Core.IndustryBonusKind.Time => time,
                    Eve.Services.Core.IndustryBonusKind.Material => material,
                    _ => cost,
                };
                list.Add(BonusModifierPayload(bonus.Amount, bonus.CategoryId, bonus.GroupId, bonus.Source));
            }
            entries.Add(new(new PyInt(activityId), Py.Tuple(
                new PyList(time), new PyList(material), new PyList(cost), new PyList([]), new PyList([]), new PyList([]))));
        }
        return new PyDict(entries);
    }

    /// <summary>One client modifier (amount, categoryID, groupID, invTypeID, reference) as the client's industry Facility reads it.</summary>
    static PyValue BonusModifierPayload(double amount, int? categoryId, int? groupId, int reference) =>
        Py.Tuple(
            Py.Number(amount),
            categoryId is { } category ? new PyInt(category) : (PyValue)PyNone.Instance,
            groupId is { } group ? new PyInt(group) : (PyValue)PyNone.Instance,
            PyNone.Instance,
            new PyInt(reference));

    /// <summary>The client's rigModifiers: each fitted rig's typeID and the activities it changes.</summary>
    static PyValue RigModifiersPayload(IReadOnlyList<Eve.Services.Core.IndustryBonus> bonuses) =>
        new PyDict(Eve.Services.Core.IndustryBonuses.RigActivities(bonuses).Select(rig => new KeyValuePair<PyValue, PyValue>(
            new PyInt(rig.Key),
            new PyList(rig.Value.Select(activityId => (PyValue)new PyInt(activityId)).ToList()))).ToList());

    /// <summary>The facility payload of an NPC station (the client's GetFacility shape) with its activities.</summary>
    async ValueTask<PyValue?> NpcFacilityPayloadAsync(Eve.Universe.Station station, CancellationToken token)
    {
        var operation = await NpcOperationAsync(station.OperationId);
        if (operation is null || !(operation.Services.Contains(14) || operation.Services.Contains(15)))
            return null;
        var taxes = await NpcCorporationTaxesAsync();
        var tax = taxes.TryGetValue(station.OwnerId, out var rate) ? rate : 0m;
        return Py.KeyVal(
            ("facilityID", new PyLong(station.Id)),
            ("typeID", new PyInt(station.TypeId)),
            ("ownerID", new PyLong(station.OwnerId)),
            ("tax", Py.Number((double)tax)),
            ("solarSystemID", new PyInt(station.SolarSystemId)),
            ("online", PyValue.Of(true)),
            ("serviceAccess", new PyDict([])),
            ("sccTaxModifier", Py.Number(1)),
            ("rigModifiers", new PyDict([])),
            ("globalModifiers", Py.Dict(("faction", Py.Number(1)), ("system", new PyDict([])))),
            ("activities", await FacilityActivitiesPayloadAsync(
                NpcActivityIds(operation), operation.ManufacturingFactor, operation.ResearchFactor,
                station.SolarSystemId, null, token)));
    }

    static IReadOnlySet<int> NpcActivityIds(NpcOperation operation)
    {
        var activities = new HashSet<int>();
        if (operation.Services.Contains(14))
            activities.Add(Eve.Services.Core.IndustryRules.Manufacturing);
        if (operation.Services.Contains(15))
            activities.UnionWith([
                Eve.Services.Core.IndustryRules.ResearchTime,
                Eve.Services.Core.IndustryRules.ResearchMaterial,
                Eve.Services.Core.IndustryRules.Copying,
                Eve.Services.Core.IndustryRules.Invention]);
        return activities;
    }

    async ValueTask<PyValue> FacilityActivitiesPayloadAsync(
        IEnumerable<int> activityIds,
        double manufacturingFactor,
        double researchFactor,
        long solarSystemId,
        IReadOnlyList<Eve.Services.Core.IndustryBonus>? bonuses,
        CancellationToken token)
    {
        var activitySet = activityIds.ToHashSet();
        var payload = AssertActivitiesDict(ActivitiesPayload(activitySet, manufacturingFactor, researchFactor, bonuses));
        var entries = new List<KeyValuePair<PyValue, PyValue>>();
        foreach (var (key, value) in payload.Entries)
        {
            var activityId = FacilityNumber(key);
            var row = (PyTuple)value;
            var columns = row.Items.ToArray();
            var costModifiers = (PyList)columns[2];
            var costIndex = await industryCostIndexes.GetAsync(solarSystemId, checked((int)activityId), token);
            columns[2] = new PyList([
                .. costModifiers.Items,
                Py.Tuple(Py.Number((double)costIndex), PyNone.Instance, PyNone.Instance, PyNone.Instance, new PyInt(4)),
            ]);
            entries.Add(new(key, new PyTuple(columns)));
        }
        return new PyDict(entries);
    }

    static PyDict AssertActivitiesDict(PyValue value) =>
        value as PyDict ?? throw new InvalidOperationException("Industry activity payload must be a dict.");

    /// <summary>GetFacilities: the NPC facilities and deployed structures in the caller's region, ordered by facility ID.</summary>
    async ValueTask<PyValue?> FacilitiesAsync(ServiceCall call, CancellationToken token)
    {
        var region = FacilityNumber(call.Session.Get("regionid"));
        var facilities = new List<(long FacilityId, PyValue Payload)>();
        foreach (var station in stations.All)
        {
            if (region > 0 && station.RegionId != region)
                continue;
            if (await NpcFacilityPayloadAsync(station, token) is { } payload)
                facilities.Add((station.Id, payload));
        }

        var structures = await ReadGameStoreTableAsync("structures", token);
        var structureRows = structures.Select(pair => (Id: IndustryLong(pair.Value?["structureID"] ?? pair.Key), Row: pair.Value as JsonObject))
            .Where(pair => pair.Id > 0 && pair.Row is not null).ToArray();
        var infos = await structureDirectoryReadModel.GetStructureInfosAsync(
            structureRows.Select(pair => pair.Id).ToArray(), token);
        var solarSystemIds = structureRows.Select(pair => IndustryLong(pair.Row!["solarSystemID"]))
            .Where(id => id > 0).Distinct().ToArray();
        var solarSystems = await sde.ReadAsync("mapSolarSystems", solarSystemIds, token);
        var regionBySystem = solarSystems.ToDictionary(
            row => row.Key,
            row => row.Doc.TryGetProperty("regionID", out var regionId) && regionId.ValueKind == System.Text.Json.JsonValueKind.Number
                ? regionId.GetInt64() : 0);
        foreach (var (facilityId, structure) in structureRows)
        {
            if (!infos.TryGetValue(facilityId, out var info) || info is not { PersistentlyDeployed: true })
                continue;
            var systemId = IndustryLong(structure!["solarSystemID"]);
            if (!regionBySystem.TryGetValue(systemId, out var structureRegion) || structureRegion != region)
                continue;
            var facility = await ResolveIndustryFacilityAsync(
                facilityId, 0, systemId, call.Session.CharacterId ?? 0, token);
            if (facility is null)
                continue;
            var typeId = checked((int)IndustryLong(structure["typeID"]));
            facilities.Add((facilityId, await IndustryFacilityPayloadAsync(
                facilityId, typeId, facility, null, structure, token)));
        }

        return new PyList(facilities.OrderBy(pair => pair.FacilityId).Select(pair => pair.Payload).ToArray());
    }

    /// <summary>GetFacilityTaxes: the SDE NPC-corporation rate or the deployed structure's saved industry rate.</summary>
    async ValueTask<PyValue?> FacilityTaxesAsync(long facilityId, CancellationToken token)
    {
        if (stations.Get(facilityId) is { } station)
        {
            var taxes = await NpcCorporationTaxesAsync();
            var rate = taxes.TryGetValue(station.OwnerId, out var value) ? value : 0m;
            return TaxPayload(new JsonObject { ["taxCorporation"] = JsonValue.Create(rate) });
        }
        if (facilityId > 0 && await structureDirectoryReadModel.GetStructureInfoAsync(facilityId, token) is { PersistentlyDeployed: true })
        {
            var record = await structureStore.GetAsync(facilityId, token);
            if (record is not null && record.Snapshot.DestroyedMs == 0)
                return TaxPayload(record.Doc["industryTaxSettings"] as JsonObject, StructureTaxCorporationRate(record.Doc));
        }
        return TaxPayload(null, null);
    }

    static readonly string[] FacilityTaxFields =
    [
        "taxCorporation", "taxAlliance", "taxStandingsHorrible", "taxStandingsBad",
        "taxStandingsNeutral", "taxStandingsGood", "taxStandingsHigh",
    ];

    static decimal StructureTaxCorporationRate(JsonObject structure)
    {
        var rate = IndustryDecimal(structure["industryTaxRate"] ?? structure["taxRate"]);
        if (rate > 1)
            rate /= 100m;
        return Math.Max(0m, rate);
    }

    static PyValue TaxPayload(JsonObject? settings, decimal? fallbackRate = null) => Py.KeyVal(
        FacilityTaxFields.Select(field => new KeyValuePair<PyValue, PyValue>(new PyStr(field),
            settings?.ContainsKey(field) == true
                ? settings[field] is JsonNode configured ? Py.Number((double)IndustryDecimal(configured)) : PyNone.Instance
                : field == "taxCorporation" && fallbackRate is { } rate
                    ? Py.Number((double)rate)
                    : PyNone.Instance)).ToArray());

    static PyValue? TaxInputValue(PyValue? values, string field)
    {
        if (values is PyDict dict)
            return dict.Entries.FirstOrDefault(entry => Eve.Macho.JsSemantics.StringValue(entry.Key) == field).Value;
        if (values is PyObject { Args: PyDict args })
            return args.Entries.FirstOrDefault(entry => Eve.Macho.JsSemantics.StringValue(entry.Key) == field).Value;
        if (values is PyObjectEx objectEx)
            return objectEx.Dict.FirstOrDefault(entry => Eve.Macho.JsSemantics.StringValue(entry.Key) == field).Value;
        return null;
    }

    static decimal? TaxInputNumber(PyValue? value) => value switch
    {
        PyNone => null,
        PyReal real when double.IsFinite(real.Value) && real.Value <= (double)decimal.MaxValue && real.Value >= (double)decimal.MinValue => Math.Max(0m, (decimal)real.Value),
        PyInt integer => Math.Max(0m, integer.Value),
        PyLong integer => Math.Max(0m, integer.Value),
        _ => null,
    };

    async ValueTask<PyValue?> SetFacilityTaxesAsync(ServiceCall call, CancellationToken token)
    {
        var facilityId = FacilityNumber(call.Arg(0));
        var ownerId = FacilityNumber(call.Arg(1));
        var characterId = call.Session.CharacterId ?? 0;
        var currentCorporationId = FacilityNumber(call.Session.Get("corpid"));
        if (facilityId <= 0 || ownerId <= 0 || ownerId != currentCorporationId || characterId <= 0 ||
            stations.Get(facilityId) is not null ||
            await structureDirectoryReadModel.GetStructureInfoAsync(facilityId, token) is not { PersistentlyDeployed: true })
            throw MachoErrors.ThrowUserError("CustomNotify", ("notify", new PyStr("That facility could not be found.")));

        var record = await structureStore.GetAsync(facilityId, token);
        if (record is null || record.Snapshot.DestroyedMs > 0 || record.OwnerCorporationId != ownerId)
            throw MachoErrors.ThrowUserError("CustomNotify", ("notify", new PyStr("You cannot configure taxes for that facility.")));
        if (await ResolveIndustryFacilityAsync(
                facilityId, 0, record.SolarSystemId, characterId, token) is null)
            throw MachoErrors.ThrowUserError("CustomNotify", ("notify", new PyStr("That facility could not be found.")));
        if (await corporations.GetAsync(ownerId, token) is not { IsNpc: false } corporation ||
            await corporations.MemberAsync(characterId, token) is not { } member || member.CorporationId != ownerId)
            throw MachoErrors.ThrowUserError("CustomNotify", ("notify", new PyStr("You cannot configure taxes for that facility.")));
        var roles = Eve.Corporations.CorporationRoles.Session(
            corporation, member, null, await corporations.TitlesAsync(ownerId, token));
        if ((roles.AtAll & Eve.Corporations.CorporationRoles.FactoryManager) == 0)
            throw MachoErrors.ThrowUserError("CustomNotify", ("notify", new PyStr("You need factory manager access to configure this facility.")));

        var settings = record.Doc["industryTaxSettings"] is JsonObject existing
            ? (JsonObject)existing.DeepClone()
            : new JsonObject();
        foreach (var field in FacilityTaxFields)
        {
            var input = TaxInputValue(call.Arg(2), field);
            if (input is null)
                continue;
            settings[field] = TaxInputNumber(input) is { } value ? JsonValue.Create(value) : null;
        }

        await using (var connection = await source.OpenConnectionAsync(token))
        await using (var transaction = await connection.BeginTransactionAsync(token))
        {
            if (!await structureStore.SetIndustryTaxSettingsAsync(
                    connection, transaction, facilityId, ownerId, settings, token))
                throw MachoErrors.ThrowUserError("CustomNotify", ("notify", new PyStr("Failed to persist facility tax settings.")));
            await transaction.CommitAsync(token);
        }

        foreach (var session in characterSessions.All())
            session.SendNotification("OnFacilitiesUpdated", "clientID", new PyList([new PyLong(facilityId)]));
        return PyNone.Instance;
    }

    async ValueTask<PyValue> IndustryFacilityPayloadAsync(
        long facilityId,
        int typeId,
        PersistenceIndustryJobOperations.IndustryFacility facility,
        NpcOperation? operation,
        JsonObject? structureDoc,
        CancellationToken token)
    {
        var activityPayload = await FacilityActivitiesPayloadAsync(
            operation is not null ? NpcActivityIds(operation) : facility.Activities ?? new HashSet<int>(),
            operation?.ManufacturingFactor ?? facility.TimeMultiplier,
            operation?.ResearchFactor ?? facility.ResearchFactor,
            facility.SolarSystemId,
            facility.ModifierBonuses,
            token);
        var serviceTaxes = structureDoc is null
            ? new Dictionary<int, decimal>()
            : facility.ServiceTaxRates ?? new Dictionary<int, decimal>();
        return Py.KeyVal(
            ("facilityID", new PyLong(facility.FacilityId)),
            ("typeID", new PyInt(typeId)),
            ("ownerID", new PyLong(facility.OwnerId)),
            ("tax", structureDoc is null ? Py.Number((double)facility.TaxRate) : PyNone.Instance),
            ("solarSystemID", new PyInt(facility.SolarSystemId)),
            ("online", PyValue.Of(facility.IsOnline)),
            ("serviceAccess", new PyDict(serviceTaxes.Select(pair => new KeyValuePair<PyValue, PyValue>(
                new PyInt(pair.Key), Py.Number((double)pair.Value))).ToArray())),
            ("sccTaxModifier", Py.Number(1)),
            ("rigModifiers", RigModifiersPayload(facility.ModifierBonuses)),
            ("globalModifiers", Py.Dict(("faction", Py.Number(1)), ("system", new PyDict([])))),
            ("activities", activityPayload));
    }

    /// <summary>
    /// GetFacilityLocations: the station hangar of an NPC station, as the client's industry.Location object (itemID, typeID,

    /// </summary>
    static PyObjectEx IndustryLocation(
        long itemId, long inventoryLocationId, int typeId, long ownerId, int flagId,
        long solarSystemId, bool canView, bool canTake, bool includeInventoryMapping = false)
    {
        var fields = new List<KeyValuePair<PyValue, PyValue>>
        {
            new(new PyStr("itemID"), new PyLong(itemId)),
            new(new PyStr("typeID"), new PyInt(typeId)),
            new(new PyStr("ownerID"), new PyLong(ownerId)),
            new(new PyStr("flagID"), new PyInt(flagId)),
            new(new PyStr("solarSystemID"), new PyInt(checked((int)solarSystemId))),
            new(new PyStr("canView"), PyValue.Of(canView)),
            new(new PyStr("canTake"), PyValue.Of(canTake)),
        };
        if (includeInventoryMapping)
            fields.Add(new(new PyStr("inventoryLocationID"), new PyLong(inventoryLocationId)));
        return new PyObjectEx(ObjectExKind.Ex1, new PyStr("industry.Location"), [], fields);
    }

    async ValueTask<PyValue> FacilityLocationsAsync(
        long facilityId, long ownerId, bool corporate, long characterId, CancellationToken token,
        bool includeInventoryMapping = false)
    {
        if (facilityId <= 0 || ownerId <= 0 || characterId <= 0)
            return new PyList([]);
        if (stations.Get(facilityId) is { } station)
        {
            var facility = await ResolveIndustryFacilityAsync(
                facilityId, facilityId, station.SolarSystemId, characterId, token);
            if (facility is null)
                return new PyList([]);
            if (!corporate)
            {
                if (ownerId != characterId)
                    return new PyList([]);
                var root = IndustryLocation(station.Id, station.Id, station.TypeId, ownerId,
                    Eve.Services.Core.IndustryRules.PersonalStationHangarFlag, station.SolarSystemId, true, true,
                    includeInventoryMapping);
                var nested = await IndustryContainerLocationsAsync(
                    [root], [station.Id], ownerId, false, token, includeInventoryMapping);
                return new PyList([root, .. nested]);
            }
            return await CorporationFacilityLocationsAsync(
                facilityId, ownerId, characterId, station.TypeId, station.SolarSystemId, token,
                includeInventoryMapping);
        }

        if (await structureDirectoryReadModel.GetStructureInfoAsync(facilityId, token) is not { PersistentlyDeployed: true })
            return new PyList([]);
        var rows = await ReadGameStoreTableAsync("structures", token);
        if (rows[facilityId.ToString(System.Globalization.CultureInfo.InvariantCulture)] is not JsonObject structure)
            return new PyList([]);
        var systemId = IndustryLong(structure["solarSystemID"]);
        var typeId = checked((int)IndustryLong(structure["typeID"]));
        var resolved = await ResolveIndustryFacilityAsync(facilityId, 0, systemId, characterId, token);
        if (resolved is null)
            return new PyList([]);
        if (!corporate)
        {
            if (ownerId != characterId)
                return new PyList([]);
            var root = IndustryLocation(facilityId, facilityId, typeId, ownerId,
                Eve.Services.Core.IndustryRules.PersonalStationHangarFlag, systemId, true, true,
                includeInventoryMapping);
            var nested = await IndustryContainerLocationsAsync(
                [root], [facilityId], ownerId, false, token, includeInventoryMapping);
            return new PyList([root, .. nested]);
        }
        if (resolved.OwnerId != ownerId)
            return new PyList([]);
        return await CorporationFacilityLocationsAsync(
            facilityId, ownerId, characterId, typeId, systemId, token, includeInventoryMapping);
    }

    async ValueTask<PyValue> CorporationFacilityLocationsAsync(
        long facilityId, long corporationId, long characterId, int facilityTypeId,
        long solarSystemId, CancellationToken token, bool includeInventoryMapping = false)
    {
        var corporation = await corporations.GetAsync(corporationId, token);
        var member = await corporations.MemberAsync(characterId, token);
        if (corporation is null || corporation.IsNpc || member is null || member.CorporationId != corporationId)
            return new PyList([]);
        var roles = Eve.Corporations.CorporationRoles.Session(
            corporation, member, facilityId, await corporations.TitlesAsync(corporationId, token));
        var office = (await corporationOfficeStore.ForCorporationAsync(corporationId, token))
            .FirstOrDefault(candidate => candidate.StationId == facilityId);
        if (office is null)
            return new PyList([]);

        var locations = new List<PyValue>();
        var hangarLocations = new List<PyValue>();
        var clientRootItemId = stations.Get(facilityId) is not null ? office.OfficeId : facilityId;
        var clientRootTypeId = stations.Get(facilityId) is not null ? IndustryOfficeTypeId : facilityTypeId;
        var factoryManager = (roles.AtAll & Eve.Corporations.CorporationRoles.FactoryManager) != 0;
        foreach (var flag in Enumerable.Range(
            Eve.Services.Core.IndustryRules.CorporationHangarFirstFlag,
            Eve.Services.Core.IndustryRules.CorporationHangarLastFlag - Eve.Services.Core.IndustryRules.CorporationHangarFirstFlag + 1))
        {
            var canView = factoryManager || Eve.Corporations.CorporationRoles.HasHangarAccess(roles, facilityId, flag, take: false);
            if (!canView)
                continue;
            var canTake = factoryManager && Eve.Corporations.CorporationRoles.HasHangarAccess(roles, facilityId, flag, take: true);
            hangarLocations.Add(IndustryLocation(clientRootItemId, office.OfficeFolderId, clientRootTypeId,
                corporationId, flag, solarSystemId, true, canTake, includeInventoryMapping));
        }
        locations.AddRange(hangarLocations);
        locations.AddRange(await IndustryContainerLocationsAsync(
            hangarLocations, [office.OfficeFolderId], corporationId, factoryManager, token,
            includeInventoryMapping));
        var deliveryView = factoryManager || Eve.Corporations.CorporationRoles.HasHangarAccess(
            roles, facilityId, Eve.Services.Core.IndustryRules.CorporationDeliveriesFlag, take: false);
        if (deliveryView)
        {
            var deliveryTake = factoryManager;
            locations.Add(IndustryLocation(facilityId, facilityId, facilityTypeId, corporationId,
                Eve.Services.Core.IndustryRules.CorporationDeliveriesFlag, solarSystemId, true, deliveryTake,
                includeInventoryMapping));
        }
        return new PyList(locations);
    }

    async ValueTask<IReadOnlyList<PyValue>> IndustryContainerLocationsAsync(
        IReadOnlyList<PyValue> roots,
        IReadOnlyCollection<long> inventoryRootIds,
        long ownerId,
        bool hasCorporationIndustryJobAccess,
        CancellationToken token,
        bool includeInventoryMapping = false)
    {
        var eligibleRoots = roots.Select(root => new
            {
                FlagId = checked((int)FacilityNumber(FacilityField(root, "flagID"))),
                SolarSystemId = FacilityNumber(FacilityField(root, "solarSystemID")),
                CanView = Eve.Macho.JsSemantics.Truthy(FacilityField(root, "canView")),
                CanTake = Eve.Macho.JsSemantics.Truthy(FacilityField(root, "canTake")),
            })
            .Where(root => root.CanView && (root.CanTake || hasCorporationIndustryJobAccess))
            .ToArray();
        if (eligibleRoots.Length == 0)
            return [];

        var accessibleFlags = eligibleRoots.Select(root => root.FlagId).ToHashSet();
        var candidates = new List<Eve.Inventory.Item>();
        foreach (var inventoryRootId in inventoryRootIds.Where(id => id > 0).Distinct())
            candidates.AddRange(await items.InContainerAsync(inventoryRootId, token));
        candidates = candidates.Where(item => item.OwnerId == ownerId && item.Singleton &&
            accessibleFlags.Contains(item.Flag)).DistinctBy(item => item.Id).ToList();
        if (candidates.Count == 0)
            return [];

        var typeRows = await sde.ReadAsync("types", candidates.Select(item => (long)item.TypeId).Distinct().ToArray(), token);
        var groupsByType = typeRows.ToDictionary(row => checked((int)row.Key), row =>
            row.Doc.TryGetProperty("groupID", out var group) && group.ValueKind == System.Text.Json.JsonValueKind.Number
                ? group.GetInt32() : 0);
        var locations = new List<PyValue>();
        foreach (var container in candidates)
        {
            if (!groupsByType.TryGetValue(container.TypeId, out var groupId) || !IndustryContainerGroupIds.Contains(groupId))
                continue;
            foreach (var root in eligibleRoots.Where(root => root.FlagId == container.Flag))
            {
                locations.Add(IndustryLocation(container.Id, container.Id, container.TypeId, ownerId,
                    root.FlagId, root.SolarSystemId, root.CanView, root.CanTake, includeInventoryMapping));
            }
        }
        return locations;
    }

    internal static PyValue ActivityModifiersPayload(IReadOnlyDictionary<int, decimal> indexes)
    {
        var activityIds = new[]
        {
            Eve.Services.Core.IndustryRules.Manufacturing,
            Eve.Services.Core.IndustryRules.ResearchTime,
            Eve.Services.Core.IndustryRules.ResearchMaterial,
            Eve.Services.Core.IndustryRules.Copying,
            Eve.Services.Core.IndustryRules.Invention,
            Eve.Services.Core.IndustryRules.Reaction,
        };
        return new PyDict(activityIds.Select(activityId => new KeyValuePair<PyValue, PyValue>(
            new PyInt(activityId), Py.Number((double)indexes[activityId]))).ToList());
    }

    async ValueTask<PyValue?> FacilityManagerRpcAsync(ServiceCall call, CancellationToken token)
    {
        async ValueTask<PyValue?> ReadFacilityAsync(
            long facilityId,
            Eve.Services.Core.PersistedStructureInfo? persistedStructure = null)
        {
            if (facilityId <= 0) return null;
            var station = stations.Get(facilityId);
            var systemId = station?.SolarSystemId ?? 0;
            var typeId = station?.TypeId ?? 0;
            if (station is null)
            {
                var info = persistedStructure ?? await structureDirectoryReadModel.GetStructureInfoAsync(facilityId, token);
                if (info is not { PersistentlyDeployed: true }) return null;
                systemId = (int)FacilityNumber(FacilityField(info.Info, "solarSystemID"));
                typeId = (int)FacilityNumber(FacilityField(info.Info, "typeID"));
            }
            if (systemId <= 0 || typeId <= 0) return null;
            var facility = await ResolveIndustryFacilityAsync(
                facilityId, 0, systemId, call.Session.CharacterId ?? 0, token);
            if (facility is null) return null;

            var operation = station is null ? null : await NpcOperationAsync(station.OperationId);
            var structureRows = station is null ? await ReadGameStoreTableAsync("structures", token) : null;
            var structureDoc = station is null && structureRows is not null &&
                structureRows[facilityId.ToString(System.Globalization.CultureInfo.InvariantCulture)] is JsonObject row
                ? row : null;
            return await IndustryFacilityPayloadAsync(
                facilityId, typeId, facility, operation, structureDoc, token);
        }

        if (call.Method == "GetFacilities")
            return await FacilitiesAsync(call, token);
        if (call.Method == "GetMaxActivityModifiers")
        {
            var indexes = new Dictionary<int, decimal>();
            foreach (var activityId in new[]
            {
                Eve.Services.Core.IndustryRules.Manufacturing,
                Eve.Services.Core.IndustryRules.ResearchTime,
                Eve.Services.Core.IndustryRules.ResearchMaterial,
                Eve.Services.Core.IndustryRules.Copying,
                Eve.Services.Core.IndustryRules.Invention,
                Eve.Services.Core.IndustryRules.Reaction,
            })
            {
                var systemIndexes = await industryCostIndexes.GetActivityAsync(activityId, token);
                indexes[activityId] = systemIndexes.Count == 0
                    ? industryCostIndexes.Fallback
                    : systemIndexes.Values.Max();
            }
            return ActivityModifiersPayload(indexes);
        }
        if (call.Method == "GetFacilityTaxes")
            return await FacilityTaxesAsync(FacilityNumber(call.Arg(0)), token);
        if (call.Method == "SetFacilityTaxes")
            return await SetFacilityTaxesAsync(call, token);
        if (call.Method == "GetFacilityLocations")
        {
            var ownerId = FacilityNumber(call.Arg(1));
            return await FacilityLocationsAsync(
                FacilityNumber(call.Arg(0)), ownerId,
                ownerId > 0 && FacilityNumber(call.Session.Get("corpid")) == ownerId,
                call.Session.CharacterId ?? 0, token);
        }
        if (call.Method == "GetFacility")
            return await ReadFacilityAsync(FacilityNumber(call.Arg(0)));
        if (call.Method != "GetFacilitiesByID")
            return null;

        var requested = call.Arg(0) switch
        {
            PyList list => list.Items,
            PyTuple tuple => tuple.Items,
            _ => Array.Empty<PyValue>(),
        };
        var structureInfos = await structureDirectoryReadModel.GetStructureInfosAsync(
            requested.Select(FacilityNumber).Where(id => id > 0 && stations.Get(id) is null).ToArray(), token);
        var facilities = new List<PyValue>();
        foreach (var raw in requested)
        {
            var facilityId = FacilityNumber(raw);
            var station = stations.Get(facilityId);
            if (facilityId > 0 && station is null && !structureInfos.TryGetValue(facilityId, out _))
                continue;
            structureInfos.TryGetValue(facilityId, out var persistedStructure);
            if (await ReadFacilityAsync(facilityId, persistedStructure) is { } facility)
                facilities.Add(facility);
        }
        return new PyList(facilities);
    }

    async ValueTask<IReadOnlyList<Eve.Services.Core.IndustryJobLocation>> ResolveIndustryJobLocationsAsync(
        long facilityId, long ownerId, long characterId, CancellationToken token)
    {
        if (facilityId <= 0 || ownerId <= 0 || characterId <= 0)
            return [];
        var corporate = false;
        if (ownerId != characterId)
        {
            var member = await corporations.MemberAsync(characterId, token);
            if (member is null || member.CorporationId != ownerId)
                return [];
            corporate = true;
        }

        var payload = await FacilityLocationsAsync(
            facilityId, ownerId, corporate, characterId, token, includeInventoryMapping: true);
        if (payload is not PyList locations)
            return [];
        var locationTypeIds = locations.Items.Select(location => FacilityNumber(FacilityField(location, "typeID")))
            .Where(typeId => typeId > 0).Distinct().ToArray();
        var typeRows = await sde.ReadAsync("types", locationTypeIds, token);
        var groupsByType = typeRows.ToDictionary(row => row.Key,
            row => row.Doc.TryGetProperty("groupID", out var group) && group.ValueKind == System.Text.Json.JsonValueKind.Number
                ? group.GetInt32() : 0);
        var authorized = new List<Eve.Services.Core.IndustryJobLocation>(locations.Items.Count);
        foreach (var location in locations.Items)
        {
            var itemId = FacilityNumber(FacilityField(location, "itemID"));
            var inventoryLocationId = FacilityNumber(FacilityField(location, "inventoryLocationID"));
            var locationOwnerId = FacilityNumber(FacilityField(location, "ownerID"));
            var flagId = FacilityNumber(FacilityField(location, "flagID"));
            if (itemId <= 0 || locationOwnerId <= 0 || inventoryLocationId <= 0 ||
                flagId < 0 || flagId > int.MaxValue)
                continue;
            var typeId = FacilityNumber(FacilityField(location, "typeID"));
            var inventoryFlagId = groupsByType.TryGetValue(typeId, out var groupId) && IndustryContainerGroupIds.Contains(groupId)
                ? groupId == 448 ? IndustryUnlockedContainerFlagId : Eve.Inventory.ItemFlags.InSpace
                : -1;
            authorized.Add(new Eve.Services.Core.IndustryJobLocation(
                itemId,
                (int)flagId,
                inventoryLocationId,
                locationOwnerId,
                Eve.Macho.JsSemantics.Truthy(FacilityField(location, "canView")),
                Eve.Macho.JsSemantics.Truthy(FacilityField(location, "canTake")),
                inventoryFlagId));
        }
        return authorized;
    }

    private async Task IndustryFacilities()
    {
        industryCostIndexes = IndustryCostIndexSetup();
        industryReady = new IndustryReadyJobs(source, industrySchema, foundations.Events, foundations.Scheduler, () => now());
        industryReady.Register();
        await industryReady.StartAsync();
        industryJobOperations = new PersistenceIndustryJobOperations(
            source, industrySchema, items, economyWallets, skills, sde, adjustedPrices, corporations,
            foundations.Events, stations, ResolveIndustryFacilityAsync, industryCostIndexes,
            async (characterId, token) => (await characters.GetAsync(characterId, token)) is { } character ? (long?)character.SolarSystemId : null,
            industryReady, now, ResolveIndustryJobLocationsAsync);

    }

    static long PositiveMapRpcId(PyValue value) => value switch
    {
        PyInt number when number.Value > 0 => number.Value,
        PyLong number when number.Value > 0 => number.Value,
        PyReal number when double.IsFinite(number.Value) && number.Value > 0 &&
            number.Value <= long.MaxValue && Math.Truncate(number.Value) == number.Value => (long)number.Value,
        PyStr text when long.TryParse(text.Value, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var number) && number > 0 => number,
        _ => 0,
    };

    static int MapActivityId(PyValue value)
    {
        var id = PositiveMapRpcId(value);
        return id > 0 && id <= int.MaxValue ? (int)id : 0;
    }

    async ValueTask<PyValue?> ReadMapRpcDataAsync(ServiceCall call)
    {
        switch (call.Method)
        {
            case "GetCurrentSovData":
                return await sovereigntyReadModel.ReadCurrentMapDataAsync(
                    PositiveMapRpcId(call.Arg(0)),
                    await mapSolarSystems.Value.WaitAsync(call.Cancellation),
                    call.Cancellation);
            case "GetFacWarZoneInfo":
                return await FactionWarfareZoneInfoAsync(PositiveMapRpcId(call.Arg(0)), call.Cancellation);
            case "GetRecentSovActivity":
                return await sovereigntyReadModel.ReadRecentSovActivityAsync(call.Cancellation);
            case "GetIndustryCostModifier":
                return await industryJobOperations.ReadMapIndustryCostModifierAsync(
                    MapActivityId(call.Arg(0)),
                    await mapIndustrySystems.Value.WaitAsync(call.Cancellation),
                    call.Cancellation);
            case "GetIndustryJobsOverLast24Hours":
                return await industryJobOperations.ReadMapIndustryJobsOverLast24HoursAsync(
                    MapActivityId(call.Arg(0)), now(), call.Cancellation);
            default:
                return null;
        }
    }

    private Task IndustryMap()
    {
        mapSolarSystems = new Lazy<Task<IReadOnlyList<Eve.Sde.SdeRow>>>(() =>
            sde.ReadAsync("mapSolarSystems", cancellationToken: CancellationToken.None));
        mapIndustrySystems = new Lazy<Task<IReadOnlyList<Eve.Sde.SdeRow>>>(() =>
            sde.ReadAsync("mapSolarSystems", cancellationToken: CancellationToken.None));
        foundations.Events.Subscribe(
            "industry.jobs.client-notifications",
            [Eve.Services.Core.IndustryJobEvents.Changed],
            (context, token) =>
            {
                token.ThrowIfCancellationRequested();
                var change = context.Event.Payload<Eve.Services.Core.IndustryJobChangedEvent>();
                if (change is null)
                    return Task.CompletedTask;

                IndustryJobClientNotifications.Send(characterSessions, change);
                return Task.CompletedTask;
            });
        return Task.CompletedTask;
    }
}
