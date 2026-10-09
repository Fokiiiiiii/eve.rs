using Eve.Macho;
using Eve.Marshal;
using Eve.Space;

namespace Eve.Services.Space;

public sealed partial class FleetStore
{
    public Task<PyValue?> FleetFinderAdvertOperationAsync(string operation, long characterId,
        IReadOnlyList<PyValue> args, ClientSession session, CancellationToken cancellationToken = default) =>
        FleetFinderAdvertOperationAsync(operation, characterId, args, session, null, cancellationToken);

    public async Task<PyValue?> FleetFinderAdvertOperationAsync(string operation, long characterId,
        IReadOnlyList<PyValue> args, ClientSession session,
        Func<long, CancellationToken, Task<FleetAdvertLeaderProfile?>>? leaderProfileForCharacter,
        CancellationToken cancellationToken)
    {
        if (characterId <= 0) return PyNone.Instance;
        if (operation == "GetAvailableFleetAds")
        {
            var adverts = await ReadFleetAdvertsAsync(null, includeInactive: false, cancellationToken);
            var visible = adverts.Where(advert =>
                advert.Payload.Leader.CharacterId == characterId || IsVisibleTo(advert.Payload, session));
            return new PyDict(visible.OrderBy(advert => advert.FleetId).Select(advert =>
                new KeyValuePair<PyValue, PyValue>(new PyLong(advert.FleetId), AdvertValue(advert.Payload))).ToArray());
        }
        if (operation == "GetMyFleetFinderAdvert")
        {
            await using var connection = await source.OpenConnectionAsync(cancellationToken);
            var fleetValue = await ScalarAsync(connection, null,
                $"SELECT fleet_id FROM {_schema}.fleet_members WHERE character_id = $1", cancellationToken, characterId);
            if (fleetValue is null or DBNull) return PyNone.Instance;
            var fleetId = Convert.ToInt64(fleetValue, System.Globalization.CultureInfo.InvariantCulture);
            var advert = await ReadFleetAdvertPayloadAsync(connection, null, fleetId, cancellationToken);
            return advert is null ? PyNone.Instance : AdvertValue(advert);
        }

        if (operation is not ("AddFleetFinderAdvert" or "UpdateFleetAdvertWithNewLeader" or
            "RemoveFleetFinderAdvert" or "UpdateAdvertInfo" or "UpdateAdvertAllowedEntities"))
            throw new ArgumentOutOfRangeException(nameof(operation), operation, "Unknown FleetFinder operation.");

        await using var writeConnection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await writeConnection.BeginTransactionAsync(cancellationToken);
        var fleetIdForMutation = await LockFleetAdvertManagerAsync(
            writeConnection, transaction, characterId, cancellationToken);
        if (fleetIdForMutation is not { } mutationFleetId) return PyNone.Instance;
        FleetAdvertPayload? result = null;

        switch (operation)
        {
            case "AddFleetFinderAdvert":
            {
                if (args.Count == 0 || !TryAdvertInput(args[0], out var input)) return PyNone.Instance;
                var memberCount = checked((int)await CountAsync(writeConnection, transaction,
                    $"SELECT count(*) FROM {_schema}.fleet_members WHERE fleet_id = $1", cancellationToken, mutationFleetId));
                var createdValue = await ScalarAsync(writeConnection, transaction,
                    $"SELECT date_created_filetime FROM {_schema}.fleets WHERE fleet_id = $1", cancellationToken, mutationFleetId);
                var dateCreated = createdValue is null or DBNull
                    ? CurrentFileTime()
                    : Convert.ToInt64(createdValue, System.Globalization.CultureInfo.InvariantCulture);
                var leader = await LeaderPayloadAsync(characterId, session, leaderProfileForCharacter, cancellationToken);
                result = new FleetAdvertPayload
                {
                    FleetId = mutationFleetId,
                    Leader = leader,
                    SolarSystemId = SessionSolarSystemId(session),
                    NumMembers = memberCount,
                    AdvertTime = CurrentFileTime(),
                    DateCreated = dateCreated,
                    FleetName = input.FleetName,
                    Description = input.Description,
                    InviteScope = input.InviteScope,
                    ActivityValue = input.ActivityValue,
                    UseAdvanceOptions = input.UseAdvanceOptions,
                    NewPlayerFriendly = input.NewPlayerFriendly,
                    PublicMinStanding = input.PublicMinStanding,
                    PublicMinSecurity = input.PublicMinSecurity,
                    PublicAllowedEntities = input.PublicAllowedEntities,
                    PublicDisallowedEntities = input.PublicDisallowedEntities,
                    MembergroupsMinStanding = input.MembergroupsMinStanding,
                    MembergroupsMinSecurity = input.MembergroupsMinSecurity,
                    MembergroupsAllowedEntities = input.MembergroupsAllowedEntities,
                    MembergroupsDisallowedEntities = input.MembergroupsDisallowedEntities,
                    JoinNeedsApproval = input.JoinNeedsApproval,
                    HideInfo = input.HideInfo,
                    UpdateOnBossChange = input.UpdateOnBossChange,
                    AdvertJoinLimit = input.AdvertJoinLimit
                };
                await using (var update = Command(writeConnection, transaction,
                    $"UPDATE {_schema}.fleets SET advert_active = TRUE, fleet_name = $1, description = $2, invite_scope = $3, join_needs_approval = $4, hide_info = $5, advert_join_limit = $6, public_min_standing = $7 WHERE fleet_id = $8",
                    input.FleetName, input.Description, input.InviteScope, input.JoinNeedsApproval, input.HideInfo,
                    input.AdvertJoinLimit ?? MaxMembers, input.PublicMinStanding ?? -10d, mutationFleetId))
                    if (await update.ExecuteNonQueryAsync(cancellationToken) != 1) return PyNone.Instance;
                await SaveFleetAdvertPayloadAsync(writeConnection, transaction, mutationFleetId, result, cancellationToken);

                break;
            }

            case "UpdateFleetAdvertWithNewLeader":
            {
                result = await ReadFleetAdvertPayloadAsync(writeConnection, transaction, mutationFleetId, cancellationToken);
                if (result is null) return PyNone.Instance;
                result.Leader = await LeaderPayloadAsync(characterId, session, leaderProfileForCharacter, cancellationToken);
                result.SolarSystemId = SessionSolarSystemId(session);
                var setInput = args.Count > 0 ? args[0] : PyNone.Instance;
                if (!TryReadAdvertEntitySets(setInput, out var sets)) return PyNone.Instance;
                result.PublicAllowedEntities = sets.PublicAllowed;
                result.PublicDisallowedEntities = sets.PublicDisallowed;
                result.MembergroupsAllowedEntities = sets.MembergroupsAllowed;
                result.MembergroupsDisallowedEntities = sets.MembergroupsDisallowed;
                result.AdvertTime = CurrentFileTime();
                await SaveFleetAdvertPayloadAsync(writeConnection, transaction, mutationFleetId, result, cancellationToken);

                break;
            }

            case "RemoveFleetFinderAdvert":
            {
                result = await ReadFleetAdvertPayloadAsync(writeConnection, transaction, mutationFleetId, cancellationToken);
                if (result is null) return PyNone.Instance;
                await using (var remove = Command(writeConnection, transaction,
                    $"UPDATE {_schema}.fleets SET advert_active = FALSE, advert_payload = NULL WHERE fleet_id = $1",
                    mutationFleetId))
                    if (await remove.ExecuteNonQueryAsync(cancellationToken) != 1) return PyNone.Instance;
                await using var clearLegacy = Command(writeConnection, transaction,
                    $"DELETE FROM {_schema}.fleet_advert_allowed_entities WHERE fleet_id = $1", mutationFleetId);
                await clearLegacy.ExecuteNonQueryAsync(cancellationToken);
                break;
            }

            case "UpdateAdvertInfo":
            {
                result = await ReadFleetAdvertPayloadAsync(writeConnection, transaction, mutationFleetId, cancellationToken);
                if (result is null) return PyNone.Instance;
                var actualMemberCount = checked((int)await CountAsync(writeConnection, transaction,
                    $"SELECT count(*) FROM {_schema}.fleet_members WHERE fleet_id = $1", cancellationToken, mutationFleetId));
                result.NumMembers = args.Count > 0 && TryReadLong(args[0], out var requestedCount)
                    ? checked((int)Math.Clamp(requestedCount, 0, int.MaxValue))
                    : actualMemberCount;
                result.SolarSystemId = SessionSolarSystemId(session);
                var diffInput = args.Count > 1 ? args[1] : PyNone.Instance;
                if (!TryReadAdvertEntityDiff(diffInput, out var diff)) return PyNone.Instance;
                result.PublicAllowedEntities = ApplySetDiff(result.PublicAllowedEntities, diff.PublicAdd, diff.PublicRemove);
                result.PublicDisallowedEntities = ApplySetDiff(result.PublicDisallowedEntities, diff.PublicDisallowedAdd, diff.PublicDisallowedRemove);
                result.MembergroupsAllowedEntities = ApplySetDiff(result.MembergroupsAllowedEntities, diff.MembergroupsAdd, diff.MembergroupsRemove);
                result.MembergroupsDisallowedEntities = ApplySetDiff(result.MembergroupsDisallowedEntities, diff.MembergroupsDisallowedAdd, diff.MembergroupsDisallowedRemove);
                result.AdvertTime = CurrentFileTime();
                await SaveFleetAdvertPayloadAsync(writeConnection, transaction, mutationFleetId, result, cancellationToken);

                break;
            }

            case "UpdateAdvertAllowedEntities":
            {
                result = await ReadFleetAdvertPayloadAsync(writeConnection, transaction, mutationFleetId, cancellationToken);
                if (result is null) return PyNone.Instance;
                var setInput = args.Count > 0 ? args[0] : PyNone.Instance;
                if (!TryReadAdvertEntitySets(setInput, out var sets)) return PyNone.Instance;
                result.PublicAllowedEntities = sets.PublicAllowed;
                result.PublicDisallowedEntities = sets.PublicDisallowed;
                result.MembergroupsAllowedEntities = sets.MembergroupsAllowed;
                result.MembergroupsDisallowedEntities = sets.MembergroupsDisallowed;
                result.AdvertTime = CurrentFileTime();
                await SaveFleetAdvertPayloadAsync(writeConnection, transaction, mutationFleetId, result, cancellationToken);

                break;
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return result is null ? PyNone.Instance : AdvertValue(result);
    }

    private sealed record FleetAdvert(long FleetId, FleetAdvertPayload Payload);

    private sealed class FleetAdvertPayload
    {
        public FleetAdvertPayload() { }
        [System.Text.Json.Serialization.JsonPropertyName("fleetID")] public long FleetId { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("leader")] public FleetAdvertLeaderPayload Leader { get; set; } = new();
        [System.Text.Json.Serialization.JsonPropertyName("solarSystemID")] public long? SolarSystemId { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("numMembers")] public int NumMembers { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("advertTime")] public long AdvertTime { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("dateCreated")] public long DateCreated { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("fleetName")] public string FleetName { get; set; } = "";
        [System.Text.Json.Serialization.JsonPropertyName("description")] public string Description { get; set; } = "";
        [System.Text.Json.Serialization.JsonPropertyName("inviteScope")] public int InviteScope { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("activityValue")] public int? ActivityValue { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("useAdvanceOptions")] public bool UseAdvanceOptions { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("newPlayerFriendly")] public bool NewPlayerFriendly { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("public_minStanding")] public double? PublicMinStanding { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("public_minSecurity")] public double? PublicMinSecurity { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("public_allowedEntities")] public long[] PublicAllowedEntities { get; set; } = [];
        [System.Text.Json.Serialization.JsonPropertyName("public_disallowedEntities")] public long[] PublicDisallowedEntities { get; set; } = [];
        [System.Text.Json.Serialization.JsonPropertyName("membergroups_minStanding")] public double? MembergroupsMinStanding { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("membergroups_minSecurity")] public double? MembergroupsMinSecurity { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("membergroups_allowedEntities")] public long[] MembergroupsAllowedEntities { get; set; } = [];
        [System.Text.Json.Serialization.JsonPropertyName("membergroups_disallowedEntities")] public long[] MembergroupsDisallowedEntities { get; set; } = [];
        [System.Text.Json.Serialization.JsonPropertyName("joinNeedsApproval")] public bool JoinNeedsApproval { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("hideInfo")] public bool HideInfo { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("updateOnBossChange")] public bool UpdateOnBossChange { get; set; } = true;
        [System.Text.Json.Serialization.JsonPropertyName("advertJoinLimit")] public int? AdvertJoinLimit { get; set; }
    }

    private sealed class FleetAdvertLeaderPayload
    {
        public FleetAdvertLeaderPayload() { }
        [System.Text.Json.Serialization.JsonPropertyName("charID")] public long CharacterId { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("corpID")] public long? CorporationId { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("allianceID")] public long? AllianceId { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("warFactionID")] public long? WarFactionId { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("securityStatus")] public double SecurityStatus { get; set; }
    }

    private sealed record FleetAdvertInput(
        string FleetName, string Description, int InviteScope, int? ActivityValue,
        bool UseAdvanceOptions, bool NewPlayerFriendly, double? PublicMinStanding, double? PublicMinSecurity,
        long[] PublicAllowedEntities, long[] PublicDisallowedEntities, double? MembergroupsMinStanding,
        double? MembergroupsMinSecurity, long[] MembergroupsAllowedEntities, long[] MembergroupsDisallowedEntities,
        bool JoinNeedsApproval, bool HideInfo, bool UpdateOnBossChange, int? AdvertJoinLimit);

    private sealed record FleetAdvertEntitySets(
        long[] PublicAllowed, long[] PublicDisallowed, long[] MembergroupsAllowed, long[] MembergroupsDisallowed);

    private sealed record FleetAdvertEntityDiff(
        long[] PublicAdd, long[] PublicRemove, long[] PublicDisallowedAdd, long[] PublicDisallowedRemove,
        long[] MembergroupsAdd, long[] MembergroupsRemove, long[] MembergroupsDisallowedAdd, long[] MembergroupsDisallowedRemove);

    private async Task<IReadOnlyList<FleetAdvert>> ReadFleetAdvertsAsync(
        long? fleetId, bool includeInactive, CancellationToken cancellationToken)
    {
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        var adverts = new List<FleetAdvert>();
        await using var command = Command(connection, null,
            $@"SELECT fleet_id, advert_payload::text
               FROM {_schema}.fleets
               WHERE ($1::bigint IS NULL OR fleet_id = $1)
                 AND ($2::boolean OR advert_active)
                 AND advert_payload IS NOT NULL
               ORDER BY fleet_id",
            fleetId, includeInactive);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var payload = System.Text.Json.JsonSerializer.Deserialize<FleetAdvertPayload>(reader.GetString(1))
                ?? throw new InvalidOperationException($"Fleet {reader.GetInt64(0)} has an invalid advert payload.");
            adverts.Add(new FleetAdvert(reader.GetInt64(0), payload));
        }
        return adverts;
    }

    private async Task<FleetAdvertPayload?> ReadFleetAdvertPayloadAsync(
        System.Data.Common.DbConnection connection, System.Data.Common.DbTransaction? transaction,
        long fleetId, CancellationToken cancellationToken)
    {
        var value = await ScalarAsync(connection, transaction,
            $"SELECT advert_payload::text FROM {_schema}.fleets WHERE fleet_id = $1 AND advert_active",
            cancellationToken, fleetId);
        if (value is not string json) return null;
        return System.Text.Json.JsonSerializer.Deserialize<FleetAdvertPayload>(json)
            ?? throw new InvalidOperationException($"Fleet {fleetId} has an invalid advert payload.");
    }

    private async Task SaveFleetAdvertPayloadAsync(System.Data.Common.DbConnection connection,
        System.Data.Common.DbTransaction transaction, long fleetId, FleetAdvertPayload payload,
        CancellationToken cancellationToken)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(payload);
        await using var update = Command(connection, transaction,
            $"UPDATE {_schema}.fleets SET advert_payload = $1::jsonb WHERE fleet_id = $2 AND advert_active",
            json, fleetId);
        if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw new InvalidOperationException($"Fleet {fleetId} lost its active advert while updating.");
    }

