using Eve.Macho;
using Eve.Marshal;
using Eve.Space;

namespace Eve.Services.Space;

/// <summary>
/// The <c>beyonce</c> service the client binds to for the solar system it is in: binding it starts the destiny stream, and its commands steer
/// the ship. The simulation does the work (<see cref="SpaceSessions"/>); this reads the client's calls.
/// </summary>
public sealed record FleetMemberSnapshot(long CharacterId, long WingId, long SquadId, int Role, int Job,
    bool AcceptsFleetWarp = true, bool AcceptsConduitJumps = true, bool AcceptsFleetRegroups = true, long? ShipTypeId = null)
{
    public long FleetId { get; init; }
}

public sealed class FleetLoginHook(Func<long, CancellationToken, Task<FleetMemberSnapshot?>> memberForCharacter)
    : Eve.Services.ICharacterEntryHook
{
    public async ValueTask<IReadOnlyList<(string Name, Eve.Marshal.PyValue Value)>> GetSessionAttributesAsync(
        Eve.Services.ClientSession session, long characterId, CancellationToken cancellationToken)
    {
        var member = await memberForCharacter(characterId, cancellationToken);
        return member is not { FleetId: > 0 }
            ? [
                ("fleetid", Eve.Marshal.PyNone.Instance),
                ("fleetrole", Eve.Marshal.PyNone.Instance),
                ("wingid", Eve.Marshal.PyNone.Instance),
                ("squadid", Eve.Marshal.PyNone.Instance),
            ]
            : [
                ("fleetid", new Eve.Marshal.PyLong(member.FleetId)),
                ("fleetrole", new Eve.Marshal.PyInt(member.Role)),
                ("wingid", member.WingId > 0 ? new Eve.Marshal.PyLong(member.WingId) : Eve.Marshal.PyNone.Instance),
                ("squadid", member.SquadId > 0 ? new Eve.Marshal.PyLong(member.SquadId) : Eve.Marshal.PyNone.Instance),
            ];
    }

    public ValueTask OnCharacterEnteredAsync(
        Eve.Services.ClientSession session, long characterId, CancellationToken cancellationToken) =>
        ValueTask.CompletedTask;
}


public sealed record FleetSquadSnapshot(long SquadId, string Name);
public sealed record FleetWingSnapshot(long WingId, string Name, IReadOnlyList<FleetSquadSnapshot> Squads);
public sealed record FleetJoinRequest(long CharacterId);
public sealed record FleetSnapshot(long FleetId, long? AutoJoinSquadId, IReadOnlyList<FleetMemberSnapshot> Members,
    IReadOnlyList<FleetWingSnapshot> Wings, IReadOnlyList<FleetJoinRequest> JoinRequests,
    int MaxMemberCount = FleetStore.MaxMembers, bool IsFreeMove = false, bool IsRegistered = false, string Motd = "");
public sealed record FleetAdvertLeaderProfile(long? CorporationId, long? AllianceId, long? WarFactionId, double SecurityStatus);

/// <summary>Durable fleet membership and hierarchy state. The fleet schema is supplied by the host migration.</summary>
public sealed class FleetStore(System.Data.Common.DbDataSource source, string schema = "fleet")
{
    public const string DefaultSchema = "fleet";
    public const long NoUnit = -1;
    public const int Leader = 1;
    public const int WingCommander = 2;
    public const int SquadCommander = 3;
    public const int Member = 4;
    public const int CreatorJob = 2;
    public const int MaxMembers = 256;
    public const int MaxWings = 25;
    public const int MaxSquadsPerWing = 25;
    public const int MaxNameLength = 10;
    private const int InviteCorp = 1;
    private const int InviteAlliance = 2;
    private const int InviteMilitia = 4;
    private const int InvitePublic = 8;
    private const int InvitePublicOpen = 16;
    private const int MaxAdvertEntities = 1000;


    public static readonly Eve.Data.Migration FleetFinderAllowedEntitiesMigration = new(
        "005_fleet_finder_allowed_entities",
        """
        CREATE TABLE fleet_advert_allowed_entities (
            fleet_id bigint NOT NULL REFERENCES fleets(fleet_id) ON DELETE CASCADE,
            entity_id bigint NOT NULL CHECK (entity_id > 0),
            PRIMARY KEY (fleet_id, entity_id)
        );
        """);

    public static readonly Eve.Data.Migration FleetFinderAdvertPayloadMigration = new(
        "006_fleet_finder_advert_payload",
        """
        ALTER TABLE fleets ADD COLUMN IF NOT EXISTS date_created_filetime bigint;
        UPDATE fleets
        SET date_created_filetime = (extract(epoch FROM clock_timestamp()) * 10000000)::bigint + 116444736000000000
        WHERE date_created_filetime IS NULL;
        ALTER TABLE fleets ALTER COLUMN date_created_filetime SET DEFAULT 0;
        ALTER TABLE fleets ALTER COLUMN date_created_filetime SET NOT NULL;
        ALTER TABLE fleets ADD COLUMN IF NOT EXISTS advert_payload jsonb;

        UPDATE fleets f
        SET advert_payload = jsonb_build_object(
            'fleetID', f.fleet_id,
            'leader', jsonb_build_object('charID', COALESCE(f.leader_character_id, 0), 'corpID', NULL,
                'allianceID', NULL, 'warFactionID', NULL, 'securityStatus', 0),
            'solarSystemID', NULL,
            'numMembers', (SELECT count(*) FROM fleet_members m WHERE m.fleet_id = f.fleet_id),
            'advertTime', 0,
            'dateCreated', f.date_created_filetime,
            'fleetName', f.fleet_name,
            'description', f.description,
            'inviteScope', f.invite_scope,
            'activityValue', NULL,
            'useAdvanceOptions', false,
            'newPlayerFriendly', false,
            'public_minStanding', CASE WHEN f.public_min_standing <= -10 THEN NULL ELSE f.public_min_standing END,
            'public_minSecurity', NULL,
            'public_allowedEntities', COALESCE((SELECT jsonb_agg(e.entity_id ORDER BY e.entity_id)
                FROM fleet_advert_allowed_entities e WHERE e.fleet_id = f.fleet_id), '[]'::jsonb),
            'public_disallowedEntities', '[]'::jsonb,
            'membergroups_minStanding', NULL,
            'membergroups_minSecurity', NULL,
            'membergroups_allowedEntities', '[]'::jsonb,
            'membergroups_disallowedEntities', '[]'::jsonb,
            'joinNeedsApproval', f.join_needs_approval,
            'hideInfo', f.hide_info,
            'updateOnBossChange', true,
            'advertJoinLimit', f.advert_join_limit)
        WHERE f.advert_active AND f.advert_payload IS NULL;
        DROP TABLE fleet_advert_allowed_entities;
        """);

    private readonly string _schema = System.Text.RegularExpressions.Regex.IsMatch(schema, "^[a-z_][a-z0-9_]*$")
        ? schema : throw new ArgumentException("not a usable fleet schema name", nameof(schema));

    public static bool IsValidAssignment(int role, long wingId, long squadId) => role switch
    {
        Leader => wingId == NoUnit && squadId == NoUnit,
        WingCommander => wingId > 0 && squadId == NoUnit,
        SquadCommander => wingId > 0 && squadId > 0,
        Member => wingId == NoUnit && squadId == NoUnit || wingId > 0 && (squadId == NoUnit || squadId > 0),
        _ => false,
    };

    public static bool HasBossJob(int job) => (job & CreatorJob) != 0;

    public static bool IsValidName(string? name) => name is not null && name.Length <= MaxNameLength;

    private static long CurrentFileTime() => DateTime.UtcNow.ToFileTimeUtc();

    public async Task<long> CreateAsync(long leaderCharacterId, CancellationToken cancellationToken = default)
    {
        if (leaderCharacterId <= 0) return 0;
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var create = Command(connection, transaction,
            $"INSERT INTO {_schema}.fleets (leader_character_id, date_created_filetime) VALUES ($1, $2) RETURNING fleet_id",
            leaderCharacterId, CurrentFileTime());
        var fleetId = Convert.ToInt64(await create.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture);
        await using var member = Command(connection, transaction,
            $"INSERT INTO {_schema}.fleet_members (fleet_id, character_id, wing_id, squad_id, role, job) VALUES ($1, $2, -1, -1, $3, $4)",
            fleetId, leaderCharacterId, Leader, CreatorJob);
        await member.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return fleetId;
    }

    public async Task<bool> ExistsAsync(long fleetId, CancellationToken cancellationToken = default)
    {
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var command = Command(connection, null,
            $"SELECT EXISTS (SELECT 1 FROM {_schema}.fleets WHERE fleet_id = $1)", fleetId);
        return Convert.ToBoolean(await command.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture);
    }

