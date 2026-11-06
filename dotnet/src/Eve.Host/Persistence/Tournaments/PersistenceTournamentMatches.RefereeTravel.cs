using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;
using Npgsql;
using NpgsqlTypes;

namespace Eve.Host.Persistence;

public sealed partial class PersistenceTournamentMatches
{
    private sealed record RefereeOrigin(long CharacterId, long ShipId, int SystemId, long? StationId, long? StructureId, double[]? Position, double[]? Direction, Guid ReturnOperation);

    public static Task MigrateRefereeTravelAsync(NpgsqlDataSource source, string schema = PersistenceTournamentStore.DefaultSchema, CancellationToken ct = default) =>
        Eve.Data.Migrations.ApplyAsync(source, Eve.Data.Sql.CheckSchema(schema), [new Eve.Data.Migration("0010_tournament_referee_travel", """
            CREATE TABLE referee_travel (
                character_id bigint PRIMARY KEY, target_system_id bigint NOT NULL, ship_id bigint NOT NULL,
                original_system_id integer NOT NULL, original_station_id bigint, original_structure_id bigint,
                original_position double precision[], lease uuid, lease_until_ms bigint,
                CHECK ((original_station_id IS NOT NULL)::int + (original_structure_id IS NOT NULL)::int + (original_position IS NOT NULL)::int = 1),
                CHECK (original_position IS NULL OR cardinality(original_position)=3)
            );
            """)], ct);

    public static Task MigrateRefereeDirectionAsync(NpgsqlDataSource source, string schema = PersistenceTournamentStore.DefaultSchema, CancellationToken ct = default) =>
        Eve.Data.Migrations.ApplyAsync(source, Eve.Data.Sql.CheckSchema(schema), [new Eve.Data.Migration("0013_referee_origin_direction", """
            ALTER TABLE referee_travel ADD COLUMN original_direction double precision[];
            ALTER TABLE referee_travel ADD CONSTRAINT referee_direction_vector CHECK (original_direction IS NULL OR cardinality(original_direction)=3);
            """)], ct);

    public static Task MigrateRefereeMoveIdsAsync(NpgsqlDataSource source, string schema = PersistenceTournamentStore.DefaultSchema, CancellationToken ct = default) =>
        Eve.Data.Migrations.ApplyAsync(source, Eve.Data.Sql.CheckSchema(schema), [new Eve.Data.Migration("0015_referee_move_ids", """
            ALTER TABLE referee_travel ADD COLUMN outbound_operation uuid;
            ALTER TABLE referee_travel ADD COLUMN return_operation uuid;
            """)], ct);

