using Eve.Sde;
using Npgsql;

namespace Eve.Host.Persistence;

/// <summary>Operator-provided hull point values and prohibited fitted item types for a particular ruleset.</summary>
public sealed record TournamentHullPoints(int TypeId, int Points);
public sealed record TournamentRulesImport(long TournamentId, TournamentHullPoints[] HullPoints, int[] ProhibitedFitTypes);

public sealed partial class PersistenceTournamentMatches
{
    public static Task MigrateRulesAsync(NpgsqlDataSource source, string schema = PersistenceTournamentStore.DefaultSchema, CancellationToken ct = default) =>
        Eve.Data.Migrations.ApplyAsync(source, Eve.Data.Sql.CheckSchema(schema),
        [new Eve.Data.Migration("0011_tournament_rules", """
            CREATE TABLE tournament_rules (tournament_id bigint PRIMARY KEY REFERENCES tournaments(id), prohibited_fit_types integer[] NOT NULL);
            CREATE TABLE tournament_ship_points (tournament_id bigint NOT NULL REFERENCES tournament_rules(tournament_id) ON DELETE CASCADE,
                type_id integer NOT NULL, points integer NOT NULL CHECK (points > 0), PRIMARY KEY(tournament_id,type_id));
            """)], ct);

    /// <summary>Imports a complete immutable rule definition; an identical retry succeeds and a conflict is explicit.</summary>
    public async Task ImportRulesAsync(TournamentRulesImport rules, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(rules);
        if (rules.TournamentId <= 0 || rules.HullPoints is null || rules.HullPoints.Length == 0 || rules.ProhibitedFitTypes is null ||
            rules.HullPoints.Any(x => x.TypeId <= 0 || x.Points <= 0) ||
            rules.HullPoints.Select(x => x.TypeId).Distinct().Count() != rules.HullPoints.Length ||
            rules.ProhibitedFitTypes.Any(x => x <= 0) || rules.ProhibitedFitTypes.Distinct().Count() != rules.ProhibitedFitTypes.Length)
            throw new ArgumentException("Invalid tournament rules.");
        var fitTypes = rules.ProhibitedFitTypes.Distinct().Select(x => (long)x).ToArray();
        if (fitTypes.Length != 0 && (await sde.ReadAsync("types", fitTypes, ct)).Count != fitTypes.Length)
            throw new ArgumentException("Prohibited fitted types must exist in the pinned SDE.");
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        int[] allowed;
        await using (var cmd = new NpgsqlCommand(Q("SELECT ship_types FROM {schema}.tournaments WHERE id=$1 FOR UPDATE"), connection, tx))
        {
            cmd.Parameters.AddWithValue(rules.TournamentId);
            allowed = (int[]?)await cmd.ExecuteScalarAsync(ct) ?? throw new InvalidOperationException("Tournament configuration does not exist.");
        }
        if (!allowed.Order().SequenceEqual(rules.HullPoints.Select(x => x.TypeId).Order()))
            throw new ArgumentException("Hull points must cover exactly the configured allowed ships.");
        await using (var prior = new NpgsqlCommand(Q("SELECT prohibited_fit_types FROM {schema}.tournament_rules WHERE tournament_id=$1"), connection, tx))
        {
            prior.Parameters.AddWithValue(rules.TournamentId);
            if (await prior.ExecuteScalarAsync(ct) is int[] previous)
            {
                var points = new Dictionary<int,int>();
                await using var cmd = new NpgsqlCommand(Q("SELECT type_id,points FROM {schema}.tournament_ship_points WHERE tournament_id=$1"), connection, tx);
                cmd.Parameters.AddWithValue(rules.TournamentId); await using var rows = await cmd.ExecuteReaderAsync(ct);
                while (await rows.ReadAsync(ct)) points.Add(rows.GetInt32(0), rows.GetInt32(1));
                await rows.CloseAsync();
                if (!previous.Order().SequenceEqual(rules.ProhibitedFitTypes.Order()) || points.Count != rules.HullPoints.Length ||
                    rules.HullPoints.Any(x => !points.TryGetValue(x.TypeId, out var value) || value != x.Points))
                    throw new InvalidOperationException("Tournament rules already exist with different values.");
                await tx.CommitAsync(ct); return;
            }
        }
        await using (var insert = new NpgsqlCommand(Q("INSERT INTO {schema}.tournament_rules VALUES ($1,$2)"), connection, tx))
        { insert.Parameters.AddWithValue(rules.TournamentId); insert.Parameters.AddWithValue(rules.ProhibitedFitTypes.Order().ToArray()); await insert.ExecuteNonQueryAsync(ct); }
        foreach (var point in rules.HullPoints.OrderBy(x => x.TypeId))
        {
            await using var insert = new NpgsqlCommand(Q("INSERT INTO {schema}.tournament_ship_points VALUES ($1,$2,$3)"), connection, tx);
            insert.Parameters.AddWithValue(rules.TournamentId); insert.Parameters.AddWithValue(point.TypeId); insert.Parameters.AddWithValue(point.Points);
            await insert.ExecuteNonQueryAsync(ct);
        }
        await events.PublishAsync(connection, tx, "tournament.rules.configured", new { tournamentId = rules.TournamentId }, cancellationToken: ct);
        await tx.CommitAsync(ct); events.Wake();
    }
}
