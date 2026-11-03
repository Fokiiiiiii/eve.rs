using Eve.Events;
using Eve.Inventory;
using Eve.Ships;
using Npgsql;

namespace Eve.Host.Persistence;

public sealed partial class PersistenceTournamentMatches
{
    public static Task MigrateParticipantDeathsAsync(NpgsqlDataSource source, string schema = PersistenceTournamentStore.DefaultSchema, CancellationToken ct = default) =>
        Eve.Data.Migrations.ApplyAsync(source, Eve.Data.Sql.CheckSchema(schema),
        [new Eve.Data.Migration("0022_tournament_participant_death_chain", """
            ALTER TABLE match_participants ADD COLUMN return_ship_id bigint, ADD COLUMN death_home_id bigint;
            ALTER TABLE match_members ADD COLUMN eliminated boolean NOT NULL DEFAULT false;
            """)], ct);

    internal void RegisterParticipantDeathHandler()
    {
        events.Subscribe("tournament.participant.death_chain", ["space.transition"], async (context, ct) =>
        {
            var transition = context.Event.Payload<ShipTransitionEvent>() ?? throw new InvalidOperationException("Missing ship transition.");
            var board = transition.Kind == "board";
            var leave = transition.Kind == "leave-ship";
            if (board || leave)
            {
                // Board reverses ShipId/RelatedShipId compared with leave-ship.
                // Only follow a committed switch wholly inside the proven clone home.
                var previous = board ? transition.RelatedShipId : transition.ShipId;
                var next = board ? transition.ShipId : transition.RelatedShipId;
                if (transition.CharacterId <= 0 || previous is not > 0 || next is not > 0 ||
                    transition.FromFlag != ItemFlags.Hangar || transition.ToFlag != ItemFlags.Hangar ||
                    transition.FromLocationId is not > 0 || transition.FromLocationId != transition.ToLocationId) return;
                await using var follow = new NpgsqlCommand(Q("""
                    UPDATE {schema}.match_participants SET return_ship_id=$3,return_operation_id=gen_random_uuid()
                    WHERE character_id=$1 AND COALESCE(return_ship_id,ship_id)=$2 AND death_home_id=$4
                    """), context.Connection, context.Transaction);
                follow.Parameters.AddWithValue(transition.CharacterId); follow.Parameters.AddWithValue(previous.Value);
                follow.Parameters.AddWithValue(next.Value); follow.Parameters.AddWithValue(transition.FromLocationId.Value);
                await follow.ExecuteNonQueryAsync(ct);
                return;
            }
            var shipDeath = transition.Kind == "ship-destroyed";
            var capsuleDeath = transition.Kind == "capsule-destroyed";
            if ((!shipDeath && !capsuleDeath) || transition.CharacterId <= 0 || transition.ShipId <= 0 ||
                transition.RelatedShipId is not > 0 || transition.FromFlag != CharacterShips.InSpaceFlag ||
                (capsuleDeath && (transition.ToLocationId is not > 0 || transition.ToFlag != ItemFlags.Hangar))) return;
            await using (var gate = new NpgsqlCommand(Q("SELECT pg_advisory_xact_lock(hashtext('{schema}.release'))"), context.Connection, context.Transaction))
                await gate.ExecuteNonQueryAsync(ct);
            long? matchId;
            await using (var update = new NpgsqlCommand(Q("""
                UPDATE {schema}.match_participants SET return_ship_id=$3,death_home_id=$4,return_operation_id=gen_random_uuid()
                WHERE character_id=$1 AND COALESCE(return_ship_id,ship_id)=$2 RETURNING match_id
                """), context.Connection, context.Transaction))
            {
                update.Parameters.AddWithValue(transition.CharacterId); update.Parameters.AddWithValue(transition.ShipId);
                update.Parameters.AddWithValue(transition.RelatedShipId.Value);
                update.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Bigint,
                    Value = capsuleDeath ? transition.ToLocationId!.Value : DBNull.Value });
                matchId = (long?)await update.ExecuteScalarAsync(ct);
            }
            if (matchId is { } id)
            {
                await using var member = new NpgsqlCommand(Q("UPDATE {schema}.match_members SET eliminated=true WHERE match_id=$1 AND character_id=$2 AND EXISTS (SELECT 1 FROM {schema}.matches WHERE id=$1 AND state<6)"), context.Connection, context.Transaction);
                member.Parameters.AddWithValue(id); member.Parameters.AddWithValue(transition.CharacterId); await member.ExecuteNonQueryAsync(ct);
                await PublishUpdateAsync(context.Connection, context.Transaction, id, ct);
                await PublishPlayerStatusAsync(context.Connection, context.Transaction, id, ct);
            }
        }, StartAt.Beginning);
    }

    private async Task<bool> IsProvenDeathHomeAsync(TournamentReturn record, CancellationToken ct)
    {
        if (record.DeathHomeId is not { } home || tournamentCharacters is null) return false;
        await using var c = await source.OpenConnectionAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        var character = await tournamentCharacters.LockAndGetAsync(c, tx, record.CharacterId, ct);
        var active = await items.LockAndGetAsync(c, tx, record.CurrentShipId, ct);
        var confirmed = character is not null && character.ShipId == record.CurrentShipId && character.StationId == home &&
            active is not null && active.OwnerId == record.CharacterId && active.LocationId == home && active.Flag == ItemFlags.Hangar;
        await tx.CommitAsync(ct);
        return confirmed;
    }
}
