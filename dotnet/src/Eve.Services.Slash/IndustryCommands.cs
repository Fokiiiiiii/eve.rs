using System.Text.Json;
using Eve.Sde;

namespace Eve.Services.Slash;

public sealed record GmBlueprintItem(long ItemId, int TypeId, long OwnerId, long LocationId, long Flag, int? Runs);

/// <summary>An industry job a character owns (<see cref="IGmIndustry"/>).</summary>
public sealed record GmIndustryJob(long JobId, int Status, int BlueprintTypeId, int ProductTypeId, int Runs, long OwnerId);

public interface IGmIndustry
{
    Task<IReadOnlyList<GmBlueprintItem>> BlueprintsOfAsync(long ownerId, CancellationToken cancellationToken);

    Task<IReadOnlyList<GmIndustryJob>> JobsOfAsync(long ownerId, CancellationToken cancellationToken);
}

/// <summary>
/// The game master's industry helper <c>/bp</c> (EVE.js <c>industryChatCommands.js</c>, <c>executeBlueprintCommand</c>): <c>help</c>, <c>list</c> (the caller's
/// blueprint items), <c>jobs</c> (the caller's industry jobs) and <c>lookup &lt;preset|product|blueprint&gt;</c> (the blueprint of a type, from the SDE).
/// </summary>
public sealed class IndustryCommands(ItemTypeNames names, SdeReader sde, IGmIndustry industry)
{
    public const string LookupUsage = "Usage: /bp lookup <preset|product|blueprint>";
    public const string NoBlueprints = "No blueprint items were found for your character.";
    public const string NoJobs = "No industry jobs were found for your character.";
    public const string Help = "/bp help\n/bp list\n/bp jobs\n/bp lookup <preset|product|blueprint>";
    private const int Shown = 20;

    /// <summary>EVE.js <c>GM_BP_PRESETS</c>: a short name for a type a game master often looks up.</summary>
    private static readonly Dictionary<string, string> Presets = new(StringComparer.OrdinalIgnoreCase)
    {
        ["frigate"] = "Rifter",
        ["destroyer"] = "Cormorant",
        ["cruiser"] = "Caracal",
        ["ammo"] = "Scourge Light Missile",
        ["lightdrones"] = "Hobgoblin I",
        ["module"] = "200mm AutoCannon I",
        ["rigs"] = "Small Core Defense Field Extender I",
        ["components"] = "Capital Armor Plates",
        ["capitalcomponents"] = "Capital Armor Plates",
    };

    /// <summary>The blueprint that makes a type, as <c>/bp lookup</c> names it.</summary>
    public sealed record Definition(int BlueprintTypeId, string BlueprintName, int ProductTypeId, string ProductName, int MaxProductionLimit);

    public async Task<string> RunAsync(ClientSession session, string argument, CancellationToken cancellationToken)
    {
        var tokens = argument.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        var subcommand = tokens.Length == 0 ? "help" : tokens[0].ToLowerInvariant();
        switch (subcommand)
        {
            case "help" or "?" or "commands":
                return Help;
            case "list" or "jobs":
                if (session.CharacterId is not { } owner) return "Select a character before using /bp.";
                return subcommand == "list" ? await ListAsync(owner, cancellationToken) : await JobsAsync(owner, cancellationToken);
            case "lookup":
                var token = string.Join(' ', tokens[1..]);
                if (token.Length == 0) return LookupUsage;
                if (await DefinitionAsync(token, cancellationToken) is not { } definition) return LookupUsage;
                return string.Join('\n',
                    $"Blueprint: {definition.BlueprintName} ({definition.BlueprintTypeId})",
                    $"Product: {definition.ProductName} ({definition.ProductTypeId})",
                    $"Max production limit: {definition.MaxProductionLimit}");
            default:
                return Help;
        }
    }