    private async Task<FleetAdvertLeaderPayload> LeaderPayloadAsync(long characterId, ClientSession session,
        Func<long, CancellationToken, Task<FleetAdvertLeaderProfile?>>? leaderProfileForCharacter,
        CancellationToken cancellationToken)
    {
        var profile = leaderProfileForCharacter is null ? null :
            await leaderProfileForCharacter(characterId, cancellationToken);
        return new FleetAdvertLeaderPayload
        {
            CharacterId = characterId,
            CorporationId = profile?.CorporationId ?? session.GetInt("corpid") ?? session.GetInt("corporationID"),
            AllianceId = profile?.AllianceId ?? session.GetInt("allianceid") ?? session.GetInt("allianceID"),
            WarFactionId = profile?.WarFactionId ?? session.GetInt("warfactionid") ?? session.GetInt("warFactionID"),
            SecurityStatus = profile?.SecurityStatus ?? SessionReal(session, "securityStatus")
        };
    }

    private static double SessionReal(ClientSession session, string name) => session.Get(name) switch
    {
        PyReal real => real.Value,
        PyInt integer => integer.Value,
        PyLong integer => integer.Value,
        _ => 0
    };

    private static long? SessionSolarSystemId(ClientSession session)
    {
        var solarSystemId = session.GetInt("solarsystemid2") ?? session.GetInt("solarsystemid");
        return solarSystemId is > 0 ? solarSystemId : null;
    }

