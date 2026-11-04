using Eve.Scheduler;
using Eve.Universe;
using Npgsql;

namespace Eve.Wormholes;

/// <summary>
/// One end of a connection as the host places it in its system: the body (its ID is the connection's ID times two, plus one for the far end),
/// its type and group, its position and radius, and the far end it leads to (the other system, its body ID, that system's class, the far end's
/// position and radius, and the largest ship the wormhole takes).
/// </summary>
public sealed record WormholeEnd(long BodyId, int SolarSystemId, string Name, int TypeId, int GroupId, double[] Position, double Radius,
    int DestinationSystemId, long DestinationEnd, int DestinationClass, double[] DestinationPosition, double DestinationRadius, double MaxJumpMass);

/// <summary>
/// The world of the wormholes in the host: the stored connections, their durable lifecycle, and the ends the host places in space.
/// far end's bearing is taken from the same seed with a different salt. The radius is 3,000 m. The positions are not published (DEVIATIONS).
/// </summary>
public sealed class WormholeWorld
{
    public const double EndOffsetMeters = 50_000.0;
    public const double EndRadiusMeters = 3_000.0;
    public const int WormholeGroupId = 988;
    private const ulong FarSalt = 0x9E3779B97F4A7C15UL;

    private readonly NpgsqlDataSource _source;
    private readonly IReadOnlyDictionary<int, WormholeType> _types;
    private readonly IReadOnlyDictionary<int, int> _classes;
    private readonly IReadOnlyList<JSpaceSystem> _jSystems;

    public WormholeWorld(NpgsqlDataSource source, DurableScheduler scheduler, string schema, IReadOnlyDictionary<int, WormholeType> types,
        IReadOnlyDictionary<int, int> classes, IReadOnlyList<JSpaceSystem> jSystems)
    {
        _source = source;
        _types = types;
        _classes = classes;
        _jSystems = jSystems;
        Store = new WormholeStore(source, schema);
        Jobs = new WormholeJobs(scheduler, Store, types, classes);
    }

    /// <summary>Registers the expiry handler with the scheduler (once, before the scheduler starts).</summary>
    public void RegisterJobs() => Jobs.Register();

    public WormholeStore Store { get; }

    public WormholeJobs Jobs { get; }

    /// <summary>Raised for every connection that opens or collapses (not for changes captured by a transit).</summary>
    public event Action<WormholeChange>? Changed
    {
        add => Jobs.Changed += value;
        remove => Jobs.Changed -= value;
    }

    public static Task<IReadOnlyList<string>> MigrateAsync(NpgsqlDataSource source, string schema) => WormholeStore.MigrateAsync(source, schema);

    /// <summary>Generates the world from the seed once: every static of every J-space system gets its connection. A world already stored is left as it is.</summary>
    public async Task EnsureWorldAsync(ulong seed, long nowMs, CancellationToken cancellationToken = default)
    {
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        if (!await Store.AnyAsync(connection, transaction, cancellationToken))
        {
            foreach (var planned in WormholePlanner.PlanStatics(_jSystems, _classes, _types, seed))
            {
                await Jobs.OpenAsync(connection, transaction, planned, isStatic: true, nowMs, cancellationToken, _ => { });
            }

            foreach (var planned in TheraConnections.Plan(_classes, _types, seed ^ 0x7E4A5A9EUL))
            {
                await Jobs.OpenAsync(connection, transaction, planned, isStatic: true, nowMs, cancellationToken, _ => { });
            }
        }
        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>The ends of the active connections that are in the system: the host places them when the system's scene starts or a ship arrives.</summary>
    public async Task<IReadOnlyList<WormholeEnd>> EndsInSystemAsync(int systemId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        var ends = new List<WormholeEnd>();
        foreach (var stored in await Store.ActiveAtAsync(connection, null, systemId, cancellationToken))
        {
            if (stored.SourceSystemId == systemId)
                ends.Add(EndOf(stored, far: false));
            if (stored.DestinationSystemId == systemId)
                ends.Add(EndOf(stored, far: true));
        }
        return ends;
    }

    /// <summary>The end with this body ID while its connection is active, or null (a collapsed or unknown end).</summary>
    public async Task<WormholeEnd?> EndAsync(long bodyId, CancellationToken cancellationToken = default)
    {
        if (bodyId <= 0)
            return null;
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        var stored = await Store.GetAsync(connection, null, bodyId / 2, forUpdate: false, cancellationToken);
        return stored is { Active: true } ? EndOf(stored, far: bodyId % 2 == 1) : null;
    }

    /// <summary>The transit's outcome for a ship of this mass, without changing the wormhole (null when the connection is not active).</summary>
    public async Task<TransitResult?> PreviewAsync(long connectionId, double shipMassKg, CancellationToken cancellationToken = default)
    {
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        var stored = await Store.GetAsync(connection, null, connectionId, forUpdate: false, cancellationToken);
        return stored is { Active: true } ? WormholeLifecycle.Transit(_types[stored.TypeId], stored.MassKg, shipMassKg) : null;
    }

    /// <summary>One ship's transit: its mass comes off the wormhole and a collapse is raised to <paramref name="sink"/> (captured) or <see cref="Changed"/>.</summary>
    public async Task<TransitResult> TransitAsync(long connectionId, double shipMassKg, long nowMs, Action<WormholeChange>? sink = null, CancellationToken cancellationToken = default)
    {
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var result = await Jobs.TransitAsync(connection, transaction, connectionId, shipMassKg, nowMs, cancellationToken, sink);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    /// <summary>The end of a stored connection: the near end (<paramref name="far"/> false) or the far end.</summary>
    public WormholeEnd EndOf(StoredConnection stored, bool far)
    {
        var type = _types[stored.TypeId];
        var ownSystem = far ? stored.DestinationSystemId : stored.SourceSystemId;
        var farSystem = far ? stored.SourceSystemId : stored.DestinationSystemId;
        return new WormholeEnd(
            BodyId: stored.Id * 2 + (far ? 1 : 0),
            SolarSystemId: ownSystem,
            Name: "Wormhole " + stored.Code,
            TypeId: stored.TypeId,
            GroupId: WormholeGroupId,
            Position: EndPosition(stored.Seed, far),
            Radius: EndRadiusMeters,
            DestinationSystemId: farSystem,
            DestinationEnd: stored.Id * 2 + (far ? 0 : 1),
            DestinationClass: _classes.GetValueOrDefault(farSystem),
            DestinationPosition: EndPosition(stored.Seed, !far),
            DestinationRadius: EndRadiusMeters,
            MaxJumpMass: type.MaxJumpMassKg);
    }

    /// <summary>An end's position relative to its system's centre: 50 km on a bearing from the seed (the far end's bearing uses another salt).</summary>
    public static double[] EndPosition(ulong seed, bool far)
    {
        var random = new SeededRandom(far ? seed ^ FarSalt : seed);
        var bearing = random.NextDouble() * Math.PI * 2;
        return [Math.Cos(bearing) * EndOffsetMeters, 0, Math.Sin(bearing) * EndOffsetMeters];
    }
}
