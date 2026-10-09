using Eve.Macho;
using Eve.Marshal;
using Eve.Space;

namespace Eve.Services.Space;

/// <summary>Durable fleet membership and hierarchy state. The fleet schema is supplied by the host migration.</summary>
public sealed partial class FleetStore(System.Data.Common.DbDataSource source, string schema = "fleet")
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
}
