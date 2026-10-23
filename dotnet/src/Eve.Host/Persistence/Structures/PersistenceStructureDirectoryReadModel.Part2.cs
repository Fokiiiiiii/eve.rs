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

public sealed partial class PersistenceStructureDirectoryReadModel
{
    private static int? SettingIdForService(long serviceId) => serviceId switch
    {
        DockingServiceId => DockAccessSettingId,
        JumpBridgeServiceId => JumpBridgeAccessSettingId,
        3 => 20,
        Eve.Structures.StructureServiceIds.Market => 4,
        Eve.Structures.StructureServiceIds.Medical => 23,
        // MOON_MINING is admitted by the control setting (EVE.js structureServiceAuthority.js SERVICE_ACCESS_SETTING_BY_ID)
        14 => 17,
        16 => 35,
        _ => null,
    };

    private sealed record Bridge(long Id, long SolarSystemId, long DestinationSolarSystemId, JsonObject Value);

    internal static bool IsPersistentlyDeployedStructure(JsonObject structure, long structureId)
    {
        var storedId = Positive(Integer(structure["structureID"] ?? structure["itemID"], structureId));
        return structureId > 0 && storedId == structureId &&
            Positive(Integer(structure["typeID"])) > 0 &&
            Positive(Integer(structure["solarSystemID"])) > 0 &&
            Positive(Integer(structure["ownerCorpID"] ?? structure["corporationID"])) > 0 &&
            !Truthy(structure["destroyedAt"]);
    }

    private static PyValue BasicStructureInfoPayload(long structureId, PyValue fullInfo)
{
    var fields = fullInfo is PyObject { Name: PyStr { Value: "util.KeyVal" }, Args: PyDict keyVal }
        ? keyVal.Entries
        : Array.Empty<KeyValuePair<PyValue, PyValue>>();
    PyValue Read(string name) => fields.FirstOrDefault(entry => entry.Key is PyStr key && key.Value == name).Value
        ?? PyNone.Instance;
    return Py.KeyVal(
        ("structureID", new PyLong(structureId)),
        ("typeID", Read("typeID")),
        ("solarSystemID", Read("solarSystemID")),
        ("ownerID", Read("ownerID")));
}

private static PyValue StructureInfoPayload(long structureId, JsonObject structure)
    {
        var ownerCorporationId = Positive(Integer(structure["ownerCorpID"] ?? structure["corporationID"]));
        var ownerId = Positive(Integer(structure["ownerID"], ownerCorporationId));
        var allianceId = Math.Max(0, Integer(structure["allianceID"]));
        var services = structure["services"] as JsonObject ??
            structure["serviceStates"] as JsonObject ?? new JsonObject();
        var serviceEntries = new List<KeyValuePair<PyValue, PyValue>>();

        foreach (var (key, value) in services)
        {
            if (!long.TryParse(key, System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out var serviceId) ||
                serviceId <= 0 || serviceId > 9_007_199_254_740_991L)
                continue;
            var state = Integer(value, long.MinValue);
            if (state != long.MinValue)
                serviceEntries.Add(new KeyValuePair<PyValue, PyValue>(new PyLong(serviceId), new PyInt(state)));
        }

        return Py.KeyVal(
            ("structureID", new PyLong(structureId)),
            ("itemID", new PyLong(structureId)),
            ("typeID", new PyInt(Positive(Integer(structure["typeID"])))),
            ("solarSystemID", new PyInt(Positive(Integer(structure["solarSystemID"])))),
            ("ownerID", ownerId > 0 ? new PyLong(ownerId) : PyNone.Instance),
            ("corporationID", new PyLong(ownerCorporationId)),
            ("ownerCorpID", new PyLong(ownerCorporationId)),
            ("allianceID", new PyLong(allianceId)),
            ("services", new PyDict(serviceEntries)));
    }

