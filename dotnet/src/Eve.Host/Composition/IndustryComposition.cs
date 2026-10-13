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

        var services = structure["services"] as JsonObject ?? structure["serviceStates"] as JsonObject;
        var industryOnline = IndustryTruthy(structure["industryServiceOnline"]) ||
            IndustryTruthy(structure["manufacturingServiceOnline"]) ||
            IndustryTruthy(structure["researchServiceOnline"]);
        if (!industryOnline && services is not null)
        {
            foreach (var (key, value) in services)
            {
                var name = key + " " + (value is JsonObject service
                    ? service["serviceName"]?.ToString() ?? service["name"]?.ToString() ?? ""
                    : "");
                if ((name.Contains("industry", StringComparison.OrdinalIgnoreCase) ||
                     name.Contains("manufactur", StringComparison.OrdinalIgnoreCase) ||
                     name.Contains("research", StringComparison.OrdinalIgnoreCase)) &&
                    IndustryOnline(value))
                {
                    industryOnline = true;
                    break;
                }
            }
        }
        if (!industryOnline)
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
            facilityId, solarSystemId, ownerCorporationId, Math.Max(0, taxRate), timeMultiplier, true);
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

    static bool IndustryTruthy(JsonNode? value) =>
        value is JsonValue scalar && scalar.TryGetValue<bool>(out var flag) ? flag : IndustryLong(value) != 0;

    static bool IndustryOnline(JsonNode? value) =>
        value is JsonObject service
            ? IndustryTruthy(service["online"] ?? service["isOnline"] ?? service["active"])
            : IndustryTruthy(value);

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

            var activities = new PyDict([]);
            return Py.KeyVal(
                ("facilityID", new PyLong(facility.FacilityId)),
                ("typeID", new PyInt(typeId)),
                ("ownerID", new PyLong(facility.OwnerId)),
                ("tax", Py.Number((double)facility.TaxRate)),
                ("solarSystemID", new PyInt(facility.SolarSystemId)),
                ("online", PyValue.Of(facility.IsOnline)),
                ("serviceAccess", new PyDict([])),
                ("sccTaxModifier", Py.Number(1)),
                ("rigModifiers", new PyDict([])),
                ("globalModifiers", Py.Dict(("faction", Py.Number(1)), ("system", new PyDict([])))),
                ("activities", activities));
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

    private Task IndustryFacilities()
    {
        industryCostIndexes = IndustryCostIndexSetup();
        industryJobOperations = new PersistenceIndustryJobOperations(
            source, industrySchema, items, economyWallets, skills, sde, adjustedPrices, corporations,
            foundations.Events, stations, ResolveIndustryFacilityAsync, industryCostIndexes, now);
        return Task.CompletedTask;
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
                foreach (var characterId in new[] { change.OwnerId, change.InstallerId }.Distinct())
                {
                    if (characterSessions.Of(characterId) is not { } session)
                        continue;
                    session.SendNotification(
                        "OnIndustryJob", "charid",
                        new PyInt(change.JobId), new PyInt(change.OwnerId), new PyInt(change.BlueprintId),
                        new PyInt(change.InstallerId), new PyInt(change.Status),
                        change.SuccessfulRuns is { } runs ? new PyInt(runs) : PyNone.Instance);
                }
                return Task.CompletedTask;
            });
        return Task.CompletedTask;
    }
}
