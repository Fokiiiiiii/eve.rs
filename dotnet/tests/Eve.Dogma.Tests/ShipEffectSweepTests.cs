using System.Text.Json;
using Eve.Dogma;

namespace Eve.Dogma.Tests;

/// <summary>
/// The dogma sweep of the ship-scope effects (SF-7): every effect of a published ship, a subsystem, or a Tactical Destroyer mode item (group 1306) is passive, and
/// each of its modifiers is a function and a domain the engine applies to the ship. The sweep reads the pinned SDE and fails on any row it cannot explain, so a
/// new function or domain in the static data is found here, not in a fight. Target and area effects (category 2 and 3) belong to modules and are not in this sweep.
/// </summary>
public class ShipEffectSweepTests
{
    private const int ShipCategory = 6, SubsystemCategory = 32, TacticalDestroyerModeGroup = 1306;
    private const int PassiveCategory = 0;

    /// <summary>The functions the engine applies (<c>DogmaStatic.LoadAsync</c>); any other function is dropped at load.</summary>
    private static readonly HashSet<string> AppliedFunctions =
    [
        "ItemModifier", "LocationModifier", "LocationGroupModifier", "LocationRequiredSkillModifier", "OwnerRequiredSkillModifier",
    ];

    /// <summary>The domains a ship-scope modifier names: the ship, its pilot, the item itself, and the other item of its module pair (charge and module).</summary>
    private static readonly HashSet<string> ShipScopeDomains = ["shipID", "charID", "itemID", "otherID"];

    [PgFact]
    public async Task Every_ship_scope_effect_is_passive_and_every_modifier_is_one_the_engine_applies_and_loads_whole()
    {
        var sde = await PgFactAttribute.SdeAsync();
        var groupCategory = (await sde.ReadAsync("groups")).ToDictionary(g => (int)g.Key, g => g.Doc.GetProperty("categoryID").GetInt32());
        var typeRows = await sde.ReadAsync("types");
        var dogmaRows = (await sde.ReadAsync("typeDogma")).ToDictionary(row => (int)row.Key, row => row.Doc);
        var effectRows = (await sde.ReadAsync("dogmaEffects")).ToDictionary(row => (int)row.Key, row => row.Doc);
        var statics = await DogmaStatic.LoadAsync(sde);

        var scopedTypes = typeRows.Where(row =>
        {
            var group = row.Doc.GetProperty("groupID").GetInt32();
            var published = row.Doc.TryGetProperty("published", out var p) && p.ValueKind == JsonValueKind.True;
            var category = groupCategory.GetValueOrDefault(group);
            return (published && category is ShipCategory or SubsystemCategory) || group == TacticalDestroyerModeGroup;
        }).Select(row => (int)row.Key).ToList();
        Assert.True(scopedTypes.Count > 400, $"the sweep read {scopedTypes.Count} types; the published ships alone are 423");

        var effectIds = scopedTypes
            .Where(dogmaRows.ContainsKey)
            .SelectMany(type => dogmaRows[type].TryGetProperty("dogmaEffects", out var effects)
                ? effects.EnumerateArray().Select(e => e.GetProperty("effectID").GetInt32()) : [])
            .Distinct()
            .ToList();
        Assert.NotEmpty(effectIds);

        var unexplained = new List<string>();
        var modifiers = 0;
        var replaced = 0;
        foreach (var effectId in effectIds)
        {
            var doc = effectRows[effectId];
            var category = doc.GetProperty("effectCategoryID").GetInt32();
            if (category != PassiveCategory)
            {
                unexplained.Add($"effect {effectId} is category {category}, not passive");
                continue;
            }
            // an effect the engine states itself (BuiltInEffects.All) takes its modifiers from the engine, not from the static data
            if (BuiltInEffects.All.FirstOrDefault(e => e.Id == effectId) is { } engine)
            {
                replaced++;
                if (statics.Effect(effectId)?.Modifiers.Count != engine.Modifiers.Count)
                {
                    unexplained.Add($"effect {effectId}: the engine's {engine.Modifiers.Count} modifiers are not the ones loaded");
                }
                continue;
            }
            var raw = doc.TryGetProperty("modifierInfo", out var info) ? info.EnumerateArray().ToList() : [];
            foreach (var modifier in raw)
            {
                modifiers++;
                var func = modifier.GetProperty("func").GetString();
                var domain = modifier.TryGetProperty("domain", out var d) ? d.GetString() : null;
                if (func is null || !AppliedFunctions.Contains(func) || domain is null || !ShipScopeDomains.Contains(domain))
                {
                    unexplained.Add($"effect {effectId}: {func ?? "(no function)"} on {domain ?? "(no domain)"}");
                }
            }
            // the loader keeps every modifier it can apply: the built-in effects add their own on top of the SDE's
            var loaded = statics.Effect(effectId)?.Modifiers.Count ?? 0;
            var builtIn = Eve.Dogma.BuiltInEffects.ClientModifiers(effectId).Count;
            if (loaded - builtIn != raw.Count)
            {
                unexplained.Add($"effect {effectId}: the loader keeps {loaded - builtIn} of {raw.Count} modifiers");
            }
        }

        Assert.True(modifiers > 1000, $"the sweep read only {modifiers} modifiers");
        Assert.True(replaced < effectIds.Count, "every ship-scope effect was replaced by the engine");
        Assert.Empty(unexplained);
    }
}
