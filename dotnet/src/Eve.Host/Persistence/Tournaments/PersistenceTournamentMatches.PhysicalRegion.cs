using Npgsql;

namespace Eve.Host.Persistence;

public sealed record TournamentRegionImport(int SolarSystemId, int CenterBeaconOrdinal, double RadiusMeters);

public sealed partial class PersistenceTournamentMatches
{
    public sealed record TournamentPhysicalRegion(int SystemId, long CenterBeaconId, double RadiusMeters, IReadOnlyList<double> Center);

    public static Task MigratePhysicalRegionsAsync(NpgsqlDataSource source, string schema = PersistenceTournamentStore.DefaultSchema, CancellationToken ct = default) =>
        Eve.Data.Migrations.ApplyAsync(source, Eve.Data.Sql.CheckSchema(schema),
        [new Eve.Data.Migration("0012_tournament_physical_regions", """
            CREATE TABLE arena_physical_regions (
                system_id bigint PRIMARY KEY, center_beacon_id bigint NOT NULL, radius_meters double precision NOT NULL
                    CHECK (radius_meters > 0 AND radius_meters < 'Infinity'::double precision)
            );
            CREATE TABLE match_cleanup_targets (
                match_id bigint NOT NULL REFERENCES matches(id) ON DELETE RESTRICT,
                item_id bigint NOT NULL, PRIMARY KEY (match_id,item_id)
            );
            """)], ct);

    /// <summary>Import an operator-selected physical arena center and radius. No radius is assumed from client-local UI.</summary>
    public async Task ConfigurePhysicalRegionAsync(int systemId, long centerBeaconId, double radiusMeters, CancellationToken ct = default)
    {
        if (systemId <= 0 || centerBeaconId <= 0 || !double.IsFinite(radiusMeters) || radiusMeters <= 0)
            throw new ArgumentException("Invalid physical arena region.");
        var arena = (await arenas.ReadSystemsAsync(ct)).SingleOrDefault(x => x.SolarSystemId == systemId)
            ?? throw new InvalidOperationException("The system has no configured tournament arena.");
        if (!arena.BeaconIds.Contains(centerBeaconId)) throw new InvalidOperationException("The center must be a configured arena beacon.");
        var beacon = await deployed.GetAsync(centerBeaconId, ct);
        if (beacon is null || beacon.SolarSystemId != systemId || beacon.Position.Length != 3 || !beacon.Position.All(double.IsFinite))
            throw new InvalidOperationException("The physical arena center is unavailable.");
        await using var c = await source.OpenConnectionAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        await using (var gate = new NpgsqlCommand(Q("SELECT pg_advisory_xact_lock(hashtext('{schema}.release'))"), c, tx)) await gate.ExecuteNonQueryAsync(ct);
        await using (var existing = new NpgsqlCommand(Q("SELECT center_beacon_id,radius_meters FROM {schema}.arena_physical_regions WHERE system_id=$1"), c, tx))
        {
            existing.Parameters.AddWithValue(systemId);
            await using var row = await existing.ExecuteReaderAsync(ct);
            if (await row.ReadAsync(ct) && row.GetInt64(0) == centerBeaconId && row.GetDouble(1) == radiusMeters)
            {
                await row.CloseAsync();
                await tx.CommitAsync(ct);
                return;
            }
        }
        await using (var active = new NpgsqlCommand(Q("SELECT EXISTS(SELECT 1 FROM {schema}.matches WHERE system_id=$1)"), c, tx))
        {
            active.Parameters.AddWithValue(systemId);
            if ((bool)(await active.ExecuteScalarAsync(ct))!) throw new InvalidOperationException("The region cannot change while a match is active.");
        }
        await using var command = new NpgsqlCommand(Q("""
            INSERT INTO {schema}.arena_physical_regions(system_id,center_beacon_id,radius_meters) VALUES ($1,$2,$3)
            ON CONFLICT(system_id) DO UPDATE SET center_beacon_id=EXCLUDED.center_beacon_id,radius_meters=EXCLUDED.radius_meters
            """), c, tx);
        command.Parameters.AddWithValue(systemId); command.Parameters.AddWithValue(centerBeaconId); command.Parameters.AddWithValue(radiusMeters);
        await command.ExecuteNonQueryAsync(ct);
        await tx.CommitAsync(ct);
    }

    private async Task<TournamentPhysicalRegion> ReadPhysicalRegionAsync(int systemId, CancellationToken ct)
    {
        long beaconId; double radius;
        await using (var c = await source.OpenConnectionAsync(ct))
        await using (var command = new NpgsqlCommand(Q("SELECT center_beacon_id,radius_meters FROM {schema}.arena_physical_regions WHERE system_id=$1"), c))
        {
            command.Parameters.AddWithValue(systemId);
            await using var row = await command.ExecuteReaderAsync(ct);
            if (!await row.ReadAsync(ct)) throw new InvalidOperationException("The physical arena region has not been configured.");
            beaconId = row.GetInt64(0); radius = row.GetDouble(1);
        }
        var arena = (await arenas.ReadSystemsAsync(ct)).SingleOrDefault(x => x.SolarSystemId == systemId);
        var beacon = await deployed.GetAsync(beaconId, ct);
        if (arena is null || !arena.BeaconIds.Contains(beaconId) || beacon is null || beacon.SolarSystemId != systemId ||
            beacon.Position.Length != 3 || !beacon.Position.All(double.IsFinite) || !double.IsFinite(radius) || radius <= 0)
            throw new InvalidOperationException("The configured physical arena region is no longer valid.");
        return new TournamentPhysicalRegion(systemId, beaconId, radius, beacon.Position);
    }
}
