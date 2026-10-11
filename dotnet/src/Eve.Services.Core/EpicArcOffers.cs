namespace Eve.Services.Core;

/// <summary>
/// Which epic arc mission an agent offers the character now, from the arc sequence of the official build (<c>epicArcs</c>).
/// Rules (eve.rs, DEVIATIONS "Epic arc offers"):
/// <list type="bullet">
/// <item>A step is offered when it is an entry (no step of its arc leads to it), or when one of the steps leading to it is completed.</item>
/// <item>A completed step is not offered again. Once one successor of a fork is completed, the other successors are not offered.</item>
/// <item>A finished arc (a terminal step completed) cools down for its restart interval. After that a new run starts: the entries are offered again and the completions of the previous run no longer count.</item>
/// <item>A step held back by <see cref="MissionStaticCatalog.IsHeldBack"/> is not offered (generic or missing kill sites, owner decision).</item>
/// <item>A step the character quit or declined is not completed, so it is offered again: the retry of the failure branch.</item>
/// <item>Among the offerable steps of the agent, the lowest mission ID is offered (a deterministic choice).</item>
/// </list>
/// </summary>
public static class EpicArcOffers
{
    /// <summary>The mission to offer, or null when the agent has none.</summary>
    public static long? Pick(
        MissionStaticCatalog catalog,
        long agentId,
        IEnumerable<(long MissionId, DateTimeOffset CompletedAt)> completed,
        DateTimeOffset now)
    {
        if (!catalog.IsLoaded) return null;
        var completions = completed
            .GroupBy(item => item.MissionId)
            .ToDictionary(group => group.Key, group => group.Max(item => item.CompletedAt));

        long? best = null;
        foreach (var arc in catalog.EpicArcs)
        {
            if (!catalog.IsOfferable(arc.ArcId) || arc.Steps.All(step => step.AgentId != agentId)) continue;
            var steps = arc.Steps.ToDictionary(step => step.MissionId);
            var predecessors = steps.Keys.ToDictionary(id => id, _ => new List<long>());
            foreach (var step in steps.Values)
                foreach (var next in step.NextMissionIds)
                    if (predecessors.TryGetValue(next, out var list)) list.Add(step.MissionId);

            // The run ends with a terminal step. The arc cools down for its restart interval after that; then a new run starts
            // and the completions of the previous run no longer count.
            var restart = TimeSpan.FromMinutes(arc.RestartIntervalMinutes);
            DateTimeOffset? runEnd = null;
            foreach (var step in steps.Values.Where(step => step.NextMissionIds.Count == 0))
                if (completions.TryGetValue(step.MissionId, out var at) && (runEnd is null || at > runEnd)) runEnd = at;
            if (runEnd is { } ended && now < ended + restart) continue;
            var done = runEnd is { } start
                ? completions.Where(pair => pair.Value > start).Select(pair => pair.Key).ToHashSet()
                : completions.Keys.ToHashSet();

            foreach (var step in steps.Values)
            {
                if (step.AgentId != agentId || done.Contains(step.MissionId)) continue;
                var leading = predecessors[step.MissionId];
                if (leading.Count > 0 && !leading.Any(done.Contains)) continue;
                if (catalog.IsHeldBack(step.MissionId)) continue;
                if (leading.Any(parent => steps[parent].NextMissionIds.Any(sibling =>
                        sibling != step.MissionId && done.Contains(sibling)))) continue;
                best = best is null || step.MissionId < best ? step.MissionId : best;
            }
        }
        return best;
    }
}
