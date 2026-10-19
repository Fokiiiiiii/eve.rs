namespace Eve.Services.Core;

/// <summary>

/// Rules (eve.rs, DEVIATIONS "Epic arc offers"):
/// <list type="bullet">






/// </list>
/// </summary>
public static class EpicArcOffers
{

    public static long? Pick(
        MissionStaticCatalog catalog,
        long agentId,
        IEnumerable<(long MissionId, DateTimeOffset CompletedAt)> completed,
        DateTimeOffset now,
        IEnumerable<(long MissionId, DateTimeOffset At)>? failed = null)
    {
        var failures = (failed ?? []).ToList();
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


            // and the completions of the previous run no longer count.
            var restart = TimeSpan.FromMinutes(arc.RestartIntervalMinutes);
            DateTimeOffset? runEnd = null;
            foreach (var step in steps.Values.Where(step => step.NextMissionIds.Count == 0))
                if (completions.TryGetValue(step.MissionId, out var at) && (runEnd is null || at > runEnd)) runEnd = at;
            if (runEnd is { } ended && now < ended + restart) continue;

            DateTimeOffset? resetAt = null;
            foreach (var (missionId, at) in failures)
                if (steps.TryGetValue(missionId, out var failedStep) && failedStep.FailMissionId is { } target && target != missionId &&
                    steps.ContainsKey(target) && (resetAt is null || at > resetAt))
                    resetAt = at;
            var floor = runEnd is null ? resetAt : resetAt is { } reset && reset > runEnd ? reset : runEnd;
            var done = floor is { } start
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
