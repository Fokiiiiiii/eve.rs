using Eve.Content;
using Eve.Dungeons;
using Eve.FactionWarfare;
using Eve.Services.Space.Waves;
using Eve.Services;
using Eve.Services.Space;
using Npgsql;

namespace Eve.Host;

/// <summary>
/// The dungeon sites of the factional warfare complexes on the space runtime. A complex's site is a dungeon of its size's SDE archetype (the first
/// startable template is picked by the complex id), started for the complex; its NPC defenders are its waves, and the site is completed when they are all
/// destroyed. The pilots inside a complex are the pilots within its capture radius of the site's point (the wiki: 30 km of the capture point).
/// </summary>
internal sealed class FwComplexSites(
    DungeonWaveDirector sites,
    DungeonCatalog catalog,
    ContentRuntime content,
    Func<int, IReadOnlyList<ClientSession>> sessionsInSystem,
    Func<ClientSession, CancellationToken, Task<WarpCoordinates?>> positionOf,
    Func<long, CancellationToken, Task<long>> warFactionOfCharacter,
    Func<long, CancellationToken, Task<IReadOnlyList<long>>> openInstancesIn) : IFwComplexSites
{
    /// <summary>The site templates of each complex size: the dungeons of its archetype that can start (ids in order).</summary>
    public IReadOnlyDictionary<ComplexSize, IReadOnlyList<long>> Templates { get; } = FwComplexes.Specs.ToDictionary(
        spec => spec.Size,
        spec => (IReadOnlyList<long>)catalog.Dungeons
            .Where(dungeon => dungeon.ArchetypeID == spec.ArchetypeId && dungeon.Waves.Count > 0 && sites.StartRefusalOf(dungeon.DungeonID) is null)
            .Select(dungeon => dungeon.DungeonID).OrderBy(id => id).ToList());

    public async Task<long> StartAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long solarSystemId, ComplexSize size, long complexId,
        CancellationToken cancellationToken)
    {
        var templates = Templates[size];
        if (templates.Count == 0)
            throw new InvalidOperationException($"the owner data has no {size} complex site");
        long dungeon = templates[(int)(complexId % templates.Count)];
        // the site sits outside its system's star (the star is at the origin: a site at its centre puts its pilots inside the star's collision)
        var origin = await SiteOriginAsync(connection, transaction, solarSystemId, cancellationToken);
        var start = await sites.StartForSystemAsync((int)solarSystemId, origin, dungeon, $"factionwarfare:{complexId}", FwComplexes.Lifetime,
            connection, transaction, cancellationToken);
        return start.InstanceId ?? throw new InvalidOperationException($"complex {complexId} ({size}, dungeon {dungeon}): {start.Refusal}");
    }

    /// <summary>The battlefield sites: the gated dungeons of the battlefield archetype that can start (ids in order).</summary>
    public IReadOnlyList<long> BattlefieldTemplates { get; } = catalog.Dungeons
        .Where(dungeon => dungeon.ArchetypeID == FwBattlefields.SiteArchetypeId && dungeon.Waves.Count > 0 && sites.StartRefusalOf(dungeon.DungeonID) is null)
        .Select(dungeon => dungeon.DungeonID).OrderBy(id => id).ToList();

    /// <summary>A battlefield's site: a battlefield-archetype dungeon at the system's site point; its gate is the room's (the pilots enter by it).</summary>
    public async Task<long> StartBattlefieldAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long solarSystemId, long battlefieldId,
        CancellationToken cancellationToken)
    {
        if (BattlefieldTemplates.Count == 0)
            throw new InvalidOperationException("the owner data has no battlefield site");
        long dungeon = BattlefieldTemplates[(int)(battlefieldId % BattlefieldTemplates.Count)];
        var origin = await OriginAsync(connection, transaction, solarSystemId, cancellationToken);
        var start = await sites.StartForSystemAsync((int)solarSystemId, origin, dungeon, $"factionwarfare:battlefield:{battlefieldId}",
            FwBattlefields.SiteLifetime, connection, transaction, cancellationToken);
        return start.InstanceId ?? throw new InvalidOperationException($"battlefield {battlefieldId} (dungeon {dungeon}): {start.Refusal}");
    }

    /// <summary>How far outside its star's surface a complex site sits, metres (shortcut: the planets are not checked; a site is 1e9 m out of the star).</summary>
    public const double SiteClearanceMeters = 1_000_000_000;

    /// <summary>The site's point: on the system's axis, outside the star's surface (the SDE mapStars radius of the system's star).</summary>
    public static async Task<double[]> SiteOriginAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long solarSystemId, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("SELECT (doc::jsonb ->> 'radius')::double precision FROM sde.\"mapStars\" WHERE doc::jsonb ->> 'solarSystemID' = $1", connection, transaction);
        command.Parameters.AddWithValue(solarSystemId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var radius = await command.ExecuteScalarAsync(cancellationToken) as double? ?? throw new InvalidOperationException($"system {solarSystemId} has no star in the SDE");
        return [0d, 0d, radius + SiteClearanceMeters];
    }

    /// <summary>The point a complex's site was started at (its dungeon instance's origin).</summary>
    private async Task<double[]> OriginOfAsync(long instanceId, CancellationToken cancellationToken) =>
        await content.GetAsync(instanceId, cancellationToken) is { } instance && instance.Data<DungeonInstanceData>() is { } data
            ? data.Origin
            : [0d, 0d, 0d];

    /// <summary>Whether a pilot's point is within the capture radius of a complex's site point.</summary>
    private async Task<bool> WithinCaptureAsync(WarpCoordinates at, long instanceId, CancellationToken cancellationToken)
    {
        var origin = await OriginOfAsync(instanceId, cancellationToken);
        var (dx, dy, dz) = (at.X - origin[0], at.Y - origin[1], at.Z - origin[2]);
        return Math.Sqrt(dx * dx + dy * dy + dz * dz) <= CaptureRadiusMeters;
    }

    public async Task<bool> DefendersClearedAsync(long instanceId, CancellationToken cancellationToken) =>
        await content.GetAsync(instanceId, cancellationToken) is { State: "completed" };

    /// <summary>The capture radius of a complex, metres (the wiki: a pilot must stay within 30 km of the capture point).</summary>
    public const double CaptureRadiusMeters = 30_000;

    public async Task<IReadOnlyList<FwComplexPilot>> PilotsInsideAsync(long solarSystemId, long instanceId, CancellationToken cancellationToken)
    {
        // shortcut: the server's read of the current dungeon knows the mission dungeons only, so presence is the distance to the site's point
        return await PilotsNearAsync(solarSystemId, await OriginOfAsync(instanceId, cancellationToken), CaptureRadiusMeters, cancellationToken);
    }

    /// <summary>
    /// The origin of a battlefield in a system: the complexes' site point moved <see cref="FwBattlefields.SiteOffsetMeters"/> along the x axis, so the
    /// battlefield's room and its points are apart from the complexes' (a pilot in one site is not counted in the other's presence).
    /// </summary>
    public async Task<double[]> OriginAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long solarSystemId, CancellationToken cancellationToken)
    {
        var site = await SiteOriginAsync(connection, transaction, solarSystemId, cancellationToken);
        return [site[0] + FwBattlefields.SiteOffsetMeters, site[1], site[2]];
    }

    /// <summary>The pilots of a system within a radius of a point, with their war factions (the pilots with a ship in space and a position).</summary>
    public async Task<IReadOnlyList<FwComplexPilot>> PilotsNearAsync(long solarSystemId, double[] at, double radius, CancellationToken cancellationToken)
    {
        var pilots = new List<FwComplexPilot>();
        foreach (var session in sessionsInSystem((int)solarSystemId))
        {
            if (session.CharacterId is not { } characterId || await positionOf(session, cancellationToken) is not { } where || where.SolarSystemId != solarSystemId)
                continue;
            var (dx, dy, dz) = (where.X - at[0], where.Y - at[1], where.Z - at[2]);
            if (Math.Sqrt(dx * dx + dy * dy + dz * dz) > radius)
                continue;
            pilots.Add(new FwComplexPilot(characterId, await warFactionOfCharacter(characterId, cancellationToken)));
        }
        return pilots;
    }

    public bool PilotsIn(long solarSystemId) => sessionsInSystem((int)solarSystemId).Count > 0;

    /// <summary>
    /// The open complex a pilot is inside (within the capture radius of the site's point), or null: the keeper's current dungeon of a complex pilot.
    /// shortcut: every complex of a system is at the origin, so a pilot inside one is inside all of them; the lowest instance answers.
    /// </summary>
    public async Task<long?> InstanceOfAsync(ClientSession session, CancellationToken cancellationToken)
    {
        if (await positionOf(session, cancellationToken) is not { } at)
            return null;
        foreach (var instance in await openInstancesIn(at.SolarSystemId, cancellationToken))
        {
            if (await WithinCaptureAsync(at, instance, cancellationToken))
                return instance;
        }
        return null;
    }

    public Task EndAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long instanceId, CancellationToken cancellationToken) =>
        content.AbandonAsync(connection, transaction, instanceId, "the complex was resolved", cancellationToken);
}
