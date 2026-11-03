using Npgsql;

namespace Eve.Host.Persistence;

public sealed partial class PersistenceTournamentMatches
{
    public static Task MigrateCleanupGuardsAsync(NpgsqlDataSource source, string schema = PersistenceTournamentStore.DefaultSchema, CancellationToken ct = default) =>
        Eve.Data.Migrations.ApplyAsync(source, Eve.Data.Sql.CheckSchema(schema),
        [new Eve.Data.Migration("0018_tournament_cleanup_guards", """
            ALTER TABLE matches ADD COLUMN cleanup_required boolean NOT NULL DEFAULT false;
            ALTER TABLE match_cleanup_targets ADD COLUMN owner_id bigint NOT NULL;
            ALTER TABLE match_cleanup_targets ADD COLUMN launcher_id bigint NOT NULL;
            ALTER TABLE match_cleanup_targets ADD COLUMN type_id integer NOT NULL;
            ALTER TABLE match_cleanup_targets ADD COLUMN center_x double precision NOT NULL;
            ALTER TABLE match_cleanup_targets ADD COLUMN center_y double precision NOT NULL;
            ALTER TABLE match_cleanup_targets ADD COLUMN center_z double precision NOT NULL;
            ALTER TABLE match_cleanup_targets ADD COLUMN radius_meters double precision NOT NULL CHECK (radius_meters > 0 AND radius_meters < 'Infinity'::double precision);
            """)], ct);
}
