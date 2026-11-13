using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Eve.Dogma;
using Eve.Inventory;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Ships;
using Npgsql;

namespace Eve.Services.Space;

/// <summary>The legacy POS management calls whose durable state belongs to the control tower deployment.</summary>
public sealed partial class SpaceContainers
{
    /// <summary>The connected clients that may have the control tower management window open.</summary>
    public CharacterSessions? CharacterSessions { get; set; }

    private const int SiloGroupId = 404;
    private const int ReactorGroupId = 438;
    private const int MoonHarvesterGroupId = 416;
    private const int StarbaseCategoryId = 23;
    private static readonly string[] DeployFlagNames = ["anchor", "unanchor", "online", "offline"];
    private static readonly string[] UsageFlagNames = ["viewput", "viewputtake", "use"];
    private static readonly JsonSerializerOptions PosMgrJson = new(JsonSerializerDefaults.Web);

    private sealed record SentrySettings(double? Standing = null, double? SecurityStatus = null, bool StatusDrop = false,
        bool War = false, bool UseAllianceStandings = false);
    private sealed record TowerNotifications(bool ShowInCalendar = false, bool SendFuelNotifications = false);
    private sealed record TowerUsage(long StructureId, int ViewPut = 3, int ViewPutTake = 0, int Use = 0);
    private sealed record TowerLink(long FromItemId, long ToItemId, int TypeId);
    private sealed record PosMgrTowerConfiguration
    {
        public int Version { get; init; } = 1;
        public bool AllowCorp { get; init; } = true;
        public bool AllowAlliance { get; init; } = true;
        public string? PasswordSalt { get; init; }
        public string? PasswordHash { get; init; }
        public SentrySettings Sentry { get; init; } = new();
        public TowerNotifications Notifications { get; init; } = new();
        public Dictionary<string, int> DeployFlags { get; init; } = new(StringComparer.Ordinal)
        {
            ["anchor"] = 0, ["unanchor"] = 0, ["online"] = 0, ["offline"] = 0,
        };
        public List<TowerUsage> UsageFlags { get; init; } = [];
        public Dictionary<long, int?> ProvisionTypes { get; init; } = [];
        public List<TowerLink> Links { get; init; } = [];
    }

    /// <summary>Routes the POS-facing methods registered by posMgr to this module's durable owner.</summary>
    public async ValueTask<PyValue> PosMgrControlTowerOperationAsync(ServiceCall call)
    {
        call.Cancellation.ThrowIfCancellationRequested();
        return call.Method switch
        {
            "GetConsumptionFactor" => new PyReal(1.0),
            "GetTowerSentrySettings" => await GetSentrySettingsAsync(call).ConfigureAwait(false),
            "SetTowerSentrySettings" => await SetSentrySettingsAsync(call).ConfigureAwait(false),
            "GetTowerNotificationSettings" => await GetNotificationSettingsAsync(call).ConfigureAwait(false),
            "SetTowerNotifications" => await SetNotificationSettingsAsync(call).ConfigureAwait(false),
            "SetTowerPassword" => await SetTowerPasswordAsync(call).ConfigureAwait(false),
            "GetStarbasePermissions" => await GetStarbasePermissionsAsync(call).ConfigureAwait(false),
            "SetStarbasePermissions" => await SetStarbasePermissionsAsync(call).ConfigureAwait(false),
            "GetMoonForTower" => await GetMoonForTowerAsync(call).ConfigureAwait(false),
            "GetSiloCapacityByItemID" => await GetSiloCapacityAsync(call).ConfigureAwait(false),
            "ChangeStructureProvisionType" => await ChangeProvisionTypeAsync(call).ConfigureAwait(false),
            "LinkResourceForTower" => await LinkResourcesAsync(call).ConfigureAwait(false),
            "RunMoonProcessCycleforTower" => throw await RunMoonCycleAsync(call).ConfigureAwait(false),
            "GetMoonProcessInfoForTower" => throw await ProcessInfoAsync(call).ConfigureAwait(false),
            _ => throw MachoErrors.ThrowUserError("CustomNotify", ("notify", new PyStr("That POS operation is not available on this server."))),
        };
    }