    private static (
        bool ProfileResolved,
        bool DockingSettingResolved,
        IReadOnlyList<Eve.Services.Core.StructureAccessListSnapshot?>? AccessLists) DockingAccessLists(
            JsonObject structure,
            long ownerCorporationId,
            JsonObject profileState,
            JsonObject accessGroupState)
    {
        var profileId = Integer(structure["profileID"]);
        if (profileId < 0)
            return (false, false, null);

        var profiles = profileState["profilesByID"] as JsonObject ?? profileState;
        JsonObject? profile = profileId > 0
            ? profiles[profileId.ToString(System.Globalization.CultureInfo.InvariantCulture)] as JsonObject
            : profiles.Select(pair => pair.Value)
                .OfType<JsonObject>()
                .FirstOrDefault(candidate =>
                    Integer(candidate["corporationID"]) == ownerCorporationId && Truthy(candidate["isDefault"]));

        if (profileId > 0 &&
            (profile is null || Integer(profile["corporationID"]) != ownerCorporationId))
            return (false, false, null);

        if (profile is not null && Integer(profile["corporationID"]) != ownerCorporationId)
            return (false, false, null);

        if (profile is null)
            return OwnerCorporationDockingAccess(ownerCorporationId);

        var settings = profile["settingsBySettingID"] as JsonObject;
        if (settings is null || settings["19"] is not JsonArray groups || groups.Count == 0)
            return profileId <= 0 || Truthy(profile["isDefault"])
                ? OwnerCorporationDockingAccess(ownerCorporationId)
                : (true, false, null);

        var accessLists = new List<Eve.Services.Core.StructureAccessListSnapshot?>();
        foreach (var node in groups)
        {
            if (node is not JsonObject row)
                return (true, false, null);
            var groupId = Integer(row["groupID"], -1);
            if (groupId < 0)
                return (true, false, null);
            if (groupId == 0)
            {
                accessLists.Add(OwnerCorporationAccessList(ownerCorporationId));
                continue;
            }

            var groupKey = groupId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (accessGroupState[groupKey] is not JsonObject group ||
                group["members"] is not JsonArray members)
            {
                accessLists.Add(new Eve.Services.Core.StructureAccessListSnapshot(groupId, false, null));
                continue;
            }

            var normalizedMembers = new List<Eve.Services.Core.StructureAccessListMember>();
            var malformed = false;
            foreach (var member in members)
            {
                if (member is not JsonObject entry)
                {
                    malformed = true;
                    break;
                }
                var memberId = Integer(entry["memberID"], -1);
                var membershipType = Integer(entry["membershipType"], -1);
                if (memberId < 0 || membershipType is < 0 or > 3)
                {
                    malformed = true;
                    break;
                }
                normalizedMembers.Add(new Eve.Services.Core.StructureAccessListMember(
                    memberId, membershipType is 0 or 1 or 2));
            }

            if (malformed)
            {
                accessLists.Add(new Eve.Services.Core.StructureAccessListSnapshot(groupId, false, null));
                continue;
            }

            var creatorId = Positive(Integer(group["creatorID"]));
            if (creatorId > 0 && normalizedMembers.All(member => member.MemberId != creatorId))
                normalizedMembers.Add(new Eve.Services.Core.StructureAccessListMember(creatorId, true));
            accessLists.Add(new Eve.Services.Core.StructureAccessListSnapshot(groupId, true, normalizedMembers));
        }

        return (true, true, accessLists);
    }

    private static (
        bool ProfileResolved,
        bool DockingSettingResolved,
        IReadOnlyList<Eve.Services.Core.StructureAccessListSnapshot?>? AccessLists) OwnerCorporationDockingAccess(
            long ownerCorporationId) =>
        ownerCorporationId > 0
            ? (true, true, [OwnerCorporationAccessList(ownerCorporationId)])
            : (false, false, null);

    private static Eve.Services.Core.StructureAccessListSnapshot OwnerCorporationAccessList(long ownerCorporationId) =>
        new(0, true, [new Eve.Services.Core.StructureAccessListMember(ownerCorporationId, true)]);