    public async ValueTask<PyValue> RefTravelAsync(TourneyRefTravelRequest request, CancellationToken ct)
    {
        if (space is null || sessions is null || scheduler is null || request.Session.CharacterId is not > 0 ||
            !ReferenceEquals(sessions.Of(request.Session.CharacterId.Value), request.Session) || request.Session.SessionId <= 0)
            throw new InvalidOperationException("Referee session or tournament space is unavailable.");
        var character = request.Session.CharacterId.Value;
        if (request.Operation == TourneyRefTravelOperation.TeleportToStation)
        {
            await ReturnRefereeAsync(character, request.SystemId, ct);
            return PyNone.Instance;
        }
        if (request.Operation != TourneyRefTravelOperation.TeleportToArena || request.SystemId is <= 0 or > int.MaxValue)
            throw new ArgumentException("Invalid referee travel operation.");
        var arena = (await arenas.ReadSystemsAsync(ct)).SingleOrDefault(x => x.SolarSystemId == request.SystemId)
            ?? throw new InvalidOperationException("Tournament arena is unavailable.");
        if (arena.BeaconIds.Count == 0) throw new InvalidOperationException("Tournament arena has no beacon.");
        var beacon = await deployed.GetAsync(arena.BeaconIds[0], ct) ?? throw new InvalidOperationException("Tournament beacon is unavailable.");
        if (beacon.SolarSystemId != request.SystemId || beacon.Position.Length != 3) throw new InvalidOperationException("Tournament beacon is invalid.");
        var ship = request.Session.GetInt("shipid");
        var station = request.Session.GetInt("stationid") is > 0 and var stationId ? stationId : (long?)null;
        var structure = request.Session.GetInt("structureid") is > 0 and var structureId ? structureId : (long?)null;
        if (ship is not > 0) throw new InvalidOperationException("Referee ship is unavailable.");
        bool alreadyJournaled;
        await using (var read = source.CreateCommand(Q("SELECT EXISTS(SELECT 1 FROM {schema}.referee_travel WHERE character_id=$1)")))
        { read.Parameters.AddWithValue(character); alreadyJournaled = (bool)(await read.ExecuteScalarAsync(ct))!; }
        (int SolarSystemId, IReadOnlyList<double>? Position)? location = null;
        (int SolarSystemId, IReadOnlyList<double> Position, IReadOnlyList<double> Direction)? pose = null;
        if (!alreadyJournaled)
        {
            location = await space.PilotPlaceAsync(request.Session, ct) ?? throw new InvalidOperationException("Referee location is unavailable.");
            pose = station is null && structure is null
                ? await space.PilotPoseAsync(request.Session, ct) ?? throw new InvalidOperationException("Referee pose is unavailable.")
                : null;
            if (pose is { } actual && (actual.SolarSystemId != location.Value.SolarSystemId || location.Value.Position is null))
                throw new InvalidOperationException("Referee location changed while recording the pose.");
            if (location.Value.SolarSystemId <= 0 || (station is > 0 && structure is > 0) ||
                ((station is > 0 || structure is > 0) == (location.Value.Position is not null)))
                throw new InvalidOperationException("Referee origin cannot be recorded safely.");
        }
        IReadOnlyList<double> heading;
        var token = Guid.NewGuid(); Guid operation;
        await using var c = await source.OpenConnectionAsync(ct); await using var tx = await c.BeginTransactionAsync(ct);
        await using (var gate = new NpgsqlCommand(Q("SELECT pg_advisory_xact_lock(hashtext('{schema}.release'))"), c, tx)) await gate.ExecuteNonQueryAsync(ct);
        if (!alreadyJournaled)
        await using (var insert = new NpgsqlCommand(Q("""
            INSERT INTO {schema}.referee_travel(character_id,target_system_id,ship_id,original_system_id,original_station_id,original_structure_id,original_position,original_direction,outbound_operation,return_operation)
            VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10) ON CONFLICT(character_id) DO NOTHING
            """), c, tx))
        {
            insert.Parameters.AddWithValue(character); insert.Parameters.AddWithValue(request.SystemId); insert.Parameters.AddWithValue(ship.Value); insert.Parameters.AddWithValue(location!.Value.SolarSystemId);
            insert.Parameters.Add(new NpgsqlParameter { NpgsqlDbType=NpgsqlDbType.Bigint,Value=(object?)station ?? DBNull.Value });
            insert.Parameters.Add(new NpgsqlParameter { NpgsqlDbType=NpgsqlDbType.Bigint,Value=(object?)structure ?? DBNull.Value });
            insert.Parameters.Add(new NpgsqlParameter { NpgsqlDbType=NpgsqlDbType.Array | NpgsqlDbType.Double,Value=(object?)pose?.Position.ToArray() ?? DBNull.Value });
            insert.Parameters.Add(new NpgsqlParameter { NpgsqlDbType=NpgsqlDbType.Array | NpgsqlDbType.Double,Value=(object?)pose?.Direction.ToArray() ?? DBNull.Value });
            insert.Parameters.AddWithValue(Guid.NewGuid()); insert.Parameters.AddWithValue(Guid.NewGuid());
            await insert.ExecuteNonQueryAsync(ct);
        }
        await using (var claim = new NpgsqlCommand(Q("""
            UPDATE {schema}.referee_travel SET lease=$4,lease_until_ms=$5,
                outbound_operation=COALESCE(outbound_operation,$7), return_operation=COALESCE(return_operation,$8)
            WHERE character_id=$1 AND target_system_id=$2 AND ship_id=$3 AND (lease_until_ms IS NULL OR lease_until_ms <= $6)
            RETURNING outbound_operation,original_direction,original_position
            """), c, tx))
        {
            claim.Parameters.AddWithValue(character); claim.Parameters.AddWithValue(request.SystemId); claim.Parameters.AddWithValue(ship.Value);
            claim.Parameters.AddWithValue(token); claim.Parameters.AddWithValue(scheduler.Now+30000); claim.Parameters.AddWithValue(scheduler.Now);
            claim.Parameters.AddWithValue(Guid.NewGuid()); claim.Parameters.AddWithValue(Guid.NewGuid());
            await using var row = await claim.ExecuteReaderAsync(ct);
            if (!await row.ReadAsync(ct)) throw new InvalidOperationException("Referee travel is already in progress or belongs to a different arena or ship.");
            operation = row.GetGuid(0);
            if (row.IsDBNull(1) && !row.IsDBNull(2)) throw new InvalidOperationException("Recorded in-space referee origin lacks a direction.");
            heading = row.IsDBNull(1) ? [-1d,0d,0d] : row.GetFieldValue<double[]>(1);
        }
        await events.PublishAsync(c,tx,"tournament.referee.travel_started",new { characterId=character, systemId=request.SystemId },cancellationToken:ct);
        await tx.CommitAsync(ct); events.Wake();
        if (!ReferenceEquals(sessions.Of(character),request.Session)) throw new InvalidOperationException("Referee session changed during travel.");
        var refusal = await space.RelocateAsync(request.Session, checked((int)request.SystemId), beacon.Position, heading, ct, operation.ToString("D"));
        if (refusal is not null) throw new InvalidOperationException(refusal);
        await ClearRefereeLeaseAsync(character,token,ct);
        return PyNone.Instance;
    }

