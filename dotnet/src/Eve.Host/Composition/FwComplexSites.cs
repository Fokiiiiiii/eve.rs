using Eve.Content;
using Eve.Dungeons;
using Eve.FactionWarfare;
using Eve.Services.Space.Waves;
using Eve.Services;
using Npgsql;

namespace Eve.Host;

/// <summary>
/// The dungeon sites of the factional warfare complexes on the space runtime. A complex's site is a dungeon of its size's SDE archetype (the first
/// startable template is picked by the complex id), started for the complex; its NPC defenders are its waves, and the site is completed when they are all
/// destroyed. The pilots inside a complex are the pilots whose current dungeon is its site (the keeper's read of the session).
/// </summary>
internal sealed class FwComplexSites(
    DungeonWaveDirector sites,
    DungeonCatalog catalog,
    ContentRuntime content,
    Func<int, IReadOnlyList<ClientSession>> sessionsInSystem,
    Func<ClientSession, CancellationToken, Task<long?>> currentInstanceOf,
    Func<long, CancellationToken, Task<long>> warFactionOfCharacter) : IFwComplexSites
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
        // shortcut: the site sits at the system's origin; the scene gives no complex position (DungeonRoomLayout places the rooms around it)
        var start = await sites.StartForSystemAsync((int)solarSystemId, [0d, 0d, 0d], dungeon, $"factionwarfare:{complexId}", FwComplexes.Lifetime,
            connection, transaction, cancellationToken);
        return start.InstanceId ?? throw new InvalidOperationException($"complex {complexId} ({size}, dungeon {dungeon}): {start.Refusal}");
    }

    public async Task<bool> DefendersClearedAsync(long instanceId, CancellationToken cancellationToken) =>
        await content.GetAsync(instanceId, cancellationToken) is { State: "completed" };

    public async Task<IReadOnlyList<long>> FactionsInsideAsync(long solarSystemId, long instanceId, CancellationToken cancellationToken)
    {
        var factions = new List<long>();
        foreach (var session in sessionsInSystem((int)solarSystemId))
        {
            if (session.CharacterId is not { } characterId || await currentInstanceOf(session, cancellationToken) != instanceId)
                continue;
            factions.Add(await warFactionOfCharacter(characterId, cancellationToken));
        }
        return factions;
    }

    public Task EndAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long instanceId, CancellationToken cancellationToken) =>
        content.AbandonAsync(connection, transaction, instanceId, "the complex was resolved", cancellationToken);
}