    private static long[] ApplySetDiff(IEnumerable<long> current, IEnumerable<long> add, IEnumerable<long> remove)
    {
        var result = current.Where(id => id > 0).ToHashSet();
        result.UnionWith(add.Where(id => id > 0));
        result.ExceptWith(remove);
        return result.Order().Take(MaxAdvertEntities).ToArray();
    }

    private static long[] ReadEntitySet(PyValue source, string fieldName)
    {
        if (!TryGetField(source, [fieldName], out var value) || value is PyNone) return [];
        return TryReadEntityIds(value, out var ids) ? ids.Take(MaxAdvertEntities).ToArray() : [];
    }

    private static void ApplyEntitySets(FleetAdvertPayload payload, FleetAdvertEntitySets sets)
    {
        payload.PublicAllowedEntities = sets.PublicAllowed;
        payload.PublicDisallowedEntities = sets.PublicDisallowed;
        payload.MembergroupsAllowedEntities = sets.MembergroupsAllowed;
        payload.MembergroupsDisallowedEntities = sets.MembergroupsDisallowed;
    }

    private static bool TryReadAdvertEntitySets(PyValue value, out FleetAdvertEntitySets sets)
    {
        if (TryGetField(value, ["allowedEntities", "allowedEntityIDs", "allowed_entities"], out var legacy))
        {
            if (!TryReadEntityIds(legacy, out var allowed))
            {
                sets = new FleetAdvertEntitySets([], [], [], []);
                return false;
            }
            sets = new FleetAdvertEntitySets(allowed.Take(MaxAdvertEntities).ToArray(), [], [], []);
            return true;
        }
        sets = new FleetAdvertEntitySets(
            ReadEntitySet(value, "public_allowedEntities"),
            ReadEntitySet(value, "public_disallowedEntities"),
            ReadEntitySet(value, "membergroups_allowedEntities"),
            ReadEntitySet(value, "membergroups_disallowedEntities"));
        return true;
    }

