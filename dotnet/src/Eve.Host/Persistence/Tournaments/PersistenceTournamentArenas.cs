using Eve.Characters;
using Eve.Events;
using Eve.Inventory;
using Eve.Sde;
using Eve.Ships;
using Npgsql;

namespace Eve.Host.Persistence;

public sealed record TournamentBeaconImport(int TypeId, string Name, double[] Position, double Radius);
public sealed record TournamentArenaImport(int SolarSystemId, long OwnerCharacterId, TournamentBeaconImport[] Beacons);
public sealed record TournamentArenaSystem(int SolarSystemId, IReadOnlyList<long> BeaconIds);

/// <summary>Imports operator-defined arena beacons as actual inventory and deployed items in a single transaction.</summary>
public sealed class PersistenceTournamentArenas(NpgsqlDataSource source, SdeReader sde, PgCharacterStore characters,
    PgItemStore items, PgDeployedStore deployed, EventBus events, string schema = PersistenceTournamentStore.DefaultSchema)
{
    public const string BeaconKind = "tournament.beacon";
    public const string ArenaConfiguredTopic = "tournament.arena.configured";
    private string Q(string sql) => Eve.Data.Sql.In(Eve.Data.Sql.CheckSchema(schema), sql);

    public static Task MigrateAsync(NpgsqlDataSource source, string schema = PersistenceTournamentStore.DefaultSchema, CancellationToken cancellationToken = default) =>
        Eve.Data.Migrations.ApplyAsync(source, Eve.Data.Sql.CheckSchema(schema),
        [new Eve.Data.Migration("0002_tournament_arenas", """
            CREATE TABLE arena_systems (system_id bigint PRIMARY KEY CHECK (system_id > 0), owner_id bigint NOT NULL CHECK (owner_id > 0));
            CREATE TABLE arena_beacons (
                item_id bigint PRIMARY KEY CHECK (item_id > 0), system_id bigint NOT NULL REFERENCES arena_systems(system_id) ON DELETE CASCADE,
                ordinal integer NOT NULL CHECK (ordinal >= 0), UNIQUE (system_id, ordinal)
            );
            """),
         new Eve.Data.Migration("0003_tournament_arena_fingerprint", """
            ALTER TABLE arena_systems ADD COLUMN configuration_hash bytea NULL;
            """)], cancellationToken);

    /// <summary>Creates an arena once. Existing arena configuration is preserved on subsequent starts.</summary>
    public async Task ImportIfMissingAsync(TournamentArenaImport arena, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(arena);
        if (arena.SolarSystemId <= 0 || arena.OwnerCharacterId <= 0 || arena.Beacons is not { Length: > 0 } ||
            arena.Beacons.Any(b => b is null || b.TypeId <= 0 || string.IsNullOrWhiteSpace(b.Name) || System.Text.Encoding.UTF8.GetByteCount(b.Name) > ushort.MaxValue ||
                b.Position is not { Length: 3 } || b.Position.Any(v => !double.IsFinite(v)) || !double.IsFinite(b.Radius) ||
                b.Radius <= 0 || !float.IsFinite((float)b.Radius) || (float)b.Radius == 0))
            throw new ArgumentException("Invalid tournament arena or beacon placement.");
        if ((await sde.ReadAsync("mapSolarSystems", [arena.SolarSystemId], cancellationToken)).Count != 1)
            throw new ArgumentException("Tournament arena system is not in the pinned SDE.");
        var typeIds = arena.Beacons.Select(b => b.TypeId).Distinct().ToArray();
        var types = await new ItemTypeCatalog(sde).GetAsync(typeIds, cancellationToken);
        if (types.Count != typeIds.Length || types.Values.Any(t => t.CategoryId != 2 || t.GroupId != 310))
            throw new ArgumentException("Tournament arena items must be SDE celestial beacons.");
        var typeRows = await sde.ReadAsync("types", typeIds.Select(id => (long)id), cancellationToken);
        if (typeRows.Count != typeIds.Length || typeRows.Any(row => !row.Doc.TryGetProperty("graphicID", out var graphic) || !graphic.TryGetInt32(out var value) || value <= 0))
            throw new ArgumentException("Tournament beacon types require an SDE graphic.");

        // Record serialization fixes property ordering; array ordering and exact coordinates are significant.
        var fingerprint = System.Security.Cryptography.SHA256.HashData(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(arena));
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var owner = await characters.LockAndGetAsync(connection, transaction, arena.OwnerCharacterId, cancellationToken);
        if (owner is null || owner.IsDeleted) throw new ArgumentException("Tournament arena owner is not an existing character.");
        await using (var create = new NpgsqlCommand(Q("INSERT INTO {schema}.arena_systems (system_id,owner_id,configuration_hash) VALUES ($1,$2,$3) ON CONFLICT DO NOTHING"), connection, transaction))
        {
            create.Parameters.AddWithValue((long)arena.SolarSystemId); create.Parameters.AddWithValue(arena.OwnerCharacterId); create.Parameters.AddWithValue(fingerprint);
            if (await create.ExecuteNonQueryAsync(cancellationToken) == 0)
            {
                await using var existing = new NpgsqlCommand(Q("SELECT configuration_hash FROM {schema}.arena_systems WHERE system_id=$1 FOR UPDATE"), connection, transaction);
                existing.Parameters.AddWithValue((long)arena.SolarSystemId);
                var stored = await existing.ExecuteScalarAsync(cancellationToken);
                if (stored is not byte[] hash || !System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(hash, fingerprint))
                    throw new InvalidOperationException("Tournament arena already exists with a different or unverified configuration.");
                await transaction.RollbackAsync(cancellationToken);
                return;
            }
        }
        for (var ordinal = 0; ordinal < arena.Beacons.Length; ordinal++)
        {
            var beacon = arena.Beacons[ordinal];
            var itemId = await items.CreateAsync(connection, transaction,
                new NewItem(beacon.TypeId, arena.OwnerCharacterId, arena.SolarSystemId, 0, 1, true, beacon.Name), cancellationToken);
            var type = types[beacon.TypeId];
            await deployed.SaveAsync(connection, transaction, new DeployedRecord(itemId, BeaconKind, arena.SolarSystemId,
                beacon.Name, beacon.TypeId, type.GroupId, beacon.Position, beacon.Radius, arena.OwnerCharacterId,
                checked((int)owner.CorporationId), checked((int)(owner.AllianceId ?? 0)), 0, long.MaxValue), cancellationToken);
            await using var insert = new NpgsqlCommand(Q("INSERT INTO {schema}.arena_beacons (item_id,system_id,ordinal) VALUES ($1,$2,$3)"), connection, transaction);
            insert.Parameters.AddWithValue(itemId); insert.Parameters.AddWithValue((long)arena.SolarSystemId); insert.Parameters.AddWithValue(ordinal);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
        await events.PublishAsync(connection, transaction, ArenaConfiguredTopic,
            new { solarSystemId = arena.SolarSystemId, ownerCharacterId = arena.OwnerCharacterId },
            key: $"system:{arena.SolarSystemId}", cancellationToken: cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        events.Wake();
    }

    /// <summary>Reads configured systems only while their inventory and deployed records still agree.</summary>
    public async Task<IReadOnlyList<TournamentArenaSystem>> ReadSystemsAsync(CancellationToken cancellationToken = default)
    {
        var rows = new List<(int SystemId, long OwnerId, long ItemId)>();
        await using (var connection = await source.OpenConnectionAsync(cancellationToken))
        await using (var command = new NpgsqlCommand(Q("SELECT a.system_id,a.owner_id,b.item_id FROM {schema}.arena_systems a JOIN {schema}.arena_beacons b USING(system_id) ORDER BY a.system_id,b.ordinal"), connection))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            while (await reader.ReadAsync(cancellationToken)) rows.Add((checked((int)reader.GetInt64(0)), reader.GetInt64(1), reader.GetInt64(2)));
        var catalog = new ItemTypeCatalog(sde);
        foreach (var ownerId in rows.Select(row => row.OwnerId).Distinct())
            if (await characters.GetAsync(ownerId, cancellationToken) is not { IsDeleted: false })
                throw new InvalidOperationException($"Tournament arena owner {ownerId} no longer exists.");
        foreach (var row in rows)
        {
            var item = await items.GetAsync(row.ItemId, cancellationToken);
            var beacon = await deployed.GetAsync(row.ItemId, cancellationToken);
            if (item is null || beacon is null || item.OwnerId != row.OwnerId || item.LocationId != row.SystemId || !item.Singleton || item.Quantity != 1 ||
                beacon.Kind != BeaconKind || beacon.SolarSystemId != row.SystemId || beacon.OwnerId != row.OwnerId || beacon.TypeId != item.TypeId ||
                beacon.GroupId != 310 || beacon.ExpiresAtMs != long.MaxValue || beacon.Name != item.CustomName ||
                beacon.Position.Length != 3 || beacon.Position.Any(v => !double.IsFinite(v)) || !double.IsFinite(beacon.Radius) || beacon.Radius <= 0 ||
                !float.IsFinite((float)beacon.Radius) || (float)beacon.Radius == 0 ||
                await catalog.GetAsync(item.TypeId, cancellationToken) is not { CategoryId: 2, GroupId: 310 })
                throw new InvalidOperationException($"Tournament arena beacon {row.ItemId} is inconsistent with its inventory or deployed state.");
        }
        return rows.GroupBy(row => row.SystemId).Select(group => new TournamentArenaSystem(group.Key, group.Select(row => row.ItemId).ToArray())).ToArray();
    }
}
