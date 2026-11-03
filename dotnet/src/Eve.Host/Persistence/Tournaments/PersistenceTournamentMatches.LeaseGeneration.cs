using Npgsql;

namespace Eve.Host.Persistence;

public sealed partial class PersistenceTournamentMatches
{
    public static Task MigrateRefereeLeaseGenerationAsync(NpgsqlDataSource source, string schema = PersistenceTournamentStore.DefaultSchema, CancellationToken ct = default) =>
        Eve.Data.Migrations.ApplyAsync(source, Eve.Data.Sql.CheckSchema(schema), [new Eve.Data.Migration("0023_tournament_referee_lease_generation", """
            ALTER TABLE match_referees ADD COLUMN lease_generation uuid NOT NULL DEFAULT '00000000-0000-0000-0000-000000000000';
            """)], ct);
}