    private async Task<PyValue> GetSentrySettingsAsync(ServiceCall call)
    {
        var (tower, configuration) = await ReadTowerConfigurationAsync(call, ReadPositiveId(call.Arg(0)), false).ConfigureAwait(false);
        var sentry = configuration.Sentry;
        var standingOwner = sentry.UseAllianceStandings && tower.AllianceId > 0 ? tower.AllianceId : tower.OwnerId;
        var header = Py.List(new PyStr("standing"), new PyStr("status"), new PyStr("statusDrop"),
            new PyStr("war"), new PyStr("standingOwnerID"));
        var line = Py.List(NumberOrNone(sentry.Standing), NumberOrNone(sentry.SecurityStatus), PyValue.Of(sentry.StatusDrop),
            PyValue.Of(sentry.War), WireId(standingOwner));
        return new PyObject(new PyStr("util.Row"), Py.Dict(("header", header), ("line", line)));
    }

    private async Task<PyValue> SetSentrySettingsAsync(ServiceCall call)
    {
        var id = ReadPositiveId(call.Arg(0));
        var standing = OptionalFiniteNumber(call.Arg(1));
        var security = OptionalFiniteNumber(call.Arg(2));
        if (standing is < -10 or > 10 || security is < -10 or > 10 ||
            !TryBoolean(call.Arg(3), out var statusDrop) || !TryBoolean(call.Arg(4), out var war) ||
            !TryBoolean(call.Arg(5), out var useAlliance))
            throw SettingsInvalid();
        await UpdateTowerConfigurationAsync(call, id, (record, config) =>
        {
            if (useAlliance && record.AllianceId <= 0) throw MachoErrors.ThrowUserError("STARBASE_ALLIANCE_REQUIRED");
            return config with { Sentry = new SentrySettings(standing, security, statusDrop, war, useAlliance) };
        }).ConfigureAwait(false);
        return PyNone.Instance;
    }

    private async Task<PyValue> GetNotificationSettingsAsync(ServiceCall call)
    {
        var (_, configuration) = await ReadTowerConfigurationAsync(call, ReadPositiveId(call.Arg(0)), false).ConfigureAwait(false);
        return Py.KeyVal(("showInCalendar", PyValue.Of(configuration.Notifications.ShowInCalendar)),
            ("sendFuelNotifications", PyValue.Of(configuration.Notifications.SendFuelNotifications)));
    }

    private async Task<PyValue> SetNotificationSettingsAsync(ServiceCall call)
    {
        if (!TryBoolean(call.Arg(1), out var showCalendar) || !TryBoolean(call.Arg(2), out var sendFuel))
            throw SettingsInvalid();
        await UpdateTowerConfigurationAsync(call, ReadPositiveId(call.Arg(0)), (_, configuration) =>
            configuration with { Notifications = new TowerNotifications(showCalendar, sendFuel) }).ConfigureAwait(false);
        return PyNone.Instance;
    }

    private async Task<PyValue> SetTowerPasswordAsync(ServiceCall call)
    {
        var password = call.Arg(1);
        string? passwordSalt = null;
        string? passwordHash = null;
        if (password is not PyBool { Value: false })
        {
            var plain = JsSemantics.StringValue(password);
            if (plain is null || plain.Length is < 1 or > 50) throw SettingsInvalid();
            var saltBytes = RandomNumberGenerator.GetBytes(16);
            var hash = Rfc2898DeriveBytes.Pbkdf2(plain, saltBytes, 100_000, HashAlgorithmName.SHA256, 32);
            passwordSalt = Convert.ToHexString(saltBytes).ToLowerInvariant();
            passwordHash = Convert.ToHexString(hash).ToLowerInvariant();
        }

        var allowCorp = OptionalBoolean(call.Arg(2));
        var allowAlliance = OptionalBoolean(call.Arg(3));
        if (call.Arg(2) is not PyNone && allowCorp is null ||
            call.Arg(3) is not PyNone && allowAlliance is null)
            throw SettingsInvalid();

        await UpdateTowerConfigurationAsync(call, ReadPositiveId(call.Arg(0)), (record, configuration) =>
        {
            var nextAllowAlliance = allowAlliance ?? configuration.AllowAlliance;
            if (allowAlliance is true && record.AllianceId <= 0)
                throw MachoErrors.ThrowUserError("STARBASE_ALLIANCE_REQUIRED");
            return configuration with
            {
                AllowCorp = allowCorp ?? configuration.AllowCorp,
                AllowAlliance = nextAllowAlliance,
                PasswordSalt = passwordSalt ?? configuration.PasswordSalt,
                PasswordHash = passwordHash ?? configuration.PasswordHash,
            };
        }).ConfigureAwait(false);
        return PyNone.Instance;
    }