    public async ValueTask<PyValue> RefHomeAsync(TourneyMatchActionRequest request, CancellationToken ct)
    {
        if (request.Session.CharacterId is not > 0 || sessions is null || !ReferenceEquals(sessions.Of(request.Session.CharacterId.Value), request.Session) ||
            request.Session.DockedLocationId is not null || request.Session.GetInt("solarsystemid") != request.SystemId)
            throw new InvalidOperationException("Referee session is not in the match system.");
        await using var c = await source.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(Q("""
            SELECT 1 FROM {schema}.matches m JOIN {schema}.match_referees r ON r.match_id=m.id
            WHERE m.id=$1 AND m.system_id=$2 AND r.oid=$3 AND r.session_id=$4 AND r.character_id=$5 AND NOT m.closing
            """), c);
        cmd.Parameters.AddWithValue(request.MatchId); cmd.Parameters.AddWithValue(request.SystemId); cmd.Parameters.AddWithValue(request.BoundObjectId);
        cmd.Parameters.AddWithValue(request.Session.SessionId); cmd.Parameters.AddWithValue(request.Session.CharacterId ?? 0);
        if (await cmd.ExecuteScalarAsync(ct) is null) throw new InvalidOperationException("Referee is not authorized for this match.");
        await ReturnRefereeAsync(request.Session.CharacterId!.Value, request.SystemId, ct);
        return PyNone.Instance;
    }

    public async Task<bool> ReturnCompletedRefereesAsync(long matchId, CancellationToken ct)
    {
        var refs = new List<(long Character,long System)>();
        await using (var c = await source.OpenConnectionAsync(ct))
        await using (var cmd = new NpgsqlCommand(Q("""
            SELECT r.character_id,r.system_id FROM {schema}.postgame_referees r
            JOIN {schema}.matches m ON m.id=r.match_id
            JOIN {schema}.referee_travel t ON t.character_id=r.character_id AND t.target_system_id=r.system_id
            WHERE m.id=$1 AND m.state=6 ORDER BY r.character_id
            """), c))
        {
            cmd.Parameters.AddWithValue(matchId); await using var rows = await cmd.ExecuteReaderAsync(ct);
            while (await rows.ReadAsync(ct)) refs.Add((rows.GetInt64(0),rows.GetInt64(1)));
        }
        if (sessions is null) throw new InvalidOperationException("Referee sessions are unavailable.");
        foreach (var referee in refs)
        {
            if (sessions.Of(referee.Character) is null) return false;
            await ReturnRefereeAsync(referee.Character,referee.System,ct);
        }
        return true;
    }

