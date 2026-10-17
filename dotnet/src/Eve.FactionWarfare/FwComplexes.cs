using Npgsql;

namespace Eve.FactionWarfare;

/// <summary>The complex sizes of the wiki's Rank/Size table that the SDE archetypes 33 to 36 carry (Scout, Small, Medium, Large).</summary>
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
public sealed record FwComplex(long ComplexId, long SolarSystemId, ComplexSize Size, long ProgressMs, long ProgressUpdatedMs, long SpawnedMs);

/// <summary>What one tick resolved: a complex captured by the attackers (its victory points added) or by the defenders (subtracted).</summary>
public sealed record FwComplexResolution(long ComplexId, long SolarSystemId, ComplexSize Size, long OccupierId, long AttackerId, bool AttackersWon,
    long VictoryPoints, long AttackerPoints);

/// <summary>
/// The complex rules of factional warfare (wiki "Faction warfare": Complexes, Complex types, Victory points). The sizes, capture times and hull limits are the
/// wiki's Rank/Size table; the victory points are the wiki's table where it has the size, and a project rule for Medium (DEVIATIONS "Factional warfare").
/// </summary>
public static class FwComplexes
{
    /// <summary>The basic variant of each size (Scout BSC, Small NVY, Large NVY) and the Medium rule. Medium: no wiki value, project rule 150 victory points.</summary>
    public static readonly IReadOnlyList<ComplexSpec> Specs =
    [
        new(ComplexSize.Scout, 33, 300, 1, 50),
        new(ComplexSize.Small, 34, 600, 2, 50),
        new(ComplexSize.Medium, 35, 900, 3, 150),
        new(ComplexSize.Large, 36, 900, 5, 250),
    ];

    /// <summary>A complex of a size comes back this long after the last one of it in the system was captured (wiki: about 30 minutes).</summary>
    public static readonly TimeSpan Respawn = TimeSpan.FromMinutes(30);

    /// <summary>How often the complexes are ticked. shortcut: the wiki gives no tick; the capture timers are advanced by at most one tick per run.</summary>
    public static readonly TimeSpan Tick = TimeSpan.FromSeconds(10);

    public static ComplexSpec SpecOf(ComplexSize size) => Specs.Single(spec => spec.Size == size);

    /// <summary>
    /// The capture timer after one tick: it advances for the side that is alone in the complex (attackers up, defenders down) and pauses when both or neither are present.
    /// The step is capped at one tick, so a restart or a busy server does not credit the downtime to a capture.
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
/// One tick of the complexes in one transaction: a complex is spawned in each occupied system for each size that has none open (after its respawn time), the
/// capture timers advance by the pilots present (the presence is the pilots' war factions in the system), and a resolved complex moves victory points.
/// </summary>
public static class FwComplexTicker
{
    public static async Task<IReadOnlyList<FwComplexResolution>> TickAsync(FwStore store, NpgsqlConnection connection, NpgsqlTransaction transaction, long nowMs,
        Func<long, CancellationToken, Task<IReadOnlyList<long>>> factionsInSystem, CancellationToken cancellationToken)
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
                open.Add(new FwComplex(id, system.SolarSystemId, spec.Size, 0, nowMs, nowMs));
            }
        }
        var results = new List<FwComplexResolution>();
        foreach (var systemGroup in open.GroupBy(complex => complex.SolarSystemId).OrderBy(group => group.Key))
        {
            var system = systems.Single(s => s.SolarSystemId == systemGroup.Key);
            var present = await factionsInSystem(system.SolarSystemId, cancellationToken);
            long attackerFaction = FwFactions.OccupationEnemyOf(system.OccupierId);
            bool attackers = present.Contains(attackerFaction);
            bool defenders = present.Contains(system.OccupierId);
            foreach (var complex in systemGroup)
            {
                var spec = FwComplexes.SpecOf(complex.Size);
                long progress = FwComplexes.Advance(complex.ProgressMs, attackers, defenders, nowMs - complex.ProgressUpdatedMs, (long)FwComplexes.Tick.TotalMilliseconds);
                var won = FwComplexes.Resolved(progress, spec);
                if (won is null)
                {
                    await store.SetComplexProgressAsync(connection, transaction, complex.ComplexId, progress, nowMs, cancellationToken);
                    continue;
                }
                long points = await store.AddAttackerPointsSignedAsync(connection, transaction, system.SolarSystemId, won.Value ? spec.VictoryPoints : -spec.VictoryPoints,
                    nowMs, cancellationToken);
                await store.CloseComplexAsync(connection, transaction, complex.ComplexId, nowMs, cancellationToken);
                results.Add(new FwComplexResolution(complex.ComplexId, system.SolarSystemId, complex.Size, system.OccupierId, attackerFaction, won.Value,
                    spec.VictoryPoints, points));
            }
        }
        return results;
    }
}
