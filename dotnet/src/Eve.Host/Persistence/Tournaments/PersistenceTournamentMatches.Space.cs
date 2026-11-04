using Npgsql;

namespace Eve.Host.Persistence;

public sealed partial class PersistenceTournamentMatches
{
    // Original locations are written before a ship is moved. A match cannot be
    // deleted while a participant still needs a return or reconciliation.
    public static Task MigrateSpaceAsync(NpgsqlDataSource source, string schema = PersistenceTournamentStore.DefaultSchema, CancellationToken ct = default) =>
        Eve.Data.Migrations.ApplyAsync(source, Eve.Data.Sql.CheckSchema(schema),
        [new Eve.Data.Migration("0007_tournament_participants", """
            ALTER TABLE matches ADD COLUMN closing boolean NOT NULL DEFAULT false;
            ALTER TABLE matches ADD COLUMN restrict_move boolean NOT NULL DEFAULT false;
            ALTER TABLE matches ADD COLUMN restrict_warp boolean NOT NULL DEFAULT false;
            ALTER TABLE matches ADD COLUMN restrict_lock boolean NOT NULL DEFAULT false;
            CREATE TABLE match_teams (
                match_id bigint NOT NULL REFERENCES matches(id) ON DELETE CASCADE,
                team integer NOT NULL CHECK (team IN (0,1)),
                is_locked boolean NOT NULL DEFAULT false,
                PRIMARY KEY (match_id,team)
            );
            CREATE TABLE match_members (
                match_id bigint NOT NULL REFERENCES matches(id) ON DELETE CASCADE,
                team integer NOT NULL CHECK (team IN (0,1)),
                character_id bigint NOT NULL,
                locked boolean NOT NULL DEFAULT false,
                PRIMARY KEY (match_id,team,character_id),
                UNIQUE (match_id,character_id)
            );
            CREATE TABLE match_participants (
                match_id bigint NOT NULL REFERENCES matches(id) ON DELETE RESTRICT,
                team integer NOT NULL CHECK (team IN (0,1)),
                character_id bigint NOT NULL,
                ship_id bigint NOT NULL,
                session_id bigint NOT NULL,
                original_system_id bigint NOT NULL,
                original_station_id bigint,
                original_structure_id bigint,
                original_x double precision,
                original_y double precision,
                original_z double precision,
                PRIMARY KEY (match_id,character_id),
                UNIQUE (character_id),
                CHECK ((original_station_id IS NOT NULL)::int + (original_structure_id IS NOT NULL)::int <= 1),
                CHECK ((original_x IS NULL AND original_y IS NULL AND original_z IS NULL) OR
                       (original_x IS NOT NULL AND original_y IS NOT NULL AND original_z IS NOT NULL))
            );
            """)], ct);

    private async Task EnsureNoParticipantsAsync(NpgsqlConnection connection, NpgsqlTransaction tx, long matchId, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand(Q("SELECT EXISTS(SELECT 1 FROM {schema}.match_participants WHERE match_id=$1)"), connection, tx);
        command.Parameters.AddWithValue(matchId);
        if ((bool)(await command.ExecuteScalarAsync(ct))!)
            throw new InvalidOperationException("Tournament participants must be returned before resetting the match.");
    }
}