    private async Task ReturnRefereeAsync(long character, long targetSystem, CancellationToken ct)
    {
        if (space is null || sessions is null || scheduler is null) throw new InvalidOperationException("Referee return is unavailable.");
        var session = sessions.Of(character) ?? throw new InvalidOperationException("Referee is offline; return remains pending.");
        var token=Guid.NewGuid(); RefereeOrigin origin;
        await using (var c = await source.OpenConnectionAsync(ct))
        await using (var cmd = new NpgsqlCommand(Q("""
            UPDATE {schema}.referee_travel SET lease=$3,lease_until_ms=$4,return_operation=COALESCE(return_operation,$6)
            WHERE character_id=$1 AND target_system_id=$2 AND (lease_until_ms IS NULL OR lease_until_ms <= $5)
            RETURNING ship_id,original_system_id,original_station_id,original_structure_id,original_position,original_direction,return_operation
            """), c))
        {
            cmd.Parameters.AddWithValue(character); cmd.Parameters.AddWithValue(targetSystem); cmd.Parameters.AddWithValue(token);
            cmd.Parameters.AddWithValue(scheduler.Now+30000); cmd.Parameters.AddWithValue(scheduler.Now); cmd.Parameters.AddWithValue(Guid.NewGuid());
            await using var row = await cmd.ExecuteReaderAsync(ct);
            if (!await row.ReadAsync(ct)) throw new InvalidOperationException("Referee origin is absent or travel is in progress.");
            origin = new RefereeOrigin(character,row.GetInt64(0),row.GetInt32(1),row.IsDBNull(2)?null:row.GetInt64(2),row.IsDBNull(3)?null:row.GetInt64(3),row.IsDBNull(4)?null:row.GetFieldValue<double[]>(4),row.IsDBNull(5)?null:row.GetFieldValue<double[]>(5),row.GetGuid(6));
        }
        if (session.GetInt("shipid") != origin.ShipId || !ReferenceEquals(sessions.Of(character),session))
            throw new InvalidOperationException("Referee session or ship changed; return remains pending.");
        string? refusal;
        if ((origin.StationId ?? origin.StructureId) is { } dock)
            refusal = await space.ReturnTournamentPilotToDockAsync(session,origin.SystemId,dock,origin.ShipId,ct);
        else if (origin.Position is { Length: 3 } position && origin.Direction is { Length: 3 } direction)
            refusal = await space.RelocateAsync(session,origin.SystemId,position,direction,ct,origin.ReturnOperation.ToString("D"));
        else throw new InvalidOperationException("Referee origin is invalid.");
        if (refusal is not null) throw new InvalidOperationException(refusal);
        await using var finish = await source.OpenConnectionAsync(ct); await using var tx = await finish.BeginTransactionAsync(ct);
        await using var delete = new NpgsqlCommand(Q("DELETE FROM {schema}.referee_travel WHERE character_id=$1 AND lease=$2"),finish,tx);
        delete.Parameters.AddWithValue(character); delete.Parameters.AddWithValue(token);
        if (await delete.ExecuteNonQueryAsync(ct) != 1) throw new InvalidOperationException("Referee return lease was lost.");
        await events.PublishAsync(finish,tx,"tournament.referee.returned",new { characterId=character,systemId=origin.SystemId },cancellationToken:ct);
        await tx.CommitAsync(ct); events.Wake();
    }

    private async Task ClearRefereeLeaseAsync(long character, Guid token, CancellationToken ct)
    {
        await using var cmd = source.CreateCommand(Q("UPDATE {schema}.referee_travel SET lease=NULL,lease_until_ms=NULL WHERE character_id=$1 AND lease=$2"));
        cmd.Parameters.AddWithValue(character); cmd.Parameters.AddWithValue(token);
        if (await cmd.ExecuteNonQueryAsync(ct) != 1) throw new InvalidOperationException("Referee travel lease was lost.");
    }
}