    private async Task<string> ListAsync(long owner, CancellationToken cancellationToken)
    {
        var blueprints = await industry.BlueprintsOfAsync(owner, cancellationToken);
        return blueprints.Count == 0
            ? NoBlueprints
            : string.Join('\n', blueprints.Take(Shown).Select(blueprint =>
                $"itemID={blueprint.ItemId} typeID={blueprint.TypeId} runs={blueprint.Runs ?? -1} owner={blueprint.OwnerId} flag={blueprint.Flag} facility={blueprint.LocationId}"));
    }

    private async Task<string> JobsAsync(long owner, CancellationToken cancellationToken)
    {
        var jobs = await industry.JobsOfAsync(owner, cancellationToken);
        return jobs.Count == 0
            ? NoJobs
            : string.Join('\n', jobs.Take(Shown).Select(job =>
                $"job={job.JobId} status={job.Status} blueprint={job.BlueprintTypeId} product={job.ProductTypeId} runs={job.Runs} owner={job.OwnerId}"));
    }

    /// <summary>
    /// The definition a name names: a preset, else the first type of that exact name that has a blueprint (the type's own, or the blueprint that makes it), else
    /// the one type the name finds. Null when none has a blueprint.
    /// </summary>
    public async Task<Definition?> DefinitionAsync(string token, CancellationToken cancellationToken)
    {
        var term = Presets.GetValueOrDefault(token.Trim()) ?? token;
        foreach (var candidate in await names.ExactAsync(term, cancellationToken))
        {
            if (await DefinitionOfTypeAsync(candidate.TypeId, cancellationToken) is { } exact) return exact;
        }
        if (await names.FindAsync(term, cancellationToken) is { Type: { } found })
            return await DefinitionOfTypeAsync(found.TypeId, cancellationToken);
        return null;
    }

    private async Task<Definition?> DefinitionOfTypeAsync(int typeId, CancellationToken cancellationToken)
    {
        var rows = await sde.ReadAsync("blueprints", [(long)typeId], cancellationToken);
        if (rows.Count > 0) return await DescribeAsync(typeId, rows[0].Doc, cancellationToken);
        return await MakingBlueprintAsync(typeId, cancellationToken);
    }

    private async Task<Definition?> MakingBlueprintAsync(int productTypeId, CancellationToken cancellationToken)
    {
        foreach (var row in await sde.ReadAsync("blueprints", cancellationToken: cancellationToken))
        {
            if (!row.Doc.TryGetProperty("activities", out var activities) || !activities.TryGetProperty("manufacturing", out var manufacturing)) continue;
            if (!manufacturing.TryGetProperty("products", out var products) || products.ValueKind != JsonValueKind.Array) continue;
            foreach (var product in products.EnumerateArray())
            {
                if (product.TryGetProperty("typeID", out var id) && id.GetInt64() == productTypeId)
                    return await DescribeAsync((int)row.Key, row.Doc, cancellationToken);
            }
        }
        return null;
    }

    private async Task<Definition?> DescribeAsync(int blueprintTypeId, JsonElement blueprint, CancellationToken cancellationToken)
    {
        if (!blueprint.TryGetProperty("activities", out var activities) || !activities.TryGetProperty("manufacturing", out var manufacturing) ||
            !manufacturing.TryGetProperty("products", out var products) || products.ValueKind != JsonValueKind.Array || products.GetArrayLength() == 0)
            return null;
        var productTypeId = products[0].GetProperty("typeID").GetInt32();
        var max = blueprint.TryGetProperty("maxProductionLimit", out var limit) ? limit.GetInt32() : 0;
        return new Definition(blueprintTypeId, await NameAsync(blueprintTypeId, cancellationToken), productTypeId,
            await NameAsync(productTypeId, cancellationToken), max);
    }

    private async Task<string> NameAsync(int typeId, CancellationToken cancellationToken)
    {
        var rows = await sde.ReadAsync("types", [(long)typeId], cancellationToken);
        return rows.Count > 0 ? SdeReader.English(rows[0].Doc.GetProperty("name")).Trim() : typeId.ToString();
    }
}
