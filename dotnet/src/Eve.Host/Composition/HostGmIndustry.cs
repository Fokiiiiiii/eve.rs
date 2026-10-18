using System.Text.Json.Nodes;
using Eve.Services.Core;
using Eve.Services.Slash;

namespace Eve.Host;

/// <summary>
/// The game master's industry helper on this host (<see cref="IGmIndustry"/>): an owner's blueprint items are the blueprint manager's read, and its jobs are the
/// industry job table's rows that name the owner (<c>ownerID</c>).
/// </summary>
internal sealed class HostGmIndustry(
    Func<long, CancellationToken, ValueTask<IReadOnlyList<BlueprintRecord>>> blueprints,
    Func<CancellationToken, ValueTask<JsonObject?>> jobs) : IGmIndustry
{
    public async Task<IReadOnlyList<GmBlueprintItem>> BlueprintsOfAsync(long ownerId, CancellationToken cancellationToken) =>
        (await blueprints(ownerId, cancellationToken))
            .Select(blueprint => new GmBlueprintItem(blueprint.ItemId, blueprint.TypeId, blueprint.OwnerId, blueprint.LocationId, blueprint.Flag, blueprint.Runs))
            .ToArray();

    public async Task<IReadOnlyList<GmIndustryJob>> JobsOfAsync(long ownerId, CancellationToken cancellationToken)
    {
        if (await jobs(cancellationToken) is not { } table || table["jobs"] is not JsonObject all)
            return [];
        return all.Select(pair => pair.Value).OfType<JsonObject>()
            .Where(job => Long(job, "ownerID") == ownerId)
            .Select(job => new GmIndustryJob(Long(job, "jobID"), (int)Long(job, "status"), (int)Long(job, "blueprintTypeID"),
                (int)Long(job, "productTypeID"), (int)Long(job, "runs"), ownerId))
            .OrderBy(job => job.JobId)
            .ToArray();
    }

    private static long Long(JsonObject job, string key) => long.TryParse(job[key]?.ToString(), out var value) ? value : 0;
}