    private static bool TryReadAdvertEntityDiff(PyValue value, out FleetAdvertEntityDiff diff)
    {
        diff = new FleetAdvertEntityDiff(
            ReadEntitySet(value, "publicToAddToAllowed"),
            ReadEntitySet(value, "publicToRemoveFromAllowed"),
            ReadEntitySet(value, "publicToAddToDisallowed"),
            ReadEntitySet(value, "publicToRemoveFromDisallowed"),
            ReadEntitySet(value, "membergroupsToAddToAllowed"),
            ReadEntitySet(value, "membergroupsToRemoveFromAllowed"),
            ReadEntitySet(value, "membergroupsToAddToDisallowed"),
            ReadEntitySet(value, "membergroupsToRemoveFromDisallowed"));
        return true;
    }

    private static PyValue AdvertValue(FleetAdvertPayload advert) => Py.Dict(
        ("fleetID", new PyLong(advert.FleetId)),
        ("leader", Py.KeyVal(
            ("charID", new PyLong(advert.Leader.CharacterId)),
            ("corpID", advert.Leader.CorporationId is { } corp ? new PyLong(corp) : PyNone.Instance),
            ("allianceID", advert.Leader.AllianceId is { } alliance ? new PyLong(alliance) : PyNone.Instance),
            ("warFactionID", advert.Leader.WarFactionId is { } faction ? new PyLong(faction) : PyNone.Instance),
            ("securityStatus", new PyReal(advert.Leader.SecurityStatus)))),
        ("solarSystemID", advert.SolarSystemId is { } system ? new PyLong(system) : PyNone.Instance),
        ("numMembers", new PyInt(advert.NumMembers)),
        ("advertTime", new PyLong(advert.AdvertTime)),
        ("dateCreated", new PyLong(advert.DateCreated)),
        ("fleetName", new PyWString(advert.FleetName)),
        ("description", new PyWString(advert.Description)),
        ("inviteScope", new PyInt(advert.InviteScope)),
        ("activityValue", advert.ActivityValue is { } activity ? new PyInt(activity) : PyNone.Instance),
        ("useAdvanceOptions", advert.UseAdvanceOptions ? PyBool.True : PyBool.False),
        ("newPlayerFriendly", advert.NewPlayerFriendly ? PyBool.True : PyBool.False),
        ("public_minStanding", advert.PublicMinStanding is { } publicStanding ? new PyReal(publicStanding) : PyNone.Instance),
        ("public_minSecurity", advert.PublicMinSecurity is { } publicSecurity ? new PyReal(publicSecurity) : PyNone.Instance),
        ("public_allowedEntities", Eve.Services.InventoryWire.Set(advert.PublicAllowedEntities.Select(id => (PyValue)new PyLong(id)))),
        ("public_disallowedEntities", Eve.Services.InventoryWire.Set(advert.PublicDisallowedEntities.Select(id => (PyValue)new PyLong(id)))),
        ("membergroups_minStanding", advert.MembergroupsMinStanding is { } groupStanding ? new PyReal(groupStanding) : PyNone.Instance),
        ("membergroups_minSecurity", advert.MembergroupsMinSecurity is { } groupSecurity ? new PyReal(groupSecurity) : PyNone.Instance),
        ("membergroups_allowedEntities", Eve.Services.InventoryWire.Set(advert.MembergroupsAllowedEntities.Select(id => (PyValue)new PyLong(id)))),
        ("membergroups_disallowedEntities", Eve.Services.InventoryWire.Set(advert.MembergroupsDisallowedEntities.Select(id => (PyValue)new PyLong(id)))),
        ("joinNeedsApproval", advert.JoinNeedsApproval ? PyBool.True : PyBool.False),
        ("hideInfo", advert.HideInfo ? PyBool.True : PyBool.False),
        ("updateOnBossChange", advert.UpdateOnBossChange ? PyBool.True : PyBool.False),
        ("advertJoinLimit", advert.AdvertJoinLimit is { } joinLimit ? new PyInt(joinLimit) : PyNone.Instance));

