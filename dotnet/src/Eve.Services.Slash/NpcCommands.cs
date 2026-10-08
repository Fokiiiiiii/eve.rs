using System.Globalization;
using Eve.Npc;
using Eve.Services;

namespace Eve.Services.Slash;

/// <summary>
/// <c>/npc [amount] [faction|profile|pool]</c>: NPCs come into space around the pilot, armed as their hulls are. The reference's command (its words and its limits:
/// five by default, fifty at most); what it names is a profile or a pool of the authored NPCs, and nothing named is the default pool of pirates. The summary it
/// answers with says what was made; the reference's sentences about fitted modules, preferred targets and cargo loot are left out, because what they say is
/// not how this server works (<c>docs/reference/compat/DEVIATIONS.md</c>).
/// </summary>
public sealed class NpcCommands(ISpaceNpcs space)
{
    public const int DefaultAmount = 5;
    public const int MaxAmount = 50;

    /// <summary><c>parseNpcSpawnArguments</c>: the first word that is a number is the amount (a whole number above nothing, or the command is refused), the rest is what is named.</summary>
    public static (int Amount, string Query)? Parse(string argument, int defaultAmount = DefaultAmount)
    {
        var trimmed = argument.Trim();
        if (trimmed.Length == 0)
        {
            return (defaultAmount, "");
        }
        var parts = trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var amount = defaultAmount;
        var amountIndex = -1;
        for (var index = 0; index < parts.Length; index++)
        {
            if (DeathTestCommands.ParseAmount(parts[index]) is not { } parsed)
            {
                continue;
            }
            if (parsed != Math.Floor(parsed) || parsed <= 0 || parsed > int.MaxValue)
            {
                return null;
            }
            amount = (int)parsed;
            amountIndex = index;
            break;
        }
        var query = amountIndex >= 0 ? string.Join(' ', parts.Where((_, index) => index != amountIndex)).Trim() : trimmed;
        return (amount, query);
    }

    /// <summary><c>formatSuggestions</c>.</summary>
    private static string Suggestions(IReadOnlyList<string> suggestions) => suggestions.Count == 0 ? "" : $" Suggestions: {string.Join(", ", suggestions)}";

    public async Task<string> RunAsync(ClientSession session, string argument, CancellationToken cancellationToken)
    {
        if (Parse(argument) is not var (amount, query))
        {
            return "Usage: /npc [amount] [faction|profile|pool]";
        }
        if (amount > MaxAmount)
        {
            return $"NPC spawn count must be between 1 and {MaxAmount}.";
        }
        var report = await space.SpawnNpcsAsync(session, query, amount, cancellationToken);
        if (report.Error is { } error)
        {
            var suggestions = Suggestions(report.Suggestions);
            return error switch
            {
                NpcSpawnReport.NotInSpace => "You must be in space before using /npc.",
                NpcSpawnReport.ShipNotFound => "Active ship was not found in space.",
                NpcSelector.ProfileNotFound => $"NPC profile or pool not found: {(query.Length > 0 ? query : "default")}.{suggestions}",
                NpcSelector.ProfileAmbiguous => $"NPC profile or pool is ambiguous: {query}.{suggestions}",
                NpcSelector.ProfileHullNotDamageable =>
                    "The selected NPC profile's entity type declares no shield, armor or structure hit points, so the spawned NPC could not be shot or killed.",
                NpcSelector.DefinitionIncomplete => "The selected NPC profile is missing authored loadout or behavior data.",
                NpcSelector.PoolEmpty => $"The selected NPC pool has no spawnable authored entries.{suggestions}",
                _ => $"NPC spawn failed: {(error.Length > 0 ? error : "UNKNOWN_ERROR")}.{suggestions}",
            };
        }
        var spawned = report.Spawned;
        var composition = string.Join(", ", report.Composition.Select(part => $"{part.Count.ToString(CultureInfo.InvariantCulture)}x {part.Name}"));
        var from = report.SelectionName.Length > 0 ? $" from {report.SelectionName}" : "";
        var partial = spawned < report.Requested ? $" Spawn stopped at {spawned + 1}/{report.Requested}: {NpcSpawnReport.SpawnFailed}." : "";
        var unarmed = report.Unarmed > 0 ? $" {report.Unarmed} of them carry no weapon this server can fire yet (missile launchers)." : "";
        return $"Spawned {spawned} hull{(spawned == 1 ? "" : "s")}{from}: {composition}. These command-spawned NPCs are transient and are not written to disk.{unarmed}{partial}";
    }
}