    public async ValueTask<PyDict> CharacterGetServicesAsync(
        long characterId,
        long structureId,
        CancellationToken cancellationToken)
    {
        var context = await ReadStructureSettingsContextAsync(characterId, structureId, cancellationToken);
        if (context is null || context.Value.Structure["serviceStates"] is not JsonObject services)
            return new PyDict([]);

        var access = context.Value;
        var result = new List<KeyValuePair<PyValue, PyValue>>();
        foreach (var (key, node) in services)
        {
            if (!long.TryParse(key, System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out var serviceId) ||
                serviceId <= 0 || serviceId > 9_007_199_254_740_991L)
                continue;
            var settingId = SettingIdForService(serviceId);
            if (settingId is null ||
                !ProfileSettingAllows(
                    access.Structure,
                    access.OwnerCorporationId,
                    (access.CharacterId, access.Affiliation),
                    access.ProfileState,
                    access.AccessGroupState,
                    settingId.Value))
                continue;
            var state = Integer(node, long.MinValue);
            if (state == long.MinValue)
                continue;
            result.Add(new KeyValuePair<PyValue, PyValue>(new PyInt(serviceId), new PyInt(state)));
        }
        return new PyDict(result);
    }

    /// <summary>The Corporation Office Rent profile setting (EVE.js STRUCTURE_SETTING_ID.CORP_RENT_OFFICE).</summary>
    private const int CorporationOfficeRentSettingId = 20;

    /// <summary>
    /// The rent of a corporation office at the structure for the character: the value of the profile's Corporation Office Rent setting that the character's access
    /// resolves to (EVE.js getProfileSettingValueForStructure with the session). 0 when no group of the profile applies to the character.
    /// </summary>
    public async ValueTask<decimal> CharacterOfficeRentAsync(long characterId, long structureId, CancellationToken cancellationToken)
    {
        var context = await ReadStructureSettingsContextAsync(characterId, structureId, cancellationToken);
        if (context is not { } access ||
            access.Profile["settingsBySettingID"] is not JsonObject settings ||
            settings[CorporationOfficeRentSettingId.ToString(System.Globalization.CultureInfo.InvariantCulture)] is not JsonArray groups)
            return 0;
        var bestRank = 0;
        JsonNode? resolved = null;
        foreach (var node in groups)
        {
            if (node is not JsonObject row) continue;
            var groupId = Integer(row["groupID"], -1);
            if (groupId < 0) continue;
            var rank = groupId == 0
                ? access.OwnerCorporationId > 0 && access.Affiliation.Corporation == access.OwnerCorporationId ? 1 : 0
                : AccessGroupMatchRank(groupId, (access.CharacterId, access.Affiliation), access.AccessGroupState);
            if (rank > bestRank)
            {
                bestRank = rank;
                resolved = row["value"];
            }
        }
        return resolved is JsonValue value && value.TryGetValue<decimal>(out var rent) ? Math.Max(0, rent) : 0;
    }

    /// <summary>

    /// profile's Corporation Office Rent setting. Null when the id is no persistently deployed structure or the profile has no such group: the rent then stays.
    /// </summary>
    public async ValueTask<decimal?> RenewalRentAsync(long structureId, CancellationToken cancellationToken)
    {
        if (structureId <= 0)
            return null;
        var structures = await readTable("structures", cancellationToken);
        if (structures[structureId.ToString(System.Globalization.CultureInfo.InvariantCulture)] is not JsonObject structure || !IsPersistentlyDeployedStructure(structure, structureId))
            return null;
        var ownerCorporationId = Positive(Integer(structure["ownerCorpID"] ?? structure["corporationID"]));
        var profileId = Integer(structure["profileID"]);
        var profileState = await readTable("structureProfiles", cancellationToken);
        var profiles = profileState["profilesByID"] as JsonObject ?? profileState;
        var profile = profileId > 0
            ? profiles[profileId.ToString(System.Globalization.CultureInfo.InvariantCulture)] as JsonObject
            : profiles.Select(pair => pair.Value).OfType<JsonObject>().FirstOrDefault(candidate =>
                Integer(candidate["corporationID"]) == ownerCorporationId && Truthy(candidate["isDefault"]));
        if (profile?["settingsBySettingID"] is not JsonObject settings ||
            settings[CorporationOfficeRentSettingId.ToString(System.Globalization.CultureInfo.InvariantCulture)] is not JsonArray groups)
            return null;
        foreach (var node in groups)
        {
            if (node is JsonObject row && Integer(row["groupID"], -1) == 0 && row["value"] is JsonValue value && value.TryGetValue<decimal>(out var rent))
                return Math.Max(0, rent);
        }
        return null;
    }