    private async Task<long?> LockFleetAdvertManagerAsync(System.Data.Common.DbConnection connection,
        System.Data.Common.DbTransaction transaction, long characterId, CancellationToken cancellationToken)
    {
        var value = await ScalarAsync(connection, transaction,
            $"SELECT fleet_id FROM {_schema}.fleet_members WHERE character_id = $1", cancellationToken, characterId);
        if (value is null or DBNull) return null;
        var fleetId = Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
        await using (var fleet = Command(connection, transaction,
            $"SELECT fleet_id FROM {_schema}.fleets WHERE fleet_id = $1 FOR UPDATE", fleetId))
            if (await fleet.ExecuteScalarAsync(cancellationToken) is null) return null;
        var member = await MemberAsync(connection, transaction, fleetId, characterId, cancellationToken, forUpdate: true);
        return member is { } current && (current.Role == Leader || HasBossJob(current.Job)) ? fleetId : null;
    }

    private async Task<bool> AdvertIsActiveAsync(System.Data.Common.DbConnection connection,
        System.Data.Common.DbTransaction transaction, long fleetId, CancellationToken cancellationToken)
    {
        var value = await ScalarAsync(connection, transaction,
            $"SELECT advert_active FROM {_schema}.fleets WHERE fleet_id = $1", cancellationToken, fleetId);
        return value is true;
    }

