using Npgsql;

namespace Eve.FactionWarfare;

public enum ComplexSize : short
{
    Scout = 1,
    Small = 2,
    Medium = 3,
    Large = 4,
}

/// <summary>
/// A complex size: its SDE archetype (titles 660881, 660883, 660885, 660887), its capture time, the largest hull that may enter it (1 frigate, 2 destroyer,
/// 3 cruiser, 4 battlecruiser, 5 battleship), and the victory points of its capture.
/// </summary>
public sealed record ComplexSpec(ComplexSize Size, long ArchetypeId, int CaptureSeconds, int MaxHullSize, long VictoryPoints);

/// <summary>An open complex of an occupied system: its progress is signed (positive: the attacking side, negative: the defending side), in milliseconds.</summary>
public sealed record FwComplex(long ComplexId, long SolarSystemId, ComplexSize Size, long ProgressMs, long ProgressUpdatedMs, long SpawnedMs, long InstanceId);

/// <summary>
/// The dungeon site of a complex (the NPC defenders and the room the pilots are in). Implemented by the host on the space runtime: the site is a dungeon
/// instance started for the complex, its pilots are the pilots whose current dungeon is that instance.
/// </summary>
public interface IFwComplexSites
{
    /// <summary>Starts the complex's site in the system (in the caller's transaction) and returns its instance id.</summary>
    Task<long> StartAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long solarSystemId, ComplexSize size, long complexId, CancellationToken cancellationToken);

    /// <summary>Whether the site's NPC defenders are all destroyed (the site is completed).</summary>
    Task<bool> DefendersClearedAsync(long instanceId, CancellationToken cancellationToken);

    /// <summary>Whether a pilot is in the system now: a complex's site starts with its first pilot (its waves spawn into that pilot's scene).</summary>
    bool PilotsIn(long solarSystemId);

    /// <summary>The pilots in the complex now, with their war factions (0 for a pilot with none).</summary>
    Task<IReadOnlyList<FwComplexPilot>> PilotsInsideAsync(long solarSystemId, long instanceId, CancellationToken cancellationToken);

    /// <summary>Ends the complex's site (its pilots are taken out and its NPCs removed).</summary>
    Task EndAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long instanceId, CancellationToken cancellationToken);

    /// <summary>Starts a battlefield's site (its gated room, archetype <see cref="FwBattlefields.SiteArchetypeId"/>) in the system and returns its instance id.</summary>
    Task<long> StartBattlefieldAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long solarSystemId, long battlefieldId, CancellationToken cancellationToken);

    /// <summary>The point a battlefield of a system is laid out from (outside the system's star, as the complex sites are).</summary>
    Task<double[]> OriginAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long solarSystemId, CancellationToken cancellationToken);

    /// <summary>The pilots of a system within a radius of a point, with their war factions (0 for none).</summary>
    Task<IReadOnlyList<FwComplexPilot>> PilotsNearAsync(long solarSystemId, double[] at, double radius, CancellationToken cancellationToken);
}

/// <summary>A pilot inside a complex: its character and its war faction (0 for none).</summary>
public sealed record FwComplexPilot(long CharacterId, long WarFactionId);

/// <summary>
/// What one tick resolved: a complex captured by the attackers (its victory points added) or by the defenders (subtracted). The winning side's pilots inside
/// it at the end split the complex's victory points evenly (<see cref="PilotShare"/> each, rounded down), and are credited in the tick's transaction.
/// </summary>
public sealed record FwComplexResolution(long ComplexId, long SolarSystemId, ComplexSize Size, long OccupierId, long AttackerId, bool AttackersWon,
    long VictoryPoints, long AttackerPoints, IReadOnlyList<long> WinnerIds, long PilotShare);
public static class FwComplexes
{

    public static readonly IReadOnlyList<ComplexSpec> Specs =
    [
        new(ComplexSize.Scout, 33, 300, 1, 50),
        new(ComplexSize.Small, 34, 600, 2, 50),
        new(ComplexSize.Medium, 35, 900, 3, 150),
        new(ComplexSize.Large, 36, 900, 5, 250),
    ];

    public static readonly TimeSpan Respawn = TimeSpan.FromMinutes(30);

    /// <summary>How often the complexes are ticked. shortcut: the wiki gives no tick; the capture timers are advanced by at most one tick per run.</summary>
    public static readonly TimeSpan Tick = TimeSpan.FromSeconds(10);

    /// <summary>How long a complex's dungeon site lasts. shortcut: the wiki gives no site lifetime; a site that is not completed by then is never resolved.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(24);

    public static ComplexSpec SpecOf(ComplexSize size) => Specs.Single(spec => spec.Size == size);

    /// <summary>
    /// The capture timer after one tick: it advances for the side that is alone in the complex (attackers up, defenders down) and pauses when both or neither are present.
    /// </summary>
    public static long Advance(long progressMs, bool attackersPresent, bool defendersPresent, long elapsedMs, long tickMs)
    {
        if (attackersPresent == defendersPresent)
            return progressMs;
        long step = Math.Clamp(elapsedMs, 0, tickMs);
        return attackersPresent ? progressMs + step : progressMs - step;
    }

