using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Eve.Dogma;
using Eve.Inventory;
using Eve.Macho;
using Eve.Marshal;
using Eve.Scheduler;
using Eve.Services;
using Eve.Ships;
using Eve.Space;
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
    /// <summary>The durable one-shot runner for configured POS production graphs.</summary>
    public const string ControlTowerProductionCycleJob = "space.control_tower.production_cycle";
    private static readonly string[] DeployFlagNames = ["anchor", "unanchor", "online", "offline"];
    private static readonly string[] UsageFlagNames = ["viewput", "viewputtake", "use"];
    private static readonly JsonSerializerOptions PosMgrJson = new(JsonSerializerDefaults.Web);

    private sealed record SentrySettings(double? Standing = null, double? SecurityStatus = null, bool StatusDrop = false,
        bool War = false, bool UseAllianceStandings = false);
    private sealed record TowerNotifications(bool ShowInCalendar = false, bool SendFuelNotifications = false);
    private sealed record ProductionCycleState(long? LastCycleAtMs = null, long? NextCycleAtMs = null, long CyclesCompleted = 0,
        string Status = "unconfigured", string? LastError = null);
    private sealed record TowerUsage(long StructureId, int ViewPut = 3, int ViewPutTake = 0, int Use = 0);
    private sealed record TowerLink(long FromItemId, long ToItemId, int TypeId);
    /// <summary>The tower whose next persisted POS production interval is due.</summary>
    public sealed record ControlTowerProductionCycleDue(long TowerId);
    private sealed class ProductionCycleNode(DeployedRecord record, bool online, double capacity,
        IReadOnlyList<Item> contents, double foreignVolume)
    {
        public DeployedRecord Record { get; } = record;
        public bool Online { get; } = online;
        public double Capacity { get; } = capacity;
        public IReadOnlyList<Item> Contents { get; } = contents;
        public double ForeignVolume { get; } = foreignVolume;
        public Dictionary<int, long> Initial { get; } = [];
        public Dictionary<int, long> Pool { get; } = [];
    }
    private sealed record PosMgrTowerConfiguration
    {
        public int Version { get; init; } = 1;
        public bool AllowCorp { get; init; } = true;
        public bool AllowAlliance { get; init; } = true;
        public string? PasswordSalt { get; init; }
        public string? PasswordHash { get; init; }
        public SentrySettings Sentry { get; init; } = new();
        public TowerNotifications Notifications { get; init; } = new();
        public ProductionCycleState Production { get; init; } = new();
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
            "RunMoonProcessCycleforTower" => await RunMoonCycleAsync(call).ConfigureAwait(false),
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
        var savedUsage = configuration.UsageFlags.ToDictionary(row => row.StructureId);
        var structures = new[] { tower.DeployableId }
            .Concat(_deployed is null ? [] : (await _deployed.ByControlTowerAsync(tower.DeployableId, call.Cancellation).ConfigureAwait(false)).Select(module => module.DeployableId));
        var usage = structures.Select(id => savedUsage.GetValueOrDefault(id) ?? new TowerUsage(id)).ToArray();
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
        var towerId = ReadPositiveId(call.Arg(0));
        await UpdateTowerConfigurationAsync(call, towerId, async (record, configuration, connection, transaction, cancellationToken) =>
        {
            var moduleIds = _deployed is null ? new HashSet<long>() :
                (await _deployed.ByControlTowerAsync(connection, transaction, record.DeployableId, cancellationToken).ConfigureAwait(false))
                    .Select(module => module.DeployableId).ToHashSet();
            var seen = new HashSet<long>();
            foreach (var row in usageInput)
            {
                if (row.StructureId != record.DeployableId && !moduleIds.Contains(row.StructureId) ||
                    !seen.Add(row.StructureId) || row.ViewPut is < 0 or > 3 || row.ViewPutTake is < 0 or > 3 || row.Use is < 0 or > 3)
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
        var towerId = ReadPositiveId(call.Arg(0));
        var moduleId = ReadPositiveId(call.Arg(1));
        long? chosen = call.Arg(2) is PyNone ? null : ReadPositiveId(call.Arg(2));
        var tower = await UpdateTowerConfigurationAsync(call, towerId, async (towerRecord, configuration, connection, transaction, cancellationToken) =>
        {
            var module = await RequireAssociatedModuleAsync(call, towerRecord, moduleId, connection, transaction).ConfigureAwait(false);
            if (module.GroupId == MoonHarvesterGroupId && chosen is not null)
                throw LegacyMoonAuthorityUnavailable();
            if (module.GroupId == ReactorGroupId)
                throw LegacyReactionAuthorityUnavailable();
            if (module.GroupId is not (SiloGroupId or MoonHarvesterGroupId) || chosen is <= 0 && call.Arg(2) is not PyNone)
                throw ProductionProvisionInvalid();
            if (chosen is { } typeId)
            {
                if (typeId > int.MaxValue) throw ProductionProvisionInvalid();
                var productTypeId = (int)typeId;
                await _statics.EnsureTypesAsync([productTypeId], cancellationToken).ConfigureAwait(false);
                var productType = _statics.Type(productTypeId);
                if (productType is null || module.GroupId == SiloGroupId && !CanSiloStoreType(module.TypeId, productType))
                    throw ProductionProvisionInvalid();
            }
            var contents = await _items.InContainerAsync(connection, transaction, moduleId, cancellationToken).ConfigureAwait(false);
            if (contents.Any(item => chosen != item.TypeId))
                throw MachoErrors.ThrowUserError("CustomNotify", ("notify", new PyStr("The structure contains items that do not match the selected provision type.")));
            var provisions = new Dictionary<long, int?>(configuration.ProvisionTypes);
            if (chosen is { } type) provisions[moduleId] = checked((int)type);
            else provisions.Remove(moduleId);
            var next = configuration with
            {
                ProvisionTypes = provisions,
                Links = [.. configuration.Links.Where(link => link.FromItemId != moduleId && link.ToItemId != moduleId)],
            };
            return await PersistProductionScheduleAsync(towerRecord, next, connection, transaction, cancellationToken).ConfigureAwait(false);
        }).ConfigureAwait(false);
        _scheduler?.Wake();
        NotifyMoonProcessChange(call.Session, tower, new PyList([]));
        return PyNone.Instance;
    }

    private async Task<PyValue> LinkResourcesAsync(ServiceCall call)
    {
        var towerId = ReadPositiveId(call.Arg(0));
        if (call.Arg(1) is not (PyList or PyTuple)) throw SettingsInvalid();
        var requested = Sequence(call.Arg(1)).Select(row => Sequence(row)).ToArray();
        if (requested.Length > 1_024) throw SettingsInvalid();
        if (requested.Any(row => row.Count != 3 || row.Any(value => !TryInteger(value, out var parsed) || parsed <= 0)))
            throw SettingsInvalid();
        var tower = await UpdateTowerConfigurationAsync(call, towerId, async (towerRecord, configuration, connection, transaction, cancellationToken) =>
        {
            var modules = _deployed is null ? [] : await _deployed.ByControlTowerAsync(connection, transaction,
                towerRecord.DeployableId, cancellationToken).ConfigureAwait(false);
            var modulesById = modules.ToDictionary(module => module.DeployableId);
            var links = new List<TowerLink>();
            var outgoing = new HashSet<(long ModuleId, int TypeId)>();
            var incoming = new HashSet<(long ModuleId, int TypeId)>();
            var graph = new Dictionary<long, List<long>>();
            foreach (var row in requested)
            {
                var fromId = ReadPositiveId(row[0]);
                var toId = ReadPositiveId(row[1]);
                var requestedTypeId = ReadPositiveId(row[2]);
                if (requestedTypeId > int.MaxValue) throw ProductionLinkInvalid();
                var typeId = (int)requestedTypeId;
                if (fromId == toId || !modulesById.TryGetValue(fromId, out var from) || !modulesById.TryGetValue(toId, out var to))
                    throw ProductionLinkInvalid();
                if (from.GroupId is MoonHarvesterGroupId or ReactorGroupId || to.GroupId is MoonHarvesterGroupId or ReactorGroupId)
                    throw from.GroupId == MoonHarvesterGroupId || to.GroupId == MoonHarvesterGroupId
                        ? LegacyMoonAuthorityUnavailable()
                        : LegacyReactionAuthorityUnavailable();
                if (from.GroupId != SiloGroupId || to.GroupId != SiloGroupId ||
                    configuration.ProvisionTypes.GetValueOrDefault(fromId) != typeId || configuration.ProvisionTypes.GetValueOrDefault(toId) != typeId)
                    throw ProductionLinkTypeMismatch();
                if (!outgoing.Add((fromId, typeId)) || !incoming.Add((toId, typeId)))
                    throw ProductionLinkDuplicate();
                links.Add(new TowerLink(fromId, toId, typeId));
                if (!graph.TryGetValue(fromId, out var next)) graph[fromId] = next = [];
                next.Add(toId);
            }
            if (HasLinkCycle(graph)) throw ProductionLinkCycle();
            links.Sort((left, right) => left.FromItemId != right.FromItemId
                ? left.FromItemId.CompareTo(right.FromItemId)
                : left.TypeId.CompareTo(right.TypeId));
            return await PersistProductionScheduleAsync(towerRecord, configuration with { Links = links },
                connection, transaction, cancellationToken).ConfigureAwait(false);
        }).ConfigureAwait(false);
        _scheduler?.Wake();
        NotifyMoonProcessChange(call.Session, tower, new PyList([]));
        return PyBool.True;
    }

    private async Task<PosMgrTowerConfiguration> PersistProductionScheduleAsync(DeployedRecord tower,
        PosMgrTowerConfiguration configuration, NpgsqlConnection connection, NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        if (_scheduler is null)
            return configuration with { Production = configuration.Production with { NextCycleAtMs = null } };

        var key = $"tower:{tower.DeployableId}";
        if (configuration.Links.Count == 0)
        {
            await _scheduler.CancelAsync(connection, transaction, ControlTowerProductionCycleJob, key, cancellationToken).ConfigureAwait(false);
            return configuration with
            {
                Production = configuration.Production with { NextCycleAtMs = null, Status = "unconfigured", LastError = null },
            };
        }

        if (await _deployed!.TowerStateAsync(connection, transaction, tower.DeployableId, cancellationToken).ConfigureAwait(false) != TowerOnline)
        {
            await _scheduler.CancelAsync(connection, transaction, ControlTowerProductionCycleJob, key, cancellationToken).ConfigureAwait(false);
            return configuration with
            {
                Production = configuration.Production with
                {
                    NextCycleAtMs = null,
                    Status = "paused",
                    LastError = "STARBASE_PRODUCTION_TOWER_UNAVAILABLE",
                },
            };
        }

        var nextCycleAtMs = checked(_nowUnixMs() + await ProductionPeriodMsAsync(tower, cancellationToken).ConfigureAwait(false) + 1);
        var production = configuration.Production with
        {
            NextCycleAtMs = nextCycleAtMs,
            Status = "waiting",
            LastError = null,
        };
        await _scheduler.ScheduleAsync(connection, transaction, new JobSpec(ControlTowerProductionCycleJob, nextCycleAtMs)
        {
            Key = key,
            Payload = new ControlTowerProductionCycleDue(tower.DeployableId),
            OnDuplicate = OnDuplicate.Replace,
        }, cancellationToken).ConfigureAwait(false);
        return configuration with { Production = production };
    }

    private async Task UpdateProductionScheduleForTowerStateAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        DeployedRecord tower, bool online, CancellationToken cancellationToken)
    {
        if (_deployed is null || _scheduler is null) return;
        var raw = await _deployed.TowerConfigurationAsync(connection, transaction, tower.DeployableId, cancellationToken).ConfigureAwait(false);
        var configuration = Normalize(raw is null ? new PosMgrTowerConfiguration() :
            JsonSerializer.Deserialize<PosMgrTowerConfiguration>(raw, PosMgrJson) ?? new PosMgrTowerConfiguration());
        PosMgrTowerConfiguration next;
        if (online)
        {
            next = await PersistProductionScheduleAsync(tower, configuration, connection, transaction, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await _scheduler.CancelAsync(connection, transaction, ControlTowerProductionCycleJob,
                $"tower:{tower.DeployableId}", cancellationToken).ConfigureAwait(false);
            next = configuration with
            {
                Production = configuration.Production with
                {
                    NextCycleAtMs = null,
                    Status = configuration.Links.Count == 0 ? "unconfigured" : "paused",
                    LastError = configuration.Links.Count == 0 ? null : "STARBASE_PRODUCTION_TOWER_UNAVAILABLE",
                },
            };
        }
        if (!await _deployed.SetTowerConfigurationAsync(connection, transaction, tower.DeployableId,
                JsonSerializer.Serialize(next, PosMgrJson), cancellationToken).ConfigureAwait(false))
            throw TowerNotFound();
    }

    /// <summary>Rebuilds the durable one-shot clocks from persisted tower configurations when the Host starts.</summary>
    public async Task RestoreProductionCyclesAsync(CancellationToken cancellationToken = default)
    {
        if (_deployed is null || _source is null || _scheduler is null) return;
        foreach (var candidate in await _deployed.ByKindAsync(ControlTowerKind, cancellationToken).ConfigureAwait(false))
        {
            await using var connection = await _source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            var tower = await _deployed.LockAndGetAsync(connection, transaction, candidate.DeployableId, cancellationToken).ConfigureAwait(false);
            if (tower is not { Kind: ControlTowerKind })
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                continue;
            }
            var raw = await _deployed.TowerConfigurationAsync(connection, transaction, tower.DeployableId, cancellationToken).ConfigureAwait(false);
            var configuration = Normalize(raw is null ? new PosMgrTowerConfiguration() :
                JsonSerializer.Deserialize<PosMgrTowerConfiguration>(raw, PosMgrJson) ?? new PosMgrTowerConfiguration());
            if (configuration.Links.Count == 0)
            {
                await _scheduler.CancelAsync(connection, transaction, ControlTowerProductionCycleJob,
                    $"tower:{tower.DeployableId}", cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await UpdateProductionScheduleForTowerStateAsync(connection, transaction, tower,
                    await _deployed.TowerStateAsync(connection, transaction, tower.DeployableId, cancellationToken).ConfigureAwait(false) == TowerOnline,
                    cancellationToken).ConfigureAwait(false);
            }
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        _scheduler.Wake();
    }

    private void NotifyMoonProcessChange(ClientSession? caller, DeployedRecord tower, PyValue structures)
    {
        var recipients = new HashSet<ClientSession>();
        if (caller is not null) recipients.Add(caller);
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

    private async Task<PyValue> RunMoonCycleAsync(ServiceCall call)
    {
        if (_deployed is null || _source is null) throw NotAvailable();
        var towerId = ReadPositiveId(call.Arg(0));
        await using var connection = await _source.OpenConnectionAsync(call.Cancellation).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(call.Cancellation).ConfigureAwait(false);
        var moved = await ExecuteMoonCycleAsync(call, towerId, true, null, null, connection, transaction).ConfigureAwait(false);
        await transaction.CommitAsync(call.Cancellation).ConfigureAwait(false);
        if (moved && await _deployed.GetAsync(towerId, call.Cancellation).ConfigureAwait(false) is { } tower)
            NotifyMoonProcessChange(call.Session, tower, new PyList([]));
        return PyNone.Instance;
    }

    /// <summary>Runs the persisted POS production clock in the scheduler's transaction.</summary>
    public async Task RunScheduledProductionCycleAsync(JobContext context, CancellationToken cancellationToken = default)
    {
        if (_deployed is null) throw NotAvailable();
        var due = context.Payload<ControlTowerProductionCycleDue>()
            ?? throw new InvalidDataException("A POS production cycle has no tower payload.");
        if (await DeferProductionUntilDueFuelAsync(context, due.TowerId, cancellationToken).ConfigureAwait(false))
            return;
        var call = new ServiceCall
        {
            Method = "RunMoonProcessCycleforTower",
            Args = [new PyLong(due.TowerId)],
            Session = null!,
            Cancellation = cancellationToken,
        };
        var moved = await ExecuteMoonCycleAsync(call, due.TowerId, false, context.ScheduledForMs, context,
            context.Connection, context.Transaction).ConfigureAwait(false);
        if (moved)
        {
            var tower = await _deployed.GetAsync(due.TowerId, cancellationToken).ConfigureAwait(false);
            if (tower is not null)
                context.AfterCommit(_ =>
                {
                    NotifyMoonProcessChange(null, tower, new PyList([]));
                    return Task.CompletedTask;
                });
        }
    }

    private async Task<bool> DeferProductionUntilDueFuelAsync(JobContext context, long towerId, CancellationToken cancellationToken)
    {
        if (_scheduler is null) return false;
        var fuelJob = await _scheduler.FindAsync(ControlTowerFuelJob, $"tower:{towerId}", cancellationToken).ConfigureAwait(false);
        if (fuelJob is null || fuelJob.RunAtMs > context.NowMs && fuelJob.LastError is null) return false;

        var tower = await _deployed!.LockAndGetAsync(context.Connection, context.Transaction, towerId, cancellationToken).ConfigureAwait(false);
        if (tower is not { Kind: ControlTowerKind }) return true;
        var raw = await _deployed.TowerConfigurationAsync(context.Connection, context.Transaction, towerId, cancellationToken).ConfigureAwait(false);
        var configuration = Normalize(raw is null ? new PosMgrTowerConfiguration() :
            JsonSerializer.Deserialize<PosMgrTowerConfiguration>(raw, PosMgrJson) ?? new PosMgrTowerConfiguration());
        if (configuration.Production.NextCycleAtMs != context.ScheduledForMs) return true;

        var deferredAtMs = checked(Math.Max(context.NowMs, fuelJob.RunAtMs) + 1);
        var production = configuration.Production with { NextCycleAtMs = deferredAtMs };
        await SaveProductionCycleStateAsync(context.Connection, context.Transaction, towerId, configuration,
            production, cancellationToken).ConfigureAwait(false);
        await context.ScheduleAsync(new JobSpec(ControlTowerProductionCycleJob, deferredAtMs)
        {
            Key = $"tower:{towerId}",
            Payload = new ControlTowerProductionCycleDue(towerId),
            OnDuplicate = OnDuplicate.Replace,
        }, cancellationToken).ConfigureAwait(false);
        return true;
    }

    private async Task<bool> ExecuteMoonCycleAsync(ServiceCall call, long towerId, bool forced, long? scheduledForMs,
        JobContext? jobContext, NpgsqlConnection connection, NpgsqlTransaction transaction)
    {
        if (_deployed is null) throw NotAvailable();
        var tower = await _deployed.LockAndGetAsync(connection, transaction, towerId, call.Cancellation).ConfigureAwait(false);
        if (tower is not { Kind: ControlTowerKind }) throw TowerNotFound();
        if (forced)
        {
            await RequireControlTowerAccessAsync(call.Session, tower, false, call.Cancellation,
                requireConfigurationRole: true, connection: connection, transaction: transaction).ConfigureAwait(false);
            if (((call.Session.GetInt("role") ?? 0) & (1L << 54)) == 0)
                throw MachoErrors.ThrowUserError("CustomNotify", ("notify", new PyStr("Only a Game Master may force a starbase production cycle.")));
        }

        var raw = await _deployed.TowerConfigurationAsync(connection, transaction, towerId, call.Cancellation).ConfigureAwait(false);
        var configuration = Normalize(raw is null ? new PosMgrTowerConfiguration() :
            JsonSerializer.Deserialize<PosMgrTowerConfiguration>(raw, PosMgrJson) ?? new PosMgrTowerConfiguration());
        if (!forced && configuration.Production.NextCycleAtMs != scheduledForMs)
            return false;
        if (configuration.Links.Count == 0)
        {
            if (forced) throw ProductionGraphRequired();
            await SaveProductionCycleStateAsync(connection, transaction, towerId, configuration,
                configuration.Production with { NextCycleAtMs = null, Status = "unconfigured", LastError = null }, call.Cancellation).ConfigureAwait(false);
            return false;
        }

        if (await _deployed.TowerStateAsync(connection, transaction, towerId, call.Cancellation).ConfigureAwait(false) != TowerOnline)
        {
            await SaveProductionCycleStateAsync(connection, transaction, towerId, configuration,
                configuration.Production with { NextCycleAtMs = null, Status = "paused", LastError = "STARBASE_PRODUCTION_TOWER_UNAVAILABLE" }, call.Cancellation).ConfigureAwait(false);
            return false;
        }

        var nodes = new Dictionary<long, ProductionCycleNode>();
        foreach (var link in configuration.Links.OrderBy(link => link.FromItemId).ThenBy(link => link.TypeId))
        {
            if (link.TypeId <= 0) throw ProductionLinkInvalid();
            if (configuration.ProvisionTypes.GetValueOrDefault(link.FromItemId) != link.TypeId ||
                configuration.ProvisionTypes.GetValueOrDefault(link.ToItemId) != link.TypeId)
                throw ProductionLinkTypeMismatch();
            foreach (var moduleId in new[] { link.FromItemId, link.ToItemId })
            {
                if (nodes.ContainsKey(moduleId)) continue;
                var module = await RequireAssociatedModuleAsync(call, tower, moduleId, connection, transaction,
                    requireOffline: false).ConfigureAwait(false);
                if (module.GroupId == MoonHarvesterGroupId) throw LegacyMoonAuthorityUnavailable();
                if (module.GroupId == ReactorGroupId) throw LegacyReactionAuthorityUnavailable();
                if (module.GroupId != SiloGroupId) throw ProductionStructureInvalid();
                await _statics.EnsureTypesAsync([module.TypeId], call.Cancellation).ConfigureAwait(false);
                if (_statics.AttributeId("capacity") is not { } capacityAttribute ||
                    !_statics.Type(module.TypeId)!.Attributes.TryGetValue(capacityAttribute, out var capacity) ||
                    !double.IsFinite(capacity) || capacity < 0)
                    throw ProductionStaticDataUnavailable();
                var contents = await _items.LockContainerAsync(connection, transaction, moduleId, call.Cancellation).ConfigureAwait(false);
                await _statics.EnsureTypesAsync(contents.Select(item => item.TypeId).Distinct().ToArray(), call.Cancellation).ConfigureAwait(false);
                var volumeAttribute = _statics.AttributeId("volume");
                if (volumeAttribute is null) throw ProductionStaticDataUnavailable();
                double VolumeOf(Item item)
                {
                    if (_statics.Type(item.TypeId) is not { } itemType ||
                        !itemType.Attributes.TryGetValue(volumeAttribute.Value, out var volume) ||
                        !double.IsFinite(volume) || volume < 0)
                        throw ProductionStaticDataUnavailable();
                    return volume * (item.Singleton ? 1 : Math.Max(0, item.Quantity));
                }

                var foreignVolume = contents.Where(item => item.OwnerId != tower.OwnerId || item.Flag != 0).Sum(VolumeOf);
                var node = new ProductionCycleNode(module, await _deployed.TowerStateAsync(connection, transaction,
                    moduleId, call.Cancellation).ConfigureAwait(false) == TowerOnline, capacity, contents, foreignVolume);
                foreach (var item in contents.Where(item => item.OwnerId == tower.OwnerId && item.Flag == 0))
                {
                    var quantity = item.Singleton ? 1 : item.Quantity;
                    node.Initial[item.TypeId] = checked(node.Initial.GetValueOrDefault(item.TypeId) + quantity);
                    node.Pool[item.TypeId] = checked(node.Pool.GetValueOrDefault(item.TypeId) + quantity);
                }
                nodes.Add(moduleId, node);
            }
        }

        var outgoing = nodes.Keys.ToDictionary(id => id, _ => new List<TowerLink>());
        var indegree = nodes.Keys.ToDictionary(id => id, _ => 0);
        foreach (var link in configuration.Links)
        {
            if (link.FromItemId == link.ToItemId || !nodes.ContainsKey(link.FromItemId) || !nodes.ContainsKey(link.ToItemId) ||
                outgoing[link.FromItemId].Any(existing => existing.TypeId == link.TypeId) ||
                configuration.Links.Count(existing => existing.ToItemId == link.ToItemId && existing.TypeId == link.TypeId) > 1)
                throw ProductionLinkInvalid();
            outgoing[link.FromItemId].Add(link);
            indegree[link.ToItemId]++;
        }
        if (HasLinkCycle(outgoing.ToDictionary(pair => pair.Key, pair => pair.Value.Select(link => link.ToItemId).ToList())))
            throw ProductionLinkCycle();

        var ready = new SortedSet<long>(indegree.Where(pair => pair.Value == 0).Select(pair => pair.Key));
        var order = new List<long>(nodes.Count);
        while (ready.Count > 0)
        {
            var current = ready.Min;
            ready.Remove(current);
            order.Add(current);
            foreach (var edge in outgoing[current])
                if (--indegree[edge.ToItemId] == 0) ready.Add(edge.ToItemId);
        }
        var volumeAttributeId = _statics.AttributeId("volume") ?? throw ProductionStaticDataUnavailable();
        double UnitVolume(int typeId)
        {
            if (_statics.Type(typeId) is not { } type || !type.Attributes.TryGetValue(volumeAttributeId, out var volume) ||
                !double.IsFinite(volume) || volume <= 0)
                throw ProductionStaticDataUnavailable();
            return volume;
        }

        var movedAny = false;
        foreach (var moduleId in order)
        {
            var source = nodes[moduleId];
            if (!source.Online) continue;
            foreach (var link in outgoing[moduleId].OrderBy(link => link.TypeId).ThenBy(link => link.ToItemId))
            {
                var destination = nodes[link.ToItemId];
                if (!destination.Online) continue;
                var available = source.Pool.GetValueOrDefault(link.TypeId);
                if (available <= 0) continue;
                var volume = UnitVolume(link.TypeId);
                var used = destination.ForeignVolume + destination.Pool.Sum(stack => stack.Value * UnitVolume(stack.Key));
                var free = Math.Max(0, destination.Capacity - used);
                var room = free >= long.MaxValue * volume ? long.MaxValue : (long)Math.Floor(free / volume);
                var transfer = Math.Min(available, room);
                if (transfer <= 0) continue;
                source.Pool[link.TypeId] = available - transfer;
                destination.Pool[link.TypeId] = checked(destination.Pool.GetValueOrDefault(link.TypeId) + transfer);
                movedAny = true;
            }
        }

        foreach (var node in nodes.Values.OrderBy(node => node.Record.DeployableId))
        {
            foreach (var typeId in node.Initial.Keys.Union(node.Pool.Keys).Order())
            {
                var delta = checked(node.Pool.GetValueOrDefault(typeId) - node.Initial.GetValueOrDefault(typeId));
                if (delta < 0)
                {
                    var remaining = -delta;
                    foreach (var stack in node.Contents.Where(item => item.TypeId == typeId && item.OwnerId == tower.OwnerId && item.Flag == 0)
                                 .OrderBy(item => item.Id))
                    {
                        var quantity = stack.Singleton ? 1 : stack.Quantity;
                        var consumed = Math.Min(remaining, quantity);
                        if (consumed == quantity)
                        {
                            await _items.DeleteAsync(connection, transaction, stack.Id, call.Cancellation).ConfigureAwait(false);
                        }
                        else
                        {
                            await _items.SetQuantityAsync(connection, transaction, stack.Id, quantity - consumed, call.Cancellation).ConfigureAwait(false);
                        }
                        remaining -= consumed;
                        if (remaining == 0) break;
                    }
                    if (remaining != 0) throw ProductionGraphRequired();
                }
                else if (delta > 0)
                {
                    _ = await _items.CreateAsync(connection, transaction,
                        new NewItem(typeId, tower.OwnerId, node.Record.DeployableId, 0, delta), call.Cancellation).ConfigureAwait(false);
                }
            }
        }

        var nextCycle = configuration.Production with
        {
            LastCycleAtMs = _nowUnixMs(),
            CyclesCompleted = checked(configuration.Production.CyclesCompleted + 1),
            Status = movedAny ? "operating" : "waiting",
            LastError = null,
        };
        if (jobContext is not null)
        {
            var nextCycleAtMs = checked(_nowUnixMs() + await ProductionPeriodMsAsync(tower, call.Cancellation).ConfigureAwait(false) + 1);
            nextCycle = nextCycle with { NextCycleAtMs = nextCycleAtMs };
            await jobContext.ScheduleAsync(new JobSpec(ControlTowerProductionCycleJob, nextCycleAtMs)
            {
                Key = $"tower:{towerId}",
                Payload = new ControlTowerProductionCycleDue(towerId),
                OnDuplicate = OnDuplicate.Replace,
            }, call.Cancellation).ConfigureAwait(false);
        }
        await SaveProductionCycleStateAsync(connection, transaction, towerId, configuration, nextCycle, call.Cancellation).ConfigureAwait(false);
        return movedAny;
    }

    private async Task<long> ProductionPeriodMsAsync(DeployedRecord tower, CancellationToken cancellationToken)
    {
        await _statics.EnsureTypesAsync([tower.TypeId], cancellationToken).ConfigureAwait(false);
        if (_statics.AttributeId("posControlTowerPeriod") is not { } periodAttribute ||
            _statics.Type(tower.TypeId) is not { } towerType ||
            !towerType.Attributes.TryGetValue(periodAttribute, out var periodMs) ||
            !double.IsFinite(periodMs) || periodMs <= 0 || periodMs != Math.Truncate(periodMs) || periodMs > long.MaxValue)
            throw ProductionStaticDataUnavailable();
        return (long)periodMs;
    }

    private async Task SaveProductionCycleStateAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        long towerId, PosMgrTowerConfiguration configuration, ProductionCycleState production, CancellationToken cancellationToken)
    {
        if (_deployed is null || !await _deployed.SetTowerConfigurationAsync(connection, transaction, towerId,
                JsonSerializer.Serialize(configuration with { Production = production }, PosMgrJson), cancellationToken).ConfigureAwait(false))
            throw TowerNotFound();
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

    private Task<DeployedRecord> UpdateTowerConfigurationAsync(ServiceCall call, long towerId,
        Func<DeployedRecord, PosMgrTowerConfiguration, PosMgrTowerConfiguration> update) =>
        UpdateTowerConfigurationAsync(call, towerId,
            (record, configuration, _, _, _) => Task.FromResult(update(record, configuration)));

    private async Task<DeployedRecord> UpdateTowerConfigurationAsync(ServiceCall call, long towerId,
        Func<DeployedRecord, PosMgrTowerConfiguration, NpgsqlConnection, NpgsqlTransaction, CancellationToken,
            Task<PosMgrTowerConfiguration>> update)
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
        var next = Normalize(await update(record, current, connection, transaction, call.Cancellation).ConfigureAwait(false));
        var json = JsonSerializer.Serialize(next, PosMgrJson);
        if (!await _deployed.SetTowerConfigurationAsync(connection, transaction, towerId, json, call.Cancellation).ConfigureAwait(false))
            throw TowerNotFound();
        await transaction.CommitAsync(call.Cancellation).ConfigureAwait(false);
        return record;
    }

    private async Task<DeployedRecord> RequireAssociatedModuleAsync(ServiceCall call, DeployedRecord tower, long moduleId,
        NpgsqlConnection connection, NpgsqlTransaction transaction, bool requireOffline = true)
    {
        if (_deployed is null || moduleId <= 0 ||
            await _deployed.LockAndGetAsync(connection, transaction, moduleId, call.Cancellation).ConfigureAwait(false) is not { Kind: StarbaseModuleKind } module ||
            module.ControlTowerId != tower.DeployableId || module.OwnerId != tower.OwnerId || module.CorporationId != tower.CorporationId ||
            module.SolarSystemId != tower.SolarSystemId || module.ActivateAtMs == NeverAnchored || module.ActivateAtMs > _nowUnixMs())
            throw ProductionStructureInvalid();
        var state = await _deployed.TowerStateAsync(connection, transaction, moduleId, call.Cancellation).ConfigureAwait(false);
        if (requireOffline && state != TowerOffline)
            throw ProductionMustBeOffline();
        if (!requireOffline && state is not (TowerOffline or TowerOnline or TowerOnlining))
            throw ProductionStructureInvalid();
        return module;
    }

    private bool CanSiloStoreType(int siloTypeId, TypeDef materialType)
    {
        var siloType = _statics.Type(siloTypeId);
        if (siloType is null) return false;
        var cargoGroupAttribute = _statics.AttributeId("cargoGroup") ?? 629;
        var cargoGroup2Attribute = _statics.AttributeId("cargoGroup2") ?? 1846;
        var cargoGroup = siloType.Attributes.GetValueOrDefault(cargoGroupAttribute);
        var cargoGroup2 = siloType.Attributes.GetValueOrDefault(cargoGroup2Attribute);
        var allowed = cargoGroup > 0
            ? new[] { cargoGroup, cargoGroup2 }.Where(group => group > 0)
            : new double[] { 427, 429, 428, 422, 18 };
        return allowed.Contains(materialType.GroupId);
    }

    private bool IsPosProductionInventory(Item item) =>
        _statics.Type(item.TypeId) is { CategoryId: StarbaseCategoryId, GroupId: SiloGroupId };

    /// <summary>
    /// POS production inventories use their deployed tower association and saved module usage row. A silo is usable only while offline,
    /// matching EveJS validateProductionInventoryAccess; each operation re-reads persisted state so a retained binding cannot keep stale access.
    /// </summary>
    private async Task<bool> ValidatePosProductionInventoryAsync(ClientSession session, Item item, string operation,
        CancellationToken cancellationToken)
    {
        if (!IsPosProductionInventory(item) || _deployed is null || _source is null || session.CharacterId is not { } characterId ||
            _space.SolarSystemOf(session) is not { } systemId || item.LocationId != systemId)
            return false;

        if (await _deployed.GetAsync(item.Id, cancellationToken).ConfigureAwait(false) is not { Kind: StarbaseModuleKind } module ||
            module.SolarSystemId != systemId || module.ActivateAtMs > _nowUnixMs() || module.ControlTowerId <= 0)
            throw MachoErrors.ThrowUserError("STARBASE_INVENTORY_OFFLINE");
        var moduleState = await _deployed.TowerStateAsync(module.DeployableId, cancellationToken).ConfigureAwait(false);
        if (moduleState != TowerOffline)
            throw MachoErrors.ThrowUserError("STARBASE_PRODUCTION_MUST_BE_OFFLINE");
        var towerState = await _deployed.TowerStateAsync(module.ControlTowerId, cancellationToken).ConfigureAwait(false);
        if (await _deployed.GetAsync(module.ControlTowerId, cancellationToken).ConfigureAwait(false) is not { Kind: ControlTowerKind } tower ||
            tower.OwnerId != module.OwnerId || tower.CorporationId != module.CorporationId || tower.SolarSystemId != systemId ||
            tower.ActivateAtMs > _nowUnixMs() || towerState is not (TowerOnline or TowerOffline or TowerOnlining))
            throw MachoErrors.ThrowUserError("STARBASE_INVENTORY_TOWER_UNAVAILABLE");

        await using var connection = await _source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var raw = await _deployed.TowerConfigurationAsync(connection, null, tower.DeployableId, cancellationToken).ConfigureAwait(false);
        var configuration = Normalize(raw is null ? new PosMgrTowerConfiguration() :
            JsonSerializer.Deserialize<PosMgrTowerConfiguration>(raw, PosMgrJson) ?? new PosMgrTowerConfiguration());

        if (!await PosModulePermissionAsync(session, tower, configuration, module.DeployableId, operation, characterId, cancellationToken).ConfigureAwait(false))
            return false;

        var maximumDistance = _statics.AttributeId("maxOperationalDistance") is { } distanceAttribute &&
            _statics.Type(item.TypeId) is { } moduleType
                ? moduleType.Attributes.GetValueOrDefault(distanceAttribute, OpenRange)
                : OpenRange;
        if (!double.IsFinite(maximumDistance) || maximumDistance < 0)
            return false;
        CommandReply reply;
        try
        {
            reply = await _sim.CommandAsync((ulong)session.SessionId, SimCommands.Measure, [(double)item.Id], cancellationToken).ConfigureAwait(false);
        }
        catch (SimUnavailableException)
        {
            throw Unavailable();
        }
        if (!reply.Accepted || reply.Distance is not { } distance)
            throw MachoErrors.ThrowUserError("CONTAINER_NOT_VISIBLE");
        if (distance > maximumDistance)
            throw MachoErrors.ThrowUserError("STARBASE_INVENTORY_TOO_FAR");
        return true;
    }

    private async Task<bool> ValidatePosSiloDepositAsync(ClientSession session, Item silo, Item item, CancellationToken cancellationToken)
    {
        if (!await ValidatePosProductionInventoryAsync(session, silo, "viewput", cancellationToken).ConfigureAwait(false))
            throw MachoErrors.ThrowUserError("STARBASE_PRODUCTION_ACCESS_DENIED");
        if (_deployed is null || _source is null ||
            await _deployed.GetAsync(silo.Id, cancellationToken).ConfigureAwait(false) is not { Kind: StarbaseModuleKind } module ||
            module.ControlTowerId <= 0)
            return false;
        await using var connection = await _source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var raw = await _deployed.TowerConfigurationAsync(connection, null, module.ControlTowerId, cancellationToken).ConfigureAwait(false);
        var configuration = Normalize(raw is null ? new PosMgrTowerConfiguration() :
            JsonSerializer.Deserialize<PosMgrTowerConfiguration>(raw, PosMgrJson) ?? new PosMgrTowerConfiguration());
        if (configuration.ProvisionTypes.GetValueOrDefault(silo.Id) is not { } provisionType || item.TypeId != provisionType)
            return false;
        await _statics.EnsureTypesAsync([item.TypeId], cancellationToken).ConfigureAwait(false);
        return _statics.Type(item.TypeId) is { } materialType && CanSiloStoreType(silo.TypeId, materialType);
    }

    private async Task<bool> PosModulePermissionAsync(ClientSession session, DeployedRecord tower,
        PosMgrTowerConfiguration configuration, long moduleId, string operation, long characterId, CancellationToken cancellationToken)
    {
        var corporationId = session.GetInt("corpid") ?? session.GetInt("corporationid") ?? 0;
        var roles = session.GetInt("corprole") ?? 0;
        var isCeo = session.Get("isCEO") switch
        {
            PyBool value => value.Value,
            PyInt value => value.Value != 0,
            PyLong value => value.Value != 0,
            _ => false,
        };
        var sameCorporation = corporationId > 0 && corporationId == tower.CorporationId && characterId > 0;
        if (sameCorporation && (isCeo || (roles & (DirectorRole | StarbaseConfigRole)) != 0))
            return true;

        var row = configuration.UsageFlags.FirstOrDefault(candidate => candidate.StructureId == moduleId) ?? new TowerUsage(moduleId);
        var permission = operation switch
        {
            "viewput" => row.ViewPut,
            "viewputtake" => row.ViewPutTake,
            "use" => row.Use,
            _ => 0,
        };
        bool Allowed(int value)
        {
            if (value == 1) return sameCorporation;
            if (value == 2)
            {
                var allianceId = session.GetInt("allianceid") ?? session.GetInt("allianceID") ?? 0;
                return sameCorporation || tower.AllianceId > 0 && allianceId == tower.AllianceId;
            }
            const long caretakerRole = 288_230_376_151_711_744L;
            return value == 3 && sameCorporation && (roles & caretakerRole) != 0;
        }
        return operation == "viewput" ? Allowed(permission) || Allowed(row.ViewPutTake) : Allowed(permission);
    }

    private static bool HasLinkCycle(IReadOnlyDictionary<long, List<long>> graph)
    {
        var visiting = new HashSet<long>();
        var visited = new HashSet<long>();
        bool Visit(long node)
        {
            if (visiting.Contains(node)) return true;
            if (!visited.Add(node)) return false;
            visiting.Add(node);
            if (graph.TryGetValue(node, out var next) && next.Any(Visit)) return true;
            visiting.Remove(node);
            return false;
        }
        return graph.Keys.Any(Visit);
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
        Production = source.Production ?? new ProductionCycleState(),
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
    private static MachoWrappedException ProductionGraphRequired() => MachoErrors.ThrowUserError("CustomNotify",
        ("notify", new PyStr("No production links are configured for this control tower. No cycle was run.")));
    private static MachoWrappedException ProductionStaticDataUnavailable() => MachoErrors.ThrowUserError("CustomNotify",
        ("notify", new PyStr("The pinned static data lacks a required POS silo or material value. No cycle was run.")));
    private static MachoWrappedException ProcessInfoUnavailable() => MachoErrors.ThrowUserError("CustomNotify",
        ("notify", new PyStr("The historical production-info RPC payload is unavailable in this client build. Use /pos production status <towerID> for server diagnostics.")));
    private static MachoWrappedException ProductionStructureInvalid() => MachoErrors.ThrowUserError("CustomNotify",
        ("notify", new PyStr("Production links must connect valid structures belonging to this control tower.")));
    private static MachoWrappedException ProductionMustBeOffline() => MachoErrors.ThrowUserError("CustomNotify",
        ("notify", new PyStr("Offline this starbase production structure before changing its material type.")));
    private static MachoWrappedException ProductionProvisionInvalid() => MachoErrors.ThrowUserError("CustomNotify",
        ("notify", new PyStr("This material cannot be stored in that silo.")));
    private static MachoWrappedException LegacyMoonAuthorityUnavailable() => MachoErrors.ThrowUserError("CustomNotify",
        ("notify", new PyStr("No verified historical POS moon-material abundance is available for this moon. Harvesting cannot be configured.")));
    private static MachoWrappedException LegacyReactionAuthorityUnavailable() => MachoErrors.ThrowUserError("CustomNotify",
        ("notify", new PyStr("Legacy POS reaction recipes are not available in the pinned data. This reactor cannot be configured.")));
    private static MachoWrappedException ProductionLinkInvalid() => MachoErrors.ThrowUserError("CustomNotify",
        ("notify", new PyStr("Production links must connect valid structures belonging to this control tower.")));
    private static MachoWrappedException ProductionLinkTypeMismatch() => MachoErrors.ThrowUserError("CustomNotify",
        ("notify", new PyStr("Both linked silos must be configured for the same material.")));
    private static MachoWrappedException ProductionLinkDuplicate() => MachoErrors.ThrowUserError("CustomNotify",
        ("notify", new PyStr("A material may have only one source and one destination at each production connection.")));
    private static MachoWrappedException ProductionLinkCycle() => MachoErrors.ThrowUserError("CustomNotify",
        ("notify", new PyStr("Production links cannot form a circular chain.")));
    private static MachoWrappedException NotAvailable() => MachoErrors.ThrowUserError("CustomNotify",
        ("notify", new PyStr("POS management is not available on this server.")));
}
