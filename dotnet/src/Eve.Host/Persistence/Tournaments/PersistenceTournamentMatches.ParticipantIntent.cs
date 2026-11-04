using Npgsql;

namespace Eve.Host.Persistence;

public sealed partial class PersistenceTournamentMatches
{
    public static Task MigrateParticipantIntentAsync(NpgsqlDataSource source, string schema = PersistenceTournamentStore.DefaultSchema, CancellationToken ct = default) =>
        Eve.Data.Migrations.ApplyAsync(source, Eve.Data.Sql.CheckSchema(schema),
        [new Eve.Data.Migration("0017_tournament_participant_intent", """
            ALTER TABLE match_participants ADD COLUMN destination_beacon_id bigint;
            ALTER TABLE match_participants ADD COLUMN outbound_complete boolean NOT NULL DEFAULT false;
            """)], ct);
}
