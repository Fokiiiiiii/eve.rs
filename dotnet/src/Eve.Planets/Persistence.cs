using System.Text.Json;
using Eve.Data;
using Npgsql;

namespace Eve.Planets;

/// <summary>A colony as stored: the domain objects as JSON, with the time and the route id counter the document keeps.</summary>
public sealed record ColonyDocument(long PlanetId, long OwnerId, int PlanetTypeId, double Radius, int Level, long CurrentSimTime, long NextRouteId,
    List<Pin> Pins, List<Link> Links, List<Route> Routes)
{
    private static readonly JsonSerializerOptions Options = new() { IncludeFields = false };

    public string ToJson() => JsonSerializer.Serialize(this, Options);

    public static ColonyDocument FromJson(string json) =>
        JsonSerializer.Deserialize<ColonyDocument>(json, Options) ?? throw new InvalidDataException("colony document is empty");

    /// <summary>The colony the document describes, with its route ids continuing from the stored counter.</summary>
    public Colony ToColony(PlanetCatalog catalog, IResourceField field)
    {
        var colony = new Colony(catalog, field, PlanetId, PlanetTypeId, Radius, OwnerId) { Level = Level, CurrentSimTime = CurrentSimTime, RouteCounter = NextRouteId };
        foreach (var pin in Pins) colony.Pins[pin.Id] = pin;
        foreach (var link in Links) colony.Links[(link.End1, link.End2)] = link;
        foreach (var route in Routes) colony.Routes[route.Id] = route;
        return colony;
    }

    /// <summary>The document of a colony (its route counter is kept).</summary>
    public static ColonyDocument From(Colony colony) => new(colony.PlanetId, colony.OwnerId, colony.PlanetTypeId, colony.Radius,
        colony.Level, colony.CurrentSimTime, colony.RouteCounter,
        [.. colony.Pins.Values.OrderBy(p => p.Id)], [.. colony.Links.Values.OrderBy(l => l.End1).ThenBy(l => l.End2)], [.. colony.Routes.Values.OrderBy(r => r.Id)]);
}

/// <summary>The PostgreSQL side of PI: the colony documents and the launches. Writes run in the caller's transaction.</summary>
public sealed class PlanetStore(NpgsqlDataSource source, string schema = PlanetStore.DefaultSchema)
{
    public const string DefaultSchema = "planets";
      public string Schema => schema;

    public static Task<IReadOnlyList<string>> MigrateAsync(NpgsqlDataSource source, string schema = DefaultSchema, CancellationToken cancellationToken = default) =>
        Migrations.ApplyAsync(source, schema, Migrations.Embedded(typeof(PlanetStore).Assembly, "Migrations"), cancellationToken);