    public async Task<FleetSnapshot?> SnapshotAsync(long fleetId, CancellationToken cancellationToken = default)
    {
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        long? autoJoinSquadId;
        int maxMemberCount;
        bool isFreeMove, isRegistered;
        string motd;
        await using (var fleet = Command(connection, null,
            $"SELECT auto_join_squad_id, max_member_count, is_free_move, advert_active, motd FROM {_schema}.fleets WHERE fleet_id = $1", fleetId))
        await using (var reader = await fleet.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken)) return null;
            autoJoinSquadId = reader.IsDBNull(0) ? null : reader.GetInt64(0);
            maxMemberCount = reader.GetInt16(1);
            isFreeMove = reader.GetBoolean(2);
            isRegistered = reader.GetBoolean(3);
            motd = reader.GetString(4);
        }

        var members = new List<FleetMemberSnapshot>();
        await using (var query = Command(connection, null,
            $"SELECT character_id, wing_id, squad_id, role, job, accepts_fleet_warp, accepts_conduit_jumps, accepts_fleet_regroups, ship_type_id FROM {_schema}.fleet_members WHERE fleet_id = $1 ORDER BY joined_at, character_id", fleetId))
        await using (var reader = await query.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
                members.Add(new FleetMemberSnapshot(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2),
                    reader.GetInt16(3), reader.GetInt16(4), reader.GetBoolean(5), reader.GetBoolean(6),
                    reader.GetBoolean(7), reader.IsDBNull(8) ? null : reader.GetInt64(8)));
        }

        var wingRows = new List<(long Id, string Name)>();
        await using (var query = Command(connection, null,
            $"SELECT wing_id, name FROM {_schema}.fleet_wings WHERE fleet_id = $1 ORDER BY position, wing_id", fleetId))
        await using (var reader = await query.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken)) wingRows.Add((reader.GetInt64(0), reader.GetString(1)));
        }

        var squadRows = new List<(long Id, long WingId, string Name)>();
        await using (var query = Command(connection, null,
            $"SELECT squad_id, wing_id, name FROM {_schema}.fleet_squads WHERE fleet_id = $1 ORDER BY position, squad_id", fleetId))
        await using (var reader = await query.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken)) squadRows.Add((reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2)));
        }

        var requests = new List<FleetJoinRequest>();
        await using (var query = Command(connection, null,
            $"SELECT character_id FROM {_schema}.fleet_join_requests WHERE fleet_id = $1 ORDER BY created_at, character_id", fleetId))
        await using (var reader = await query.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken)) requests.Add(new FleetJoinRequest(reader.GetInt64(0)));
        }

        var wings = wingRows.Select(wing => new FleetWingSnapshot(wing.Id, wing.Name,
            squadRows.Where(squad => squad.WingId == wing.Id)
                .Select(squad => new FleetSquadSnapshot(squad.Id, squad.Name)).ToArray())).ToArray();
        return new FleetSnapshot(fleetId, autoJoinSquadId, members, wings, requests,
            maxMemberCount, isFreeMove, isRegistered, motd);
    }

    public async Task<bool> ApplyFleetSetupAsync(long fleetId, long issuerId,
        System.Text.Json.Nodes.JsonObject setup, CancellationToken cancellationToken = default)
    {
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var lockFleet = Command(connection, transaction,
            $"SELECT fleet_id FROM {_schema}.fleets WHERE fleet_id = $1 FOR UPDATE", fleetId))
            if (await lockFleet.ExecuteScalarAsync(cancellationToken) is null) return false;
        if (!await CanManageAsync(connection, transaction, fleetId, issuerId, cancellationToken)) return false;

        static string Text(System.Text.Json.Nodes.JsonNode? value) =>
            value is System.Text.Json.Nodes.JsonValue json && json.TryGetValue<string>(out var text) ? text : "";
        static int Integer(System.Text.Json.Nodes.JsonNode? value) =>
            int.TryParse(value?.ToString(), System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var number) ? number : 0;
        static bool Truthy(System.Text.Json.Nodes.JsonNode? value)
        {
            if (value is null) return false;
            if (value is System.Text.Json.Nodes.JsonValue json)
            {
                if (json.TryGetValue<bool>(out var boolean)) return boolean;
                if (json.TryGetValue<string>(out var text)) return text.Length > 0;
                if (double.TryParse(value.ToString(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var number))
                    return number != 0;
            }
            return true;
        }

        var rawWings = setup["wingsInfo"] switch
        {
            System.Text.Json.Nodes.JsonObject objectWings => objectWings.Select(entry => entry.Value).ToArray(),
            System.Text.Json.Nodes.JsonArray arrayWings => arrayWings.ToArray(),
            _ => []
        };
        var desiredWings = rawWings.OfType<System.Text.Json.Nodes.JsonObject>()
            .Select(wing =>
            {
                var squads = wing["squadNames"] is System.Text.Json.Nodes.JsonArray squadNames
                    ? squadNames.Select(name => Text(name) is { Length: > MaxNameLength } text ? text[..MaxNameLength] : Text(name)).ToArray()
                    : [];
                var name = Text(wing["wingName"]);
                return (Index: Integer(wing["wingIdx"]), Name: name.Length > MaxNameLength ? name[..MaxNameLength] : name, Squads: squads);
            })
            .OrderBy(wing => wing.Index)
            .ToArray();

        var memberWings = new HashSet<long>();
        var memberSquads = new HashSet<long>();
        await using (var members = Command(connection, transaction,
            $"SELECT wing_id, squad_id FROM {_schema}.fleet_members WHERE fleet_id = $1", fleetId))
        await using (var reader = await members.ExecuteReaderAsync(cancellationToken))
            while (await reader.ReadAsync(cancellationToken))
            {
                memberWings.Add(reader.GetInt64(0));
                memberSquads.Add(reader.GetInt64(1));
            }

        var wings = new List<(long WingId, string Name, List<FleetSquadSnapshot> Squads)>();
        await using (var wingQuery = Command(connection, transaction,
            $"SELECT wing_id, name FROM {_schema}.fleet_wings WHERE fleet_id = $1 ORDER BY position, wing_id", fleetId))
        await using (var reader = await wingQuery.ExecuteReaderAsync(cancellationToken))
            while (await reader.ReadAsync(cancellationToken))
                wings.Add((reader.GetInt64(0), reader.GetString(1), []));

        var squadsByWing = wings.ToDictionary(wing => wing.WingId, _ => new List<FleetSquadSnapshot>());
        await using (var squadQuery = Command(connection, transaction,
            $"SELECT squad_id, wing_id, name FROM {_schema}.fleet_squads WHERE fleet_id = $1 ORDER BY position, squad_id", fleetId))
        await using (var reader = await squadQuery.ExecuteReaderAsync(cancellationToken))
            while (await reader.ReadAsync(cancellationToken))
                if (squadsByWing.TryGetValue(reader.GetInt64(1), out var squads))
                    squads.Add(new FleetSquadSnapshot(reader.GetInt64(0), reader.GetString(2)));
        for (var i = 0; i < wings.Count; i++)
            wings[i] = (wings[i].WingId, wings[i].Name, squadsByWing[wings[i].WingId]);

        while (wings.Count < desiredWings.Length)
        {
            var position = await CountAsync(connection, transaction,
                $"SELECT COUNT(*) FROM {_schema}.fleet_wings WHERE fleet_id = $1", cancellationToken, fleetId);
            if (position >= MaxWings) break;
            await using var createWing = Command(connection, transaction,
                $"INSERT INTO {_schema}.fleet_wings (fleet_id, name, position) VALUES ($1, '', $2) RETURNING wing_id",
                fleetId, checked((int)position));
            var wingId = Convert.ToInt64(await createWing.ExecuteScalarAsync(cancellationToken),
                System.Globalization.CultureInfo.InvariantCulture);
            wings.Add((wingId, "", []));
            squadsByWing[wingId] = [];
        }

        for (var index = 0; index < desiredWings.Length && index < wings.Count; index++)
        {
            var desired = desiredWings[index];
            var current = wings[index];
            if (!string.Equals(current.Name, desired.Name, StringComparison.Ordinal))
            {
                await using var renameWing = Command(connection, transaction,
                    $"UPDATE {_schema}.fleet_wings SET name = $1 WHERE fleet_id = $2 AND wing_id = $3",
                    desired.Name, fleetId, current.WingId);
                if (await renameWing.ExecuteNonQueryAsync(cancellationToken) != 1) return false;
                current = (current.WingId, desired.Name, current.Squads);
            }

            var squads = current.Squads;
            while (squads.Count < desired.Squads.Length)
            {
                var position = await CountAsync(connection, transaction,
                    $"SELECT COUNT(*) FROM {_schema}.fleet_squads WHERE fleet_id = $1 AND wing_id = $2",
                    cancellationToken, fleetId, current.WingId);
                if (position >= MaxSquadsPerWing) break;
                await using var createSquad = Command(connection, transaction,
                    $"INSERT INTO {_schema}.fleet_squads (fleet_id, wing_id, name, position) VALUES ($1, $2, '', $3) RETURNING squad_id",
                    fleetId, current.WingId, checked((int)position));
                var squadId = Convert.ToInt64(await createSquad.ExecuteScalarAsync(cancellationToken),
                    System.Globalization.CultureInfo.InvariantCulture);
                squads.Add(new FleetSquadSnapshot(squadId, ""));
            }
            for (var squadIndex = 0; squadIndex < desired.Squads.Length && squadIndex < squads.Count; squadIndex++)
            {
                var squad = squads[squadIndex];
                var desiredName = desired.Squads[squadIndex];
                if (string.Equals(squad.Name, desiredName, StringComparison.Ordinal)) continue;
                await using var renameSquad = Command(connection, transaction,
                    $"UPDATE {_schema}.fleet_squads SET name = $1 WHERE fleet_id = $2 AND squad_id = $3",
                    desiredName, fleetId, squad.SquadId);
                if (await renameSquad.ExecuteNonQueryAsync(cancellationToken) != 1) return false;
                squads[squadIndex] = new FleetSquadSnapshot(squad.SquadId, desiredName);
            }
            for (var squadIndex = squads.Count - 1; squadIndex >= desired.Squads.Length; squadIndex--)
            {
                var squad = squads[squadIndex];
                if (memberSquads.Contains(squad.SquadId)) continue;
                await using var deleteSquad = Command(connection, transaction,
                    $"DELETE FROM {_schema}.fleet_squads WHERE fleet_id = $1 AND squad_id = $2", fleetId, squad.SquadId);
                if (await deleteSquad.ExecuteNonQueryAsync(cancellationToken) == 1)
                    squads.RemoveAt(squadIndex);
            }
            wings[index] = (current.WingId, current.Name, squads);
        }

        for (var index = wings.Count - 1; index >= desiredWings.Length; index--)
        {
            var wing = wings[index];
            if (memberWings.Contains(wing.WingId)) continue;
            await using var deleteWing = Command(connection, transaction,
                $"DELETE FROM {_schema}.fleet_wings WHERE fleet_id = $1 AND wing_id = $2", fleetId, wing.WingId);
            if (await deleteWing.ExecuteNonQueryAsync(cancellationToken) == 1)
                wings.RemoveAt(index);
        }

        if (setup.TryGetPropertyValue("defaultSquad", out var defaultNode) &&
            defaultNode is System.Text.Json.Nodes.JsonArray defaultSquad)
        {
            var targetWingName = defaultSquad.Count > 0 ? Text(defaultSquad[0]).ToLowerInvariant() : "";
            var squadIndex = defaultSquad.Count > 1 ? Integer(defaultSquad[1]) : -1;
            long? targetSquadId = null;
            var targetWing = wings.FirstOrDefault(wing =>
                string.Equals(wing.Name, targetWingName, StringComparison.OrdinalIgnoreCase));
            if (squadIndex >= 0 && targetWing.Squads is not null && squadIndex < targetWing.Squads.Count)
                targetSquadId = targetWing.Squads[squadIndex].SquadId;
            await using var updateAutoJoin = Command(connection, transaction,
                $"UPDATE {_schema}.fleets SET auto_join_squad_id = $1 WHERE fleet_id = $2", targetSquadId, fleetId);
            if (await updateAutoJoin.ExecuteNonQueryAsync(cancellationToken) != 1) return false;
        }
        if (setup.TryGetPropertyValue("motd", out var motd))
        {
            await using var updateMotd = Command(connection, transaction,
                $"UPDATE {_schema}.fleets SET motd = $1 WHERE fleet_id = $2", Text(motd), fleetId);
            if (await updateMotd.ExecuteNonQueryAsync(cancellationToken) != 1) return false;
        }
        if (setup.TryGetPropertyValue("isFreeMove", out var freeMove))
        {
            await using var updateFreeMove = Command(connection, transaction,
                $"UPDATE {_schema}.fleets SET is_free_move = $1 WHERE fleet_id = $2", Truthy(freeMove), fleetId);
            if (await updateFreeMove.ExecuteNonQueryAsync(cancellationToken) != 1) return false;
        }
        if (setup.TryGetPropertyValue("maxFleetSize", out var maxFleetSize) &&
            Integer(maxFleetSize) is var requestedMax && requestedMax > 0)
        {
            if (requestedMax > MaxMembers) return false;
            await using var updateMax = Command(connection, transaction,
                $"UPDATE {_schema}.fleets SET max_member_count = $1 WHERE fleet_id = $2",
                checked((short)requestedMax), fleetId);
            if (await updateMax.ExecuteNonQueryAsync(cancellationToken) != 1) return false;
        }

        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<FleetMemberSnapshot?> MemberForCharacterAsync(long characterId, CancellationToken cancellationToken = default)
    {
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var command = Command(connection, null,
            $"SELECT fleet_id, character_id, wing_id, squad_id, role, job FROM {_schema}.fleet_members WHERE character_id = $1",
            characterId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new FleetMemberSnapshot(reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3), reader.GetInt16(4), reader.GetInt16(5))
            {
                FleetId = reader.GetInt64(0),
            }
            : null;
    }

    public async Task<long?> FleetIdForCharacterAsync(long characterId, CancellationToken cancellationToken = default)
    {
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        return await FleetIdForCharacterAsync(connection, null, characterId, cancellationToken);
    }

    public async Task<bool> InviteAsync(long fleetId, long inviterId, long characterId, long wingId, long squadId, int role,
        CancellationToken cancellationToken = default)
    {
        if (characterId <= 0 || characterId == inviterId || role == Leader || !IsValidAssignment(role, wingId, squadId)) return false;
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var memberLimit = await MemberLimitAsync(connection, transaction, fleetId, lockFleet: true, cancellationToken);
        if (memberLimit is null ||
            !await CanManageAsync(connection, transaction, fleetId, inviterId, cancellationToken, lockMember: true) ||
            await MemberAsync(connection, transaction, fleetId, characterId, cancellationToken) is not null ||
            await FleetIdForCharacterAsync(connection, transaction, characterId, cancellationToken) is not null ||
            !await PositionExistsAsync(connection, transaction, fleetId, wingId, squadId, cancellationToken) ||
            await CountAsync(connection, transaction, $"SELECT COUNT(*) FROM {_schema}.fleet_members WHERE fleet_id = $1", cancellationToken, fleetId) >= memberLimit)
            return false;

        await using var command = Command(connection, transaction,
            $"INSERT INTO {_schema}.fleet_invitations (fleet_id, character_id, inviter_id, wing_id, squad_id, role) VALUES ($1, $2, $3, $4, $5, $6) ON CONFLICT (fleet_id, character_id) DO UPDATE SET inviter_id = EXCLUDED.inviter_id, wing_id = EXCLUDED.wing_id, squad_id = EXCLUDED.squad_id, role = EXCLUDED.role, created_at = now()",
            fleetId, characterId, inviterId, wingId, squadId, role);
        await command.ExecuteNonQueryAsync(cancellationToken);
        await using var removeRequest = Command(connection, transaction,
            $"DELETE FROM {_schema}.fleet_join_requests WHERE fleet_id = $1 AND character_id = $2", fleetId, characterId);
        await removeRequest.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<FleetMemberSnapshot?> JoinAsync(long fleetId, long characterId, CancellationToken cancellationToken = default)
    {
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var memberLimit = await MemberLimitAsync(connection, transaction, fleetId, lockFleet: true, cancellationToken);
        if (memberLimit is null) return null;
        if (await FleetIdForCharacterAsync(connection, transaction, characterId, cancellationToken) is not null)
        {
            await using var removeInvitation = Command(connection, transaction,
                $"DELETE FROM {_schema}.fleet_invitations WHERE fleet_id = $1 AND character_id = $2", fleetId, characterId);
            await removeInvitation.ExecuteNonQueryAsync(cancellationToken);
            await using var removeRequest = Command(connection, transaction,
                $"DELETE FROM {_schema}.fleet_join_requests WHERE fleet_id = $1 AND character_id = $2", fleetId, characterId);
            await removeRequest.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return null;
        }
        if (await CountAsync(connection, transaction, $"SELECT COUNT(*) FROM {_schema}.fleet_members WHERE fleet_id = $1", cancellationToken, fleetId) >= memberLimit)
            return null;

        long wingId, squadId;
        int role;
        await using (var invitation = Command(connection, transaction,
            $"SELECT wing_id, squad_id, role FROM {_schema}.fleet_invitations WHERE fleet_id = $1 AND character_id = $2 FOR UPDATE",
            fleetId, characterId))
        await using (var reader = await invitation.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken)) return null;
            wingId = reader.GetInt64(0);
            squadId = reader.GetInt64(1);
            role = reader.GetInt16(2);
        }
        if (!IsValidAssignment(role, wingId, squadId) ||
            !await PositionExistsAsync(connection, transaction, fleetId, wingId, squadId, cancellationToken)) return null;

        await using (var add = Command(connection, transaction,
            $"INSERT INTO {_schema}.fleet_members (fleet_id, character_id, wing_id, squad_id, role, job) VALUES ($1, $2, $3, $4, $5, 0) ON CONFLICT (character_id) DO NOTHING",
            fleetId, characterId, wingId, squadId, role))
        {
            if (await add.ExecuteNonQueryAsync(cancellationToken) == 0)
            {
                await using var removeInvitation = Command(connection, transaction,
                    $"DELETE FROM {_schema}.fleet_invitations WHERE fleet_id = $1 AND character_id = $2", fleetId, characterId);
                await removeInvitation.ExecuteNonQueryAsync(cancellationToken);
                await using var removeRequest = Command(connection, transaction,
                    $"DELETE FROM {_schema}.fleet_join_requests WHERE fleet_id = $1 AND character_id = $2", fleetId, characterId);
                await removeRequest.ExecuteNonQueryAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return null;
            }
        }
        await using (var remove = Command(connection, transaction,
            $"DELETE FROM {_schema}.fleet_invitations WHERE fleet_id = $1 AND character_id = $2", fleetId, characterId))
            await remove.ExecuteNonQueryAsync(cancellationToken);
        await using (var removeRequest = Command(connection, transaction,
            $"DELETE FROM {_schema}.fleet_join_requests WHERE fleet_id = $1 AND character_id = $2", fleetId, characterId))
            await removeRequest.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new FleetMemberSnapshot(characterId, wingId, squadId, role, 0);
    }

    public async Task<bool> RejectInviteAsync(long fleetId, long characterId, CancellationToken cancellationToken = default)
    {
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var command = Command(connection, null,
            $"DELETE FROM {_schema}.fleet_invitations WHERE fleet_id = $1 AND character_id = $2", fleetId, characterId);
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    public async Task<bool> RequestJoinAsync(long fleetId, long characterId, CancellationToken cancellationToken = default)
    {
        if (characterId <= 0) return false;
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        if (await MemberLimitAsync(connection, transaction, fleetId, lockFleet: true, cancellationToken) is null ||
            await FleetIdForCharacterAsync(connection, transaction, characterId, cancellationToken) is not null)
            return false;

        await using var invitation = Command(connection, transaction,
            $"SELECT 1 FROM {_schema}.fleet_invitations WHERE fleet_id = $1 AND character_id = $2 FOR UPDATE",
            fleetId, characterId);
        if (await invitation.ExecuteScalarAsync(cancellationToken) is not null and not DBNull) return false;

        await using var command = Command(connection, transaction,
            $"INSERT INTO {_schema}.fleet_join_requests (fleet_id, character_id) VALUES ($1, $2) ON CONFLICT (fleet_id, character_id) DO NOTHING",
            fleetId, characterId);
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<bool> RejectJoinRequestAsync(long fleetId, long issuerId, long characterId, CancellationToken cancellationToken = default)
    {
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        if (await MemberLimitAsync(connection, transaction, fleetId, lockFleet: true, cancellationToken) is null ||
            !await CanManageAsync(connection, transaction, fleetId, issuerId, cancellationToken, lockMember: true))
            return false;
        await using var command = Command(connection, transaction,
            $"DELETE FROM {_schema}.fleet_join_requests WHERE fleet_id = $1 AND character_id = $2", fleetId, characterId);
        var removed = await command.ExecuteNonQueryAsync(cancellationToken) > 0;
        if (removed) await transaction.CommitAsync(cancellationToken);
        return removed;
    }

    public async Task<FleetMemberSnapshot?> LeaveAsync(long fleetId, long characterId, CancellationToken cancellationToken = default)
    {
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var fleet = Command(connection, transaction,
            $"SELECT fleet_id FROM {_schema}.fleets WHERE fleet_id = $1 FOR UPDATE", fleetId))
        await using (var reader = await fleet.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken)) return null;
        }

        var member = await MemberAsync(connection, transaction, fleetId, characterId, cancellationToken);
        if (member is null || (member.Role == Leader && !HasBossJob(member.Job))) return null;

        long? successorId = null;
        if (HasBossJob(member.Job))
        {
            await using var successor = Command(connection, transaction,
                $@"SELECT m.character_id
                   FROM {_schema}.fleet_members m
                   LEFT JOIN {_schema}.fleet_wings w
                     ON w.fleet_id = m.fleet_id AND w.wing_id = m.wing_id
                   LEFT JOIN {_schema}.fleet_squads s
                     ON s.fleet_id = m.fleet_id AND s.squad_id = m.squad_id
                   WHERE m.fleet_id = $1 AND m.character_id <> $2
                   ORDER BY CASE m.role WHEN $3 THEN 0 WHEN $4 THEN 1 WHEN $5 THEN 2 ELSE 3 END,
                            COALESCE(w.position, 32767), COALESCE(s.position, 32767),
                            m.joined_at, m.character_id
                   LIMIT 1",
                fleetId, characterId, Leader, WingCommander, SquadCommander);
            var value = await successor.ExecuteScalarAsync(cancellationToken);
            if (value is null or DBNull) return null;
            successorId = Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
        }

        if (member.Role == Leader)
        {
            await using var clearLeader = Command(connection, transaction,
                $"UPDATE {_schema}.fleets SET leader_character_id = NULL WHERE fleet_id = $1", fleetId);
            if (await clearLeader.ExecuteNonQueryAsync(cancellationToken) != 1) return null;
        }

        await using (var command = Command(connection, transaction,
            $"DELETE FROM {_schema}.fleet_members WHERE fleet_id = $1 AND character_id = $2", fleetId, characterId))
            await command.ExecuteNonQueryAsync(cancellationToken);

        if (successorId is { } nextBoss)
        {
            await using var promote = Command(connection, transaction,
                $"UPDATE {_schema}.fleet_members SET job = job | $1::smallint WHERE fleet_id = $2 AND character_id = $3",
                (short)CreatorJob, fleetId, nextBoss);
            if (await promote.ExecuteNonQueryAsync(cancellationToken) != 1) return null;
        }

        await transaction.CommitAsync(cancellationToken);
        return member;
    }

    public async Task<bool> DisbandAsync(long fleetId, long issuerId, CancellationToken cancellationToken = default)
    {
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var member = await MemberAsync(connection, transaction, fleetId, issuerId, cancellationToken);
        if (member is null || !HasBossJob(member.Job)) return false;
        await using var command = Command(connection, transaction,
            $"DELETE FROM {_schema}.fleets WHERE fleet_id = $1", fleetId);
        var deleted = await command.ExecuteNonQueryAsync(cancellationToken) > 0;
        if (deleted) await transaction.CommitAsync(cancellationToken);
        return deleted;
    }

    public async Task<long> CreateWingAsync(long fleetId, long issuerId, CancellationToken cancellationToken = default)
    {
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        if (!await CanManageAsync(connection, transaction, fleetId, issuerId, cancellationToken) ||
            await CountAsync(connection, transaction, $"SELECT COUNT(*) FROM {_schema}.fleet_wings WHERE fleet_id = $1", cancellationToken, fleetId) >= MaxWings)
            return 0;
        await using var command = Command(connection, transaction,
            $"INSERT INTO {_schema}.fleet_wings (fleet_id, name, position) VALUES ($1, '', (SELECT COUNT(*) FROM {_schema}.fleet_wings WHERE fleet_id = $1)) RETURNING wing_id",
            fleetId);
        var id = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture);
        await transaction.CommitAsync(cancellationToken);
        return id;
    }

    public async Task<bool> DeleteWingAsync(long fleetId, long issuerId, long wingId, CancellationToken cancellationToken = default)
    {
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        if (!await CanManageAsync(connection, transaction, fleetId, issuerId, cancellationToken)) return false;
        await using (var move = Command(connection, transaction,
            $"UPDATE {_schema}.fleet_members SET wing_id = -1, squad_id = -1, role = $1 WHERE fleet_id = $2 AND wing_id = $3 AND role <> $4",
            Member, fleetId, wingId, Leader))
            await move.ExecuteNonQueryAsync(cancellationToken);
        await using var command = Command(connection, transaction,
            $"DELETE FROM {_schema}.fleet_wings WHERE fleet_id = $1 AND wing_id = $2", fleetId, wingId);
        var deleted = await command.ExecuteNonQueryAsync(cancellationToken) > 0;
        if (deleted) await transaction.CommitAsync(cancellationToken);
        return deleted;
    }

    public async Task<long> CreateSquadAsync(long fleetId, long issuerId, long wingId, CancellationToken cancellationToken = default)
    {
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        if (!await CanManageAsync(connection, transaction, fleetId, issuerId, cancellationToken) ||
            !await WingExistsAsync(connection, transaction, fleetId, wingId, cancellationToken) ||
            await CountAsync(connection, transaction, $"SELECT COUNT(*) FROM {_schema}.fleet_squads WHERE wing_id = $1", cancellationToken, wingId) >= MaxSquadsPerWing)
            return 0;
        await using var command = Command(connection, transaction,
            $"INSERT INTO {_schema}.fleet_squads (fleet_id, wing_id, name, position) VALUES ($1, $2, '', (SELECT COUNT(*) FROM {_schema}.fleet_squads WHERE wing_id = $2)) RETURNING squad_id",
            fleetId, wingId);
        var id = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture);
        await transaction.CommitAsync(cancellationToken);
        return id;
    }

    public async Task<bool> DeleteSquadAsync(long fleetId, long issuerId, long squadId, CancellationToken cancellationToken = default)
    {
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        if (!await CanManageAsync(connection, transaction, fleetId, issuerId, cancellationToken)) return false;
        long wingId;
        await using (var query = Command(connection, transaction,
            $"SELECT wing_id FROM {_schema}.fleet_squads WHERE fleet_id = $1 AND squad_id = $2", fleetId, squadId))
        {
            var value = await query.ExecuteScalarAsync(cancellationToken);
            if (value is null or DBNull) return false;
            wingId = Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
        }
        await using (var move = Command(connection, transaction,
            $"UPDATE {_schema}.fleet_members SET squad_id = -1, role = $1 WHERE fleet_id = $2 AND squad_id = $3 AND role <> $4",
            Member, fleetId, squadId, Leader))
            await move.ExecuteNonQueryAsync(cancellationToken);
        await using var command = Command(connection, transaction,
            $"DELETE FROM {_schema}.fleet_squads WHERE fleet_id = $1 AND squad_id = $2", fleetId, squadId);
        var deleted = await command.ExecuteNonQueryAsync(cancellationToken) > 0;
        if (deleted) await transaction.CommitAsync(cancellationToken);
        return deleted;
    }

    public async Task<bool> RenameWingAsync(long fleetId, long issuerId, long wingId, string name, CancellationToken cancellationToken = default) =>
        await RenameUnitAsync("fleet_wings", "wing_id", fleetId, issuerId, wingId, name, cancellationToken);

    public async Task<bool> RenameSquadAsync(long fleetId, long issuerId, long squadId, string name, CancellationToken cancellationToken = default) =>
        await RenameUnitAsync("fleet_squads", "squad_id", fleetId, issuerId, squadId, name, cancellationToken);

    private async Task<bool> RenameUnitAsync(string table, string idColumn, long fleetId, long issuerId, long id, string name, CancellationToken cancellationToken)
    {
        if (!IsValidName(name)) return false;
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        if (!await CanManageAsync(connection, null, fleetId, issuerId, cancellationToken)) return false;
        await using var command = Command(connection, null,
            $"UPDATE {_schema}.{table} SET name = $1 WHERE fleet_id = $2 AND {idColumn} = $3", name, fleetId, id);
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    public async Task<bool> MoveMemberAsync(long fleetId, long issuerId, long characterId, long wingId, long squadId, int role,
        CancellationToken cancellationToken = default)
    {
        if (role == Leader || !IsValidAssignment(role, wingId, squadId)) return false;
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var moved = await MoveMemberAsync(connection, transaction, fleetId, issuerId, characterId, wingId, squadId, role, cancellationToken);
        if (moved) await transaction.CommitAsync(cancellationToken);
        return moved;
    }

    public async Task<bool> MassMoveMembersAsync(long fleetId, long issuerId, IEnumerable<long> characterIds, long wingId, long squadId, int role,
        CancellationToken cancellationToken = default)
    {
        if (role == Leader || !IsValidAssignment(role, wingId, squadId)) return false;
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var moved = false;
        foreach (var characterId in characterIds.Where(id => id > 0).Distinct())
            moved |= await MoveMemberAsync(connection, transaction, fleetId, issuerId, characterId, wingId, squadId, role, cancellationToken);
        if (moved) await transaction.CommitAsync(cancellationToken);
        return moved;
    }

    public async Task<bool> MakeLeaderAsync(long fleetId, long issuerId, long characterId, CancellationToken cancellationToken = default)
    {
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        if (await MemberLimitAsync(connection, transaction, fleetId, lockFleet: true, cancellationToken) is null) return false;
        var issuer = await MemberAsync(connection, transaction, fleetId, issuerId, cancellationToken);
        var target = await MemberAsync(connection, transaction, fleetId, characterId, cancellationToken);
        if (issuer is null || !HasBossJob(issuer.Job) || target is null) return false;
        await using (var oldLeader = Command(connection, transaction,
            $"UPDATE {_schema}.fleet_members SET role = $1, wing_id = -1, squad_id = -1 WHERE fleet_id = $2 AND role = $3",
            Member, fleetId, Leader))
            await oldLeader.ExecuteNonQueryAsync(cancellationToken);
        await using (var newLeader = Command(connection, transaction,
            $"UPDATE {_schema}.fleet_members SET role = $1, wing_id = -1, squad_id = -1 WHERE fleet_id = $2 AND character_id = $3",
            Leader, fleetId, characterId))
            await newLeader.ExecuteNonQueryAsync(cancellationToken);
        await using (var fleet = Command(connection, transaction,
            $"UPDATE {_schema}.fleets SET leader_character_id = $1 WHERE fleet_id = $2", characterId, fleetId))
            await fleet.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<bool> KickMemberAsync(long fleetId, long issuerId, long characterId, CancellationToken cancellationToken = default)
    {
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        if (!await CanManageAsync(connection, transaction, fleetId, issuerId, cancellationToken)) return false;
        var target = await MemberAsync(connection, transaction, fleetId, characterId, cancellationToken);
        if (target is null || target.Role == Leader) return false;
        await using var command = Command(connection, transaction,
            $"DELETE FROM {_schema}.fleet_members WHERE fleet_id = $1 AND character_id = $2", fleetId, characterId);
        var removed = await command.ExecuteNonQueryAsync(cancellationToken) > 0;
        if (removed) await transaction.CommitAsync(cancellationToken);
        return removed;
    }

    public async Task<bool> SetAutoJoinSquadAsync(long fleetId, long issuerId, long? squadId, CancellationToken cancellationToken = default)
    {
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        if (!await CanManageAsync(connection, transaction, fleetId, issuerId, cancellationToken) ||
            squadId is { } id && !await UnitExistsAsync(connection, transaction,
                $"SELECT EXISTS (SELECT 1 FROM {_schema}.fleet_squads WHERE fleet_id = $1 AND squad_id = $2)", cancellationToken, fleetId, id))
            return false;
        await using var command = Command(connection, transaction,
            $"UPDATE {_schema}.fleets SET auto_join_squad_id = $1 WHERE fleet_id = $2", squadId, fleetId);
        var updated = await command.ExecuteNonQueryAsync(cancellationToken) > 0;
        if (updated) await transaction.CommitAsync(cancellationToken);
        return updated;
    }

    private async Task<bool> MoveMemberAsync(System.Data.Common.DbConnection connection, System.Data.Common.DbTransaction transaction,
        long fleetId, long issuerId, long characterId, long wingId, long squadId, int role, CancellationToken cancellationToken)
    {
        if (!await CanManageAsync(connection, transaction, fleetId, issuerId, cancellationToken) ||
            !await PositionExistsAsync(connection, transaction, fleetId, wingId, squadId, cancellationToken) ||
            await MemberAsync(connection, transaction, fleetId, characterId, cancellationToken) is not { Role: not Leader })
            return false;
        await using var command = Command(connection, transaction,
            $"UPDATE {_schema}.fleet_members SET wing_id = $1, squad_id = $2, role = $3 WHERE fleet_id = $4 AND character_id = $5",
            wingId, squadId, role, fleetId, characterId);
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    private async Task<bool> CanManageAsync(System.Data.Common.DbConnection connection, System.Data.Common.DbTransaction? transaction,
        long fleetId, long characterId, CancellationToken cancellationToken, bool lockMember = false)
    {
        var member = await MemberAsync(connection, transaction, fleetId, characterId, cancellationToken, lockMember);
        return member is { } m && (m.Role == Leader || HasBossJob(m.Job));
    }

    private async Task<FleetMemberSnapshot?> MemberAsync(System.Data.Common.DbConnection connection, System.Data.Common.DbTransaction? transaction,
        long fleetId, long characterId, CancellationToken cancellationToken, bool forUpdate = false)
    {
        var sql = $"SELECT character_id, wing_id, squad_id, role, job, accepts_fleet_warp, accepts_conduit_jumps, accepts_fleet_regroups, ship_type_id FROM {_schema}.fleet_members WHERE fleet_id = $1 AND character_id = $2" +
            (forUpdate ? " FOR UPDATE" : "");
        await using var command = Command(connection, transaction, sql, fleetId, characterId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new FleetMemberSnapshot(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt16(3),
                reader.GetInt16(4), reader.GetBoolean(5), reader.GetBoolean(6), reader.GetBoolean(7),
                reader.IsDBNull(8) ? null : reader.GetInt64(8))
            : null;
    }

    public async Task<FleetMemberSnapshot?> SetMemberOptionsAsync(long fleetId, long characterId,
        bool? acceptsFleetWarp = null, bool? acceptsConduitJumps = null, bool? acceptsFleetRegroups = null,
        CancellationToken cancellationToken = default)
    {
        if (acceptsFleetWarp is null && acceptsConduitJumps is null && acceptsFleetRegroups is null) return null;
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        if (await MemberAsync(connection, transaction, fleetId, characterId, cancellationToken) is null) return null;
        await using var update = Command(connection, transaction,
            $"UPDATE {_schema}.fleet_members SET accepts_fleet_warp = COALESCE($1, accepts_fleet_warp), accepts_conduit_jumps = COALESCE($2, accepts_conduit_jumps), accepts_fleet_regroups = COALESCE($3, accepts_fleet_regroups) WHERE fleet_id = $4 AND character_id = $5",
            acceptsFleetWarp, acceptsConduitJumps, acceptsFleetRegroups, fleetId, characterId);
        if (await update.ExecuteNonQueryAsync(cancellationToken) != 1) return null;
        var updated = await MemberAsync(connection, transaction, fleetId, characterId, cancellationToken);
        if (updated is not null) await transaction.CommitAsync(cancellationToken);
        return updated;
    }

    public async Task<FleetMemberSnapshot?> SetMemberShipTypeAsync(long fleetId, long characterId, long? shipTypeId,
        CancellationToken cancellationToken = default)
    {
        if (shipTypeId is <= 0) shipTypeId = null;
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        if (await MemberAsync(connection, transaction, fleetId, characterId, cancellationToken) is null) return null;
        await using var update = Command(connection, transaction,
            $"UPDATE {_schema}.fleet_members SET ship_type_id = $1 WHERE fleet_id = $2 AND character_id = $3",
            shipTypeId, fleetId, characterId);
        if (await update.ExecuteNonQueryAsync(cancellationToken) != 1) return null;
        var updated = await MemberAsync(connection, transaction, fleetId, characterId, cancellationToken);
        if (updated is not null) await transaction.CommitAsync(cancellationToken);
        return updated;
    }

    public async Task<bool> SetFreeMoveAsync(long fleetId, long characterId, bool isFreeMove,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        if (!await CanManageAsync(connection, transaction, fleetId, characterId, cancellationToken)) return false;
        await using var update = Command(connection, transaction,
            $"UPDATE {_schema}.fleets SET is_free_move = $1 WHERE fleet_id = $2", isFreeMove, fleetId);
        if (await update.ExecuteNonQueryAsync(cancellationToken) != 1) return false;
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<bool> SetMotdAsync(long fleetId, long characterId, string motd,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var member = await MemberAsync(connection, transaction, fleetId, characterId, cancellationToken);
        if (member is null || !HasBossJob(member.Job)) return false;
        await using var update = Command(connection, transaction,
            $"UPDATE {_schema}.fleets SET motd = $1 WHERE fleet_id = $2", motd, fleetId);
        if (await update.ExecuteNonQueryAsync(cancellationToken) != 1) return false;
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<string?> GetMotdAsync(long fleetId, CancellationToken cancellationToken = default)
    {
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var query = Command(connection, null,
            $"SELECT motd FROM {_schema}.fleets WHERE fleet_id = $1", fleetId);
        var value = await query.ExecuteScalarAsync(cancellationToken);
        return value is string motd ? motd : null;
    }

    private async Task<long?> FleetIdForCharacterAsync(System.Data.Common.DbConnection connection, System.Data.Common.DbTransaction? transaction,
        long characterId, CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction,
            $"SELECT fleet_id FROM {_schema}.fleet_members WHERE character_id = $1", characterId);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null or DBNull ? null : Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task<bool> PositionExistsAsync(System.Data.Common.DbConnection connection, System.Data.Common.DbTransaction? transaction,
        long fleetId, long wingId, long squadId, CancellationToken cancellationToken)
    {
        if (wingId == NoUnit) return squadId == NoUnit;
        if (!await WingExistsAsync(connection, transaction, fleetId, wingId, cancellationToken)) return false;
        return squadId == NoUnit || await UnitExistsAsync(connection, transaction,
            $"SELECT EXISTS (SELECT 1 FROM {_schema}.fleet_squads WHERE fleet_id = $1 AND wing_id = $2 AND squad_id = $3)",
            cancellationToken, fleetId, wingId, squadId);
    }

    private async Task<bool> WingExistsAsync(System.Data.Common.DbConnection connection, System.Data.Common.DbTransaction? transaction,
        long fleetId, long wingId, CancellationToken cancellationToken) =>
        await UnitExistsAsync(connection, transaction,
            $"SELECT EXISTS (SELECT 1 FROM {_schema}.fleet_wings WHERE fleet_id = $1 AND wing_id = $2)", cancellationToken, fleetId, wingId);

    private async Task<bool> UnitExistsAsync(System.Data.Common.DbConnection connection, System.Data.Common.DbTransaction? transaction,
        string sql, CancellationToken cancellationToken, params object?[] values)
    {
        await using var command = Command(connection, transaction, sql, values);
        return Convert.ToBoolean(await command.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task<long> CountAsync(System.Data.Common.DbConnection connection, System.Data.Common.DbTransaction? transaction,
        string sql, CancellationToken cancellationToken, params object?[] values)
    {
        await using var command = Command(connection, transaction, sql, values);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture);
    }

    public async Task<int> MaxMemberCountAsync(long fleetId, CancellationToken cancellationToken = default)
    {
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        return await MemberLimitAsync(connection, null, fleetId, lockFleet: false, cancellationToken) ?? 0;
    }

    public async Task<bool> SetMaxMemberCountAsync(long fleetId, long characterId, int maxMemberCount,
        CancellationToken cancellationToken = default)
    {
        if (maxMemberCount is < 1 or > MaxMembers) return false;
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        if (await MemberLimitAsync(connection, transaction, fleetId, lockFleet: true, cancellationToken) is null) return false;
        var member = await MemberAsync(connection, transaction, fleetId, characterId, cancellationToken, forUpdate: true);
        if (member?.Role != Leader) return false;
        await using var update = Command(connection, transaction,
            $"UPDATE {_schema}.fleets SET max_member_count = $1 WHERE fleet_id = $2", maxMemberCount, fleetId);
        if (await update.ExecuteNonQueryAsync(cancellationToken) != 1) return false;
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    private async Task<int?> MemberLimitAsync(System.Data.Common.DbConnection connection,
        System.Data.Common.DbTransaction? transaction, long fleetId, bool lockFleet,
        CancellationToken cancellationToken)
    {
        var sql = $"SELECT max_member_count FROM {_schema}.fleets WHERE fleet_id = $1" +
            (lockFleet ? " FOR UPDATE" : "");
        await using var command = Command(connection, transaction, sql, fleetId);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        if (value is null or DBNull) return null;
        var limit = Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture);
        return limit is >= 1 and <= MaxMembers ? limit : null;
    }

    private static System.Data.Common.DbCommand Command(System.Data.Common.DbConnection connection, System.Data.Common.DbTransaction? transaction,
        string sql, params object?[] values)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var value in values)
        {
            var parameter = command.CreateParameter();
            parameter.Value = value ?? DBNull.Value;
            command.Parameters.Add(parameter);
        }
        return command;
    }


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

[Service("fleetObjectHandler")]
public sealed partial class FleetObjectService(FleetStore store, Func<long> nowUnixMs, Func<long, ClientSession?>? sessionForCharacter = null, Func<IReadOnlyList<long>, CancellationToken, Task<PyValue>>? compositionForMembers = null, Func<long, long, string, ClientSession, CancellationToken, Task<PyValue?>>? loadFleetSetup = null, Func<long, long, PyValue, ClientSession, CancellationToken, Task<PyValue?>>? createFleetAdvert = null, Func<ClientSession, ClientSession, bool>? sameBubble = null) : IBoundObjectReleaseHandler
{
    private readonly Binding _binding = new(nowUnixMs);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> _fleetByOid = new(StringComparer.Ordinal);
    private static PyValue None => PyNone.Instance;

    public void OnBoundObjectReleased(string oid, ClientSession? session)
    {
        _binding.Release(oid);
        _fleetByOid.TryRemove(oid, out _);
    }

    [Method("MachoResolveObject")]
    public async ValueTask<PyValue> MachoResolveObject(ServiceCall call)
    {
        var fleetId = BoundFleetId(call) ?? Binding.PlaceOf(call.Arg(0)) ?? Number(call.Arg(0));
        return fleetId is > 0 && await store.ExistsAsync(fleetId.Value, call.Cancellation)
            ? new PyInt(MachoMessage.ProxyNodeId) : None;
    }

    [Method("MachoBindObject")]
    public async ValueTask<PyValue> MachoBindObject(ServiceCall call)
    {
        var fleetId = BoundFleetId(call) ?? Binding.PlaceOf(call.Arg(0)) ?? Number(call.Arg(0));
        if (fleetId is not > 0 || !await store.ExistsAsync(fleetId.Value, call.Cancellation)) return None;
        var (bound, oid) = _binding.Bind(call.Session);
        _fleetByOid[oid] = fleetId.Value;
        return Py.Tuple(bound, await Binding.NestedAsync(this, call, oid));
    }

    [Method("CreateFleet")]
    public async ValueTask<PyValue> CreateFleet(ServiceCall call)
    {
        var characterId = CharacterId(call.Session);
        if (characterId <= 0 || await store.MemberForCharacterAsync(characterId, call.Cancellation) is not null) return None;
        var fleetId = await store.CreateAsync(characterId, call.Cancellation);
        if (fleetId <= 0) return None;
        var (bound, oid) = _binding.Bind(call.Session);
        _fleetByOid[oid] = fleetId;
        SetSession(call.Session, fleetId, new FleetMemberSnapshot(characterId, FleetStore.NoUnit, FleetStore.NoUnit, FleetStore.Leader, FleetStore.CreatorJob));
        return bound;
    }

    [Method("Init")]
    public async ValueTask<PyValue> Init(ServiceCall call)
    {
        if (FleetId(call) is not { } fleetId) return None;
        var characterId = CharacterId(call.Session);
        if (characterId <= 0 ||
            await store.FleetIdForCharacterAsync(characterId, call.Cancellation) != fleetId)
            return None;

        var shipTypeId = call.Arg(0) is PyNone ? null : Number(call.Arg(0));
        if (shipTypeId is < 0) shipTypeId = null;
        await store.SetMemberShipTypeAsync(fleetId, characterId, shipTypeId, call.Cancellation);

        var setupName = Text(call.Arg(1));
        if (!string.IsNullOrWhiteSpace(setupName))
        {
            if (loadFleetSetup is null)
                throw new InvalidOperationException("Fleet setup loading requires the Host characterSettings callback.");
            await loadFleetSetup(fleetId, characterId, setupName, call.Session, call.Cancellation);
        }

        var advertData = call.Kwarg("adInfoData");
        if (advertData is null or PyNone) return None;
        if (createFleetAdvert is null)
            throw new InvalidOperationException("Fleet advert creation requires the Host FleetStore callback.");
        return await createFleetAdvert(fleetId, characterId, advertData, call.Session, call.Cancellation)
            ?? None;
    }

    [Method("GetFleetID")]
    public async ValueTask<PyValue> GetFleetID(ServiceCall call)
    {
        if (FleetId(call) is not { } id) return None;
        var characterId = CharacterId(call.Session);
        return characterId > 0 &&
               await store.FleetIdForCharacterAsync(characterId, call.Cancellation) == id
            ? new PyLong(id)
            : None;
    }

    [Method("GetInitState")]
    public async ValueTask<PyValue> GetInitState(ServiceCall call)
    {
        if (FleetId(call) is not { } id) return None;
        var characterId = CharacterId(call.Session);
        if (characterId <= 0 ||
            await store.FleetIdForCharacterAsync(characterId, call.Cancellation) != id ||
            await store.SnapshotAsync(id, call.Cancellation) is not { } state)
            return None;

        var members = new PyDict(state.Members.Select(member =>
            new System.Collections.Generic.KeyValuePair<PyValue, PyValue>(new PyLong(member.CharacterId), MemberValue(member))).ToArray());
        var wings = new PyDict(state.Wings.Select(wing =>
            new System.Collections.Generic.KeyValuePair<PyValue, PyValue>(new PyLong(wing.WingId), WingValue(wing))).ToArray());
        return Py.KeyVal([
            ("fleetID", new PyLong(state.FleetId)),
            ("members", members),
            ("wings", wings),
            ("motd", new PyWString(state.Motd)),
            ("options", FleetOptionsValue(state))
        ]);
    }

    [Method("GetFleetMaxSize")]
    public async ValueTask<PyValue> GetFleetMaxSize(ServiceCall call)
    {
        if (FleetId(call) is not { } fleetId) return None;
        var characterId = CharacterId(call.Session);
        if (characterId <= 0 ||
            await store.FleetIdForCharacterAsync(characterId, call.Cancellation) != fleetId)
            return None;
        var maxMemberCount = await store.MaxMemberCountAsync(fleetId, call.Cancellation);
        return maxMemberCount is > 0 ? new PyInt(maxMemberCount) : None;
    }

    [Method("SetFleetMaxSize")]
    public async ValueTask<PyValue> SetFleetMaxSize(ServiceCall call)
    {
        var maxMemberCount = Number(call.Arg(0));
        if (FleetId(call) is { } fleetId && maxMemberCount is >= 1 and <= FleetStore.MaxMembers)
            await store.SetMaxMemberCountAsync(fleetId, CharacterId(call.Session), (int)maxMemberCount.Value,
                call.Cancellation);
        return None;
    }

    [Method("Invite")]
    public async ValueTask<PyValue> Invite(ServiceCall call)
    {
        if (FleetId(call) is { } fleetId)
            await store.InviteAsync(fleetId, CharacterId(call.Session), Number(call.Arg(0)) ?? 0,
                Number(call.Arg(1)) ?? FleetStore.NoUnit, Number(call.Arg(2)) ?? FleetStore.NoUnit,
                (int)(Number(call.Arg(3)) ?? FleetStore.Member), call.Cancellation);
        return None;
    }

    [Method("AcceptInvite")]
    public async ValueTask<PyValue> AcceptInvite(ServiceCall call)
    {
        if (FleetId(call) is not { } fleetId) return None;
        var before = await store.SnapshotAsync(fleetId, call.Cancellation);
        var member = await store.JoinAsync(fleetId, CharacterId(call.Session), call.Cancellation);
        if (member is null) return None;
        SetSession(call.Session, fleetId, member);
        var after = await store.SnapshotAsync(fleetId, call.Cancellation);
        if (before is not null) PublishMemberChanges(call.Session, fleetId, before, after);
        return None;
    }

    [Method("RejectInvite")]
    public async ValueTask<PyValue> RejectInvite(ServiceCall call)
    {
        if (FleetId(call) is { } fleetId) await store.RejectInviteAsync(fleetId, CharacterId(call.Session), call.Cancellation);
        return None;
    }

    [Method("LeaveFleet")]
    public async ValueTask<PyValue> LeaveFleet(ServiceCall call)
    {
        if (FleetId(call) is not { } fleetId) return None;
        var before = await store.SnapshotAsync(fleetId, call.Cancellation);
        if (await store.LeaveAsync(fleetId, CharacterId(call.Session), call.Cancellation) is null) return None;
        ClearSession(call.Session);
        var after = await store.SnapshotAsync(fleetId, call.Cancellation);
        if (before is not null) PublishMemberChanges(call.Session, fleetId, before, after);
        return None;
    }

    [Method("DisbandFleet")]
    public async ValueTask<PyValue> DisbandFleet(ServiceCall call)
    {
        if (FleetId(call) is not { } fleetId) return None;
        var before = await store.SnapshotAsync(fleetId, call.Cancellation);
        if (before is null || !await store.DisbandAsync(fleetId, CharacterId(call.Session), call.Cancellation)) return None;
        var after = await store.SnapshotAsync(fleetId, call.Cancellation);
        PublishMemberChanges(call.Session, fleetId, before, after);
        return None;
    }

    [Method("CreateWing")]
    public async ValueTask<PyValue> CreateWing(ServiceCall call) =>
        FleetId(call) is { } id && await store.CreateWingAsync(id, CharacterId(call.Session), call.Cancellation) is var wing && wing > 0
            ? new PyLong(wing) : None;

    [Method("DeleteWing")]
    public async ValueTask<PyValue> DeleteWing(ServiceCall call)
    {
        if (FleetId(call) is { } id) await store.DeleteWingAsync(id, CharacterId(call.Session), Number(call.Arg(0)) ?? 0, call.Cancellation);
        return None;
    }

    [Method("CreateSquad")]
    public async ValueTask<PyValue> CreateSquad(ServiceCall call) =>
        FleetId(call) is { } id && await store.CreateSquadAsync(id, CharacterId(call.Session), Number(call.Arg(0)) ?? 0, call.Cancellation) is var squad && squad > 0
            ? new PyLong(squad) : None;

    [Method("DeleteSquad")]
    public async ValueTask<PyValue> DeleteSquad(ServiceCall call)
    {
        if (FleetId(call) is { } id) await store.DeleteSquadAsync(id, CharacterId(call.Session), Number(call.Arg(0)) ?? 0, call.Cancellation);
        return None;
    }

    [Method("ChangeWingName")]
    public async ValueTask<PyValue> ChangeWingName(ServiceCall call)
    {
        if (FleetId(call) is { } id) await store.RenameWingAsync(id, CharacterId(call.Session), Number(call.Arg(0)) ?? 0, Text(call.Arg(1)), call.Cancellation);
        return None;
    }

    [Method("ChangeSquadName")]
    public async ValueTask<PyValue> ChangeSquadName(ServiceCall call)
    {
        if (FleetId(call) is { } id) await store.RenameSquadAsync(id, CharacterId(call.Session), Number(call.Arg(0)) ?? 0, Text(call.Arg(1)), call.Cancellation);
        return None;
    }

    [Method("MoveMember")]
    public async ValueTask<PyValue> MoveMember(ServiceCall call)
    {
        if (FleetId(call) is not { } fleetId) return None;
        var before = await store.SnapshotAsync(fleetId, call.Cancellation);
        if (before is null) return None;
        var characterId = Number(call.Arg(0)) ?? 0;
        var wingId = Number(call.Arg(1)) ?? FleetStore.NoUnit;
        var squadId = Number(call.Arg(2)) ?? FleetStore.NoUnit;
        var role = (int)(Number(call.Arg(3)) ?? FleetStore.Member);
        if (!await store.MoveMemberAsync(fleetId, CharacterId(call.Session), characterId, wingId, squadId, role, call.Cancellation))
            return None;
        var after = await store.SnapshotAsync(fleetId, call.Cancellation);
        PublishMemberChanges(call.Session, fleetId, before, after);
        return None;
    }

    [Method("MassMoveMembers")]
    public async ValueTask<PyValue> MassMoveMembers(ServiceCall call)
    {
        if (FleetId(call) is not { } fleetId) return None;
        var before = await store.SnapshotAsync(fleetId, call.Cancellation);
        if (before is null) return None;
        var wingId = Number(call.Arg(1)) ?? FleetStore.NoUnit;
        var squadId = Number(call.Arg(2)) ?? FleetStore.NoUnit;
        var role = (int)(Number(call.Arg(3)) ?? FleetStore.Member);
        var ids = Sequence(call.Arg(0)).Select(value => Number(value) ?? 0).ToArray();
        if (!await store.MassMoveMembersAsync(fleetId, CharacterId(call.Session), ids, wingId, squadId, role, call.Cancellation))
            return None;
        var after = await store.SnapshotAsync(fleetId, call.Cancellation);
        PublishMemberChanges(call.Session, fleetId, before, after);
        return None;
    }

    [Method("MakeLeader")]
    public async ValueTask<PyValue> MakeLeader(ServiceCall call)
    {
        if (FleetId(call) is not { } fleetId) return None;
        var before = await store.SnapshotAsync(fleetId, call.Cancellation);
        if (before is null) return None;
        var targetId = Number(call.Arg(0)) ?? 0;
        if (!await store.MakeLeaderAsync(fleetId, CharacterId(call.Session), targetId, call.Cancellation))
            return None;
        var after = await store.SnapshotAsync(fleetId, call.Cancellation);
        PublishMemberChanges(call.Session, fleetId, before, after);
        return None;
    }

    [Method("KickMember")]
    public async ValueTask<PyValue> KickMember(ServiceCall call)
    {
        if (FleetId(call) is not { } fleetId) return None;
        var before = await store.SnapshotAsync(fleetId, call.Cancellation);
        if (before is null ||
            !await store.KickMemberAsync(fleetId, CharacterId(call.Session), Number(call.Arg(0)) ?? 0, call.Cancellation))
            return None;
        var after = await store.SnapshotAsync(fleetId, call.Cancellation);
        PublishMemberChanges(call.Session, fleetId, before, after);
        return None;
    }

    [Method("SetAutoJoinSquadID")]
    public async ValueTask<PyValue> SetAutoJoinSquadID(ServiceCall call)
    {
        if (FleetId(call) is { } id)
        {
            var raw = call.Arg(0);
            var squadId = raw is PyNone ? null : Number(raw);
            await store.SetAutoJoinSquadAsync(id, CharacterId(call.Session), squadId, call.Cancellation);
        }
        return None;
    }

    [Method("GetJoinRequests")]
    public async ValueTask<PyValue> GetJoinRequests(ServiceCall call)
    {
        if (FleetId(call) is not { } id ||
            await store.SnapshotAsync(id, call.Cancellation) is not { } state)
            return None;

        var characterId = CharacterId(call.Session);
        var requester = state.Members.SingleOrDefault(member => member.CharacterId == characterId);
        if (requester is null ||
            requester.Role != FleetStore.Leader && !FleetStore.HasBossJob(requester.Job))
            return None;

        return new PyDict(state.JoinRequests.Select(request =>
            new System.Collections.Generic.KeyValuePair<PyValue, PyValue>(new PyLong(request.CharacterId),
                Py.KeyVal([("charID", new PyLong(request.CharacterId))]))).ToArray());
    }

    [Method("RejectJoinRequest")]
    public async ValueTask<PyValue> RejectJoinRequest(ServiceCall call)
    {
        if (FleetId(call) is { } id)
            await store.RejectJoinRequestAsync(id, CharacterId(call.Session), Number(call.Arg(0)) ?? 0, call.Cancellation);
        return None;
    }

    private async Task RefreshSessionAsync(ClientSession session, long fleetId, long characterId, CancellationToken cancellationToken)
    {
        var snapshot = await store.SnapshotAsync(fleetId, cancellationToken);
        var member = snapshot?.Members.FirstOrDefault(item => item.CharacterId == characterId);
        if (member is not null && characterId == CharacterId(session)) SetSession(session, fleetId, member);
    }

    private long? FleetId(ServiceCall call) => call.BoundObjectId is { } oid && _fleetByOid.TryGetValue(oid, out var id) ? id : null;
    private long? BoundFleetId(ServiceCall call) => FleetId(call);

    private static PyValue MemberValue(FleetMemberSnapshot member) => Py.KeyVal([
        ("charID", new PyLong(member.CharacterId)),
        ("wingID", new PyLong(member.WingId)),
        ("squadID", new PyLong(member.SquadId)),
        ("role", new PyInt(member.Role)),
        ("job", new PyInt(member.Job)),
        ("memberOptOuts", MemberOptOutsValue(member))
    ]);

    private static PyValue WingValue(FleetWingSnapshot wing) => Py.KeyVal([
        ("wingID", new PyLong(wing.WingId)),
        ("name", new PyWString(wing.Name)),
        ("squads", new PyDict(wing.Squads.Select(squad =>
            new System.Collections.Generic.KeyValuePair<PyValue, PyValue>(new PyLong(squad.SquadId),
                Py.KeyVal([("squadID", new PyLong(squad.SquadId)), ("name", new PyWString(squad.Name))]))).ToArray()))
    ]);

    private static long CharacterId(ClientSession session) => session.CharacterId ?? session.GetInt("charid") ?? 0;
    private static void SetSession(ClientSession session, long fleetId, FleetMemberSnapshot member)
    {
        session.Set("fleetid", new PyLong(fleetId));
        session.Set("fleetrole", new PyInt(member.Role));
        session.Set("wingid", new PyLong(member.WingId));
        session.Set("squadid", new PyLong(member.SquadId));
    }

    private static void ClearSession(ClientSession session)
    {
        session.Set("fleetid", PyNone.Instance);
        session.Set("fleetrole", PyNone.Instance);
        session.Set("wingid", PyNone.Instance);
        session.Set("squadid", PyNone.Instance);
    }

    private static long? Number(PyValue value) => value switch
    {
        PyInt number => number.Value,
        PyLong number => number.Value,
        PyReal number when double.IsFinite(number.Value) && number.Value >= long.MinValue && number.Value <= long.MaxValue => (long)number.Value,
        PyStr text when long.TryParse(text.Value, out var parsed) => parsed,
        PyWString text when long.TryParse(text.Value, out var parsed) => parsed,
        _ => null
    };

    private static string Text(PyValue value) => value switch
    {
        PyStr text => text.Value,
        PyWString text => text.Value,
        _ => ""
    };

    private static IReadOnlyList<PyValue> Sequence(PyValue value) => value switch
    {
        PyList list => list.Items,
        PyTuple tuple => tuple.Items,
        _ => []
    };
}


public sealed partial class FleetObjectService
{
private static readonly System.Collections.Generic.HashSet<string> BroadcastNames = new(StringComparer.Ordinal)
    {
        "EnemySpotted", "HealArmor", "HealShield", "HealCapacitor", "InPosition", "NeedBackup",
        "HoldPosition", "Location", "TravelTo", "JumpBeacon", "Target", "RepTarget", "WarpTo",
        "AlignTo", "JumpTo"
    };

    [Method("GetWings")]
    public async ValueTask<PyValue> GetWings(ServiceCall call)
    {
        var fleetId = FleetId(call);
        var characterId = CharacterId(call.Session);
        if (fleetId is not { } id || characterId <= 0 ||
            await store.FleetIdForCharacterAsync(characterId, call.Cancellation) != id ||
            await store.SnapshotAsync(id, call.Cancellation) is not { } snapshot)
            return new PyDict([]);

        return new PyDict(snapshot.Wings.Select(wing =>
            new System.Collections.Generic.KeyValuePair<PyValue, PyValue>(new PyLong(wing.WingId), WingValue(wing))).ToArray());
    }

    [Method("LoadFleetSetup")]
    public async ValueTask<PyValue> LoadFleetSetup(ServiceCall call)
    {
        if (FleetId(call) is not { } fleetId ||
            await store.SnapshotAsync(fleetId, call.Cancellation) is not { } snapshot)
            return None;
        var characterId = CharacterId(call.Session);
        var member = snapshot.Members.FirstOrDefault(item => item.CharacterId == characterId);
        if (member is null || member.Role != FleetStore.Leader && !FleetStore.HasBossJob(member.Job))
            return None;
        var setupName = Text(call.Arg(0));
        if (string.IsNullOrWhiteSpace(setupName)) return None;
        if (loadFleetSetup is null)
            throw new InvalidOperationException("Fleet setup loading requires the Host characterSettings callback.");
        await loadFleetSetup(fleetId, characterId, setupName, call.Session, call.Cancellation);
        return None;
    }

    [Method("SetTakesFleetWarp")]
    public async ValueTask<PyValue> SetTakesFleetWarp(ServiceCall call)
    {
        var value = BooleanValue(call.Arg(0));
        if (value is not null) await SetMemberOptionsAsync(call, acceptsFleetWarp: value);
        return None;
    }

    [Method("SetAcceptsConduitJumpsValue")]
    public async ValueTask<PyValue> SetAcceptsConduitJumpsValue(ServiceCall call)
    {
        var value = BooleanValue(call.Arg(0));
        if (value is not null) await SetMemberOptionsAsync(call, acceptsConduitJumps: value);
        return None;
    }

    [Method("SetAcceptsRegroupValue")]
    public async ValueTask<PyValue> SetAcceptsRegroupValue(ServiceCall call)
    {
        var value = BooleanValue(call.Arg(0));
        if (value is not null) await SetMemberOptionsAsync(call, acceptsFleetRegroups: value);
        return None;
    }

    [Method("SetOptions")]
    public async ValueTask<PyValue> SetOptions(ServiceCall call)
    {
        if (FleetId(call) is not { } fleetId ||
            await store.SnapshotAsync(fleetId, call.Cancellation) is not { } before ||
            BooleanValue(Field(call.Arg(0), "isFreeMove") ?? PyNone.Instance) is not { } isFreeMove ||
            !await store.SetFreeMoveAsync(fleetId, CharacterId(call.Session), isFreeMove, call.Cancellation))
            return None;

        var after = await store.SnapshotAsync(fleetId, call.Cancellation);
        if (after is not null && after.IsFreeMove != before.IsFreeMove)
            NotifyFleetOptionsChanged(fleetId, before, after, call.Session);
        return None;
    }

    [Method("GetFleetComposition")]
    public async ValueTask<PyValue> GetFleetComposition(ServiceCall call)
    {
        if (FleetId(call) is not { } fleetId ||
            await store.SnapshotAsync(fleetId, call.Cancellation) is not { } snapshot)
            return new PyList([]);

        var viewer = snapshot.Members.FirstOrDefault(member => member.CharacterId == CharacterId(call.Session));
        if (viewer is null) return new PyList([]);
        var visible = snapshot.Members.Where(member =>
            FleetStore.HasBossJob(viewer.Job) || viewer.Role == FleetStore.Leader ||
            viewer.Role == FleetStore.WingCommander && member.WingId == viewer.WingId ||
            viewer.Role == FleetStore.SquadCommander && member.SquadId == viewer.SquadId).ToArray();
        var characterIds = visible.Select(member => member.CharacterId).ToArray();
        if (compositionForMembers is not null)
            return await compositionForMembers(characterIds, call.Cancellation);

        return new PyList(visible.Select(member =>
        {
            var session = sessionForCharacter?.Invoke(member.CharacterId);
            var shipTypeId = session?.GetInt("shiptypeid") ?? member.ShipTypeId;
            return (PyValue)Py.KeyVal([
                ("characterID", new PyLong(member.CharacterId)),
                ("solarSystemID", session?.GetInt("solarsystemid2") is { } systemId ? new PyLong(systemId) : PyNone.Instance),
                ("stationID", session?.GetInt("stationid") is { } stationId ? new PyLong(stationId) : PyNone.Instance),
                ("shipTypeID", shipTypeId is { } typeId ? new PyLong(typeId) : PyNone.Instance),
                ("skills", new PyList([])),
                ("skillIDs", new PyList([]))
            ]);
        }).ToArray());
    }

    [Method("SendBroadcast")]
    public async ValueTask<PyValue> SendBroadcast(ServiceCall call)
    {
        if (FleetId(call) is not { } fleetId ||
            await store.SnapshotAsync(fleetId, call.Cancellation) is not { } snapshot)
            return None;
        var senderId = CharacterId(call.Session);
        var sender = snapshot.Members.FirstOrDefault(member => member.CharacterId == senderId);
        var name = Text(call.Arg(0));
        var scope = Number(call.Arg(1));
        if (sender is null || !BroadcastNames.Contains(name) || scope is not (1 or 2 or 3)) return None;

        var systemId = call.Session.GetInt("solarsystemid2") ?? call.Session.GetInt("solarsystemid");
        var range = name switch
        {
            "Target" or "RepTarget" or "HealArmor" or "HealShield" or "HealCapacitor" => 2,
            "WarpTo" or "AlignTo" or "JumpTo" => 1,
            _ => 0
        };
        foreach (var member in snapshot.Members)
        {
            if (!ShouldReceiveBroadcast(member, sender, (int)scope.Value)) continue;
            var recipient = member.CharacterId == senderId
                ? call.Session
                : sessionForCharacter?.Invoke(member.CharacterId);
            if (recipient?.CharacterId != member.CharacterId) continue;
            if (range == 1 && (systemId is null ||
                (recipient.GetInt("solarsystemid2") ?? recipient.GetInt("solarsystemid")) != systemId)) continue;
            if (range == 2)
            {
                var sameGrid = sameBubble?.Invoke(call.Session, recipient) ??
                    (call.Session.GetInt("ballparkid") is { } bubbleId && recipient.GetInt("ballparkid") == bubbleId);
                if (!sameGrid) continue;
            }

            recipient.SendNotification("OnFleetBroadcast", "fleetid",
                new PyStr(name), new PyInt((int)scope.Value), new PyLong(senderId),
                systemId is { } currentSystem ? new PyLong(currentSystem) : PyNone.Instance,
                WireId(Number(call.Arg(2))), WireId(Number(call.Arg(3))));
        }
        return None;
    }

    [Method("UpdateMemberInfo")]
    public async ValueTask<PyValue> UpdateMemberInfo(ServiceCall call)
    {
        if (FleetId(call) is not { } fleetId) return None;
        var characterId = CharacterId(call.Session);
        var before = await store.SnapshotAsync(fleetId, call.Cancellation);
        var oldMember = before?.Members.FirstOrDefault(member => member.CharacterId == characterId);
        if (oldMember is null) return None;
        var shipTypeId = call.Arg(0) is PyNone ? null : Number(call.Arg(0));
        if (shipTypeId is < 0) shipTypeId = null;
        var updated = await store.SetMemberShipTypeAsync(fleetId, characterId, shipTypeId, call.Cancellation);
        var after = await store.SnapshotAsync(fleetId, call.Cancellation);
        if (updated is not null && after is not null && updated != oldMember)
            NotifyMemberChanged(fleetId, oldMember, updated, after, call.Session);
        return None;
    }

    [Method("SetMotdEx")]
    public async ValueTask<PyValue> SetMotdEx(ServiceCall call)
    {
        if (FleetId(call) is not { } fleetId) return None;
        var motd = Text(call.Arg(0));
        if (!await store.SetMotdAsync(fleetId, CharacterId(call.Session), motd, call.Cancellation)) return None;
        var snapshot = await store.SnapshotAsync(fleetId, call.Cancellation);
        if (snapshot is not null)
            foreach (var member in snapshot.Members)
                (member.CharacterId == CharacterId(call.Session) ? call.Session : sessionForCharacter?.Invoke(member.CharacterId))
                    ?.SendNotification("OnFleetMotdChanged", "fleetid", new PyWString(motd), PyBool.True);
        return None;
    }

    [Method("GetMotd")]
    public async ValueTask<PyValue> GetMotd(ServiceCall call)
    {
        if (FleetId(call) is not { } fleetId ||
            await store.FleetIdForCharacterAsync(CharacterId(call.Session), call.Cancellation) != fleetId)
            return None;
        return new PyWString(await store.GetMotdAsync(fleetId, call.Cancellation) ?? "");
    }

    private async ValueTask<PyValue> SetMemberOptionsAsync(ServiceCall call,
        bool? acceptsFleetWarp = null, bool? acceptsConduitJumps = null, bool? acceptsFleetRegroups = null)
    {
        if (FleetId(call) is not { } fleetId) return None;
        var characterId = CharacterId(call.Session);
        var before = await store.SnapshotAsync(fleetId, call.Cancellation);
        var oldMember = before?.Members.FirstOrDefault(member => member.CharacterId == characterId);
        if (oldMember is null) return None;
        var updated = await store.SetMemberOptionsAsync(fleetId, characterId, acceptsFleetWarp,
            acceptsConduitJumps, acceptsFleetRegroups, call.Cancellation);
        var after = await store.SnapshotAsync(fleetId, call.Cancellation);
        if (updated is not null && after is not null && updated != oldMember)
            NotifyMemberChanged(fleetId, oldMember, updated, after, call.Session);
        return None;
    }

    private void NotifyFleetOptionsChanged(long fleetId, FleetSnapshot before, FleetSnapshot after, ClientSession issuer)
    {
        var oldOptions = FleetOptionsValue(before);
        var newOptions = FleetOptionsValue(after);
        foreach (var member in after.Members)
            (member.CharacterId == CharacterId(issuer) ? issuer : sessionForCharacter?.Invoke(member.CharacterId))
                ?.SendNotification("OnFleetOptionsChanged", "fleetid", oldOptions, newOptions);
    }

    private void NotifyMemberChanged(long fleetId, FleetMemberSnapshot oldMember, FleetMemberSnapshot newMember,
        FleetSnapshot after, ClientSession issuer)
    {
        var recipients = after.Members
            .Select(member => member.CharacterId == CharacterId(issuer) ? issuer : sessionForCharacter?.Invoke(member.CharacterId))
            .Where(session => session is not null)
            .Cast<ClientSession>()
            .Distinct()
            .ToArray();
        foreach (var recipient in recipients)
            recipient.SendNotification("OnFleetMemberChanged", "fleetid",
                new PyLong(newMember.CharacterId), new PyLong(fleetId),
                new PyLong(oldMember.WingId), new PyLong(oldMember.SquadId),
                new PyInt(oldMember.Role), new PyInt(oldMember.Job), MemberOptOutsValue(oldMember),
                new PyLong(newMember.WingId), new PyLong(newMember.SquadId),
                new PyInt(newMember.Role), new PyInt(newMember.Job), MemberOptOutsValue(newMember),
                after.Members.Count == 1 ? PyBool.True : PyBool.False);
    }

    private static PyValue FleetOptionsValue(FleetSnapshot snapshot) => Py.KeyVal([
        ("isFreeMove", snapshot.IsFreeMove ? PyBool.True : PyBool.False),
        ("isRegistered", snapshot.IsRegistered ? PyBool.True : PyBool.False),
        ("autoJoinSquadID", snapshot.AutoJoinSquadId is { } squadId ? new PyLong(squadId) : PyNone.Instance)
    ]);

    private static PyValue MemberOptOutsValue(FleetMemberSnapshot member) => Py.KeyVal([
        ("acceptsFleetWarp", member.AcceptsFleetWarp ? PyBool.True : PyBool.False),
        ("acceptsConduitJumps", member.AcceptsConduitJumps ? PyBool.True : PyBool.False),
        ("acceptsFleetRegroups", member.AcceptsFleetRegroups ? PyBool.True : PyBool.False)
    ]);

    private static bool ShouldReceiveBroadcast(FleetMemberSnapshot recipient, FleetMemberSnapshot sender, int scope)
    {
        if (scope == 3 || recipient.CharacterId == sender.CharacterId) return true;
        if (scope == 2)
            return sender.Role != FleetStore.Leader &&
                (recipient.Role == FleetStore.Leader ||
                 sender.SquadId > 0 && recipient.Role != FleetStore.Member && recipient.WingId == sender.WingId);
        if (scope == 1)
            return sender.Role == FleetStore.Leader ||
                sender.SquadId > 0 && recipient.SquadId == sender.SquadId ||
                sender.Role == FleetStore.WingCommander && recipient.WingId == sender.WingId;
        return false;
    }

    private static bool? BooleanValue(PyValue value) => value switch
    {
        PyBool boolean => boolean.Value,
        PyInt number => number.Value != 0,
        _ => null
    };

    private static PyValue? Field(PyValue value, string name)
    {
        if (value is not PyDict dict) return null;
        foreach (var (key, fieldValue) in dict.Entries)
            if (Eve.Macho.JsSemantics.StringValue(key) == name) return fieldValue;
        return null;
    }

    private static PyValue WireId(long? id) => id is { } value ? new PyLong(value) : PyNone.Instance;

    private void PublishMemberChanges(ClientSession issuer, long fleetId, FleetSnapshot before, FleetSnapshot? after)
    {
        var oldMembers = before.Members.ToDictionary(member => member.CharacterId);
        var newMembers = after?.Members.ToDictionary(member => member.CharacterId) ?? [];
        var recipients = new System.Collections.Generic.HashSet<ClientSession> { issuer };
        foreach (var member in before.Members.Concat(after?.Members ?? []))
            if (sessionForCharacter?.Invoke(member.CharacterId) is { } session && session.CharacterId == member.CharacterId)
                recipients.Add(session);

        foreach (var characterId in oldMembers.Keys.Union(newMembers.Keys))
        {
            oldMembers.TryGetValue(characterId, out var oldMember);
            newMembers.TryGetValue(characterId, out var newMember);
            if (oldMember == newMember) continue;
            foreach (var recipient in recipients)
            {
                if (recipient.CharacterId is not { } recipientId ||
                    !oldMembers.ContainsKey(recipientId) && !newMembers.ContainsKey(recipientId)) continue;
                recipient.SendNotification("OnFleetMemberChanged", "fleetid",
                    new PyLong(characterId), new PyLong(fleetId),
                    NullableLong(oldMember?.WingId), NullableLong(oldMember?.SquadId),
                    NullableInt(oldMember?.Role), NullableInt(oldMember?.Job),
                    oldMember is null ? PyNone.Instance : MemberOptOutsValue(oldMember),
                    NullableLong(newMember?.WingId), NullableLong(newMember?.SquadId),
                    NullableInt(newMember?.Role), NullableInt(newMember?.Job),
                    newMember is null ? PyNone.Instance : MemberOptOutsValue(newMember),
                    after?.Members.Count == 1 ? PyBool.True : PyBool.False);
            }
        }

        foreach (var member in before.Members.Concat(after?.Members ?? []).DistinctBy(member => member.CharacterId))
        {
            var session = member.CharacterId == CharacterId(issuer) ? issuer : sessionForCharacter?.Invoke(member.CharacterId);
            if (session?.CharacterId != member.CharacterId) continue;
            if (newMembers.TryGetValue(member.CharacterId, out var current))
                SetSession(session, fleetId, current);
            else
                ClearSession(session);
        }
    }

    private static PyValue NullableLong(long? value) => value is { } id ? new PyLong(id) : PyNone.Instance;
    private static PyValue NullableInt(int? value) => value is { } number ? new PyInt(number) : PyNone.Instance;
}

[Service("fleetMgr")]
public sealed partial class FleetManagerService(
    FleetStore store,
    Func<long, ClientSession?>? sessionOf = null,
    Func<ClientSession, ClientSession, bool>? sameBubble = null)
{
    private const string WatchlistSessionKey = "fleetWatchlist";
    private const string DamageWatchlistSessionKey = "fleetDamageWatchlist";
    private static readonly System.Collections.Generic.HashSet<string> BroadcastNames = new(StringComparer.Ordinal)
    {
        "EnemySpotted", "HealArmor", "HealShield", "HealCapacitor", "InPosition", "NeedBackup",
        "HoldPosition", "Location", "TravelTo", "JumpBeacon", "Target", "RepTarget", "WarpTo",
        "AlignTo", "JumpTo"
    };

    [Method("ForceLeaveFleet")]
    public async ValueTask<PyValue> ForceLeaveFleet(ServiceCall call)
    {
        var context = await ContextAsync(call);
        if (context is null) return PyNone.Instance;
        var (fleetId, before, _) = context.Value;
        var characterId = CharacterId(call.Session);
        if (await store.LeaveAsync(fleetId, characterId, call.Cancellation) is null) return PyNone.Instance;
        ClearSession(call.Session);
        var after = await store.SnapshotAsync(fleetId, call.Cancellation);
        PublishMemberChanges(call.Session, fleetId, before, after);
        return PyNone.Instance;
    }

    [Method("AddToWatchlist")]
    public async ValueTask<PyValue> AddToWatchlist(ServiceCall call)
    {
        var context = await ContextAsync(call);
        if (context is null) return PyNone.Instance;
        var (fleetId, snapshot, _) = context.Value;
        var members = snapshot.Members.Select(member => member.CharacterId).ToHashSet();
        var added = Ids(call.Arg(0));
        var favorites = Ids(call.Arg(1));
        var watchlist = favorites.Concat(added).Where(members.Contains).Distinct().Take(100).ToArray();
        SetWatchlist(call.Session, WatchlistSessionKey, watchlist);
        return PyNone.Instance;
    }

    [Method("RemoveFromWatchlist")]
    public async ValueTask<PyValue> RemoveFromWatchlist(ServiceCall call)
    {
        var context = await ContextAsync(call);
        if (context is null) return PyNone.Instance;
        var (fleetId, snapshot, _) = context.Value;
        var members = snapshot.Members.Select(member => member.CharacterId).ToHashSet();
        var removed = Number(call.Arg(0));
        var remaining = Ids(call.Arg(1)).Where(id => id != removed && members.Contains(id)).Distinct().Take(100).ToArray();
        SetWatchlist(call.Session, WatchlistSessionKey, remaining);
        var damageWatchlist = Ids(call.Session.Get(DamageWatchlistSessionKey) ?? PyNone.Instance)
            .Where(id => id != removed && members.Contains(id)).Distinct().Take(100).ToArray();
        SetWatchlist(call.Session, DamageWatchlistSessionKey, damageWatchlist);
        return PyNone.Instance;
    }

    [Method("RegisterForDamageUpdates")]
    public async ValueTask<PyValue> RegisterForDamageUpdates(ServiceCall call)
    {
        var context = await ContextAsync(call);
        if (context is null) return PyNone.Instance;
        var (fleetId, snapshot, _) = context.Value;
        var members = snapshot.Members.Select(member => member.CharacterId).ToHashSet();
        var watchlist = Ids(call.Arg(0)).Where(members.Contains).Distinct().Take(100).ToArray();
        SetWatchlist(call.Session, DamageWatchlistSessionKey, watchlist);
        return PyNone.Instance;
    }

    [Method("BroadcastToBubble")]
    public async ValueTask<PyValue> BroadcastToBubble(ServiceCall call)
    {
        await BroadcastAsync(call, systemOnly: false);
        return PyNone.Instance;
    }

    [Method("BroadcastToSystem")]
    public async ValueTask<PyValue> BroadcastToSystem(ServiceCall call)
    {
        await BroadcastAsync(call, systemOnly: true);
        return PyNone.Instance;
    }

    private async Task BroadcastAsync(ServiceCall call, bool systemOnly)
    {
        var context = await ContextAsync(call);
        if (context is null) return;
        var (fleetId, snapshot, sender) = context.Value;
        var name = Text(call.Arg(0));
        var scope = Number(call.Arg(1));
        if (!BroadcastNames.Contains(name) || scope is not (1 or 2 or 3)) return;
        var senderSystem = SystemId(call.Session);
        if (senderSystem is null) return;
        var senderIsInSpace = call.Session.GetInt("stationid") is null;
        if (!senderIsInSpace && !systemOnly) return;

        var itemId = Number(call.Arg(2));
        var typeId = Number(call.Arg(3));
        foreach (var member in snapshot.Members)
        {
            if (!ShouldReceive(member, sender, (int)scope.Value)) continue;
            var recipient = member.CharacterId == sender.CharacterId
                ? call.Session
                : sessionOf?.Invoke(member.CharacterId);
            if (recipient?.CharacterId != member.CharacterId || SystemId(recipient) != senderSystem) continue;
            if (systemOnly)
            {
                recipient.SendNotification("OnFleetBroadcast", "fleetid",
                    new PyStr(name), new PyInt((int)scope.Value), new PyLong(sender.CharacterId),
                    new PyLong(senderSystem.Value), WireId(itemId), WireId(typeId));
                continue;
            }

            var sameGrid = sameBubble?.Invoke(call.Session, recipient) ??
                (call.Session.GetInt("ballparkid") is { } bubbleId &&
                 recipient.GetInt("ballparkid") == bubbleId);
            if (sameGrid)
                recipient.SendNotification("OnFleetBroadcast", "fleetid",
                    new PyStr(name), new PyInt((int)scope.Value), new PyLong(sender.CharacterId),
                    new PyLong(senderSystem.Value), WireId(itemId), WireId(typeId));
        }
    }

    private async Task<(long FleetId, FleetSnapshot Snapshot, FleetMemberSnapshot Member)?> ContextAsync(ServiceCall call)
    {
        var characterId = CharacterId(call.Session);
        if (characterId <= 0 ||
            await store.FleetIdForCharacterAsync(characterId, call.Cancellation) is not { } fleetId ||
            await store.SnapshotAsync(fleetId, call.Cancellation) is not { } snapshot)
            return null;
        var member = snapshot.Members.FirstOrDefault(item => item.CharacterId == characterId);
        return member is null ? null : (fleetId, snapshot, member);
    }

    private void PublishMemberChanges(ClientSession issuer, long fleetId, FleetSnapshot before, FleetSnapshot? after)
    {
        var oldMembers = before.Members.ToDictionary(member => member.CharacterId);
        var newMembers = after?.Members.ToDictionary(member => member.CharacterId) ?? [];
        var recipients = new System.Collections.Generic.HashSet<ClientSession> { issuer };
        foreach (var member in before.Members.Concat(after?.Members ?? []))
        {
            if (sessionOf?.Invoke(member.CharacterId) is { } session && session.CharacterId == member.CharacterId)
                recipients.Add(session);
        }
        foreach (var id in oldMembers.Keys.Union(newMembers.Keys))
        {
            oldMembers.TryGetValue(id, out var oldMember);
            newMembers.TryGetValue(id, out var newMember);
            if (oldMember == newMember) continue;
            var oldOptOuts = oldMember is null ? PyNone.Instance : MemberOptOuts(oldMember);
            var newOptOuts = newMember is null ? PyNone.Instance : MemberOptOuts(newMember);
            foreach (var recipient in recipients)
            {
                if (recipient.CharacterId is not { } recipientId ||
                    recipientId != id && !newMembers.ContainsKey(recipientId)) continue;
                recipient.SendNotification("OnFleetMemberChanged", "fleetid",
                    new PyLong(id), new PyLong(fleetId),
                    NullableLong(oldMember?.WingId), NullableLong(oldMember?.SquadId),
                    NullableInt(oldMember?.Role), NullableInt(oldMember?.Job), oldOptOuts,
                    NullableLong(newMember?.WingId), NullableLong(newMember?.SquadId),
                    NullableInt(newMember?.Role), NullableInt(newMember?.Job), newOptOuts,
                    after?.Members.Count == 1 ? PyBool.True : PyBool.False);
            }
        }
        if (after is not null)
            foreach (var member in after.Members)
                if (sessionOf?.Invoke(member.CharacterId) is { } session && session.CharacterId == member.CharacterId)
                    SetSession(session, fleetId, member);
    }

    private static PyValue MemberOptOuts(FleetMemberSnapshot member) => Py.KeyVal([
        ("acceptsFleetWarp", member.AcceptsFleetWarp ? PyBool.True : PyBool.False),
        ("acceptsConduitJumps", member.AcceptsConduitJumps ? PyBool.True : PyBool.False),
        ("acceptsFleetRegroups", member.AcceptsFleetRegroups ? PyBool.True : PyBool.False)
    ]);

    private static bool ShouldReceive(FleetMemberSnapshot recipient, FleetMemberSnapshot sender, int scope)
    {
        if (scope == 3 || recipient.CharacterId == sender.CharacterId) return true;
        if (scope == 2)
            return sender.Role != FleetStore.Leader &&
                   (recipient.Role == FleetStore.Leader ||
                    sender.SquadId > 0 && recipient.Role != FleetStore.Member && recipient.WingId == sender.WingId);
        if (scope == 1)
            return sender.Role == FleetStore.Leader ||
                   sender.SquadId > 0 && recipient.SquadId == sender.SquadId ||
                   sender.Role == FleetStore.WingCommander && recipient.WingId == sender.WingId;
        return false;
    }

    private static IReadOnlyList<long> Ids(PyValue value) => Sequence(value)
        .Select(Number).Where(id => id is > 0).Select(id => id!.Value).ToArray();

    private static IReadOnlyList<PyValue> Sequence(PyValue value) => value switch
    {
        PyList list => list.Items,
        PyTuple tuple => tuple.Items,
        _ => []
    };

    private static void SetWatchlist(ClientSession session, string key, IReadOnlyList<long> ids) =>
        session.Set(key, new PyList(ids.Select(id => (PyValue)new PyLong(id)).ToArray()));

    private static void SetSession(ClientSession session, long fleetId, FleetMemberSnapshot member)
    {
        session.Set("fleetid", new PyLong(fleetId));
        session.Set("fleetrole", new PyInt(member.Role));
        session.Set("wingid", new PyLong(member.WingId));
        session.Set("squadid", new PyLong(member.SquadId));
    }

    private static void ClearSession(ClientSession session)
    {
        session.Set("fleetid", PyNone.Instance);
        session.Set("fleetrole", PyNone.Instance);
        session.Set("wingid", PyNone.Instance);
        session.Set("squadid", PyNone.Instance);
    }

    private static long CharacterId(ClientSession session) => session.CharacterId ?? session.GetInt("charid") ?? 0;

    private static long? SystemId(ClientSession session) =>
        session.GetInt("solarsystemid2") ?? session.GetInt("solarsystemid");

    private static PyValue WireId(long? id) => id is { } value ? new PyLong(value) : PyNone.Instance;

    private static PyValue NullableLong(long? value) => value is { } id ? new PyLong(id) : PyNone.Instance;
    private static PyValue NullableInt(int? value) => value is { } number ? new PyInt(number) : PyNone.Instance;

    private static long? Number(PyValue? value) => value switch
    {
        PyInt number => number.Value,
        PyLong number => number.Value,
        PyReal number when double.IsFinite(number.Value) && number.Value >= long.MinValue && number.Value <= long.MaxValue => (long)number.Value,
        PyStr text when long.TryParse(text.Value, out var parsed) => parsed,
        PyWString text when long.TryParse(text.Value, out var parsed) => parsed,
        _ => null
    };

    private static string Text(PyValue value) => value switch
    {
        PyStr text => text.Value,
        PyWString text => text.Value,
        _ => ""
    };
}

[Service("fleetProxy")]
public sealed partial class FleetProxyService(FleetStore store, Func<string, long, IReadOnlyList<PyValue>, ClientSession, CancellationToken, Task<PyValue?>>? fleetAdvertOperation = null, Func<long, ClientSession?>? sessionForCharacter = null)
{
    [Method("ApplyToJoinFleet")]
    public async ValueTask<PyValue> ApplyToJoinFleet(ServiceCall call)
    {
        var fleetId = call.Arg(0) switch
        {
            PyInt number => number.Value,
            PyLong number => number.Value,
            PyStr text when long.TryParse(text.Value, out var parsed) => parsed,
            PyWString text when long.TryParse(text.Value, out var parsed) => parsed,
            _ => 0
        };
        var characterId = call.Session.CharacterId ?? call.Session.GetInt("charid") ?? 0;
        if (fleetId <= 0 || characterId <= 0) return PyBool.False;
        var autoAccept = call.Arg(1) switch
        {
            PyBool boolean => boolean.Value,
            PyInt number => number.Value != 0,
            _ => false
        };
        if (autoAccept && await store.JoinAsync(fleetId, characterId, call.Cancellation) is { } member)
        {
            call.Session.Set("fleetid", new PyLong(fleetId));
            call.Session.Set("fleetrole", new PyInt(member.Role));
            call.Session.Set("wingid", new PyLong(member.WingId));
            call.Session.Set("squadid", new PyLong(member.SquadId));
            return PyBool.False;
        }
        return await store.RequestJoinAsync(fleetId, characterId, call.Cancellation) ? PyBool.True : PyBool.False;
    }
}


public sealed partial class FleetProxyService
{
    [Method("GetAvailableFleetAds")]
    public async ValueTask<PyValue> GetAvailableFleetAds(ServiceCall call)
    {
        if (CharacterId(call.Session) <= 0) return new PyDict([]);
        var result = await AdvertOperationAsync("GetAvailableFleetAds", call, []);
        return result is PyDict
            ? result
            : throw new InvalidOperationException("GetAvailableFleetAds persistence callback must return a fleetID-to-advert PyDict.");
    }

    [Method("AddFleetFinderAdvert")]
    public ValueTask<PyValue> AddFleetFinderAdvert(ServiceCall call) =>
        MutateFleetAdvertAsync("AddFleetFinderAdvert", call, [call.Arg(0)]);

    [Method("UpdateFleetAdvertWithNewLeader")]
    public ValueTask<PyValue> UpdateFleetAdvertWithNewLeader(ServiceCall call) =>
        MutateFleetAdvertAsync("UpdateFleetAdvertWithNewLeader", call, [call.Arg(0)]);

    [Method("RemoveFleetFinderAdvert")]
    public ValueTask<PyValue> RemoveFleetFinderAdvert(ServiceCall call) =>
        MutateFleetAdvertAsync("RemoveFleetFinderAdvert", call, []);

    [Method("GetMyFleetFinderAdvert")]
    public ValueTask<PyValue> GetMyFleetFinderAdvert(ServiceCall call) =>
        AdvertOperationAsync("GetMyFleetFinderAdvert", call, []);

    [Method("UpdateAdvertInfo")]
    public ValueTask<PyValue> UpdateAdvertInfo(ServiceCall call) =>
        MutateFleetAdvertAsync("UpdateAdvertInfo", call, [call.Arg(0), call.Arg(1)]);

    [Method("UpdateAdvertAllowedEntities")]
    public ValueTask<PyValue> UpdateAdvertAllowedEntities(ServiceCall call) =>
        MutateFleetAdvertAsync("UpdateAdvertAllowedEntities", call, [call.Arg(0)]);

    private async ValueTask<PyValue> MutateFleetAdvertAsync(string operation, ServiceCall call, IReadOnlyList<PyValue> args)
    {
        if (await BossFleetAsync(call) is not { } fleet) return PyNone.Instance;
        var result = await AdvertOperationAsync(operation, call, args);
        var after = await store.SnapshotAsync(fleet.FleetId, call.Cancellation);
        if (after is not null && fleet.Snapshot.IsRegistered != after.IsRegistered)
        {
            var oldOptions = FleetOptionsValue(fleet.Snapshot);
            var newOptions = FleetOptionsValue(after);
            foreach (var member in after.Members)
                sessionForCharacter?.Invoke(member.CharacterId)?.SendNotification(
                    "OnFleetOptionsChanged", "charid", oldOptions, newOptions);
        }
        return result;
    }

    private async ValueTask<PyValue> AdvertOperationAsync(string operation, ServiceCall call, IReadOnlyList<PyValue> args)
    {
        var characterId = CharacterId(call.Session);
        if (characterId <= 0) return PyNone.Instance;
        if (fleetAdvertOperation is null)
            throw new InvalidOperationException($"FleetFinder operation '{operation}' requires the Host FleetStore callback.");
        return await fleetAdvertOperation(operation, characterId, args, call.Session, call.Cancellation)
            ?? PyNone.Instance;
    }

    private async Task<(long FleetId, FleetSnapshot Snapshot)?> BossFleetAsync(ServiceCall call)
    {
        var characterId = CharacterId(call.Session);
        if (characterId <= 0 ||
            await store.FleetIdForCharacterAsync(characterId, call.Cancellation) is not { } fleetId ||
            await store.SnapshotAsync(fleetId, call.Cancellation) is not { } snapshot ||
            snapshot.Members.FirstOrDefault(member => member.CharacterId == characterId) is not { } member ||
            (member.Role != FleetStore.Leader && !FleetStore.HasBossJob(member.Job)))
            return null;
        return (fleetId, snapshot);
    }

    private static PyValue FleetOptionsValue(FleetSnapshot snapshot) => Py.KeyVal([
        ("autoJoinSquadID", snapshot.AutoJoinSquadId is { } squadId ? new PyLong(squadId) : PyNone.Instance),
        ("isFreeMove", snapshot.IsFreeMove ? PyBool.True : PyBool.False),
        ("isRegistered", snapshot.IsRegistered ? PyBool.True : PyBool.False)
    ]);

    private static long CharacterId(ClientSession session) => session.CharacterId ?? session.GetInt("charid") ?? 0;
}

public readonly record struct WarpCoordinates(int SolarSystemId, double X, double Y, double Z);

[Service("beyonce")]
public sealed partial class BeyonceService(SpaceSessions space, Func<long> nowUnixMs, FleetStore? fleetStore = null, Func<long, ClientSession?>? sessionForCharacter = null) : IBoundObjectReleaseHandler
{
    private readonly Binding _binding = new(nowUnixMs);

    private static PyValue None => PyNone.Instance;

    public void OnBoundObjectReleased(string oid, ClientSession? session) => _binding.Release(oid);

    private static double? Number(PyValue value) => value switch
    {
        PyInt number => number.Value,
        PyLong number => number.Value,
        PyReal number => number.Value,
        _ => null,
    };


    /// <summary>Resolves a bookmark only when the caller may warp to it. The host supplies the persisted/access-checked lookup.</summary>
    public Func<long, long, CancellationToken, Task<WarpCoordinates?>>? BookmarkTargetForCharacter { get; set; }

public Func<long, long, CancellationToken, Task<int>>? SkillLevelOfCharacter { get; set; }

    private static WarpCoordinates? Coordinates(PyValue value)
    {
        if (value is PyObject objectValue)
        {
            return Coordinates(objectValue.Args);
        }
        if (value is PyTuple tuple && tuple.Items.Count >= 3
            && Number(tuple.Items[0]) is { } tx
            && Number(tuple.Items[1]) is { } ty
            && Number(tuple.Items[2]) is { } tz)
        {
            return new WarpCoordinates(0, tx, ty, tz);
        }
        if (value is PyList list && list.Items.Count >= 3
            && Number(list.Items[0]) is { } lx
            && Number(list.Items[1]) is { } ly
            && Number(list.Items[2]) is { } lz)
        {
            return new WarpCoordinates(0, lx, ly, lz);
        }

        var entries = value switch
        {
            PyDict dictionary => dictionary.Entries,
            PyObjectEx objectEx => objectEx.Dict,
            _ => null,
        };
        if (entries is null)
        {
            return null;
        }

        double? Field(string name)
        {
            foreach (var entry in entries)
            {
                if (JsSemantics.PlainText(entry.Key) == name)
                {
                    return Number(entry.Value);
                }
            }
            return null;
        }

        return Field("x") is { } x && Field("y") is { } y && Field("z") is { } z
            ? new WarpCoordinates(0, x, y, z)
            : null;
    }

    /// <summary>The proxy node serves every ballpark.</summary>
    [Method("MachoResolveObject")]
    public PyValue MachoResolveObject(ServiceCall call) => new PyInt(MachoMessage.ProxyNodeId);

    /// <summary>
    /// <c>MachoBindObject((solarSystemID, groupID), (method, args, kwargs))</c>: binds the ballpark of the solar system the ship is in. Once the
    /// answer has gone the simulation sends the scene.
    /// </summary>
    [Method("MachoBindObject")]
    public async ValueTask<PyValue> MachoBindObject(ServiceCall call)
    {
        if (Binding.PlaceOf(call.Arg(0)) is not { } place || call.Session.GetInt("solarsystemid") != place || !space.IsInSpace(call.Session))
        {
            return None;
        }
        var (bound, oid) = _binding.Bind(call.Session);
        var result = await Binding.NestedAsync(this, call, oid);
        call.Session.AfterResponse(() => space.Bind(call.Session));
        return Py.Tuple(bound, result);
    }

    private const long BeyonceViewRoleMask = 140667119611150336L;

    /// <summary>The host can provide the live bubble lookup when the simulation exposes it.</summary>
    public Func<ServiceCall, long, CancellationToken, ValueTask<long?>>? BubbleIdForItem { get; set; }

    [Method("AdminGetBubbleID")]
    public async ValueTask<PyValue> AdminGetBubbleID(ServiceCall call)
    {
        var itemId = call.Arg(0) switch
        {
            PyInt integer => integer.Value,
            PyLong integer => integer.Value,
            _ => 0,
        };
        var roleMask = call.Session.GetInt("role") ??
            call.Session.GetInt("accountRole") ??
            call.Session.GetInt("accountRoles") ?? 0;
        if (itemId <= 0 || (roleMask & BeyonceViewRoleMask) == 0)
            return None;

        var bubbleId = BubbleIdForItem is { } lookup
            ? await lookup(call, itemId, call.Cancellation)
            : call.Session.GetInt("shipid") == itemId
                ? call.Session.GetInt("ballparkid")
                : null;
        return bubbleId is > 0 ? new PyLong(bubbleId.Value) : None;
    }

    /// <summary>The host supplies the persisted cyno jammer activation time for a solar system.</summary>
    public Func<long, CancellationToken, ValueTask<long?>>? CynoJammerOnlineSimTimeForSystem { get; set; }

    [Method("GetCynoJammerState")]
    public async ValueTask<PyValue> GetCynoJammerState(ServiceCall call)
    {
        var solarSystemId = call.Session.GetInt("solarsystemid2") ?? 0;
        if (solarSystemId == 0)
            solarSystemId = call.Session.GetInt("solarsystemid") ?? 0;
        if (solarSystemId <= 0 || CynoJammerOnlineSimTimeForSystem is null)
            return None;

        var activationFileTime = await CynoJammerOnlineSimTimeForSystem(solarSystemId, call.Cancellation);
        return activationFileTime is { } fileTime ? new PyLong(fileTime) : None;
    }

    /// <summary><c>GetFormations()</c>: the formations a fleet can fly in, which the client keeps for the length of the session.</summary>
    [Method("GetFormations")]
    public PyValue GetFormations(ServiceCall call) => ObjectCaching.MethodResult(Formations(), nowUnixMs());

    private static PyValue Formations()
    {
        static PyValue Point(int x, int y, int z) => Py.Tuple(new PyInt(x), new PyInt(y), new PyInt(z));
        return new PyList([
            Py.Tuple(new PyStr("Diamond"), new PyList([Point(100, 0, 0), Point(0, 100, 0), Point(-100, 0, 0), Point(0, -100, 0)])),
            Py.Tuple(new PyStr("Arrow"), new PyList([Point(100, 0, -50), Point(50, 0, 0), Point(-100, 0, -50), Point(-50, 0, 0)])),
        ]);
    }

    /// <summary><c>UpdateStateRequest()</c>: the client wants the state of every ball again.</summary>
    [Method("UpdateStateRequest")]
    public PyValue UpdateStateRequest(ServiceCall call)
    {
        if (_binding.Owns(call))
        {
            space.UpdateState(call.Session);
        }
        return None;
    }

    /// <summary><c>CmdSetSpeedFraction(fraction)</c>: how much of its top speed the ship is to fly at.</summary>
    [Method("CmdSetSpeedFraction")]
    public PyValue CmdSetSpeedFraction(ServiceCall call)
    {
        if (_binding.Owns(call) && Number(call.Arg(0)) is { } fraction)
        {
            space.Command(call.Session, SimCommands.SetSpeedFraction, fraction);
        }
        return None;
    }

    /// <summary><c>CmdGotoDirection(x, y, z)</c>: fly along this direction.</summary>
    [Method("CmdGotoDirection")]
    public PyValue CmdGotoDirection(ServiceCall call)
    {
        if (_binding.Owns(call) && Number(call.Arg(0)) is { } x && Number(call.Arg(1)) is { } y && Number(call.Arg(2)) is { } z)
        {
            space.Command(call.Session, SimCommands.GotoDirection, x, y, z);
        }
        return None;
    }

    /// <summary><c>CmdStop()</c>: slow to a stop.</summary>
    [Method("CmdStop")]
    public PyValue CmdStop(ServiceCall call)
    {
        if (_binding.Owns(call))
        {
            space.Command(call.Session, SimCommands.Stop);
        }
        return None;
    }

    /// <summary><c>CmdFollowBall(targetID, range)</c>: go to a body and stay <c>range</c> metres off it.</summary>
    [Method("CmdFollowBall")]
    public PyValue CmdFollowBall(ServiceCall call)
    {
        if (_binding.Owns(call) && Number(call.Arg(0)) is { } target)
        {
            space.Command(call.Session, SimCommands.FollowBall, target, Number(call.Arg(1)) ?? 0);
        }
        return None;
    }

    /// <summary><c>CmdOrbit(targetID, distance)</c>: circle a body <c>distance</c> metres off it.</summary>
    [Method("CmdOrbit")]
    public PyValue CmdOrbit(ServiceCall call)
    {
        if (_binding.Owns(call) && Number(call.Arg(0)) is { } target)
        {
            space.Command(call.Session, SimCommands.Orbit, target, Number(call.Arg(1)) ?? 0);
        }
        return None;
    }

    /// <summary><c>CmdAlignTo(targetID)</c> (or <c>dstID=</c>): turn towards a body and fly at it.</summary>
    [Method("CmdAlignTo")]
    public PyValue CmdAlignTo(ServiceCall call)
    {
        var target = Number(call.Arg(0)) is { } positional && positional != 0
            ? positional
            : call.Kwarg("dstID") is { } named ? Number(named) : null;
        if (_binding.Owns(call) && target is { } id && id > 0)
        {
            space.Command(call.Session, SimCommands.AlignTo, id);
        }
        return None;
    }

    /// <summary>
    /// <c>CmdWarpToStuff(type, target, minRange=)</c>: warp to a system body (<c>"item"</c>) or bookmark (<c>"bookmark"</c>). Other destination types,
    /// scanned sites and the like are told as a warp that cannot be done).
    /// </summary>
    [Method("CmdWarpToStuff")]
    public async ValueTask<PyValue> CmdWarpToStuff(ServiceCall call)
    {
        if (!_binding.Owns(call))
        {
            return None;
        }

        var type = JsSemantics.PlainText(call.Arg(0));
        if (type is not ("item" or "bookmark" or "char" or "position"))
        {
            throw WarpRefusal();
        }

        var coordinate = type == "position" ? Coordinates(call.Arg(1)) : null;
        var requestedTarget = type == "position" ? null : Number(call.Arg(1));
        if (type == "position")
        {
            if (coordinate is not { } parsedPoint
                || !double.IsFinite(parsedPoint.X)
                || !double.IsFinite(parsedPoint.Y)
                || !double.IsFinite(parsedPoint.Z))
            {
                throw WarpRefusal();
            }
        }
        else if (requestedTarget is not { } targetNumber
            || !double.IsFinite(targetNumber)
            || targetNumber <= 0
            || targetNumber > long.MaxValue
            || targetNumber != Math.Truncate(targetNumber))
        {
            throw WarpRefusal();
        }
        var targetId = requestedTarget is { } number ? (long)number : 0;
        var range = call.Kwarg("minRange") is { } named ? Number(named) ?? 0 : 0;
        if (!double.IsFinite(range))
        {
            throw WarpRefusal();
        }
        var fleetWarp = call.Kwarg("fleet") switch
        {
            PyBool flag => flag.Value,
            PyInt integer => integer.Value != 0,
            PyLong integer => integer.Value != 0,
            _ => false
        };
        var characterId = call.Session.CharacterId ?? call.Session.GetInt("charid") ?? 0;

        FleetSnapshot? fleet = null;
        FleetMemberSnapshot? requester = null;
        if (fleetWarp || type == "char")
        {
            if (fleetStore is null || characterId <= 0
                || await fleetStore.FleetIdForCharacterAsync(characterId, call.Cancellation) is not { } fleetId
                || await fleetStore.SnapshotAsync(fleetId, call.Cancellation) is not { } snapshot)
            {
                throw WarpRefusal();
            }
            fleet = snapshot;
            requester = snapshot.Members.FirstOrDefault(member => member.CharacterId == characterId);
            if (requester is null)
            {
                throw WarpRefusal();
            }
            if (fleetWarp && requester.Role is not (FleetStore.Leader or FleetStore.WingCommander or FleetStore.SquadCommander))
            {
                throw WarpRefusal();
            }
        }

        var callerSystem = space.SolarSystemOf(call.Session);
        if (type == "position")
        {
            if (callerSystem is not { } systemId || coordinate is not { } position)
            {
                throw WarpRefusal();
            }
            coordinate = position with { SolarSystemId = systemId };
        }
        else if (type == "bookmark")
        {
            if (characterId <= 0 || callerSystem is not { } systemId
                || BookmarkTargetForCharacter is not { } resolveBookmark
                || await resolveBookmark(characterId, targetId, call.Cancellation) is not { } bookmark
                || bookmark.SolarSystemId != systemId
                || !double.IsFinite(bookmark.X)
                || !double.IsFinite(bookmark.Y)
                || !double.IsFinite(bookmark.Z))
            {
                throw WarpRefusal();
            }
            coordinate = bookmark;
        }
        else if (type == "char")
        {
            var targetCharacterId = targetId;
            if (fleet is null || fleet.Members.All(member => member.CharacterId != targetCharacterId)
                || sessionForCharacter?.Invoke(targetCharacterId) is not { } targetSession
                || targetSession.CharacterId != targetCharacterId
                || callerSystem is not { } targetSystem
                || space.SolarSystemOf(targetSession) != targetSystem
                || space.ShipIdOf(targetSession) is not { } targetShipId)
            {
                throw WarpRefusal();
            }
            targetId = targetShipId;
        }

        if (!fleetWarp)
        {
            return coordinate is { } destinationPoint
                ? await space.WarpToPointAsync(call.Session, destinationPoint.X, destinationPoint.Y, destinationPoint.Z, range, call.Cancellation)
                : await space.WarpAsync(call.Session, targetId, range, call.Cancellation);
        }
        if (fleet is null || requester is null || sessionForCharacter is null || callerSystem is not { } warpSystem)
        {
            throw WarpRefusal();
        }

        var scopedMembers = fleet.Members.Where(member => requester.Role switch
        {
            FleetStore.Leader => true,
            FleetStore.WingCommander => member.WingId == requester.WingId,
            FleetStore.SquadCommander => member.WingId == requester.WingId && member.SquadId == requester.SquadId,
            _ => false
        });
        if (!scopedMembers.Any(member => member.CharacterId == characterId))
        {
            throw WarpRefusal();
        }

        var recipients = scopedMembers
            .Where(member => member.CharacterId == characterId || member.AcceptsFleetWarp)
            .Select(member => (
                Member: member,
                Session: member.CharacterId == characterId ? call.Session : sessionForCharacter(member.CharacterId)))
            .Where(recipient => recipient.Session is { } session && space.SolarSystemOf(session) == warpSystem)
            .OrderBy(recipient => recipient.Member.CharacterId == characterId ? 0 : 1)
            .ToArray();

        var formationValue = call.Kwarg("fleetFormationSettings");
        var settings = FleetWarpFormation.Parse(formationValue);
        if (formationValue is not null && formationValue != PyNone.Instance && settings is null)
        {
            throw WarpRefusal();
        }
        if (settings is { } requestedFormation)
        {
            var formationSkillLevel = SkillLevelOfCharacter is { } readFormationSkill
                ? await readFormationSkill(characterId, FleetWarpFormation.FormationSkillTypeId, call.Cancellation)
                : 0;
            var distanceSkillLevel = requestedFormation.Type is >= 1 and <= 4 && SkillLevelOfCharacter is { } readDistanceSkill
                ? await readDistanceSkill(characterId, FleetWarpFormation.FormationDistanceSkillTypeId, call.Cancellation)
                : 0;
            if (!FleetWarpFormation.Available(requestedFormation, formationSkillLevel, distanceSkillLevel))
            {
                throw WarpRefusal();
            }
        }
        Dictionary<long, WarpCoordinates> formationDestinations = [];
        if (settings is { } formation)
        {
            var commanderShip = space.ShipIdOf(call.Session);
            if (commanderShip is not { } commanderShipId
                || await space.PositionOfAsync(call.Session, commanderShipId, call.Cancellation) is not { } commanderStart)
            {
                throw WarpRefusal();
            }
            var landingPoint = coordinate is { } requestedPoint
                ? FleetWarpFormation.LandingPoint(requestedPoint, commanderStart, range)
                : await space.WarpTargetPointAsync(call.Session, targetId, range, call.Cancellation);
            if (landingPoint is not { } landing)
            {
                throw WarpRefusal();
            }

            Dictionary<long, WarpCoordinates> starts = [];
            if (formation.Type == 5)
            {
                foreach (var recipient in recipients.Where(item => item.Member.CharacterId != characterId))
                {
                    if (recipient.Session is not { } memberSession
                        || space.ShipIdOf(memberSession) is not { } shipId
                        || await space.PositionOfAsync(memberSession, shipId, call.Cancellation) is not { } memberStart)
                    {
                        continue;
                    }
                    starts[recipient.Member.CharacterId] = memberStart;
                }
            }

            var followerCount = recipients.Count(item => item.Member.CharacterId != characterId);
            var followerIndex = 0;
            foreach (var recipient in recipients.Where(item => item.Member.CharacterId != characterId))
            {
                starts.TryGetValue(recipient.Member.CharacterId, out var memberStart);
                if (FleetWarpFormation.Destination(
                    formation,
                    followerIndex++,
                    followerCount,
                    commanderStart,
                    landing,
                    starts.ContainsKey(recipient.Member.CharacterId) ? memberStart : null) is { } destination)
                {
                    formationDestinations[recipient.Member.CharacterId] = destination;
                }
            }
        }

        MachoWrappedException? requesterRefusal = null;
        foreach (var recipient in recipients)
        {
            if (recipient.Session is not { } session)
            {
                continue;
            }
            try
            {
                if (recipient.Member.CharacterId != characterId
                    && settings is not null)
                {
                    if (!formationDestinations.TryGetValue(recipient.Member.CharacterId, out var destination))
                    {
                        continue;
                    }
                    await space.WarpToPointAsync(session, destination.X, destination.Y, destination.Z, 0, call.Cancellation);
                }
                else if (coordinate is { } fleetPoint)
                {
                    await space.WarpToPointAsync(session, fleetPoint.X, fleetPoint.Y, fleetPoint.Z, range, call.Cancellation);
                }
                else
                {
                    await space.WarpAsync(session, targetId, range, call.Cancellation);
                }
            }
            catch (MachoWrappedException error) when (recipient.Member.CharacterId == characterId)
            {
                requesterRefusal = error;
            }
            catch (MachoWrappedException)
            {
                // A single member's warp refusal must not stop the rest of the command scope.
            }
        }
        if (requesterRefusal is not null)
        {
            throw requesterRefusal;
        }
        return None;
    }


    private static MachoWrappedException WarpRefusal() =>
        MachoErrors.ThrowUserError("CustomNotify", ("notify", new PyStr("You cannot warp there right now.")));




    /// <summary>Autopilot uses the same warp command but arrives 10 km from its destination.</summary>
    [Method("CmdWarpToStuffAutopilot")]
    public async ValueTask<PyValue> CmdWarpToStuffAutopilot(ServiceCall call)
    {
        if (!_binding.Owns(call))
        {
            return None;
        }
        var target = Number(call.Arg(0)) ?? 0;
        return await space.WarpAsync(call.Session, (long)target, 10_000, call.Cancellation);
    }

    /// <summary>
    /// <c>CmdStargateJump(fromGate, toGate, shipID)</c>: go into a stargate. The answer is the bound object and the time it can be used from again;
    /// the ship is in the other solar system a few seconds later, when the client is told the session has changed.
    /// </summary>
    [Method("CmdStargateJump")]
    public async ValueTask<PyValue> CmdStargateJump(ServiceCall call) =>
        _binding.Owns(call) && Number(call.Arg(0)) is { } gate
            ? await space.JumpAsync(call.Session, (long)gate, (long)(Number(call.Arg(1)) ?? 0), (long)(Number(call.Arg(2)) ?? 0), call.BoundObjectId, call.Cancellation)
            : None;

    /// <summary>
    /// <c>CmdDock(stationID, shipID)</c>: dock at a station. The answer is the time the docking was accepted; the docking itself is done a few
    /// seconds later, when the client is told the session has changed.
    /// </summary>
    [Method("CmdDock")]
    public async ValueTask<PyValue> CmdDock(ServiceCall call) =>
        _binding.Owns(call) && Number(call.Arg(0)) is { } station ? await space.DockAsync(call.Session, (long)station, call.Cancellation) : None;
}


internal readonly record struct FleetFormationSettings(
    int Type,
    double SpacingMeters,
    double SizeMeters,
    int SpacingSkillLevel = 0,
    int SizeSkillLevel = 0);

internal static class FleetWarpFormation
{
internal const long FormationSkillTypeId = 57317;
    internal const long FormationDistanceSkillTypeId = 57318;

    internal static FleetFormationSettings? Parse(PyValue? value)
    {
        var fields = value switch
        {
            PyDict dictionary => dictionary.Entries,
            PyObjectEx objectEx => objectEx.Dict,
            _ => null,
        };
        double? Field(string name)
        {
            if (fields is null) return null;
            foreach (var entry in fields)
            {
                if (JsSemantics.PlainText(entry.Key) == name)
                {
                    return entry.Value switch
                    {
                        PyInt integer => integer.Value,
                        PyLong integer => integer.Value,
                        PyReal real => real.Value,
                        _ => null,
                    };
                }
            }
            return null;
        }

        var rawType = Field("formationType");
        if (fields is null || rawType is not { } type || !double.IsFinite(type)
            || type != Math.Truncate(type) || type is < 0 or > 5)
        {
            return null;
        }

        var rawSpacing = Field("formationSpacing");
        var rawSize = Field("formationSize");
        return new FleetFormationSettings(
            (int)type,
            Spacing(rawSpacing),
            Size(rawSize),
            SpacingSkillLevel(rawSpacing),
            SizeSkillLevel(rawSize));
    }

    internal static WarpCoordinates? Destination(
        FleetFormationSettings settings,
        int followerIndex,
        int followerCount,
        WarpCoordinates commanderStart,
        WarpCoordinates landingPoint,
        WarpCoordinates? followerStart)
    {
        if (settings.Type == 5)
        {
            if (followerStart is not { } current) return null;
            var relativeOffset = new Vector(
                current.X - commanderStart.X,
                current.Y - commanderStart.Y,
                current.Z - commanderStart.Z);
            if (Length(relativeOffset) > 200_000) return null;
            return Add(landingPoint, relativeOffset);
        }
        if (followerCount <= 0 || followerIndex < 0 || followerIndex >= followerCount)
        {
            return null;
        }

        var forward = Unit(new Vector(
            landingPoint.X - commanderStart.X,
            landingPoint.Y - commanderStart.Y,
            landingPoint.Z - commanderStart.Z)) ?? new Vector(1, 0, 0);
        var referenceUp = Math.Abs(forward.Y) < 0.9 ? new Vector(0, 1, 0) : new Vector(0, 0, 1);
        var right = Unit(Cross(forward, referenceUp)) ?? new Vector(1, 0, 0);
        var up = Cross(right, forward);
        var spacing = settings.SpacingMeters;
        var radius = settings.SizeMeters / 2;
        Vector layoutOffset = settings.Type switch
        {
            0 => SphereOffset(followerIndex, followerCount, Math.Min(2_000, radius)),
            1 => SphereOffset(followerIndex, followerCount, radius),
            2 => GridOffset(followerIndex, followerCount, spacing, radius, wall: false),
            3 => GridOffset(followerIndex, followerCount, spacing, radius, wall: true),
            4 => ArrowOffset(followerIndex, spacing, radius),
            _ => default,
        };
        var worldOffset = Add(Add(Scale(right, layoutOffset.X), Scale(up, layoutOffset.Y)), Scale(forward, layoutOffset.Z));
        return Add(landingPoint, worldOffset);
    }

    internal static WarpCoordinates LandingPoint(WarpCoordinates target, WarpCoordinates origin, double range)
    {
        var toward = Unit(new Vector(target.X - origin.X, target.Y - origin.Y, target.Z - origin.Z))
            ?? new Vector(1, 0, 0);
        return Add(target, Scale(toward, -Math.Max(0, range)));
    }

    private static double Spacing(double? raw) => raw switch
    {
        100 => 1_000,
        101 => 2_000,
        102 => 3_000,
        103 => 4_000,
        104 => 5_000,
        20 => 10_000,
        { } metres when metres is 1 or 2 or 3 or 4 or 5 or 10 => metres * 1_000,
        _ => 1_000,
    };

    private static double Size(double? raw) => raw switch
    {
        20 => 10_000,
        21 => 20_000,
        22 => 30_000,
        105 => 40_000,
        106 => 50_000,
        107 => 100_000,
        { } metres when metres is 10 or 20 or 30 or 40 or 50 or 100 => metres * 1_000,
        _ => 10_000,
    };

    private static int SpacingSkillLevel(double? raw) => raw switch
    {
        100 or 1 => 0,
        101 or 2 => 1,
        102 or 3 => 2,
        103 or 4 => 3,
        104 or 5 => 4,
        20 or 10 => 5,
        _ => 0,
    };

    private static int SizeSkillLevel(double? raw) => raw switch
    {
        20 or 10 => 0,
        21 => 1,
        22 or 30 => 2,
        105 or 40 => 3,
        106 or 50 => 4,
        107 or 100 => 5,
        _ => 0,
    };

    internal static bool Available(
        FleetFormationSettings settings,
        int formationSkillLevel,
        int distanceSkillLevel)
    {
        formationSkillLevel = Math.Clamp(formationSkillLevel, 0, 5);
        distanceSkillLevel = Math.Clamp(distanceSkillLevel, 0, 5);
        return settings.Type is >= 0 and <= 5
            && settings.Type <= formationSkillLevel
            && (settings.Type is not (>= 1 and <= 4) || settings.SizeSkillLevel <= distanceSkillLevel)
            && (settings.Type is not (>= 2 and <= 4) || settings.SpacingSkillLevel <= distanceSkillLevel);
    }

    private static Vector SphereOffset(int index, int count, double radius)
    {
        var zUnit = count == 1 ? 0 : 1 - 2 * (index + 0.5) / count;
        var radial = Math.Sqrt(Math.Max(0, 1 - zUnit * zUnit));
        var angle = index * 2.399963229728653;
        var fill = Math.Cbrt((index + 1.0) / count);
        var distance = radius * fill;
        return new Vector(Math.Cos(angle) * radial * distance, Math.Sin(angle) * radial * distance, zUnit * distance);
    }

    private static Vector GridOffset(
        int index,
        int count,
        double spacing,
        double radius,
        bool wall)
    {
        var columns = (int)Math.Ceiling(Math.Sqrt(count));
        var rows = (int)Math.Ceiling((double)count / columns);
        var column = index % columns;
        var row = index / columns;
        var x = (column - (columns - 1) / 2.0) * spacing;
        var y = (row - (rows - 1) / 2.0) * spacing;
        var extent = Math.Max(Math.Abs(x), Math.Abs(y));
        var scale = extent <= radius || extent == 0 ? 1 : radius / extent;
        x *= scale;
        y *= scale;
        return wall ? new Vector(x, y, 0) : new Vector(x, 0, y);
    }

    private static Vector ArrowOffset(int index, double spacing, double radius)
    {
        var row = index / 2 + 1;
        var side = index % 2 == 0 ? -1 : 1;
        var lateral = Math.Min(radius, row * spacing * 0.5);
        var depth = Math.Min(radius, row * spacing);
        return new Vector(side * lateral, 0, -depth);
    }

    private static WarpCoordinates Add(WarpCoordinates point, Vector offset) =>
        point with { X = point.X + offset.X, Y = point.Y + offset.Y, Z = point.Z + offset.Z };


    private static Vector Add(Vector left, Vector right) =>
        new(left.X + right.X, left.Y + right.Y, left.Z + right.Z);

    private static double Length(Vector value) =>
        Math.Sqrt(value.X * value.X + value.Y * value.Y + value.Z * value.Z);

    private static Vector? Unit(Vector value)
    {
        var length = Length(value);
        return length == 0 || !double.IsFinite(length)
            ? null
            : new Vector(value.X / length, value.Y / length, value.Z / length);
    }

    private static Vector Cross(Vector a, Vector b) =>
        new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);

    private static Vector Scale(Vector value, double factor) =>
        new(value.X * factor, value.Y * factor, value.Z * factor);

    private readonly record struct Vector(double X, double Y, double Z);
}
