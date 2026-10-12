using System.Globalization;
using System.Text.Json.Nodes;
using Eve.GameStore;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;

namespace Eve.Host;

public sealed class PersistenceStructureProfilesRpcBackend(
    Func<string, CancellationToken, Task<JsonObject>> readTable,
    GameStoreCache gameStoreCache,
    CharacterSessions characterSessions,
    Func<IReadOnlyCollection<long>, long, CancellationToken, Task>? setStructureProfileAsync = null)
{
    private const string Table = "structureProfiles";
    private const string DefaultName = "Default Profile";

    public async ValueTask<IReadOnlyList<StructureProfileRecord>> GetProfilesAsync(
        long corporationId,
        CancellationToken cancellationToken)
    {
        if (corporationId <= 0)
            return Array.Empty<StructureProfileRecord>();
        var root = await ReadRootAsync(cancellationToken).ConfigureAwait(false);
        var changed = EnsureRoot(root);
        changed |= EnsureDefaultProfile(root, corporationId);
        if (changed)
            await WriteRootAsync(root, cancellationToken).ConfigureAwait(false);
        return ListProfileEntries(root, corporationId)
            .Where(entry => ProfileId(entry.Profile, entry.Key) > 0)
            .OrderByDescending(entry => Truthy(entry.Profile["isDefault"]))
            .ThenBy(entry => Text(entry.Profile["name"]), StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => ProfileId(entry.Profile, entry.Key))
            .Select(entry => new StructureProfileRecord(
                ProfileId(entry.Profile, entry.Key),
                Text(entry.Profile["name"]),
                Text(entry.Profile["description"]),
                Truthy(entry.Profile["isDefault"])))
            .ToArray();
    }

    public async ValueTask<IReadOnlyDictionary<long, IReadOnlyList<StructureProfileSetting>>> GetProfileSettingsAsync(
        long corporationId,
        long profileId,
        CancellationToken cancellationToken)
    {
        if (corporationId <= 0 || profileId <= 0)
            return EmptySettings();
        var root = await ReadRootAsync(cancellationToken).ConfigureAwait(false);
        if (FindProfile(root, corporationId, profileId) is not { } profile ||
            profile["settingsBySettingID"] is not JsonObject settings)
            return EmptySettings();

        var result = new Dictionary<long, IReadOnlyList<StructureProfileSetting>>();
        foreach (var (rawSettingId, node) in settings)
        {
            if (!long.TryParse(rawSettingId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var settingId) ||
                settingId <= 0 || node is not JsonArray groups)
                continue;
            result[settingId] = groups.OfType<JsonObject>()
                .Select(group => new StructureProfileSetting(
                    Integer(group["groupID"]),
                    ToPyValue(group["value"])))
                .ToArray();
        }
        return result;
    }

    public async ValueTask<PyValue?> GetMyHangarViewStateAsync(long structureId, CancellationToken cancellationToken)
    {
        if (structureId <= 0)
            return null;
        var structures = await readTable("structures", cancellationToken).ConfigureAwait(false);
        if (structures[structureId.ToString(CultureInfo.InvariantCulture)] is not JsonObject structure ||
            Truthy(structure["destroyedAt"]))
            return null;

        var operatingState = NormalizeOperatingState(Integer(structure["state"]));
        var upkeepState = NormalizeUpkeepState(Integer(structure["upkeepState"]));
        var damage = DamageState(structure, operatingState);
        var entries = new List<KeyValuePair<PyValue, PyValue>>
        {
            Pair("_operatingState", new PyInt(operatingState)),
            Pair("_upkeepState", new PyInt(upkeepState)),
            Pair("_damageState", new PyList(damage.Select(value => (PyValue)new PyReal(value)).ToArray())),
        };

        var timerStart = FileTime(structure["stateStartedAt"]);
        var timerEnd = FileTime(structure["stateEndsAt"]);
        var timerPause = FileTime(structure["timerPausedAt"]);
        if (timerStart is not null || timerEnd is not null || timerPause is not null)
        {
            entries.Add(Pair("_timerStartAt", timerStart is { } start ? new PyLong(start) : PyNone.Instance));
            entries.Add(Pair("_timerEndAt", timerEnd is { } end ? new PyLong(end) : PyNone.Instance));
            entries.Add(Pair("_timerPauseAt", timerPause is { } pause ? new PyLong(pause) : PyNone.Instance));
        }

        var header = new PyList([
            new PyList([new PyToken("structures.structureHangarViewState.StructureHangarViewState")]),
            new PyDict(entries),
        ]);
        return new PyObjectEx(ObjectExKind.Ex2, header, [], []);
    }

    public async ValueTask<long?> CreateProfileAsync(
        long corporationId,
        string name,
        string description,
        CancellationToken cancellationToken)
    {
        if (corporationId <= 0)
            return null;
        var root = await ReadRootAsync(cancellationToken).ConfigureAwait(false);
        EnsureRoot(root);
        var profile = CreateProfile(root, corporationId, name, description, null, isDefault: false);
        await WriteRootAsync(root, cancellationToken).ConfigureAwait(false);
        var profileId = Integer(profile["profileID"]);
        NotifyProfileSettingsChanged(corporationId, [profileId]);
        return profileId;
    }

    public async ValueTask UpdateProfileAsync(
        long corporationId,
        long profileId,
        string? name,
        string? description,
        CancellationToken cancellationToken)
    {
        var root = await ReadRootAsync(cancellationToken).ConfigureAwait(false);
        if (FindProfile(root, corporationId, profileId) is not { } profile)
            return;
        if (!string.IsNullOrEmpty(name))
            profile["name"] = UniqueName(root, corporationId, NormalizeName(name), profileId);
        if (description is not null)
            profile["description"] = NormalizeDescription(description);
        profile["updatedAt"] = Now();
        await WriteRootAsync(root, cancellationToken).ConfigureAwait(false);
        NotifyProfileSettingsChanged(corporationId, [profileId]);
    }

    public async ValueTask SaveProfileSettingsAsync(
        long corporationId,
        long profileId,
        PyValue rawSettings,
        CancellationToken cancellationToken)
    {
        var root = await ReadRootAsync(cancellationToken).ConfigureAwait(false);
        if (FindProfile(root, corporationId, profileId) is not { } profile)
            return;
        profile["settingsBySettingID"] = NormalizeSettings(rawSettings);
        profile["updatedAt"] = Now();
        await WriteRootAsync(root, cancellationToken).ConfigureAwait(false);
        NotifyProfileSettingsChanged(corporationId, [profileId]);
    }

    public async ValueTask SetDefaultProfileAsync(
        long corporationId,
        long profileId,
        CancellationToken cancellationToken)
    {
        var root = await ReadRootAsync(cancellationToken).ConfigureAwait(false);
        if (FindProfile(root, corporationId, profileId) is not { } target)
            return;
        foreach (var profile in ListProfiles(root, corporationId))
            profile["isDefault"] = false;
        target["isDefault"] = true;
        target["updatedAt"] = Now();
        await WriteRootAsync(root, cancellationToken).ConfigureAwait(false);
        NotifyProfileSettingsChanged(corporationId, ProfileIds(root, corporationId).Append(profileId));
    }

    public async ValueTask ChangeProfilesAsync(
        long corporationId,
        IReadOnlyList<long> structureIds,
        long requestedProfileId,
        CancellationToken cancellationToken)
    {
        if (corporationId <= 0 || structureIds.Count == 0)
            return;
        var profileRoot = await ReadRootAsync(cancellationToken).ConfigureAwait(false);
        var profilesChanged = EnsureRoot(profileRoot);
        var profileId = requestedProfileId;
        if (FindProfile(profileRoot, corporationId, profileId) is null)
        {
            profilesChanged |= EnsureDefaultProfile(profileRoot, corporationId);
            profileId = DefaultProfileId(profileRoot, corporationId);
        }
        if (profilesChanged)
            await WriteRootAsync(profileRoot, cancellationToken).ConfigureAwait(false);

        var structures = await readTable("structures", cancellationToken).ConfigureAwait(false);
        var moved = new List<long>();
        foreach (var structureId in structureIds.Where(id => id > 0).Distinct())
        {
            if (structures[structureId.ToString(CultureInfo.InvariantCulture)] is not JsonObject structure ||
                Truthy(structure["destroyedAt"]) ||
                Integer(structure["ownerCorpID"] ?? structure["ownerID"]) != corporationId)
                continue;
            moved.Add(structureId);
        }
        if (moved.Count > 0)
        {
            // the structures schema owns the profile of a structure (ST-0); the game store's table is no longer written
            var set = setStructureProfileAsync ?? throw new InvalidOperationException("structure profiles are not wired to the structure store");
            await set(moved, profileId, cancellationToken).ConfigureAwait(false);
            NotifyCorporationStructuresUpdated(corporationId);
        }
    }

    public async ValueTask DeleteProfileAsync(
        long corporationId,
        long profileId,
        CancellationToken cancellationToken)
    {
        var root = await ReadRootAsync(cancellationToken).ConfigureAwait(false);
        var target = FindProfile(root, corporationId, profileId);
        var profiles = root["profilesByID"] as JsonObject;
        if (target is null || profiles is null)
            return;
        var wasDefault = Truthy(target["isDefault"]);
        profiles.Remove(profileId.ToString(CultureInfo.InvariantCulture));
        var remaining = ListProfiles(root, corporationId);
        if (remaining.Count == 0)
            CreateProfile(root, corporationId, DefaultName, "", null, isDefault: true);
        else if (wasDefault)
        {
            foreach (var profile in remaining)
                profile["isDefault"] = false;
            remaining[0]["isDefault"] = true;
            remaining[0]["updatedAt"] = Now();
        }
        await WriteRootAsync(root, cancellationToken).ConfigureAwait(false);
        NotifyProfileSettingsChanged(corporationId, ProfileIds(root, corporationId).Append(profileId));
    }

    public async ValueTask<long?> DuplicateProfileAsync(
        long corporationId,
        long profileId,
        CancellationToken cancellationToken)
    {
        var root = await ReadRootAsync(cancellationToken).ConfigureAwait(false);
        if (FindProfile(root, corporationId, profileId) is not { } source)
            return null;
        var settings = source["settingsBySettingID"]?.DeepClone() as JsonObject ?? new JsonObject();
        var duplicate = CreateProfile(
            root,
            corporationId,
            $"{Text(source["name"])} Copy",
            Text(source["description"]),
            settings,
            isDefault: false);
        await WriteRootAsync(root, cancellationToken).ConfigureAwait(false);
        var duplicateId = Integer(duplicate["profileID"]);
        NotifyProfileSettingsChanged(corporationId, [duplicateId]);
        return duplicateId;
    }

    private async Task<JsonObject> ReadRootAsync(CancellationToken cancellationToken) =>
        await readTable(Table, cancellationToken).ConfigureAwait(false);

    private async ValueTask WriteRootAsync(JsonObject root, CancellationToken cancellationToken) =>
        await WriteTableAsync(Table, root, cancellationToken).ConfigureAwait(false);

    private async ValueTask WriteTableAsync(string table, JsonObject root, CancellationToken cancellationToken)
    {
        if (table == Table)
        {
            var meta = root["meta"] as JsonObject ?? new JsonObject();
            meta["version"] = 1;
            meta["description"] = "DB-backed corporation structure deployment/access profile state.";
            meta["updatedAt"] = Now();
            root["meta"] = meta;
        }
        var result = gameStoreCache.Write(table, null, root);
        if (!result.Success)
            throw new InvalidOperationException($"Could not update {table}: {result.ErrorMsg}");
        await gameStoreCache.FlushTableAsync(table, cancellationToken).ConfigureAwait(false);
    }

    private static bool EnsureRoot(JsonObject root)
    {
        var changed = false;
        if (root["meta"] is not JsonObject)
        {
            root["meta"] = new JsonObject
            {
                ["version"] = 1,
                ["description"] = "DB-backed corporation structure deployment/access profile state.",
                ["updatedAt"] = null,
            };
            changed = true;
        }
        if (root["profilesByID"] is not JsonObject)
        {
            root["profilesByID"] = new JsonObject();
            changed = true;
        }
        if (Integer(root["nextProfileID"]) <= 0)
        {
            root["nextProfileID"] = 1;
            changed = true;
        }
        return changed;
    }

    private bool EnsureDefaultProfile(JsonObject root, long corporationId)
    {
        var profiles = ListProfiles(root, corporationId);
        if (profiles.Count == 0)
        {
            CreateProfile(root, corporationId, DefaultName, "", null, isDefault: true);
            return true;
        }
        if (profiles.Count(profile => Truthy(profile["isDefault"])) == 1)
            return false;
        foreach (var profile in profiles)
            profile["isDefault"] = false;
        profiles[0]["isDefault"] = true;
        profiles[0]["updatedAt"] = Now();
        return true;
    }

    private static JsonObject CreateProfile(
        JsonObject root,
        long corporationId,
        string name,
        string description,
        JsonObject? settings,
        bool isDefault)
    {
        EnsureRoot(root);
        var profiles = (JsonObject)root["profilesByID"]!;
        var profileId = Math.Max(1, Integer(root["nextProfileID"], 1));
        while (profiles.ContainsKey(profileId.ToString(CultureInfo.InvariantCulture)))
            profileId++;
        root["nextProfileID"] = profileId + 1;
        if (isDefault)
            foreach (var existing in ListProfiles(root, corporationId))
                existing["isDefault"] = false;
        var now = Now();
        var profile = new JsonObject
        {
            ["profileID"] = profileId,
            ["corporationID"] = corporationId,
            ["name"] = UniqueName(root, corporationId, NormalizeName(name), 0),
            ["description"] = NormalizeDescription(description),
            ["isDefault"] = isDefault,
            ["settingsBySettingID"] = settings?.DeepClone() ?? new JsonObject(),
            ["createdAt"] = now,
            ["updatedAt"] = now,
        };
        profiles[profileId.ToString(CultureInfo.InvariantCulture)] = profile;
        return profile;
    }

    private static IReadOnlyList<(string Key, JsonObject Profile)> ListProfileEntries(JsonObject root, long corporationId) =>
        (root["profilesByID"] as JsonObject ?? new JsonObject())
        .Where(entry => entry.Value is JsonObject profile && Integer(profile["corporationID"]) == corporationId)
        .Select(entry => (Key: entry.Key, Profile: (JsonObject)entry.Value!))
        .OrderByDescending(entry => Truthy(entry.Item2["isDefault"]))
        .ThenBy(entry => Text(entry.Item2["name"]), StringComparer.OrdinalIgnoreCase)
        .ThenBy(entry => ProfileId(entry.Item2, entry.Item1))
        .ToArray();

    private static IReadOnlyList<JsonObject> ListProfiles(JsonObject root, long corporationId) =>
        ListProfileEntries(root, corporationId).Select(entry => entry.Item2).ToArray();

    private static IReadOnlyList<long> ProfileIds(JsonObject root, long corporationId) =>
        ListProfileEntries(root, corporationId)
            .Select(entry => ProfileId(entry.Item2, entry.Item1))
            .Where(id => id > 0)
            .ToArray();

    private static long DefaultProfileId(JsonObject root, long corporationId) =>
        ListProfileEntries(root, corporationId)
            .Where(entry => Truthy(entry.Item2["isDefault"]))
            .Select(entry => ProfileId(entry.Item2, entry.Item1))
            .FirstOrDefault(id => id > 0) is var id && id > 0 ? id : 1;

    private static JsonObject? FindProfile(JsonObject root, long corporationId, long profileId) =>
        profileId > 0 && root["profilesByID"] is JsonObject profiles &&
        profiles[profileId.ToString(CultureInfo.InvariantCulture)] is JsonObject profile &&
        Integer(profile["corporationID"]) == corporationId ? profile : null;

    private static long ProfileId(JsonObject profile, string key)
    {
        var id = Integer(profile["profileID"]);
        return id > 0 || !long.TryParse(key, NumberStyles.Integer, CultureInfo.InvariantCulture, out var keyId)
            ? id
            : keyId;
    }

    private static string UniqueName(JsonObject root, long corporationId, string name, long ignoredProfileId)
    {
        var profiles = ListProfiles(root, corporationId)
            .Where(profile => Integer(profile["profileID"]) != ignoredProfileId)
            .Select(profile => Text(profile["name"]).Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!profiles.Contains(name))
            return name;
        for (var suffix = 2; suffix < 1000; suffix++)
        {
            var candidate = NormalizeName($"{name} {suffix}");
            if (!profiles.Contains(candidate))
                return candidate;
        }
        return name;
    }

    private static string NormalizeName(string? value) =>
        string.IsNullOrWhiteSpace(value) ? DefaultName : value.Trim()[..Math.Min(30, value.Trim().Length)];

    private static string NormalizeDescription(string? value) =>
        string.IsNullOrEmpty(value) ? "" : value.Trim()[..Math.Min(200, value.Trim().Length)];

    private static string Now() => DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);

    private static JsonObject NormalizeSettings(PyValue raw)
    {
        var groupsBySetting = new Dictionary<long, SortedDictionary<long, JsonNode?>>();
        foreach (var entry in Items(raw))
        {
            var values = Items(entry);
            var settingId = Positive(values.Count > 0 ? IntegerValue(values[0]) : IntegerValue(Property(entry, "settingID")));
            if (settingId <= 0)
                continue;
            var settingValue = values.Count > 1 ? values[1] : Property(entry, "value");
            var groupId = values.Count > 2 ? IntegerValue(values[2]) : IntegerValue(Property(entry, "groupID"));
            if (!groupsBySetting.TryGetValue(settingId, out var groups))
                groupsBySetting[settingId] = groups = new SortedDictionary<long, JsonNode?>();
            groups[groupId] = NormalizeSettingValue(settingValue);
        }

        var settings = new JsonObject();
        foreach (var (settingId, groups) in groupsBySetting.OrderBy(pair => pair.Key))
        {
            var rows = new JsonArray();
            foreach (var (groupId, value) in groups)
                rows.Add(new JsonObject { ["groupID"] = groupId, ["value"] = value?.DeepClone() });
            settings[settingId.ToString(CultureInfo.InvariantCulture)] = rows;
        }
        return settings;
    }

    private static PyValue ToPyValue(JsonNode? value) => value switch
    {
        null => PyNone.Instance,
        JsonObject obj => new PyDict([.. obj.Select(pair => new KeyValuePair<PyValue, PyValue>(
            new PyStr(pair.Key), ToPyValue(pair.Value)))]),
        JsonArray array => new PyList([.. array.Select(ToPyValue)]),
        JsonValue scalar when scalar.TryGetValue<bool>(out var boolean) => boolean ? PyBool.True : PyBool.False,
        JsonValue scalar when scalar.TryGetValue<long>(out var integer) => new PyInt(integer),
        JsonValue scalar when scalar.TryGetValue<double>(out var number) && double.IsFinite(number) => new PyReal(number),
        JsonValue scalar when scalar.TryGetValue<string>(out var text) => new PyStr(text),
        _ => PyNone.Instance,
    };

    private static KeyValuePair<PyValue, PyValue> Pair(string key, PyValue value) =>
        new(new PyStr(key), value);

    private static long NormalizeOperatingState(long value) => value is 0 or 1 or 2 or 100 or 101 or 102 or
        110 or 111 or 112 or 113 or 114 or 115 or 116 or 118 ? value : 0;

    private static long NormalizeUpkeepState(long value) => value is 1 or 2 or 3 ? value : 1;

    private static double[] DamageState(JsonObject structure, long operatingState)
    {
        var defaults = operatingState switch
        {
            2 or 101 or 102 or 113 or 114 or 115 or 116 => new[] { 0d, 0d, 1d },
            111 or 112 => new[] { 0d, 1d, 1d },
            _ => new[] { 1d, 1d, 1d },
        };
        var condition = structure["conditionState"] as JsonObject;
        var shield = condition is not null && condition.ContainsKey("shieldCharge")
            ? Clamp01(JsNumber(condition["shieldCharge"]), defaults[0])
            : defaults[0];
        var armor = condition is not null && condition.ContainsKey("armorDamage")
            ? Clamp01(1 - JsNumber(condition["armorDamage"]), defaults[1])
            : defaults[1];
        var hull = condition is not null && condition.ContainsKey("damage")
            ? Clamp01(1 - JsNumber(condition["damage"]), defaults[2])
            : defaults[2];
        return [shield, armor, hull];
    }

    private static double Clamp01(double value, double fallback) =>
        double.IsFinite(value) ? Math.Clamp(value, 0, 1) : Math.Clamp(fallback, 0, 1);

    private static double Number(JsonNode? value, double fallback) =>
        double.TryParse(value?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
            ? number
            : fallback;

    private static double JsNumber(JsonNode? value)
    {
        if (value is null)
            return 0;
        if (bool.TryParse(value.ToString(), out var boolean))
            return boolean ? 1 : 0;
        return double.TryParse(value.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
            ? number
            : double.NaN;
    }

    private static long? FileTime(JsonNode? milliseconds)
    {
        var unixMilliseconds = Number(milliseconds, double.NaN);
        if (!double.IsFinite(unixMilliseconds) || unixMilliseconds <= 0 || unixMilliseconds > long.MaxValue / 10_000d)
            return null;
        var rounded = (long)Math.Truncate(unixMilliseconds);
        try
        {
            return checked(116_444_736_000_000_000L + rounded * 10_000L);
        }
        catch (OverflowException)
        {
            return null;
        }
    }

    private static JsonNode NormalizeSettingValue(PyValue? value) => value switch
    {
        PyBool boolean => JsonValue.Create(boolean.Value)!,
        PyInt integer => JsonValue.Create(integer.Value)!,
        PyLong integer => JsonValue.Create(integer.Value)!,
        PyReal number when double.IsFinite(number.Value) => JsonValue.Create(number.Value)!,
        var text when Eve.Macho.JsSemantics.StringValue(text) is not null => NormalizeTextSetting(Eve.Macho.JsSemantics.StringValue(text)!),
        _ => JsonValue.Create(0)!,
    };

    private static JsonNode NormalizeTextSetting(string value)
    {
        var trimmed = value.Trim();
        return double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var numeric) &&
            double.IsFinite(numeric)
            ? JsonValue.Create(numeric)!
            : JsonValue.Create(trimmed)!;
    }

    private static IReadOnlyList<PyValue> Items(PyValue? value) => value switch
    {
        PyList list => list.Items,
        PyTuple tuple => tuple.Items,
        _ => Array.Empty<PyValue>(),
    };

    private static PyValue? Property(PyValue? value, string name)
    {
        var entries = value switch
        {
            PyDict dict => dict.Entries,
            PyObjectEx obj => obj.Dict,
            _ => Array.Empty<KeyValuePair<PyValue, PyValue>>(),
        };
        return entries.FirstOrDefault(pair => Text(pair.Key) == name).Value;
    }

    private static long IntegerValue(PyValue? value) => value switch
    {
        PyInt integer => integer.Value,
        PyLong integer => integer.Value,
        PyReal number when double.IsFinite(number.Value) && number.Value >= long.MinValue && number.Value <= long.MaxValue =>
            (long)Math.Truncate(number.Value),
        _ => 0,
    };

    private static long Positive(long value) => value > 0 ? value : 0;

    private static long Integer(JsonNode? value, long fallback = 0) =>
        long.TryParse(value?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) ? number : fallback;

    private static string Text(JsonNode? value) => value?.ToString() ?? "";

    private static string Text(PyValue value) => value switch
    {
        var text when Eve.Macho.JsSemantics.StringValue(text) is not null => Eve.Macho.JsSemantics.StringValue(text)!,
        PyInt integer => integer.Value.ToString(CultureInfo.InvariantCulture),
        PyLong integer => integer.Value.ToString(CultureInfo.InvariantCulture),
        PyReal number => number.Value.ToString("G", CultureInfo.InvariantCulture),
        _ => "",
    };

    private static bool Truthy(JsonNode? value) =>
        bool.TryParse(value?.ToString(), out var boolean) ? boolean : Integer(value) != 0;

    private void NotifyProfileSettingsChanged(long corporationId, IEnumerable<long> profileIds)
    {
        var ids = profileIds.Where(id => id > 0).Distinct().Order().Select(id => (PyValue)new PyInt(id)).ToArray();
        if (corporationId <= 0 || ids.Length == 0)
            return;
        var payload = new PyList(ids);
        foreach (var session in characterSessions.OfCorporation(corporationId))
            session.SendNotification("OnProfileSettingsChange", "clientID", payload);
    }

    private void NotifyCorporationStructuresUpdated(long corporationId)
    {
        if (corporationId <= 0)
            return;
        foreach (var session in characterSessions.OfCorporation(corporationId))
            session.SendNotification("OnCorporationStructuresUpdated", "clientID");
    }

    private static IReadOnlyDictionary<long, IReadOnlyList<StructureProfileSetting>> EmptySettings() =>
        new Dictionary<long, IReadOnlyList<StructureProfileSetting>>();
}
