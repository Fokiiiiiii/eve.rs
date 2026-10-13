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
    static Eve.Marshal.PyValue MailingListJoinValue(
        long listId, JsonObject list, long characterId, long role) =>
        Py.KeyVal([
            ("id", new Eve.Marshal.PyLong(listId)),
            ("name", new Eve.Marshal.PyStr(MailingListJsonText(list["name"]))),
            ("displayName", new Eve.Marshal.PyStr(MailingListJsonText(list["displayName"]))),
            ("isMuted", role == 0 ? Eve.Marshal.PyBool.True : Eve.Marshal.PyBool.False),
            ("isOperator", role == 2 || role == 3 ? Eve.Marshal.PyBool.True : Eve.Marshal.PyBool.False),
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
        var timeMultiplier = IndustryDouble(structure["industryTimeMultiplier"], 1d);
        if (!double.IsFinite(timeMultiplier) || timeMultiplier <= 0)
            return null;
        return new PersistenceIndustryJobOperations.IndustryFacility(
            facilityId, solarSystemId, ownerCorporationId, Math.Max(0, taxRate), timeMultiplier, true, null, null, activities,
            await StructureBonusesAsync(facilityId, IndustryLong(structure["typeID"]), solarSystemId, token));
    }

    /// <summary>
    /// The facility tax of an NPC station: the SDE <c>npcCorporations.taxRate</c> of the station's owning corporation
    /// (CCP data; most NPC corporations are 0, some 0.10 or 0.11). Zero when the owner has no row.
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

    /// <summary>
    /// The activities of an NPC station as the client reads them: per activity the time modifiers, then empty material, cost,
    /// category, group and type lists (no restriction). Manufacturing (1) needs the Factory (14); the science activities (3, 4, 5, 8)
    /// need the Laboratory (15). A modifier is listed only for a factor other than 1, as EveJS does.
    /// </summary>
    static PyValue NpcActivitiesPayload(NpcOperation operation)
    {
        var activities = new List<int>();
        if (operation.Services.Contains(14))
            activities.Add(Eve.Services.Core.IndustryRules.Manufacturing);
        if (operation.Services.Contains(15))
            activities.AddRange([Eve.Services.Core.IndustryRules.ResearchTime, Eve.Services.Core.IndustryRules.ResearchMaterial,
                Eve.Services.Core.IndustryRules.Copying, Eve.Services.Core.IndustryRules.Invention]);
        return ActivitiesPayload(activities, operation.ManufacturingFactor, operation.ResearchFactor);
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
            ("activities", NpcActivitiesPayload(operation)));
    }

    /// <summary>GetFacilities: the NPC stations that run industry in the caller's region (every region when the session has none).</summary>
    async ValueTask<PyValue?> NpcFacilitiesAsync(ServiceCall call, CancellationToken token)
    {
        var region = FacilityNumber(call.Session.Get("regionid"));
        var facilities = new List<PyValue>();
        foreach (var station in stations.All)
        {
            if (region > 0 && station.RegionId != region)
                continue;
            if (await NpcFacilityPayloadAsync(station, token) is { } payload)
                facilities.Add(payload);
        }
        return new PyList(facilities);
    }

    /// <summary>GetFacilityTaxes: the corporation tax of an NPC station (a fraction, as the facility payload gives it).</summary>
    async ValueTask<PyValue?> FacilityTaxesAsync(long facilityId)
    {
        var station = stations.Get(facilityId);
        var rate = station is null ? (decimal?)null
            : (await NpcCorporationTaxesAsync()).TryGetValue(station.OwnerId, out var value) ? value : 0m;
        return Py.KeyVal(
            ("taxCorporation", rate is { } tax ? Py.Number((double)tax) : PyNone.Instance),
            ("taxAlliance", PyNone.Instance),
            ("taxStandingsHorrible", PyNone.Instance),
            ("taxStandingsBad", PyNone.Instance),
            ("taxStandingsNeutral", PyNone.Instance),
            ("taxStandingsGood", PyNone.Instance),
            ("taxStandingsHigh", PyNone.Instance));
    }

    /// <summary>
    /// GetFacilityLocations: the station hangar of an NPC station, as the client's industry.Location object (itemID, typeID,
    /// ownerID, flagID, canView, canTake). Corporation deliveries are not offered yet.
    /// </summary>
    PyValue? FacilityLocations(long facilityId, long ownerId, bool corporate)
    {
        var station = stations.Get(facilityId);
        if (station is null)
            return new PyList([]);
        // A corporation's places are its deliveries and its hangar divisions 1..7; a character's is its station hangar.
        var flags = corporate
            ? new[] { Eve.Services.Core.IndustryRules.CorporationDeliveriesFlag }.Concat(
                Enumerable.Range(Eve.Services.Core.IndustryRules.CorporationHangarFirstFlag,
                    Eve.Services.Core.IndustryRules.CorporationHangarLastFlag - Eve.Services.Core.IndustryRules.CorporationHangarFirstFlag + 1))
            : [ItemFlags.Hangar];
        return new PyList(flags.Select(flag => (PyValue)new PyObjectEx(
            ObjectExKind.Ex1,
            new PyStr("util.KeyVal"),
            [],
            [
                new(new PyStr("itemID"), new PyLong(station.Id)),
                new(new PyStr("typeID"), new PyInt(station.TypeId)),
                new(new PyStr("ownerID"), new PyLong(ownerId)),
                new(new PyStr("flagID"), new PyInt(flag)),
                new(new PyStr("canView"), PyValue.Of(true)),
                new(new PyStr("canTake"), PyValue.Of(true)),
            ])).ToList());
    }

    async ValueTask<PyValue?> FacilityManagerRpcAsync(ServiceCall call, CancellationToken token)
    {
        async ValueTask<PyValue?> ReadFacilityAsync(long facilityId)
        {
            if (facilityId <= 0) return null;
            var station = stations.Get(facilityId);
            var systemId = station?.SolarSystemId ?? 0;
            var typeId = station?.TypeId ?? 0;
            if (station is null)
            {
                var info = await structureDirectoryReadModel.GetStructureInfoAsync(facilityId, token);
                if (info is not { PersistentlyDeployed: true }) return null;
                systemId = (int)FacilityNumber(FacilityField(info.Info, "solarSystemID"));
                typeId = (int)FacilityNumber(FacilityField(info.Info, "typeID"));
            }
            if (systemId <= 0 || typeId <= 0) return null;
            var facility = await ResolveIndustryFacilityAsync(
                facilityId, 0, systemId, call.Session.CharacterId ?? 0, token);
            if (facility is null) return null;

            // An NPC station lists the activities of its SDE operation; a structure lists those its online services run.
            var operation = station is null ? null : await NpcOperationAsync(station.OperationId);
            var activities = operation is not null ? NpcActivitiesPayload(operation)
                : ActivitiesPayload(facility.Activities ?? new HashSet<int>(), facility.TimeMultiplier, facility.ResearchFactor,
                    facility.ModifierBonuses);
            return Py.KeyVal(
                ("facilityID", new PyLong(facility.FacilityId)),
                ("typeID", new PyInt(typeId)),
                ("ownerID", new PyLong(facility.OwnerId)),
                ("tax", Py.Number((double)facility.TaxRate)),
                ("solarSystemID", new PyInt(facility.SolarSystemId)),
                ("online", PyValue.Of(facility.IsOnline)),
                ("serviceAccess", new PyDict([])),
                ("sccTaxModifier", Py.Number(1)),
                ("rigModifiers", RigModifiersPayload(facility.ModifierBonuses)),
                ("globalModifiers", Py.Dict(("faction", Py.Number(1)), ("system", new PyDict([])))),
                ("activities", activities));
        }

        if (call.Method == "GetFacilities")
            return await NpcFacilitiesAsync(call, token);
        if (call.Method == "GetFacilityTaxes")
            return await FacilityTaxesAsync(FacilityNumber(call.Arg(0)));
        if (call.Method == "GetFacilityLocations")
        {
            var ownerId = FacilityNumber(call.Arg(1));
            return FacilityLocations(FacilityNumber(call.Arg(0)), ownerId, ownerId > 0 && FacilityNumber(call.Session.Get("corpid")) == ownerId);
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
        var facilities = new List<PyValue>();
        foreach (var raw in requested)
            if (await ReadFacilityAsync(FacilityNumber(raw)) is { } facility)
                facilities.Add(facility);
        return new PyList(facilities);
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
            industryReady, now);
        
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
                // The owner and the installer, and for a corporation's job the online members who hold Factory Manager (the
                // corporation's job list is theirs; the client's Factory Manager role is 1024).
                var recipients = new List<Eve.Services.ClientSession>();
                foreach (var characterId in new[] { change.OwnerId, change.InstallerId }.Distinct())
                    if (characterSessions.Of(characterId) is { } owned)
                        recipients.Add(owned);
                foreach (var member in characterSessions.OfCorporation(change.OwnerId))
                    if (((member.GetInt("corprole") ?? 0) & 1024) != 0)
                        recipients.Add(member);
                foreach (var session in recipients.Distinct())
                    session.SendNotification(
                        "OnIndustryJob", "charid",
                        new PyInt(change.JobId), new PyInt(change.OwnerId), new PyInt(change.BlueprintId),
                        new PyInt(change.InstallerId), new PyInt(change.Status),
                        change.SuccessfulRuns is { } runs ? new PyInt(runs) : PyNone.Instance);
                return Task.CompletedTask;
            });
        return Task.CompletedTask;
    }
}