    /// <summary>The colony of an owner on a planet, locked for the change; null when there is none.</summary>
    public async Task<ColonyDocument?> LockAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long ownerId, long planetId, CancellationToken cancellationToken = default)
    {
        await using var command = new NpgsqlCommand($"SELECT doc::text FROM {schema}.colonies WHERE owner_id = $1 AND planet_id = $2 FOR UPDATE", connection, transaction)
        {
            Parameters = { new() { Value = ownerId }, new() { Value = planetId } },
        };
        var text = await command.ExecuteScalarAsync(cancellationToken) as string;
        return text is null ? null : ColonyDocument.FromJson(text);
    }

    /// <summary>Writes the colony (null: the colony is gone).</summary>
    public async Task SaveAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long ownerId, long planetId, ColonyDocument? doc, long nowMs, CancellationToken cancellationToken = default)
    {
        if (doc is null)
        {
            await using var delete = new NpgsqlCommand($"DELETE FROM {schema}.colonies WHERE owner_id = $1 AND planet_id = $2", connection, transaction)
            {
                Parameters = { new() { Value = ownerId }, new() { Value = planetId } },
            };
            await delete.ExecuteNonQueryAsync(cancellationToken);
            return;
        }
        await using var command = new NpgsqlCommand($"""
            INSERT INTO {schema}.colonies (owner_id, planet_id, doc, updated_ms) VALUES ($1, $2, $3::jsonb, $4)
            ON CONFLICT (owner_id, planet_id) DO UPDATE SET doc = EXCLUDED.doc, updated_ms = EXCLUDED.updated_ms
            """, connection, transaction)
        {
            Parameters = { new() { Value = ownerId }, new() { Value = planetId }, new() { Value = doc.ToJson() }, new() { Value = nowMs } },
        };
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>The colonies of an owner (the planets the planetMgr list shows).</summary>
    public async Task<IReadOnlyList<ColonyDocument>> ColoniesOfAsync(long ownerId, CancellationToken cancellationToken = default)
    {
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand($"SELECT doc::text FROM {schema}.colonies WHERE owner_id = $1 ORDER BY planet_id", connection)
        {
            Parameters = { new() { Value = ownerId } },
        };
        var found = new List<ColonyDocument>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) found.Add(ColonyDocument.FromJson(reader.GetString(0)));
        return found;
    }

    public async Task<ColonyDocument?> ReadAsync(long ownerId, long planetId, CancellationToken cancellationToken = default)
    {
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand($"SELECT doc::text FROM {schema}.colonies WHERE owner_id = $1 AND planet_id = $2", connection)
        {
            Parameters = { new() { Value = ownerId }, new() { Value = planetId } },
        };
        var text = await command.ExecuteScalarAsync(cancellationToken) as string;
        return text is null ? null : ColonyDocument.FromJson(text);
    }

    /// <summary>A launch (planetMgr.GetMyLaunchesDetails): recorded in the caller's transaction.</summary>
    public async Task<long> InsertLaunchAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, LaunchRecord launch, CancellationToken cancellationToken = default)
    {
        await using var command = new NpgsqlCommand($"""
            INSERT INTO {schema}.launches (owner_id, planet_id, solar_system_id, item_id, status, launch_time, expires_ticks, x, y, z, contents)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11::jsonb) RETURNING launch_id
            """, connection, transaction)
        {
            Parameters =
            {
                new() { Value = launch.OwnerId }, new() { Value = launch.PlanetId }, new() { Value = launch.SolarSystemId }, new() { Value = launch.ItemId },
                new() { Value = launch.Status }, new() { Value = launch.LaunchTime }, new() { Value = launch.ExpiresTicks },
                new() { Value = launch.X }, new() { Value = launch.Y }, new() { Value = launch.Z }, new() { Value = launch.ContentsJson },
            },
        };
        return (long)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    /// <summary>The launches of an owner that are not deleted and not yet over (their time is <paramref name="nowTicks"/> or later).</summary>
    public async Task<IReadOnlyList<LaunchRecord>> LaunchesOfAsync(long ownerId, long nowTicks, CancellationToken cancellationToken = default)
    {
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand($"""
            SELECT launch_id, owner_id, planet_id, solar_system_id, item_id, status, launch_time, expires_ticks, x, y, z, contents::text
            FROM {schema}.launches WHERE owner_id = $1 AND NOT deleted AND expires_ticks > $2 ORDER BY launch_id
            """, connection)
        {
            Parameters = { new() { Value = ownerId }, new() { Value = nowTicks } },
        };
        var found = new List<LaunchRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            found.Add(new LaunchRecord(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3), reader.GetInt64(4), reader.GetBoolean(5),
                reader.GetInt64(6), reader.GetInt64(7), reader.GetDouble(8), reader.GetDouble(9), reader.GetDouble(10), reader.GetString(11)));
        return found;
    }

    /// <summary>Marks the launches whose time is up as deleted (their containers stay where they are; the owner's items are not destroyed).</summary>
    public async Task ExpireLaunchesAsync(long ownerId, long nowTicks, CancellationToken cancellationToken = default)
    {
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand($"UPDATE {schema}.launches SET deleted = true WHERE owner_id = $1 AND NOT deleted AND expires_ticks <= $2", connection)
        {
            Parameters = { new() { Value = ownerId }, new() { Value = nowTicks } },
        };
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>Marks a launch deleted; false when the launch is not the owner's (or not there).</summary>
    public async Task<bool> DeleteLaunchAsync(long ownerId, long launchId, CancellationToken cancellationToken = default)
    {
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand($"UPDATE {schema}.launches SET deleted = true WHERE launch_id = $1 AND owner_id = $2 AND NOT deleted", connection)
        {
            Parameters = { new() { Value = launchId }, new() { Value = ownerId } },
        };
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }
}

/// <summary>A launch record: the commodities put in orbit by a command center, their item and their place.</summary>
public sealed record LaunchRecord(long LaunchId, long OwnerId, long PlanetId, long SolarSystemId, long ItemId, bool Status, long LaunchTime,
    long ExpiresTicks, double X, double Y, double Z, string ContentsJson);
