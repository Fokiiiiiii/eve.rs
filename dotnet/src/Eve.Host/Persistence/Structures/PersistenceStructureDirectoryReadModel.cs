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

public sealed partial class PersistenceStructureDirectoryReadModel(
    Func<string, CancellationToken, Task<JsonObject>> readTable,
    Func<long, CancellationToken, Task<Eve.Relations.Affiliation>> affiliationOf,
    Eve.Sde.SdeReader sde,
    Func<long, string, CancellationToken, Task>? setDescriptionAsync,
    Func<long, CancellationToken, ValueTask<bool>>? cynoJammedAsync = null,
    Func<long, long, CancellationToken, Task<bool>>? stationManagerOfAsync = null) :
    Eve.Services.Core.IStructureDirectoryReadCallbacks,
    Eve.Services.Core.IStructureSettingsReadCallbacks
{
    public PersistenceStructureDirectoryReadModel(
        Func<string, CancellationToken, Task<JsonObject>> readTable,
        Func<long, CancellationToken, Task<Eve.Relations.Affiliation>> affiliationOf)
        : this(readTable, affiliationOf, null!, null)
    {
    }

    private const long AnsiblexJumpBridgeTypeId = 35841;
    private const int JumpBridgeServiceId = 15;
    private const int DockingServiceId = 1;
    private const int JumpBridgeAccessSettingId = 34;
    private const int DockAccessSettingId = 19;

    public async ValueTask<long?> GetJbStructureDestinationAsync(long structureId, CancellationToken cancellationToken)
    {
        if (structureId <= 0)
            return null;
        var structures = await readTable("structures", cancellationToken);
        var bridge = ReadBridges(structures).FirstOrDefault(candidate => candidate.Id == structureId);
        return bridge is { DestinationSolarSystemId: > 0 } ? bridge.DestinationSolarSystemId : null;
    }

    public async ValueTask<(long StructureId, long SolarSystemId)?> GetLinkedStructureAsync(
        long structureId,
        CancellationToken cancellationToken)
    {
        if (structureId <= 0)
            return null;
        var structures = await readTable("structures", cancellationToken);
        var bridges = ReadBridges(structures);
        var source = bridges.FirstOrDefault(candidate => candidate.Id == structureId);
        if (source is null || source.DestinationSolarSystemId <= 0)
            return null;
        var linked = bridges.FirstOrDefault(candidate =>
            candidate.Id != source.Id &&
            candidate.SolarSystemId == source.DestinationSolarSystemId &&
            candidate.DestinationSolarSystemId == source.SolarSystemId);
        return linked is { Id: > 0, SolarSystemId: > 0 }
            ? (linked.Id, linked.SolarSystemId)
            : null;
    }

    public async ValueTask<PyValue> GetJumpBridgesWithMyAccessAsync(ServiceCall call)
    {
        var access = await ReadAccessContextAsync(call, call.Cancellation);
        if (access.CharacterId <= 0)
            return EmptyJumpBridgeAccess();

        var structures = await readTable("structures", call.Cancellation);
        var bridges = ReadBridges(structures);
        var profiles = await readTable("structureProfiles", call.Cancellation);
        var groups = await readTable("accessGroups", call.Cancellation);
        var pairs = ReciprocalPairs(bridges);
        var pairsWire = new List<PyValue>();
        var accessIDs = new HashSet<long>();
        var deniedIDs = new HashSet<long>();

        foreach (var (source, destination) in pairs)
        {
            pairsWire.Add(new PyList([BridgeMapPayload(source), BridgeMapPayload(destination)]));
            foreach (var bridge in new[] { source, destination })
            {
                if (CanUseJumpBridge(bridge, access, profiles, groups))
                    accessIDs.Add(bridge.Id);
                else
                    deniedIDs.Add(bridge.Id);
            }
        }

        return new PyTuple([
            new PyList(pairsWire),
            new PyList(accessIDs.Order().Select(InventoryWire.Id).ToArray()),
            new PyList(deniedIDs.Order().Select(InventoryWire.Id).ToArray()),
        ]);
    }

    public async ValueTask<PyValue> GetNearbyJumpBridgesAsync(ServiceCall call)
    {
        var access = await ReadAccessContextAsync(call, call.Cancellation);
        if (access.CharacterId <= 0)
            return new PyList([]);

        var structures = await readTable("structures", call.Cancellation);
        var profiles = await readTable("structureProfiles", call.Cancellation);
        var groups = await readTable("accessGroups", call.Cancellation);
        var currentSystem = call.Session.GetInt("solarsystemid2") ?? call.Session.GetInt("solarsystemid") ?? 0;
        return new PyList(ReadBridges(structures)
            .Where(bridge => IsJumpBridgeServiceOnline(bridge) && CanUseJumpBridge(bridge, access, profiles, groups))
            .Select(bridge => NearbyBridgePayload(bridge, currentSystem))
            .ToArray());
    }

    public async ValueTask<PyValue> GetStructureDescriptionAsync(ServiceCall call)
    {
        var structureId = Eve.Macho.JsSemantics.ToPositiveInteger(call.Arg(0));
        if (structureId <= 0)
            return new PyStr("");
        var structures = await readTable("structures", call.Cancellation);
        return structures[structureId.ToString(System.Globalization.CultureInfo.InvariantCulture)] is JsonObject structure
            ? new PyStr(Text(structure["description"])[..Math.Min(1000, Text(structure["description"]).Length)])
            : new PyStr("");
    }

public async ValueTask<Eve.Services.Core.PersistedStructureInfo?> GetStructureInfoAsync(
        long structureId,
        CancellationToken cancellationToken)
    {
        if (structureId <= 0)
            return null;

        var structures = await readTable("structures", cancellationToken);
        var key = structureId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (structures[key] is not JsonObject structure || !IsPersistentlyDeployedStructure(structure, structureId))
            return null;

        return new Eve.Services.Core.PersistedStructureInfo(
            structureId,
            true,
            StructureInfoPayload(structureId, structure));
    }

    public async ValueTask<IReadOnlyList<Eve.Services.Core.StructureDockingAccessSnapshot>> GetDockingAccessSnapshotsAsync(
        long characterId,
        long corporationId,
        long allianceId,
        IReadOnlyList<long> structureIds,
        CancellationToken cancellationToken)
    {
        if (characterId <= 0 || corporationId <= 0 || structureIds.Count == 0)
            return Array.Empty<Eve.Services.Core.StructureDockingAccessSnapshot>();

        var affiliation = await affiliationOf(characterId, cancellationToken);
        if (affiliation.Corporation != corporationId || (affiliation.Alliance ?? 0) != allianceId)
            return Array.Empty<Eve.Services.Core.StructureDockingAccessSnapshot>();

        var structures = await readTable("structures", cancellationToken);
        var profileState = await readTable("structureProfiles", cancellationToken);
        var accessGroupState = await readTable("accessGroups", cancellationToken);
        var result = new List<Eve.Services.Core.StructureDockingAccessSnapshot>();

        foreach (var structureId in structureIds.Distinct())
        {
            if (structureId <= 0 ||
                structures[structureId.ToString(System.Globalization.CultureInfo.InvariantCulture)] is not JsonObject structure ||
                !IsPersistentlyDeployedStructure(structure, structureId))
                continue;

            var ownerCorporationId = Positive(Integer(structure["ownerCorpID"] ?? structure["corporationID"]));
            if (ownerCorporationId <= 0)
                continue;

            var access = DockingAccessLists(structure, ownerCorporationId, profileState, accessGroupState);
            result.Add(new Eve.Services.Core.StructureDockingAccessSnapshot(
                structureId,
                ownerCorporationId,
                Math.Max(0, Integer(structure["allianceID"])),
                PersistentlyDeployed: true,
                access.ProfileResolved,
                access.DockingSettingResolved,
                access.AccessLists));
        }

        return result;
    }

    public async ValueTask<PyDict> GetMyCharacterStructuresAsync(
        long characterId,
        CancellationToken cancellationToken)
    {
        if (characterId <= 0)
            return new PyDict([]);
        var affiliation = await affiliationOf(characterId, cancellationToken);
        var corporationId = affiliation.Corporation ?? 0;
        return corporationId > 0
            ? await ReadStructuresForCorporationAsync(corporationId, cancellationToken)
            : new PyDict([]);
    }

    public async ValueTask<PyDict> GetMyCorporationStructuresAsync(
        long characterId,
        long corporationId,
        CancellationToken cancellationToken)
    {
        if (characterId <= 0 || corporationId <= 0)
            return new PyDict([]);
        var affiliation = await affiliationOf(characterId, cancellationToken);
        return affiliation.Corporation == corporationId
            ? await ReadStructuresForCorporationAsync(corporationId, cancellationToken)
            : new PyDict([]);
    }

    private async ValueTask<PyDict> ReadStructuresForCorporationAsync(
        long corporationId,
        CancellationToken cancellationToken)
    {
        var state = await readTable("structures", cancellationToken);
        var entries = new List<KeyValuePair<PyValue, PyValue>>();
        foreach (var (key, node) in state)
        {
            if (node is not JsonObject structure ||
                !long.TryParse(key, System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out var structureId) ||
                !IsPersistentlyDeployedStructure(structure, structureId) ||
                Positive(Integer(structure["ownerCorpID"] ?? structure["corporationID"])) != corporationId)
                continue;

            entries.Add(new KeyValuePair<PyValue, PyValue>(
                new PyLong(structureId),
                StructureInfoPayload(structureId, structure)));
        }

        entries.Sort(static (left, right) => ((PyLong)left.Key).Value.CompareTo(((PyLong)right.Key).Value));
        return new PyDict(entries);
    }




    public async ValueTask<PyValue> GetStructureMapDataAsync(
        long solarSystemId,
        CancellationToken cancellationToken)
    {
        if (sde is null || solarSystemId <= 0)
            return new PyList([]);

        var state = await readTable("structures", cancellationToken);
        var structures = new List<(long Id, JsonObject Row)>();
        foreach (var (key, node) in state)
        {
            if (node is not JsonObject row ||
                !long.TryParse(key, System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out var structureId) ||
                !IsPersistentlyDeployedStructure(row, structureId) ||
                Integer(row["solarSystemID"]) != solarSystemId)
                continue;
            structures.Add((structureId, row));
        }
        structures.Sort(static (left, right) => left.Id.CompareTo(right.Id));

        var typeIds = structures.Select(entry => (long)Positive(Integer(entry.Row["typeID"]))).Distinct().ToArray();
        var types = (await sde.ReadAsync("invTypes", typeIds, cancellationToken))
            .ToDictionary(row => row.Key);

        var descriptor = Descriptor.FromHeader(new PyObjectEx(
            ObjectExKind.Ex1,
            Py.Tuple(
                new PyToken("blue.DBRowDescriptor"),
                Py.Tuple(Py.Tuple(
                    Py.Tuple(new PyStr("groupID"), new PyInt(0x03)),
                    Py.Tuple(new PyStr("typeID"), new PyInt(0x03)),
                    Py.Tuple(new PyStr("itemID"), new PyInt(0x14)),
                    Py.Tuple(new PyStr("itemName"), new PyInt(0x82)),
                    Py.Tuple(new PyStr("locationID"), new PyInt(0x03)),
                    Py.Tuple(new PyStr("orbitID"), new PyInt(0x03)),
                    Py.Tuple(new PyStr("connector"), new PyInt(0x0b)),
                    Py.Tuple(new PyStr("x"), new PyInt(0x05)),
                    Py.Tuple(new PyStr("y"), new PyInt(0x05)),
                    Py.Tuple(new PyStr("z"), new PyInt(0x05)),
                    Py.Tuple(new PyStr("celestialIndex"), new PyInt(0x03)),
                    Py.Tuple(new PyStr("orbitIndex"), new PyInt(0x03))))),
            [],
            []));

        static double Coordinate(JsonNode? node) =>
            double.TryParse(node?.ToString(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var value) && double.IsFinite(value)
                    ? value
                    : 0;

        var rows = new List<PyValue>(structures.Count);
        foreach (var (structureId, row) in structures)
        {
            var typeId = Positive(Integer(row["typeID"]));
            var groupId = types.TryGetValue(typeId, out var typeRow)
                ? Positive(SdeInteger(typeRow, "groupID"))
                : 0;
            var ownerId = Positive(Integer(row["ownerID"], Positive(Integer(row["ownerCorpID"] ?? row["corporationID"]))));
            var position = row["position"] as JsonObject;
            var name = Text(row["itemName"] ?? row["name"]);
            rows.Add(new PyPackedRow(descriptor, [
                new PyInt(groupId),
                new PyInt(typeId),
                new PyLong(structureId),
                new PyWString(name),
                new PyInt(solarSystemId),
                new PyInt(ownerId),
                PyBool.False,
                new PyReal(Coordinate(position?["x"])),
                new PyReal(Coordinate(position?["y"])),
                new PyReal(Coordinate(position?["z"])),
                PyNone.Instance,
                PyNone.Instance,
            ]));
        }

        return new PyObjectEx(
            ObjectExKind.Ex2,
            Py.Tuple(
                Py.Tuple(new PyToken("carbon.common.script.sys.crowset.CRowset")),
                Py.Dict(("header", descriptor.Header))),
            rows,
            []);
    }

    public async ValueTask<PyValue> GetMyAccessibleOnlineCynoBeaconStructuresAsync(ServiceCall call)
    {
        var access = await ReadAccessContextAsync(call, call.Cancellation);
        if (access.CharacterId <= 0)
            return new PyList([]);

        var state = await readTable("structures", call.Cancellation);
        var profiles = await readTable("structureProfiles", call.Cancellation);
        var groups = await readTable("accessGroups", call.Cancellation);
        var rows = new List<(long Id, PyValue Row)>();
        foreach (var (key, node) in state)
        {
            if (node is not JsonObject structure ||
                !long.TryParse(key, System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out var structureId) ||
                !IsPersistentlyDeployedStructure(structure, structureId) ||
                Integer(structure["typeID"]) != 35840 ||
                Integer((structure["serviceStates"] as JsonObject)?["16"]) != 1)
                continue;

            var ownerCorporationId = Positive(Integer(structure["ownerCorpID"] ?? structure["corporationID"]));
            if (ownerCorporationId <= 0 ||
                !ProfileSettingAllows(
                    structure,
                    ownerCorporationId,
                    access,
                    profiles,
                    groups,
                    35))
                continue;

            var solarSystemId = Positive(Integer(structure["solarSystemID"]));
            if (solarSystemId <= 0 ||
                (cynoJammedAsync is not null && await cynoJammedAsync(solarSystemId, call.Cancellation)))
                continue;

            var ownerId = Positive(Integer(structure["ownerID"], ownerCorporationId));
            var stateId = Integer(structure["state"]);
            var name = Text(structure["itemName"] ?? structure["name"]);
            if (name.Length == 0)
                name = $"Structure {structureId}";
            rows.Add((structureId, new PyList([
                new PyInt(structureId),
                new PyInt(Positive(Integer(structure["typeID"]))),
                new PyInt(ownerId),
                new PyInt(solarSystemId),
                new PyInt(stateId),
                new PyStr(name),
            ])));
        }

        return new PyList(rows
            .OrderBy(entry => entry.Id)
            .Select(entry => entry.Row)
            .ToArray());
    }

    public async ValueTask<PyValue> GetSolarSystemsWithBeaconsAsync(CancellationToken cancellationToken)
    {
        var structures = await readTable("structures", cancellationToken);
        var systems = new SortedSet<long>();
        var jammedBySystem = new Dictionary<long, bool>();
        foreach (var (key, node) in structures)
        {
            if (node is not JsonObject structure ||
                !long.TryParse(key, System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out var structureId) ||
                !IsPersistentlyDeployedStructure(structure, structureId) ||
                Integer(structure["typeID"]) != 35840 ||
                Integer((structure["serviceStates"] as JsonObject)?["16"]) != 1)
                continue;
            var solarSystemId = Positive(Integer(structure["solarSystemID"]));
            if (solarSystemId <= 0)
                continue;
            if (cynoJammedAsync is not null)
            {
                if (!jammedBySystem.TryGetValue(solarSystemId, out var jammed))
                {
                    jammed = await cynoJammedAsync(solarSystemId, cancellationToken);
                    jammedBySystem.Add(solarSystemId, jammed);
                }
                if (jammed)
                    continue;
            }
            systems.Add(solarSystemId);
        }

        return new PyList(systems.Select(id => (PyValue)new PyInt(id)).ToArray());
    }

    public async ValueTask<PyValue> GetValidWarHQsAsync(
        ServiceCall call,
        long ownerId,
        CancellationToken cancellationToken)
    {
        var characterId = call.Session?.CharacterId ?? 0;
        var corporationId = call.Session?.GetInt("corpid") ?? 0;
        var allianceId = call.Session?.GetInt("allianceid") ?? 0;
        if (characterId <= 0 || ownerId <= 0 ||
            (ownerId != corporationId && ownerId != allianceId))
            return new PyList([]);

        var state = await readTable("structures", cancellationToken);
        var structures = new List<(long Id, JsonObject Row)>();
        foreach (var (key, node) in state)
        {
            if (node is not JsonObject structure ||
                !long.TryParse(key, System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out var structureId) ||
                !IsPersistentlyDeployedStructure(structure, structureId))
                continue;
            var ownerCorporationId = Positive(Integer(structure["ownerCorpID"] ?? structure["ownerID"]));
            var ownerAllianceId = Positive(Integer(structure["allianceID"]));
            if (ownerCorporationId != ownerId && ownerAllianceId != ownerId)
                continue;
            structures.Add((structureId, structure));
        }

        var typeRows = await sde.ReadAsync(
            "invTypes", structures.Select(entry => (long)Positive(Integer(entry.Row["typeID"]))).Distinct(), cancellationToken);
        var types = typeRows.ToDictionary(row => row.Key);
        var systemRows = await sde.ReadAsync(
            "mapSolarSystems", structures.Select(entry => (long)Positive(Integer(entry.Row["solarSystemID"]))).Distinct(), cancellationToken);
        var systems = systemRows.ToDictionary(row => row.Key);

        var valid = new List<(long Id, PyValue Payload)>();
        foreach (var (structureId, structure) in structures)
        {
            var typeId = Positive(Integer(structure["typeID"]));
            var solarSystemId = Positive(Integer(structure["solarSystemID"]));
            if (!types.TryGetValue(typeId, out var type) ||
                SdeInteger(type, "categoryID") != 65 ||
                !SdeBoolean(type, "dockable") ||
                !systems.TryGetValue(solarSystemId, out var system) ||
                SdeDouble(system, "security") < 0.45)
                continue;

            var ownerCorporationId = Positive(Integer(structure["ownerCorpID"] ?? structure["ownerID"]));
            var ownerIdForPayload = Positive(Integer(structure["ownerID"], ownerCorporationId));
            var itemName = Text(structure["itemName"] ?? structure["name"]);
            if (itemName.Length == 0)
                itemName = $"Structure {structureId}";
            var upkeepState = Positive(Integer(structure["upkeepState"], 1));
            var inSpace = structure["inSpace"] is JsonValue inSpaceValue &&
                inSpaceValue.TryGetValue<bool>(out var isInSpace) && !isInSpace
                    ? new PyInt(0)
                    : new PyInt(1);
            var payload = Py.KeyVal(
                ("typeID", new PyInt(typeId)),
                ("structureID", new PyInt(structureId)),
                ("upkeepState", new PyInt(upkeepState)),
                ("wars", new PyList([])),
                ("ownerID", new PyInt(ownerIdForPayload)),
                ("solarSystemID", new PyInt(solarSystemId)),
                ("itemName", new PyStr(itemName)),
                ("inSpace", inSpace));
            valid.Add((structureId, payload));
        }

        return new PyList(valid
            .OrderBy(entry => entry.Id)
            .Select(entry => entry.Payload)
            .ToArray());
    }

    public async ValueTask<bool> SetStructureDescriptionAsync(ServiceCall call)
    {
        if (setDescriptionAsync is null)
            return false;
        var characterId = call.Session?.CharacterId ?? 0;
        var corporationId = call.Session?.GetInt("corpid") ?? 0;
        var structureId = call.Arg(0) switch
        {
            PyInt id when id.Value > 0 => id.Value,
            PyLong id when id.Value > 0 => id.Value,
            _ => 0,
        };
        if (characterId <= 0 || corporationId <= 0 || structureId <= 0 || stationManagerOfAsync is null)
            return false;

        var affiliation = await affiliationOf(characterId, call.Cancellation);
        if (affiliation.Corporation != corporationId ||
            !await stationManagerOfAsync(characterId, corporationId, call.Cancellation))
            return false;

        var state = await readTable("structures", call.Cancellation);
        var key = structureId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (state[key] is not JsonObject structure ||
            !IsPersistentlyDeployedStructure(structure, structureId) ||
            Positive(Integer(structure["ownerCorpID"] ?? structure["ownerID"])) != corporationId)
            return false;

        var text = call.Arg(1) switch
        {
            var value when Eve.Macho.JsSemantics.StringValue(value) is not null => Eve.Macho.JsSemantics.StringValue(value)!,
            PyBytes value => System.Text.Encoding.UTF8.GetString(value.Value.Span),
            PyInt value => value.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
            PyLong value => value.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
            PyReal value => value.Value.ToString("G", System.Globalization.CultureInfo.InvariantCulture),
            PyBool value => value.Value ? "True" : "False",
            _ => string.Empty,
        };
        var description = text[..Math.Min(1000, text.Length)];

        await setDescriptionAsync(structureId, description, call.Cancellation);
        return true;
    }

    public async ValueTask<IReadOnlyList<long>> GetStructureIdsInSolarSystemAsync(
        long solarSystemId,
        CancellationToken cancellationToken)
    {
        if (solarSystemId <= 0)
            return Array.Empty<long>();

        var state = await readTable("structures", cancellationToken);
        var ids = new List<long>();
        foreach (var (key, node) in state)
        {
            if (node is JsonObject structure &&
                long.TryParse(key, System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out var structureId) &&
                IsPersistentlyDeployedStructure(structure, structureId) &&
                Integer(structure["solarSystemID"]) == solarSystemId &&
                Integer((structure["serviceStates"] as JsonObject)?[DockingServiceId.ToString(System.Globalization.CultureInfo.InvariantCulture)]) == 1)
                ids.Add(structureId);
        }

        ids.Sort();
        return ids;
    }

    private async ValueTask<(
        JsonObject Structure,
        JsonObject Profile,
        JsonObject ProfileState,
        JsonObject AccessGroupState,
        long OwnerCorporationId,
        long OwnerAllianceId,
        long CharacterId,
        Eve.Relations.Affiliation Affiliation)?> ReadStructureSettingsContextAsync(
        long characterId,
        long structureId,
        CancellationToken cancellationToken)
    {
        if (characterId <= 0 || structureId <= 0)
            return null;

        var structureKey = structureId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var structures = await readTable("structures", cancellationToken);
        if (structures[structureKey] is not JsonObject structure || !IsPersistentlyDeployedStructure(structure, structureId))
            return null;

        var ownerCorporationId = Positive(Integer(structure["ownerCorpID"] ?? structure["corporationID"]));
        if (ownerCorporationId <= 0)
            return null;
        var ownerAllianceId = Math.Max(0, Integer(structure["allianceID"]));

        var profileId = Integer(structure["profileID"]);
        if (profileId < 0)
            return null;
        var profileState = await readTable("structureProfiles", cancellationToken);
        var profiles = profileState["profilesByID"] as JsonObject ?? profileState;
        JsonObject? profile = profileId > 0
            ? profiles[profileId.ToString(System.Globalization.CultureInfo.InvariantCulture)] as JsonObject
            : profiles.Select(pair => pair.Value)
                .OfType<JsonObject>()
                .FirstOrDefault(candidate =>
                    Integer(candidate["corporationID"]) == ownerCorporationId && Truthy(candidate["isDefault"]));

        if (profileId > 0 &&
            (profile is null || Integer(profile["corporationID"]) != ownerCorporationId))
            return null;
        if (profile is not null && Integer(profile["corporationID"]) != ownerCorporationId)
            return null;
        if (profile is null)
        {
            if (profileId > 0)
                return null;
            profile = new JsonObject
            {
                ["corporationID"] = ownerCorporationId,
                ["isDefault"] = true,
                ["settingsBySettingID"] = new JsonObject(),
            };
        }
        else if (profile["settingsBySettingID"] is not JsonObject)
        {
            if (profileId > 0 && !Truthy(profile["isDefault"]))
                return null;
            profile["settingsBySettingID"] = new JsonObject();
        }

        var accessGroupState = await readTable("accessGroups", cancellationToken);
        var affiliation = await affiliationOf(characterId, cancellationToken);
        return (
            structure,
            profile,
            profileState,
            accessGroupState,
            ownerCorporationId,
            ownerAllianceId,
            characterId,
            affiliation);
    }

    private async ValueTask<(long CharacterId, Eve.Relations.Affiliation Affiliation)> ReadAccessContextAsync(
        ServiceCall call,
        CancellationToken cancellationToken)
    {
        var characterId = call.Session.CharacterId.GetValueOrDefault();
        if (characterId <= 0)
            return (0, new Eve.Relations.Affiliation(null, null, null));
        return (characterId, await affiliationOf(characterId, cancellationToken));
    }

    private static bool AllowedMembership(JsonObject member)
    {
        var membership = Integer(member["membershipType"], 0);
        return membership is 0 or 1 or 2;
    }

    private static PyValue BridgeMapPayload(Bridge bridge)
    {
        var owner = Integer(bridge.Value["ownerCorpID"]);
        if (owner <= 0)
            owner = Integer(bridge.Value["ownerID"]);
        var name = Text(bridge.Value["itemName"]);
        if (name.Length == 0)
            name = Text(bridge.Value["name"]);
        if (name.Length == 0)
            name = "Ansiblex Jump Bridge";
        return Py.KeyVal(
            ("structureID", new PyInt(bridge.Id)),
            ("itemID", new PyInt(bridge.Id)),
            ("typeID", new PyInt(Integer(bridge.Value["typeID"]))),
            ("solarSystemID", new PyInt(bridge.SolarSystemId)),
            ("ownerID", owner > 0 ? new PyInt(owner) : PyNone.Instance),
            ("corporationID", owner > 0 ? new PyInt(owner) : PyNone.Instance),
            ("allianceID", Integer(bridge.Value["allianceID"]) > 0 ? new PyInt(Integer(bridge.Value["allianceID"])) : PyNone.Instance),
            ("itemName", new PyStr(name)),
            ("structureName", new PyStr(name)),
            ("destinationSolarsystemID", bridge.DestinationSolarSystemId > 0 ? new PyInt(bridge.DestinationSolarSystemId) : PyNone.Instance));
    }

    private static PyValue NearbyBridgePayload(Bridge bridge, long currentSystem)
    {
        var owner = Integer(bridge.Value["ownerCorpID"]);
        if (owner <= 0)
            owner = Integer(bridge.Value["ownerID"]);
        var name = Text(bridge.Value["itemName"]);
        if (name.Length == 0)
            name = Text(bridge.Value["name"]);
        if (name.Length == 0)
            name = "Ansiblex Jump Bridge";
        return Py.KeyVal(
            ("structureID", new PyInt(bridge.Id)),
            ("typeID", new PyInt(Integer(bridge.Value["typeID"]))),
            ("solarSystemID", new PyInt(bridge.SolarSystemId)),
            ("ownerID", owner > 0 ? new PyInt(owner) : PyNone.Instance),
            ("structureName", new PyStr(name)),
            ("destinationSolarsystemID", bridge.DestinationSolarSystemId > 0 ? new PyInt(bridge.DestinationSolarSystemId) : PyNone.Instance),
            ("alignedToCurrentSystem", PyValue.Of(bridge.DestinationSolarSystemId > 0 && bridge.DestinationSolarSystemId == currentSystem)));
    }

    private static PyValue EmptyJumpBridgeAccess() =>
        new PyTuple([new PyList([]), new PyList([]), new PyList([])]);

    private static long Positive(long value) => value > 0 ? value : 0;
    private static long Integer(JsonNode? value, long fallback = 0) =>
        long.TryParse(value?.ToString(), System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var number) ? number : fallback;
    private static string Text(JsonNode? value) =>
        value is JsonValue scalar && scalar.TryGetValue<string>(out var text) ? text : "";
    private static bool Truthy(JsonNode? value)
    {
        if (value is null)
            return false;
        if (value is JsonValue scalar && scalar.TryGetValue<bool>(out var boolean))
            return boolean;
        if (double.TryParse(value.ToString(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var number))
            return number != 0;
        return value.ToString().Length > 0;
    }

    private static long SdeInteger(Eve.Sde.SdeRow row, string field)
    {
        if (!row.Doc.TryGetProperty(field, out var value))
            return 0;
        if (value.TryGetInt64(out var integer))
            return integer;
        return value.TryGetDouble(out var number) && double.IsFinite(number)
            ? (long)Math.Truncate(number)
            : 0;
    }

    private static double SdeDouble(Eve.Sde.SdeRow row, string field) =>
        row.Doc.TryGetProperty(field, out var value) &&
        value.TryGetDouble(out var number) && double.IsFinite(number)
            ? number
            : 0;

    private static bool SdeBoolean(Eve.Sde.SdeRow row, string field)
    {
        if (!row.Doc.TryGetProperty(field, out var value))
            return false;
        if (value.ValueKind is System.Text.Json.JsonValueKind.True)
            return true;
        if (value.ValueKind is System.Text.Json.JsonValueKind.Number)
            return value.TryGetInt64(out var integer) && integer != 0;
        return false;
    }

    private static PyValue StructureSettingValue(JsonNode? value)
    {
        if (value is null)
            return PyBool.True;
        if (value is JsonValue scalar)
        {
            if (scalar.TryGetValue<bool>(out var boolean))
                return PyValue.Of(boolean);
            if (scalar.TryGetValue<int>(out var int32))
                return new PyInt(int32);
            if (scalar.TryGetValue<long>(out var int64))
                return new PyLong(int64);
            if (scalar.TryGetValue<double>(out var real) && double.IsFinite(real))
                return new PyReal(real);
            if (scalar.TryGetValue<string>(out var text))
                return new PyStr(text);
        }
        return new PyStr(value.ToString());
    }
}