    public async ValueTask<PyValue> CharacterGetServiceAsync(
        long characterId,
        long structureId,
        long serviceId,
        CancellationToken cancellationToken)
    {
        if (!await CharacterHasServiceAsync(characterId, structureId, serviceId, cancellationToken))
            return PyNone.Instance;

        var settingId = SettingIdForService(serviceId);
        if (settingId is not > 0)
            return PyBool.True;

        var context = await ReadStructureSettingsContextAsync(characterId, structureId, cancellationToken);
        if (context is null)
            return PyNone.Instance;
        var settings = context.Value.Profile["settingsBySettingID"] as JsonObject;
        var settingGroups = settings?[settingId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)] as JsonArray;
        if (settingGroups is not { Count: > 0 })
            return PyBool.True;

        var access = (context.Value.CharacterId, context.Value.Affiliation);
        var bestRank = 0;
        JsonNode? resolvedValue = null;
        foreach (var node in settingGroups)
        {
            if (node is not JsonObject row)
                continue;
            var groupId = Integer(row["groupID"], -1);
            if (groupId < 0)
                continue;
            var rank = groupId == 0
                ? context.Value.OwnerCorporationId > 0 &&
                    context.Value.Affiliation.Corporation == context.Value.OwnerCorporationId ? 1 : 0
                : AccessGroupMatchRank(groupId, access, context.Value.AccessGroupState);
            if (rank > bestRank)
            {
                bestRank = rank;
                resolvedValue = row["value"];
            }
        }

