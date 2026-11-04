using Eve.Macho;
using Eve.Marshal;
using Eve.Sde;
using Eve.Services;
using Eve.Services.Core;
using Npgsql;

namespace Eve.Host.Persistence;

/// <summary>Operator supplied tournament configuration. Ship types are resolved from the pinned SDE.</summary>
public sealed record TournamentConfiguration(long Id, string Name, int MaximumPoints, int MatchLength,
    int OvertimeLength, long? ExternalRulesetId, int[] AllowedShipTypes);

public sealed record TournamentSeries(long Id, string RedTeam, string BlueTeam, long SystemId,
    int CurrentMatch, int MatchCount);

public sealed record TournamentImport(TournamentConfiguration Configuration, TournamentSeries[] Series);

/// <summary>Durable tournament configuration and series. Arena provisioning is a separate authority.</summary>
public sealed class PersistenceTournamentStore(NpgsqlDataSource source, SdeReader sde,
    Func<CancellationToken, Task<IReadOnlyList<TournamentArenaSystem>>>? readArenas = null,
    bool includeActiveMatches = false, string schema = "tournament")
{
    public const string DefaultSchema = "tournament";
    private string Q(string sql) => Eve.Data.Sql.In(Eve.Data.Sql.CheckSchema(schema), sql);
    public static Task MigrateAsync(NpgsqlDataSource source, CancellationToken ct = default) =>
        Eve.Data.Migrations.ApplyAsync(source, DefaultSchema,
        [new Eve.Data.Migration("0001_tournament_configuration", """
            CREATE TABLE tournaments (
                id bigint PRIMARY KEY CHECK (id > 0), name text NOT NULL CHECK (length(name) > 0),
                maximum_points integer NOT NULL CHECK (maximum_points > 0),
                match_length integer NOT NULL CHECK (match_length > 0),
                overtime_length integer NOT NULL CHECK (overtime_length >= 0),
                external_ruleset_id bigint NULL, ship_types integer[] NOT NULL
            );
            CREATE TABLE series (
                tournament_id bigint NOT NULL REFERENCES tournaments(id) ON DELETE CASCADE,
                id bigint NOT NULL CHECK (id >= 0), red_team text NOT NULL, blue_team text NOT NULL,
                system_id bigint NOT NULL CHECK (system_id >= 0), current_match integer NOT NULL CHECK (current_match >= 0),
                match_count integer NOT NULL CHECK (match_count > 0 AND current_match < match_count),
                PRIMARY KEY (tournament_id, id)
            );
            """)], ct);

    /// <summary>Atomically imports a new configuration and its series after validating SDE ship types. Existing tournaments are left intact.</summary>
    public async Task ImportIfMissingAsync(TournamentConfiguration configuration, IReadOnlyCollection<TournamentSeries> series,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(series);
        if (configuration.Id <= 0 || string.IsNullOrWhiteSpace(configuration.Name) || configuration.MaximumPoints <= 0 ||
            configuration.MatchLength <= 0 || configuration.OvertimeLength < 0 || configuration.AllowedShipTypes is null ||
            configuration.AllowedShipTypes.Any(id => id <= 0) || configuration.AllowedShipTypes.Distinct().Count() != configuration.AllowedShipTypes.Length ||
            series.Any(x => x.Id < 0 || string.IsNullOrWhiteSpace(x.RedTeam) || string.IsNullOrWhiteSpace(x.BlueTeam) ||
                x.SystemId < 0 || x.CurrentMatch < 0 || x.MatchCount <= x.CurrentMatch) || series.Select(x => x.Id).Distinct().Count() != series.Count)
            throw new ArgumentException("Invalid tournament configuration or series.");
        var types = await new Eve.Inventory.ItemTypeCatalog(sde).GetAsync(configuration.AllowedShipTypes, ct);
        if (types.Count != configuration.AllowedShipTypes.Length || types.Values.Any(x => x.CategoryId != 6))
            throw new ArgumentException("Tournament ship types must be ships in the pinned SDE.");
        var systems = series.Select(x => x.SystemId).Where(x => x > 0).Distinct().ToArray();
        if ((await sde.ReadAsync("mapSolarSystems", systems, ct)).Count != systems.Length)
            throw new ArgumentException("Series systems must exist in the pinned SDE.");
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await using (var cmd = new NpgsqlCommand("""
            INSERT INTO tournament.tournaments (id,name,maximum_points,match_length,overtime_length,external_ruleset_id,ship_types)
            VALUES ($1,$2,$3,$4,$5,$6,$7) ON CONFLICT (id) DO NOTHING
            """, connection, tx))
        {
            cmd.Parameters.AddWithValue(configuration.Id); cmd.Parameters.AddWithValue(configuration.Name);
            cmd.Parameters.AddWithValue(configuration.MaximumPoints); cmd.Parameters.AddWithValue(configuration.MatchLength);
            cmd.Parameters.AddWithValue(configuration.OvertimeLength);
            cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Bigint, Value = (object?)configuration.ExternalRulesetId ?? DBNull.Value });
            cmd.Parameters.AddWithValue(configuration.AllowedShipTypes);
            if (await cmd.ExecuteNonQueryAsync(ct) == 0)
            {
                await tx.RollbackAsync(ct);
                return;
            }
        }
        await using (var delete = new NpgsqlCommand("DELETE FROM tournament.series WHERE tournament_id=$1", connection, tx))
        { delete.Parameters.AddWithValue(configuration.Id); await delete.ExecuteNonQueryAsync(ct); }
        foreach (var row in series)
        {
            await using var cmd = new NpgsqlCommand("INSERT INTO tournament.series VALUES ($1,$2,$3,$4,$5,$6,$7)", connection, tx);
            cmd.Parameters.AddWithValue(configuration.Id); cmd.Parameters.AddWithValue(row.Id); cmd.Parameters.AddWithValue(row.RedTeam);
            cmd.Parameters.AddWithValue(row.BlueTeam); cmd.Parameters.AddWithValue(row.SystemId);
            cmd.Parameters.AddWithValue(row.CurrentMatch); cmd.Parameters.AddWithValue(row.MatchCount);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
    }

    private static PyValue Wire(long id) => id is >= int.MinValue and <= int.MaxValue ? new PyInt(id) : new PyLong(id);

    public async ValueTask<PyValue?> QueryAsync(TourneyQueryRequest request, CancellationToken ct)
    {
        await using var connection = await source.OpenConnectionAsync(ct);
        if (request.Query == TourneyQuery.GetTourneySetup)
        {
            var entries = new List<KeyValuePair<PyValue, PyValue>>();
            await using var cmd = new NpgsqlCommand(Q("SELECT id,name FROM {schema}.tournaments ORDER BY id"), connection);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) entries.Add(new(Wire(reader.GetInt64(0)), new PyStr(reader.GetString(1))));
            await reader.CloseAsync();
            var active = new List<PyValue>();
            if (includeActiveMatches)
            {
                await using var activeCommand = new NpgsqlCommand(Q("SELECT system_id FROM {schema}.matches ORDER BY system_id"), connection);
                await using var activeReader = await activeCommand.ExecuteReaderAsync(ct);
                while (await activeReader.ReadAsync(ct)) active.Add(Wire(activeReader.GetInt64(0)));
            }
            await connection.CloseAsync();
            IReadOnlyList<TournamentArenaSystem> arenas = readArenas is null ? [] : await readArenas(ct);
            var systems = new PyDict([.. arenas.Select(arena => new KeyValuePair<PyValue, PyValue>(
                Wire(arena.SolarSystemId), Py.Tuple(new PyList([.. arena.BeaconIds.Select(Wire)]), new PyList([]))))]);
            return Py.Tuple(new PyDict(entries), systems, new PyList(active));
        }
        if (request.TournamentId is not { } id) return null;
        if (request.Query is TourneyQuery.GetPotentialMatches or TourneyQuery.GetAllCurrentSeriesMatchDetails)
        {
            var entries = new List<KeyValuePair<PyValue, PyValue>>();
            var sql = includeActiveMatches
                ? "SELECT s.id,s.red_team,s.blue_team,COALESCE(m.system_id,s.system_id),s.current_match,s.match_count FROM {schema}.series s LEFT JOIN {schema}.matches m ON m.tournament_id=s.tournament_id AND m.series_id=s.id WHERE s.tournament_id=$1 ORDER BY s.id"
                : "SELECT s.id,s.red_team,s.blue_team,s.system_id,s.current_match,s.match_count FROM {schema}.series s WHERE s.tournament_id=$1 ORDER BY s.id";
            await using var cmd = new NpgsqlCommand(Q(sql), connection);
            cmd.Parameters.AddWithValue(id);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                PyValue value = request.Query == TourneyQuery.GetPotentialMatches
                    ? Py.Dict(("redTeam", new PyStr(reader.GetString(1))), ("blueTeam", new PyStr(reader.GetString(2))), ("system", Wire(reader.GetInt64(3))))
                    : Py.Tuple(new PyInt(reader.GetInt32(4)), new PyInt(reader.GetInt32(5)));
                entries.Add(new(Wire(reader.GetInt64(0)), value));
            }
            return new PyDict(entries);
        }
        await using var detail = new NpgsqlCommand(Q("SELECT maximum_points,match_length,overtime_length,external_ruleset_id,ship_types FROM {schema}.tournaments WHERE id=$1"), connection);
        detail.Parameters.AddWithValue(id);
        await using var row = await detail.ExecuteReaderAsync(ct);
        if (!await row.ReadAsync(ct)) return null;
        var maximum = row.GetInt32(0); var length = row.GetInt32(1); var overtime = row.GetInt32(2);
        long? ruleset = row.IsDBNull(3) ? null : row.GetInt64(3); var typeIds = row.GetFieldValue<int[]>(4);
        await row.CloseAsync();
        var types = await sde.ReadAsync("types", typeIds.Select(x => (long)x), ct);
        static string ShipName(Eve.Sde.SdeRow type)
        {
            if (!type.Doc.TryGetProperty("name", out var names) || names.ValueKind != System.Text.Json.JsonValueKind.Object)
                throw new InvalidOperationException($"SDE ship {type.Key} has no localized name.");
            if (names.TryGetProperty("en", out var english) && english.GetString() is { Length: > 0 } name)
                return name;
            return names.EnumerateObject().Select(x => x.Value.GetString()).FirstOrDefault(x => !string.IsNullOrEmpty(x))
                ?? throw new InvalidOperationException($"SDE ship {type.Key} has no localized name.");
        }
        var ships = new PyList([.. types.Select(x => (PyValue)Py.Tuple(new PyStr(ShipName(x)), Wire(x.Key)))]);
        if (request.Query == TourneyQuery.QueryShipList) return Py.Dict(("allowedShipList", ships));
        return Py.Dict(("allowedShipList", ships), ("maximumPointsMatch", new PyInt(maximum)),
            ("matchLength", new PyInt(length)), ("overtimeLength", new PyInt(overtime)),
            ("externalRulesetID", ruleset is { } rulesetId ? Wire(rulesetId) : PyNone.Instance));
    }
}
