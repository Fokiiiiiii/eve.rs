using Npgsql;

namespace Eve.Host.Persistence;

public sealed partial class PersistenceTournamentMatches
{
    public static Task MigrateCleanupStagesAsync(NpgsqlDataSource source, string schema = PersistenceTournamentStore.DefaultSchema, CancellationToken ct = default) =>
        Eve.Data.Migrations.ApplyAsync(source, Eve.Data.Sql.CheckSchema(schema),
        [new Eve.Data.Migration("0019_tournament_cleanup_stages", """
            ALTER TABLE match_cleanup_targets ADD COLUMN sim_seen boolean NOT NULL DEFAULT false;
            ALTER TABLE match_cleanup_targets ADD COLUMN sim_removed boolean NOT NULL DEFAULT false;
            """)], ct);
}
