using Eve.Npc;
using Eve.Services.Space.Waves;
using Eve.Space;

namespace Eve.Services.Space;

/// <summary>The NPCs of the dungeon waves (<see cref="DungeonWaveDirector"/>): made with the same roll and the same spawn as the NPCs of <c>/npc</c>, and taken out of space.</summary>
public sealed partial class SpaceSessions
{
    /// <summary>
    /// Makes the NPCs of one wave beside <paramref name="origin"/> in the system: the selection rolled for <paramref name="amount"/> hulls that can be hurt, each spawned with the
    /// authority's profile and armament (<see cref="SpawnGroupAsync"/>). A refusal by the simulation stops the batch; the answer lists what was made.
    /// </summary>
    public async Task<DungeonSpawnResult> SpawnDungeonWaveAsync(int solarSystemId, double[] origin, NpcSelection selection, int amount, CancellationToken cancellationToken)
    {
        if (Npcs is not { } selector)
        {
            return new DungeonSpawnResult([], "the host has no NPC authority");
        }
        await EnsureNpcTypesAsync(selection.ShipTypes, cancellationToken);
        var plan = selector.Roll(selection, amount, type => NpcHull.HasHitPoints(_statics.Type(type)), Random.Shared.NextDouble);
        if (!plan.Succeeded)
        {
            return new DungeonSpawnResult([], $"the NPCs of '{selection.Name}' could not be rolled: {plan.Error}");
        }
        var ships = new List<long>();
        try
        {
            await SpawnGroupAsync(plan.Profiles, new SpawnAnchor(solarSystemId, 0, origin), cancellationToken, shipIds: ships);
            return new DungeonSpawnResult(ships, null);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            return new DungeonSpawnResult(ships, error.Message);
        }
    }

    /// <summary>Whether the character is in space in the solar system (a pilot that docked, jumped away or logged off is not).</summary>
    public bool IsPilotInSystem(long characterId, int solarSystemId) =>
        _pilots.Values.Any(pilot => pilot.CharacterId == characterId && pilot.SolarSystemId == solarSystemId);

    /// <summary>The sessions of the pilots in space in the solar system (a complex's presence reads each one's current dungeon).</summary>
    public IReadOnlyList<ClientSession> SessionsInSystem(int solarSystemId) =>
        _pilots.Values.Where(pilot => pilot.SolarSystemId == solarSystemId).Select(pilot => pilot.Session).ToArray();

    /// <summary>
    /// Takes NPCs out of space. The host forgets them first, so their death is not announced as a kill; the simulation destroys them (it has no other way to take a ship out).
    /// </summary>
    public void RemoveDungeonNpcs(IReadOnlyList<long> shipIds)
    {
        foreach (var shipId in shipIds)
        {
            _npcShips.TryRemove(shipId, out _);
            try
            {
                _sim.Destroy(shipId, new SimCredit(0, 0, 0, 0));
            }
            catch (SimUnavailableException error)
            {
                _log?.Invoke($"dungeon NPC {shipId} could not be taken out: {error.Message}");
            }
        }
    }
}

/// <summary>The space as the dungeon waves see it (<see cref="IDungeonNpcSpawner"/>).</summary>
public sealed class SpaceDungeonNpcs(SpaceSessions space) : IDungeonNpcSpawner
{
    public Task<DungeonSpawnResult> SpawnAsync(int solarSystemId, double[] origin, NpcSelection selection, int amount, CancellationToken cancellationToken) =>
        space.SpawnDungeonWaveAsync(solarSystemId, origin, selection, amount, cancellationToken);

    public bool IsPilotInSystem(long characterId, int solarSystemId) => space.IsPilotInSystem(characterId, solarSystemId);

    public void Remove(IReadOnlyList<long> shipIds) => space.RemoveDungeonNpcs(shipIds);
}