        return StructureSettingValue(resolvedValue);
    }

    public async ValueTask<bool> CharacterHasServiceAsync(
        long characterId,
        long structureId,
        long serviceId,
        CancellationToken cancellationToken)
    {
        if (serviceId <= 0 || serviceId > 9_007_199_254_740_991L)
            return false;
        var settingId = SettingIdForService(serviceId);
        if (settingId is null)
            return false;

        var context = await ReadStructureSettingsContextAsync(characterId, structureId, cancellationToken);
        return context is { } access &&
            ProfileSettingAllows(
                access.Structure,
                access.OwnerCorporationId,
                (access.CharacterId, access.Affiliation),
                access.ProfileState,
                access.AccessGroupState,
                settingId.Value) &&
            access.Structure["serviceStates"] is JsonObject services &&
            Integer(services[serviceId.ToString(System.Globalization.CultureInfo.InvariantCulture)]) == 1;
    }

    public async ValueTask<bool> CharacterHasSettingAsync(
        long characterId,
        long structureId,
        long settingId,
        CancellationToken cancellationToken)
    {
        if (settingId <= 0 || settingId > int.MaxValue)
            return false;
        var context = await ReadStructureSettingsContextAsync(characterId, structureId, cancellationToken);
        return context is { } access &&
            ProfileSettingAllows(
                access.Structure,
                access.OwnerCorporationId,
                (access.CharacterId, access.Affiliation),
                access.ProfileState,
                access.AccessGroupState,
                (int)settingId);
    }

    private static IReadOnlyList<Bridge> ReadBridges(JsonObject state)
    {
        var bridges = new List<Bridge>();
        foreach (var (key, node) in state)
        {
            if (node is not JsonObject structure || Integer(structure["typeID"]) != AnsiblexJumpBridgeTypeId ||
                Truthy(structure["destroyedAt"]))
                continue;
            var id = Integer(structure["structureID"]);
            if (id <= 0)
                id = Integer(JsonValue.Create(key));
            var systemId = Integer(structure["solarSystemID"]);
            var destinationId = JumpBridgeDestination(structure);
            if (id > 0 && systemId > 0)
                bridges.Add(new Bridge(id, systemId, destinationId, structure));
        }
        bridges.Sort((left, right) => left.Id.CompareTo(right.Id));
        return bridges;
    }

    private static IReadOnlyList<(Bridge Source, Bridge Destination)> ReciprocalPairs(IReadOnlyList<Bridge> bridges)
    {
        var pairs = new List<(Bridge Source, Bridge Destination)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var source in bridges)
        {
            if (!IsJumpBridgeServiceOnline(source) || source.DestinationSolarSystemId <= 0)
                continue;
            var destination = bridges.FirstOrDefault(candidate =>
                candidate.Id != source.Id &&
                candidate.SolarSystemId == source.DestinationSolarSystemId &&
                candidate.DestinationSolarSystemId == source.SolarSystemId &&
                IsJumpBridgeServiceOnline(candidate));
            if (destination is null)
                continue;
            var pairKey = source.Id < destination.Id
                ? $"{source.Id}:{destination.Id}"
                : $"{destination.Id}:{source.Id}";
            if (seen.Add(pairKey))
                pairs.Add((source, destination));
        }
        return pairs;
    }

    private static bool IsJumpBridgeServiceOnline(Bridge bridge) =>
        Integer((bridge.Value["serviceStates"] as JsonObject)?[JumpBridgeServiceId.ToString(System.Globalization.CultureInfo.InvariantCulture)]) == 1;

    private static long JumpBridgeDestination(JsonObject structure)
    {
        var flags = structure["devFlags"] as JsonObject;
        return Positive(Integer(flags?["destinationSolarsystemID"])) is var direct && direct > 0
            ? direct
            : Positive(Integer(flags?["sovereigntyJumpBridgeDestinationSolarsystemID"]));
    }

    private static bool CanUseJumpBridge(
        Bridge bridge,
        (long CharacterId, Eve.Relations.Affiliation Affiliation) access,
        JsonObject profileState,
        JsonObject accessGroupState)
    {
        if (!IsJumpBridgeServiceOnline(bridge))
            return false;

        var ownerCorporationId = Integer(bridge.Value["ownerCorpID"]);
        if (ownerCorporationId <= 0)
            ownerCorporationId = Integer(bridge.Value["ownerID"]);
        var ownerAllianceId = Integer(bridge.Value["allianceID"]);
        var ownerAccess =
            ownerCorporationId > 0 && access.Affiliation.Corporation == ownerCorporationId ||
            ownerAllianceId > 0 && access.Affiliation.Alliance == ownerAllianceId;
        var dockAccess = HasDockAccess(bridge, access, ownerCorporationId, ownerAllianceId, profileState, accessGroupState);
        if (!ownerAccess && !dockAccess)
            return false;

        return ProfileSettingAllows(
            bridge.Value,
            ownerCorporationId,
            access,
            profileState,
            accessGroupState,
            JumpBridgeAccessSettingId);
    }

    private static bool HasDockAccess(
        Bridge bridge,
        (long CharacterId, Eve.Relations.Affiliation Affiliation) access,
        long ownerCorporationId,
        long ownerAllianceId,
        JsonObject profileState,
        JsonObject accessGroupState)
    {
        if (Integer((bridge.Value["serviceStates"] as JsonObject)?[DockingServiceId.ToString(System.Globalization.CultureInfo.InvariantCulture)]) != 1)
            return false;

        var accessProfile = bridge.Value["accessProfile"] as JsonObject;
        var policy = Text(accessProfile?["docking"]);
        if (policy.Length == 0)
            policy = "public";
        var policyAllows = policy switch
        {
            "public" => true,
            "none" => false,
            "owner" or "corp" => ownerCorporationId > 0 && access.Affiliation.Corporation == ownerCorporationId,
            "alliance" => ownerAllianceId > 0
                ? access.Affiliation.Alliance == ownerAllianceId
                : ownerCorporationId > 0 && access.Affiliation.Corporation == ownerCorporationId,
            _ => false,
        };
        return policyAllows && ProfileSettingAllows(
            bridge.Value,
            ownerCorporationId,
            access,
            profileState,
            accessGroupState,
            DockAccessSettingId);
    }

    private static bool ProfileSettingAllows(
        JsonObject structure,
        long ownerCorporationId,
        (long CharacterId, Eve.Relations.Affiliation Affiliation) access,
        JsonObject profileState,
        JsonObject accessGroupState,
        int settingId)
    {
        var profiles = profileState["profilesByID"] as JsonObject ?? profileState;
        var requestedProfileId = Integer(structure["profileID"]);
        if (requestedProfileId < 0)
            return false;

        JsonObject? profile = requestedProfileId > 0
            ? profiles[requestedProfileId.ToString(System.Globalization.CultureInfo.InvariantCulture)] as JsonObject
            : profiles.Select(pair => pair.Value)
                .OfType<JsonObject>()
                .FirstOrDefault(candidate =>
                    Integer(candidate["corporationID"]) == ownerCorporationId && Truthy(candidate["isDefault"]));

        if (requestedProfileId > 0 &&
            (profile is null || Integer(profile["corporationID"]) != ownerCorporationId))
            return false;
        if (profile is not null && Integer(profile["corporationID"]) != ownerCorporationId)
            return false;
        if (profile is null)
            return requestedProfileId <= 0 && ownerCorporationId > 0 &&
                access.Affiliation.Corporation == ownerCorporationId;

        var settings = profile["settingsBySettingID"] as JsonObject;
        if (settings?[settingId.ToString(System.Globalization.CultureInfo.InvariantCulture)] is not JsonArray settingGroups ||
            settingGroups.Count == 0)
        {
            return ownerCorporationId > 0 &&
                access.Affiliation.Corporation == ownerCorporationId &&
                (requestedProfileId <= 0 || Truthy(profile["isDefault"]));
        }

        var hasDefaultGroup = false;
        var bestRank = 0;
        foreach (var node in settingGroups)
        {
            if (node is not JsonObject row)
                continue;
            var groupId = Integer(row["groupID"], -1);
            if (groupId < 0)
                return false;
            if (groupId == 0)
            {
                // The built-in default access list contains only the owning corporation.
                hasDefaultGroup |= ownerCorporationId > 0 &&
                    access.Affiliation.Corporation == ownerCorporationId;
                continue;
            }
            bestRank = Math.Max(bestRank, AccessGroupMatchRank(groupId, access, accessGroupState));
        }
        return bestRank > 0 || hasDefaultGroup;
    }

    internal static int AccessGroupMatchRank(
        long groupId,
        (long CharacterId, Eve.Relations.Affiliation Affiliation) access,
        JsonObject accessGroupState)
    {
        if (groupId <= 0 || access.CharacterId <= 0 ||
            accessGroupState[groupId.ToString(System.Globalization.CultureInfo.InvariantCulture)] is not JsonObject group)
            return 0;

        var members = group["members"] as JsonArray ?? [];
        var direct = members.OfType<JsonObject>()
            .FirstOrDefault(member => Integer(member["memberID"], -1) == access.CharacterId);
        if (direct is not null)
            return AllowedMembership(direct) ? 430 : 0;
        if (Integer(group["creatorID"]) == access.CharacterId)
            return 430;

        foreach (var (memberId, specificity) in new (long Id, int Specificity)[]
        {
            (access.Affiliation.Corporation ?? 0, 300),
            (access.Affiliation.Alliance ?? 0, 200),
            (0, 100),
        })
        {
            var member = members.OfType<JsonObject>()
                .FirstOrDefault(candidate => Integer(candidate["memberID"], -1) == memberId);
            if (member is null)
                continue;
            return AllowedMembership(member) ? specificity + 30 : 0;
        }
        return 0;
    }
}