    private async Task<PyValue> GetStarbasePermissionsAsync(ServiceCall call)
    {
        var (tower, configuration) = await ReadTowerConfigurationAsync(call, ReadPositiveId(call.Arg(0)), false).ConfigureAwait(false);
        var deployFlags = configuration.DeployFlags;
        var deploy = new PyDict([.. DeployFlagNames.Select(key => new KeyValuePair<PyValue, PyValue>(new PyStr(key),
            new PyInt(deployFlags.GetValueOrDefault(key))))]);
        var usage = configuration.UsageFlags.Count == 0
            ? [new TowerUsage(tower.DeployableId)]
            : configuration.UsageFlags;
        var rows = usage.OrderBy(row => row.StructureId).Select(row => (PyValue)Py.List(WireId(row.StructureId),
            new PyInt(row.ViewPut), new PyInt(row.ViewPutTake), new PyInt(row.Use)));
        return Py.Tuple(deploy, Py.Rowset(["structureID", "viewput", "viewputtake", "use"], rows));
    }

    private async Task<PyValue> SetStarbasePermissionsAsync(ServiceCall call)
    {
        if (call.Arg(1) is not PyDict deployInput) throw SettingsInvalid();
        var deploy = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var key in DeployFlagNames)
        {
            var raw = DictValue(deployInput, key);
            if (!TryInteger(raw, out var value) || value is < 0 or > 2) throw SettingsInvalid();
            deploy[key] = (int)value;
        }
        var usageInput = ReadUsageRows(call.Arg(2));
        await UpdateTowerConfigurationAsync(call, ReadPositiveId(call.Arg(0)), (record, configuration) =>
        {
            foreach (var row in usageInput)
            {
                if (row.StructureId != record.DeployableId || row.ViewPut is < 0 or > 3 || row.ViewPutTake is < 0 or > 3 || row.Use is < 0 or > 3)
                    throw SettingsInvalid();
            }
            return configuration with { DeployFlags = deploy,
                UsageFlags = usageInput.Count == 0 ? [new TowerUsage(record.DeployableId)] : usageInput };
        }).ConfigureAwait(false);
        return PyNone.Instance;
    }

    private async Task<PyValue> GetMoonForTowerAsync(ServiceCall call)
    {
        if (_deployed is null || _source is null) throw NotAvailable();
        var id = ReadPositiveId(call.Arg(0));
        await using var connection = await _source.OpenConnectionAsync(call.Cancellation).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(call.Cancellation).ConfigureAwait(false);
        var record = await _deployed.LockAndGetAsync(connection, transaction, id, call.Cancellation).ConfigureAwait(false);
        if (record is not { Kind: ControlTowerKind }) throw TowerNotFound();
        await RequireControlTowerAccessAsync(call.Session, record, false, call.Cancellation, requireConfigurationRole: false).ConfigureAwait(false);
        var moons = await ReadMoonAnchorPointsAsync(connection, transaction, record.SolarSystemId, call.Cancellation).ConfigureAwait(false);
        var nearest = NearestMoon(moons, record.Position);
        await transaction.CommitAsync(call.Cancellation).ConfigureAwait(false);
        if (nearest is null) throw MachoErrors.ThrowUserError("CustomNotify", ("notify", new PyStr("No moon is available for this control tower.")));
        // The pinned SDE has moon positions, not the removed legacy abundance profiles. EveJS returns null when no verified profile is loaded.
        return Py.Tuple(WireId(nearest.Moon.MoonId), PyNone.Instance);
    }

    private async Task<PyValue> GetSiloCapacityAsync(ServiceCall call)
    {
        if (_statics is null) throw NotAvailable();
        var id = ReadPositiveId(call.Arg(0));
        var item = await _items.GetAsync(id, call.Cancellation).ConfigureAwait(false);
        if (item is null) throw MachoErrors.ThrowUserError("FakeItemNotFound");
        await _statics.EnsureTypesAsync([item.TypeId], call.Cancellation).ConfigureAwait(false);
        var type = _statics.Type(item.TypeId);
        if (type is not { CategoryId: StarbaseCategoryId, GroupId: SiloGroupId }) throw MachoErrors.ThrowUserError("FakeItemNotFound");
        if (_statics.AttributeId("capacity") is not { } capacityAttribute ||
            !type.Attributes.TryGetValue(capacityAttribute, out var capacity))
            throw MachoErrors.ThrowUserError("FakeItemNotFound");
        if (!double.IsFinite(capacity) || capacity < 0) throw MachoErrors.ThrowUserError("FakeItemNotFound");
        return new PyReal(capacity);
    }

    private async Task<PyValue> ChangeProvisionTypeAsync(ServiceCall call)
    {
        _ = await ReadTowerConfigurationAsync(call, ReadPositiveId(call.Arg(0)), true).ConfigureAwait(false);
        // The current Host has no authoritative POS module-to-tower attachment model. Refuse rather than bind a silo from the same solar system by guess.
        throw MachoErrors.ThrowUserError("CustomNotify", ("notify", new PyStr("This control tower has no verified starbase structure association.")));
    }

    private async Task<PyValue> LinkResourcesAsync(ServiceCall call)
    {
        var (tower, _) = await ReadTowerConfigurationAsync(call, ReadPositiveId(call.Arg(0)), true).ConfigureAwait(false);
        if (call.Arg(1) is not (PyList or PyTuple)) throw SettingsInvalid();
        var requested = Sequence(call.Arg(1)).Select(row => Sequence(row)).ToArray();
        if (requested.Length > 1_024) throw SettingsInvalid();
        if (requested.Any(row => row.Count != 3 || row.Any(value => !TryInteger(value, out var parsed) || parsed <= 0)))
            throw SettingsInvalid();
        if (requested.Length != 0)
        {
            // No POS module association or historical reaction authority is present in the current state owner.
            throw MachoErrors.ThrowUserError("CustomNotify", ("notify", new PyStr("This control tower has no verified starbase production graph.")));
        }
        await UpdateTowerConfigurationAsync(call, tower.DeployableId, (_, configuration) => configuration with { Links = [] }).ConfigureAwait(false);
        NotifyMoonProcessChange(call.Session, tower, new PyList([]));
        return PyBool.True;
    }

    private void NotifyMoonProcessChange(ClientSession caller, DeployedRecord tower, PyValue structures)
    {
        var recipients = new HashSet<ClientSession> { caller };
        if (CharacterSessions is { } sessions)
        {
            foreach (var session in sessions.InSolarSystem(tower.SolarSystemId))
            {
                if (session.CharacterId is null || session.DockedLocationId is not null ||
                    session.GetInt("corpid") != tower.CorporationId)
                    continue;
                recipients.Add(session);
            }
        }

        foreach (var recipient in recipients)
            recipient.SendNotification("OnMoonProcessChange", "clientID", WireId(tower.DeployableId), structures);
    }

    private async Task<Exception> RunMoonCycleAsync(ServiceCall call)
    {
        _ = await ReadTowerConfigurationAsync(call, ReadPositiveId(call.Arg(0)), true).ConfigureAwait(false);
        if (((call.Session.GetInt("role") ?? 0) & (1L << 54)) == 0)
            return MachoErrors.ThrowUserError("CustomNotify", ("notify", new PyStr("Only a Game Master may force a starbase production cycle.")));
        return LegacyProductionUnavailable();
    }

    private async Task<Exception> ProcessInfoAsync(ServiceCall call)
    {
        _ = await ReadTowerConfigurationAsync(call, ReadPositiveId(call.Arg(0)), true).ConfigureAwait(false);
        return ProcessInfoUnavailable();
    }

    private async Task<(DeployedRecord Record, PosMgrTowerConfiguration Configuration)> ReadTowerConfigurationAsync(
        ServiceCall call, long towerId, bool write)
    {
        if (_deployed is null || _source is null || towerId <= 0) throw TowerNotFound();
        await using var connection = await _source.OpenConnectionAsync(call.Cancellation).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(call.Cancellation).ConfigureAwait(false);
        var record = await _deployed.LockAndGetAsync(connection, transaction, towerId, call.Cancellation).ConfigureAwait(false);
        if (record is not { Kind: ControlTowerKind }) throw TowerNotFound();
        await RequireControlTowerAccessAsync(call.Session, record, false, call.Cancellation,
            requireConfigurationRole: write).ConfigureAwait(false);
        var raw = await _deployed.TowerConfigurationAsync(connection, transaction, towerId, call.Cancellation).ConfigureAwait(false);
        var configuration = raw is null ? new PosMgrTowerConfiguration() :
            JsonSerializer.Deserialize<PosMgrTowerConfiguration>(raw, PosMgrJson) ?? new PosMgrTowerConfiguration();
        await transaction.CommitAsync(call.Cancellation).ConfigureAwait(false);
        return (record, Normalize(configuration));
    }

    private async Task UpdateTowerConfigurationAsync(ServiceCall call, long towerId,
        Func<DeployedRecord, PosMgrTowerConfiguration, PosMgrTowerConfiguration> update)
    {
        if (_deployed is null || _source is null || towerId <= 0) throw TowerNotFound();
        await using var connection = await _source.OpenConnectionAsync(call.Cancellation).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(call.Cancellation).ConfigureAwait(false);
        var record = await _deployed.LockAndGetAsync(connection, transaction, towerId, call.Cancellation).ConfigureAwait(false);
        if (record is not { Kind: ControlTowerKind }) throw TowerNotFound();
        await RequireControlTowerAccessAsync(call.Session, record, false, call.Cancellation).ConfigureAwait(false);
        var raw = await _deployed.TowerConfigurationAsync(connection, transaction, towerId, call.Cancellation).ConfigureAwait(false);
        var current = Normalize(raw is null ? new PosMgrTowerConfiguration() :
            JsonSerializer.Deserialize<PosMgrTowerConfiguration>(raw, PosMgrJson) ?? new PosMgrTowerConfiguration());
        var next = Normalize(update(record, current));
        var json = JsonSerializer.Serialize(next, PosMgrJson);
        if (!await _deployed.SetTowerConfigurationAsync(connection, transaction, towerId, json, call.Cancellation).ConfigureAwait(false))
            throw TowerNotFound();
        await transaction.CommitAsync(call.Cancellation).ConfigureAwait(false);
    }

    private static PosMgrTowerConfiguration Normalize(PosMgrTowerConfiguration source) => source with
    {
        Version = 1,
        DeployFlags = DeployFlagNames.ToDictionary(key => key,
            key => ValidDeployPermission(source.DeployFlags.GetValueOrDefault(key)),
            StringComparer.Ordinal),
        UsageFlags = [.. source.UsageFlags.Where(row => row.StructureId > 0)
            .Select(row => row with { ViewPut = ValidPermission(row.ViewPut), ViewPutTake = ValidPermission(row.ViewPutTake), Use = ValidPermission(row.Use) })
            .DistinctBy(row => row.StructureId)],
        ProvisionTypes = source.ProvisionTypes.Where(pair => pair.Key > 0 && pair.Value is > 0).ToDictionary(pair => pair.Key, pair => pair.Value),
        Links = [.. source.Links.Where(link => link.FromItemId > 0 && link.ToItemId > 0 && link.TypeId > 0)
            .DistinctBy(link => (link.FromItemId, link.ToItemId, link.TypeId))],
        Sentry = source.Sentry ?? new SentrySettings(),
        Notifications = source.Notifications ?? new TowerNotifications(),
    };

    private static List<TowerUsage> ReadUsageRows(PyValue? value)
    {
        var result = new List<TowerUsage>();
        var rowset = ObjectFields(value);
        var header = Sequence(Find(rowset, "header")).Select(JsSemantics.StringValue).ToArray();
        foreach (var rawRow in Sequence(Find(rowset, "lines")))
        {
            var rowFields = ObjectFields(rawRow);
            var values = Sequence(Find(rowFields, "line"));
            if (values.Count == 0) values = Sequence(rawRow);
            PyValue? Column(string name, int fallback) =>
                header.Select((column, index) => (column, index)).FirstOrDefault(pair => pair.column == name) is var found && found.index < values.Count
                    ? values[found.index] : values.Count > fallback ? values[fallback] : null;
            if (!TryInteger(Column("structureID", 0), out var id) || id <= 0 ||
                !TryInteger(Column("viewput", 1), out var viewPut) ||
                !TryInteger(Column("viewputtake", 2), out var viewPutTake) || !TryInteger(Column("use", 3), out var use))
                throw SettingsInvalid();
            result.Add(new TowerUsage(id, checked((int)viewPut), checked((int)viewPutTake), checked((int)use)));
        }
        // Some client serializers send just the row list rather than a Rowset object.
        if (result.Count == 0 && value is PyList or PyTuple)
        {
            foreach (var rawRow in Sequence(value))
            {
                var values = Sequence(Find(ObjectFields(rawRow), "line"));
                if (values.Count == 0) values = Sequence(rawRow);
                if (values.Count != 4 || !TryInteger(values[0], out var id) || id <= 0 ||
                    !TryInteger(values[1], out var a) || !TryInteger(values[2], out var b) || !TryInteger(values[3], out var c))
                    throw SettingsInvalid();
                result.Add(new TowerUsage(id, checked((int)a), checked((int)b), checked((int)c)));
            }
        }
        return result;
    }

    private static PyDict? ObjectFields(PyValue? value) => value switch
    {
        PyObject obj => obj.Args as PyDict,
        PyObjectEx obj => new PyDict([.. obj.Dict]),
        _ => null,
    };

    private static PyValue? Find(PyDict? fields, string name) => fields?.Entries
        .FirstOrDefault(pair => JsSemantics.StringValue(pair.Key) == name).Value;

    private static PyValue? DictValue(PyDict fields, string name) => Find(fields, name);

    private static IReadOnlyList<PyValue> Sequence(PyValue? value) => value switch
    {
        PyList list => list.Items,
        PyTuple tuple => tuple.Items,
        _ => [],
    };

    private static bool TryInteger(PyValue? value, out long result)
    {
        switch (value)
        {
            case PyInt integer: result = integer.Value; return true;
            case PyLong integer: result = integer.Value; return true;
            case PyReal real when double.IsFinite(real.Value) && real.Value == Math.Truncate(real.Value) && real.Value >= long.MinValue && real.Value <= long.MaxValue:
                result = (long)real.Value; return true;
            case PyBool boolean: result = boolean.Value ? 1 : 0; return true;
            default: result = 0; return false;
        }
    }

    private static bool TryBoolean(PyValue? value, out bool result)
    {
        switch (value)
        {
            case PyBool boolean: result = boolean.Value; return true;
            case PyInt integer when integer.Value is 0 or 1: result = integer.Value == 1; return true;
            case PyLong integer when integer.Value is 0 or 1: result = integer.Value == 1; return true;
            default: result = false; return false;
        }
    }

    private static bool? OptionalBoolean(PyValue? value) => value switch
    {
        PyNone => null,
        _ when TryBoolean(value, out var parsed) => parsed,
        _ => null,
    };

    private static double? OptionalFiniteNumber(PyValue? value) => value switch
    {
        PyNone => null,
        PyReal real when double.IsFinite(real.Value) => real.Value,
        PyInt integer => integer.Value,
        PyLong integer => integer.Value,
        _ => double.NaN,
    };

    private static long ReadPositiveId(PyValue? value) => TryInteger(value, out var id) && id > 0 ? id : 0;
    private static PyValue NumberOrNone(double? value) => value is { } number ? new PyReal(number) : PyNone.Instance;
    private static PyValue WireId(long value) => value <= int.MaxValue ? new PyInt((int)value) : new PyLong(value);
    private static int ValidPermission(int value) => value is >= 0 and <= 3 ? value : 0;
    private static int ValidDeployPermission(int value) => value is >= 0 and <= 2 ? value : 0;

    private static MachoWrappedException SettingsInvalid() => MachoErrors.ThrowUserError("CustomNotify",
        ("notify", new PyStr("The starbase settings contain an invalid value.")));
    private static MachoWrappedException TowerNotFound() => MachoErrors.ThrowUserError("CustomNotify",
        ("notify", new PyStr("The control tower could not be found or is not available in this system.")));
    private static MachoWrappedException LegacyProductionUnavailable() => MachoErrors.ThrowUserError("CustomNotify",
        ("notify", new PyStr("Legacy POS production is not available: historical recipes, moon abundance and the removed production interface have not been restored. No cycle was run.")));
    private static MachoWrappedException ProcessInfoUnavailable() => MachoErrors.ThrowUserError("CustomNotify",
        ("notify", new PyStr("The historical production-info RPC payload is unavailable in this client build.")));
    private static MachoWrappedException NotAvailable() => MachoErrors.ThrowUserError("CustomNotify",
        ("notify", new PyStr("POS management is not available on this server.")));
}