    private static bool IsVisibleTo(FleetAdvertPayload advert, ClientSession session)
    {
        var corporationId = session.GetInt("corpid") ?? session.GetInt("corporationID");
        var allianceId = session.GetInt("allianceid") ?? session.GetInt("allianceID");
        var warFactionId = session.GetInt("warfactionid") ?? session.GetInt("warFactionID");
        if ((advert.InviteScope & InvitePublicOpen) != 0) return true;
        if ((advert.InviteScope & InviteCorp) != 0 && corporationId is { } corp &&
            advert.Leader.CorporationId == corp) return true;
        if ((advert.InviteScope & InviteAlliance) != 0 && advert.Leader.AllianceId is { } leaderAlliance &&
            allianceId == leaderAlliance) return true;
        if ((advert.InviteScope & InviteMilitia) != 0 && advert.Leader.WarFactionId is { } leaderFaction &&
            warFactionId == leaderFaction) return true;
        return (advert.InviteScope & InvitePublic) != 0;
    }

    private static PyValue AdvertValue(FleetAdvert advert) => AdvertValue(advert.Payload);

    private static bool TryAdvertInput(PyValue value, out FleetAdvertInput input)
    {
        PyValue Field(string[] names) => TryGetField(value, names, out var found) ? found : PyNone.Instance;
        string Text(string[] names, int maximumLength)
        {
            var text = Field(names) switch
            {
                PyStr narrow => narrow.Value,
                PyWString wide => wide.Value,
                _ => ""
            };
            return text.Length > maximumLength ? text[..maximumLength] : text;
        }
        long Integer(PyValue field, long fallback = 0) => field switch
        {
            PyInt number => number.Value,
            PyLong number => number.Value,
            PyReal number when double.IsFinite(number.Value) && number.Value >= long.MinValue && number.Value <= long.MaxValue =>
                (long)number.Value,
            PyStr text when long.TryParse(text.Value, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var parsed) => parsed,
            PyWString text when long.TryParse(text.Value, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => fallback
        };
        double? OptionalNumber(string[] names)
        {
            var field = Field(names);
            if (field is PyNone) return null;
            return field switch
            {
                PyReal number when double.IsFinite(number.Value) => number.Value,
                PyInt number => number.Value,
                PyLong number => number.Value,
                PyStr text when double.TryParse(text.Value, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var parsed) => parsed,
                PyWString text when double.TryParse(text.Value, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var parsed) => parsed,
                _ => null
            };
        }
        bool Boolean(string[] names, bool fallback)
        {
            var field = Field(names);
            return field switch
            {
                PyNone => fallback,
                PyBool flag => flag.Value,
                PyInt number => number.Value != 0,
                PyLong number => number.Value != 0,
                PyReal number => number.Value != 0,
                PyStr text => text.Value.Length > 0,
                PyWString text => text.Value.Length > 0,
                _ => fallback
            };
        }

        if (!TryReadAdvertEntitySets(value, out var sets))
        {
            input = new FleetAdvertInput("", "", 0, null, false, false, null, null,
                [], [], null, null, [], [], false, false, true, null);
            return false;
        }
        var activity = Field(["activityValue"]);
        int? activityValue = activity is PyNone
            ? null
            : checked((int)Math.Clamp(Integer(activity), int.MinValue, int.MaxValue));
        var joinLimit = Field(["advertJoinLimit"]);
        int? advertJoinLimit = joinLimit is PyNone
            ? null
            : checked((int)Math.Clamp(Integer(joinLimit, 1), 1, MaxMembers));

        input = new FleetAdvertInput(
            Text(["fleetName"], 32),
            Text(["description"], 250),
            checked((int)Math.Clamp(Integer(Field(["inviteScope"]), 0), 0, int.MaxValue)),
            activityValue,
            Boolean(["useAdvanceOptions"], false),
            Boolean(["newPlayerFriendly"], false),
            OptionalNumber(["public_minStanding"]),
            OptionalNumber(["public_minSecurity"]),
            sets.PublicAllowed,
            sets.PublicDisallowed,
            OptionalNumber(["membergroups_minStanding"]),
            OptionalNumber(["membergroups_minSecurity"]),
            sets.MembergroupsAllowed,
            sets.MembergroupsDisallowed,
            Boolean(["joinNeedsApproval"], false),
            Boolean(["hideInfo"], false),
            Boolean(["updateOnBossChange"], true),
            advertJoinLimit);
        return true;
    }

    private static bool TryOptionalText(PyValue data, string[] names, ref string value)
    {
        if (!TryGetField(data, names, out var field)) return true;
        return TryReadText(field, out value);
    }

    private static bool TryOptionalInt(PyValue data, string[] names, ref int value, int minimum, int maximum)
    {
        if (!TryGetField(data, names, out var field)) return true;
        if (!TryReadLong(field, out var number) || number < minimum || number > maximum) return false;
        value = (int)number;
        return true;
    }

    private static bool TryOptionalBool(PyValue data, string[] names, ref bool value)
    {
        if (!TryGetField(data, names, out var field)) return true;
        return TryReadBool(field, out value);
    }

    private static bool TryOptionalDouble(PyValue data, string[] names, ref double value)
    {
        if (!TryGetField(data, names, out var field)) return true;
        if (field is PyReal real) value = real.Value;
        else if (TryReadLong(field, out var number)) value = number;
        else return false;
        return double.IsFinite(value);
    }

    private static bool TryGetField(PyValue data, string[] names, out PyValue value)
    {
        var entries = data switch
        {
            PyDict dict => dict.Entries,
            PyObjectEx objectEx => objectEx.Dict,
            PyObject { Name: PyStr { Value: "util.KeyVal" }, Args: PyDict keyVal } => keyVal.Entries,
            _ => Array.Empty<KeyValuePair<PyValue, PyValue>>()
        };
        foreach (var entry in entries)
        {
            var key = entry.Key switch
            {
                PyStr text => text.Value,
                PyWString text => text.Value,
                _ => null
            };
            if (key is not null && names.Contains(key, StringComparer.OrdinalIgnoreCase))
            {
                value = entry.Value;
                return true;
            }
        }
        value = PyNone.Instance;
        return false;
    }

    private static bool TryReadText(PyValue value, out string text)
    {
        switch (value)
        {
            case PyStr narrow: text = narrow.Value; return true;
            case PyWString wide: text = wide.Value; return true;
            default: text = ""; return false;
        }
    }

    private static bool TryReadLong(PyValue value, out long number)
    {
        switch (value)
        {
            case PyInt integer: number = integer.Value; return true;
            case PyLong integer: number = integer.Value; return true;
            default: number = 0; return false;
        }
    }

    private static bool TryReadBool(PyValue value, out bool boolean)
    {
        switch (value)
        {
            case PyBool flag: boolean = flag.Value; return true;
            case PyInt integer: boolean = integer.Value != 0; return true;
            case PyLong integer: boolean = integer.Value != 0; return true;
            default: boolean = false; return false;
        }
    }

    private static bool TryReadEntityIds(PyValue value, out IReadOnlyList<long> ids)
    {
        IReadOnlyList<PyValue>? values = value switch
        {
            PyList list => list.Items,
            PyTuple tuple => tuple.Items,
            _ => null
        };
        if (values is null && value is PyObjectEx { Kind: ObjectExKind.Ex1, Header: PyTuple header } &&
            header.Items.Count == 2 && header.Items[0] is PyToken { Value: "__builtin__.set" } &&
            header.Items[1] is PyTuple setArgs && setArgs.Items.Count == 1)
            values = setArgs.Items[0] switch
            {
                PyList list => list.Items,
                PyTuple tuple => tuple.Items,
                _ => null
            };
        if (values is null)
        {
            ids = [];
            return false;
        }
        var parsed = new List<long>(values.Count);
        foreach (var item in values)
        {
            if (!TryReadLong(item, out var id) || id <= 0)
            {
                ids = [];
                return false;
            }
            parsed.Add(id);
        }
        ids = parsed.Distinct().Order().ToArray();
        return true;
    }

    private static async Task<object?> ScalarAsync(System.Data.Common.DbConnection connection,
        System.Data.Common.DbTransaction? transaction, string sql, CancellationToken cancellationToken,
        params object?[] args)
    {
        await using var command = Command(connection, transaction, sql, args);
        return await command.ExecuteScalarAsync(cancellationToken);
    }
}