    /// <summary>The side that captured the complex (true: the attackers), or null while the timer runs.</summary>
    public static bool? Resolved(long progressMs, ComplexSpec spec)
    {
        long capture = spec.CaptureSeconds * 1000L;
        if (progressMs >= capture)
            return true;
        return progressMs <= -capture ? false : null;
    }
}

/// <summary>
/// One tick of the complexes in one transaction: a complex (with its dungeon site) is spawned in each occupied system for each size that has none open (after
/// pilots inside it (their war factions): the attackers' side up, the occupier's down. A resolved complex moves victory points and ends its site.
/// </summary>
public static class FwComplexTicker
{
    public static async Task<IReadOnlyList<FwComplexResolution>> TickAsync(FwStore store, IFwComplexSites sites, NpgsqlConnection connection, NpgsqlTransaction transaction,
        long nowMs, CancellationToken cancellationToken)
    {
        var systems = await store.SystemsAsync(connection, transaction, cancellationToken);
        var open = (await store.OpenComplexesAsync(connection, transaction, cancellationToken)).ToList();
        var lastClosed = await store.LastClosedBySystemAsync(connection, transaction, cancellationToken);
        var respawnMs = (long)FwComplexes.Respawn.TotalMilliseconds;
        foreach (var system in systems)
        {
            foreach (var spec in FwComplexes.Specs)
            {
                if (open.Any(complex => complex.SolarSystemId == system.SolarSystemId && complex.Size == spec.Size))
                    continue;
                if (lastClosed.TryGetValue((system.SolarSystemId, spec.Size), out long closedMs) && nowMs < closedMs + respawnMs)
                    continue;
                long id = await store.InsertComplexAsync(connection, transaction, system.SolarSystemId, spec.Size, nowMs, cancellationToken);
                open.Add(new FwComplex(id, system.SolarSystemId, spec.Size, 0, nowMs, nowMs, 0));
            }
        }
        // a complex without its site starts it when a pilot is in its system (a site with no pilot has no scene for its NPCs: its wave would fail)
        for (int index = 0; index < open.Count; index++)
        {
            var complex = open[index];
            if (complex.InstanceId > 0 || !sites.PilotsIn(complex.SolarSystemId))
                continue;
            long instance = await sites.StartAsync(connection, transaction, complex.SolarSystemId, complex.Size, complex.ComplexId, cancellationToken);
            await store.SetComplexInstanceAsync(connection, transaction, complex.ComplexId, instance, cancellationToken);
            open[index] = complex with { InstanceId = instance };
        }
        var results = new List<FwComplexResolution>();
        int day = FwRules.DayOf(nowMs);
        foreach (var complex in open.OrderBy(complex => complex.SolarSystemId).ThenBy(complex => complex.Size))
        {
            var system = systems.Single(s => s.SolarSystemId == complex.SolarSystemId);
            var spec = FwComplexes.SpecOf(complex.Size);
            long elapsed = nowMs - complex.ProgressUpdatedMs;
            long progress = complex.ProgressMs;
            IReadOnlyList<FwComplexPilot> inside = [];
            if (await sites.DefendersClearedAsync(complex.InstanceId, cancellationToken))
            {
                inside = await sites.PilotsInsideAsync(complex.SolarSystemId, complex.InstanceId, cancellationToken);
                bool attackers = inside.Any(pilot => pilot.WarFactionId == FwFactions.OccupationEnemyOf(system.OccupierId));
                bool defenders = inside.Any(pilot => pilot.WarFactionId == system.OccupierId);
                progress = FwComplexes.Advance(progress, attackers, defenders, elapsed, (long)FwComplexes.Tick.TotalMilliseconds);
            }
            var won = FwComplexes.Resolved(progress, spec);
            if (won is null)
            {
                await store.SetComplexProgressAsync(connection, transaction, complex.ComplexId, progress, nowMs, cancellationToken);
                continue;
            }
            long attackerFaction = FwFactions.OccupationEnemyOf(system.OccupierId);
            long points = await store.AddAttackerPointsSignedAsync(connection, transaction, system.SolarSystemId, won.Value ? spec.VictoryPoints : -spec.VictoryPoints,
                nowMs, cancellationToken);

            long winningFaction = won.Value ? attackerFaction : system.OccupierId;
            var winners = inside.Where(pilot => pilot.WarFactionId == winningFaction).Select(pilot => pilot.CharacterId).Distinct().ToList();
            long share = winners.Count == 0 ? 0 : spec.VictoryPoints / winners.Count;
            if (share > 0)
            {
                foreach (long characterId in winners)
                    await store.AddPilotDailyAsync(connection, transaction, characterId, day, 0, share, cancellationToken);
            }
            await store.CloseComplexAsync(connection, transaction, complex.ComplexId, nowMs, cancellationToken);
            await sites.EndAsync(connection, transaction, complex.InstanceId, cancellationToken);
            results.Add(new FwComplexResolution(complex.ComplexId, system.SolarSystemId, complex.Size, system.OccupierId, attackerFaction, won.Value,
                spec.VictoryPoints, points, winners, share));
        }
        return results;
    }
}
