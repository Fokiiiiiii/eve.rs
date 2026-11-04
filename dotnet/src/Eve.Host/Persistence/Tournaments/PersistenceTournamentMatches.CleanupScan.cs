using Npgsql;

namespace Eve.Host.Persistence;

public sealed partial class PersistenceTournamentMatches
{
    public static Task MigrateCleanupScanAsync(NpgsqlDataSource source, string schema = PersistenceTournamentStore.DefaultSchema, CancellationToken ct = default) =>
        Eve.Data.Migrations.ApplyAsync(source, Eve.Data.Sql.CheckSchema(schema),
        [new Eve.Data.Migration("0020_tournament_cleanup_scan", """
            ALTER TABLE matches ADD COLUMN cleanup_scan_complete boolean NOT NULL DEFAULT false;
            ALTER TABLE match_cleanup_targets ADD COLUMN cleanup_lease uuid, ADD COLUMN cleanup_lease_until_ms bigint;
            ALTER TABLE match_cleanup_targets ADD COLUMN status text NOT NULL DEFAULT 'pending' CHECK (status IN ('pending','skipped'));
            ALTER TABLE match_cleanup_targets ADD COLUMN skip_reason text;
            """)], ct);
}
