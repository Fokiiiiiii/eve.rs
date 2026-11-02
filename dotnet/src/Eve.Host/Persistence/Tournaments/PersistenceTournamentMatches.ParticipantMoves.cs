using Npgsql;

namespace Eve.Host.Persistence;

public sealed partial class PersistenceTournamentMatches
{
    /// <summary>Each durable participant record owns separate stable IDs for outbound and return moves.</summary>
    public static Task MigrateParticipantMoveIdsAsync(NpgsqlDataSource source, string schema = PersistenceTournamentStore.DefaultSchema, CancellationToken ct = default) =>
        Eve.Data.Migrations.ApplyAsync(source, Eve.Data.Sql.CheckSchema(schema),
        [new Eve.Data.Migration("0016_tournament_participant_move_ids", """
            ALTER TABLE match_participants ADD COLUMN outbound_operation_id uuid NOT NULL DEFAULT gen_random_uuid();
            ALTER TABLE match_participants ADD COLUMN return_operation_id uuid NOT NULL DEFAULT gen_random_uuid();
            CREATE UNIQUE INDEX match_participant_outbound_operation ON match_participants(outbound_operation_id);
            CREATE UNIQUE INDEX match_participant_return_operation ON match_participants(return_operation_id);
            """)], ct);
}
