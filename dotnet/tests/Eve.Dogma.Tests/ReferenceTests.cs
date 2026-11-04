using System.Text;
using System.Text.Json;
using Eve.Skills;

namespace Eve.Dogma.Tests;

/// <summary>
/// The engine against what the reference server sent for the same characters (oracle/fixtures/scenarios/dogma.json):
/// a fresh character of each race, in the ship the game gives it, with the skills it starts with.
/// </summary>
public class ReferenceTests
{
    private sealed record Recorded1(int RaceId, long CharId, int CharType, long ShipId, int ShipType,
        Dictionary<long, (int Type, int Flag, Dictionary<int, double> Attributes)> Fitted,
        Dictionary<int, double> ShipAttributes, Dictionary<int, double> CharAttributes, Dictionary<int, double> CharInfoAttributes,
        List<string>[] Brain);

    private static IEnumerable<Recorded1> Races()
    {
        var root = Recorded.Load("dogma.json");
        foreach (var race in root.GetProperty("races").EnumerateArray())
        {
            var info = Recorded.KeyVal(Recorded.Tuple(race.GetProperty("bind").GetProperty("ok"))[1]);
            var ship = Recorded.Long(info["activeShipID"]);
            var fitted = new Dictionary<long, (int, int, Dictionary<int, double>)>();
            int shipType = 0;
            var shipAttributes = new Dictionary<int, double>();
            foreach (var (id, item) in Recorded.Dict(info["shipInfo"]))
            {
                var kv = Recorded.KeyVal(item);
                var row = kv["invItem"].GetProperty("$row");
                var attributes = Recorded.Attributes(kv["attributes"]);
                if (Recorded.Long(id) == ship)
                {
                    shipType = row.GetProperty("typeID").GetInt32();
                    shipAttributes = attributes;
                }
                else
                {
                    fitted[Recorded.Long(id)] = (row.GetProperty("typeID").GetInt32(), row.GetProperty("flagID").GetInt32(), attributes);
                }
            }
            var charInfo = Recorded.Tuple(info["charInfo"])[0];
            var (charKey, charItem) = Recorded.Dict(charInfo).Single();
            var charKv = Recorded.KeyVal(charItem);
            yield return new Recorded1(
                race.GetProperty("raceID").GetInt32(), Recorded.Long(charKey),
                charKv["invItem"].GetProperty("$row").GetProperty("typeID").GetInt32(), ship, shipType, fitted, shipAttributes,
                Recorded.Attributes(info["shipModifiedCharAttribs"].GetProperty("args").GetProperty("$dict").EnumerateArray().Single(p => p[0].GetString() == "attributes")[1]),
                Recorded.Attributes(charKv["attributes"]),
                BrainLists(Recorded.Tuple(info["charInfo"])[1]));
        }
    }

    /// <summary>The brain the reference sent as (skill, kind, target attribute, operation, extras, value) per effect, for the character, the ship and the structure.</summary>
    private static List<string>[] BrainLists(JsonElement brain)
    {
        var parts = Recorded.Tuple(brain);
        return [.. new[] { 1, 2, 3 }.Select(index =>
            Recorded.Tuple(parts[index]).Select(effect =>
            {
                var state = Recorded.Dict(effect.GetProperty("$objectex").GetProperty("$tuple")[2]).ToDictionary(p => p.Key.GetString()!, p => p.Value);
                var extras = string.Join(',', Recorded.Tuple(state["extras"]).Select(e => e.GetInt32()));
                return $"{state["skills"][0].GetInt32()} {state["modifierType"].GetString()} {state["toAttribID"].GetInt32()} op{state["operation"].GetInt32()} [{extras}] {Recorded.Number(state["value"]).ToString(System.Globalization.CultureInfo.InvariantCulture)}";
            }).ToList())];
    }

    private static List<string> Listed(IEnumerable<BrainEffect> effects) =>
        [.. effects.Select(e => $"{e.SkillTypeId} {e.ModifierType} {e.TargetAttributeId} op{e.Operation} [{string.Join(',', e.Extras)}] {e.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)}")];

    public static IEnumerable<object[]> RaceIds() => Races().Select(r => new object[] { r.RaceId });

    private static async Task<(DogmaWorld World, Recorded1 Expected, DogmaStatic Statics)> BuildAsync(int raceId)
    {
        var sde = await PgFactAttribute.SdeAsync();
        var statics = await DogmaStatic.LoadAsync(sde);
        var skills = await SkillCatalog.LoadAsync(sde);
        var expected = Races().Single(r => r.RaceId == raceId);
        var starting = skills.StartingSkills(raceId);
        await statics.EnsureTypesAsync([expected.CharType, expected.ShipType, .. expected.Fitted.Values.Select(f => f.Type), .. starting.Select(s => s.TypeId)]);

        var world = new DogmaWorld(statics, expected.CharId, expected.ShipId);
        world.Add(new DogmaItem(expected.CharId, expected.CharType, expected.CharId, expected.ShipId, 57));
        foreach (var learning in new[] { 164, 165, 166, 167, 168 })
        {
            world.SetValue(expected.CharId, learning, 20); // what a new character is given: the five attributes that set how fast it learns
        }
        world.Add(new DogmaItem(expected.ShipId, expected.ShipType, expected.CharId, 60015249, 4));
        foreach (var (id, (type, flag, _)) in expected.Fitted)
        {
            world.Add(new DogmaItem(id, type, expected.CharId, expected.ShipId, flag, ItemState.Online));
        }
        var next = expected.CharId * 100_000L;
        foreach (var skill in starting)
        {
            var id = expected.CharId * 100_000L + skill.TypeId;
            world.Add(new DogmaItem(id, skill.TypeId, expected.CharId, expected.CharId, 7));
            world.SetValue(id, DogmaWorld.SkillLevel, skill.Level);
        }
        _ = next;
        return (world, expected, statics);
    }

    private static readonly HashSet<int> ShipSupplied = [18, 264, 266, 352, 458, 1152, 2610];

    private static readonly HashSet<int> ModuleSupplied = [2, 3, 4, 38, 162, 264, 266, 805];

    private static readonly HashSet<int> CharacterSupplied = [979, 1921, 2610, 2662];

    /// <summary>
    /// Compares the engine's attributes with the reference's. The reference lists some attributes the engine does not work out
    /// (<paramref name="supplied"/>: the service adds them from an item's state) and the engine has some the reference leaves out
    /// (<paramref name="notReported"/>); <paramref name="deviations"/> are values where the two differ on purpose, each with the engine's value.
    /// </summary>
    private static string Differences(
        IReadOnlyDictionary<int, double> expected, IReadOnlyDictionary<int, double> actual, string what,
        IReadOnlySet<int> supplied, IReadOnlySet<int>? notReported = null, IReadOnlyDictionary<int, double>? deviations = null)
    {
        var sb = new StringBuilder();
        foreach (var (attribute, value) in expected)
        {
            if (deviations?.TryGetValue(attribute, out var deviant) == true)
            {
                if (!actual.TryGetValue(attribute, out var mine) || Math.Abs(mine - deviant) > 1e-9)
                {
                    sb.AppendLine($"{what}: attribute {attribute} was expected to deviate to {deviant}, is {(actual.TryGetValue(attribute, out var m) ? m : double.NaN)}");
                }
                continue;
            }
            if (!actual.TryGetValue(attribute, out var got))
            {
                if (!supplied.Contains(attribute))
                {
                    sb.AppendLine($"{what}: attribute {attribute} is missing (reference {value})");
                }
            }
            else if (Math.Abs(got - value) > 1e-6 * Math.Max(1, Math.Abs(value)))
            {
                sb.AppendLine($"{what}: attribute {attribute} is {got}, reference {value}");
            }
        }
        foreach (var attribute in actual.Keys.Except(expected.Keys))
        {
            if (notReported?.Contains(attribute) != true)
            {
                sb.AppendLine($"{what}: attribute {attribute} = {actual[attribute]} is not in the reference");
            }
        }
        return sb.ToString();
    }

    [PgTheory]
    [MemberData(nameof(RaceIds))]
    public async Task The_ship_has_the_attributes_the_reference_gives_it(int raceId)
    {
        var (world, expected, _) = await BuildAsync(raceId);
        var diff = Differences(expected.ShipAttributes, world.Attributes(expected.ShipId), $"ship {expected.ShipType}", ShipSupplied);
        Assert.True(diff.Length == 0, diff);
    }

    [PgTheory]
    [MemberData(nameof(RaceIds))]
    public async Task The_fitted_modules_have_the_attributes_the_reference_gives_them(int raceId)
    {
        var (world, expected, _) = await BuildAsync(raceId);
        var diff = string.Concat(expected.Fitted.Select(f => Differences(f.Value.Attributes, world.Attributes(f.Key), $"module {f.Value.Type}@{f.Value.Flag}", ModuleSupplied)));
        Assert.True(diff.Length == 0, diff);
    }

    [PgTheory]
    [MemberData(nameof(RaceIds))]
    public async Task The_character_has_the_attributes_the_reference_gives_it(int raceId)
    {
        var (world, expected, _) = await BuildAsync(raceId);

        // The reference reports 0 for the trade premium although its own brain says -10% of 1 (= 0.9), and leaves out the rookie ship's
        // mining-drone bonus, which is the whole point of "modified by the ship".
        var deviations = new Dictionary<int, double> { [443] = 0.9 };
        if (raceId == 8)
        {
            deviations[428] = 120;
        }
        var diff = Differences(expected.CharAttributes, world.Attributes(expected.CharId), $"character {expected.CharType}", CharacterSupplied, notReported: new HashSet<int> { 161 }, deviations: deviations);
        Assert.True(diff.Length == 0, diff);
    }

    [PgTheory]
    [MemberData(nameof(RaceIds))]
    public async Task The_brain_lists_what_the_skills_do_as_the_reference_lists_it(int raceId)
    {
        var (world, expected, statics) = await BuildAsync(raceId);
        var brain = BrainBuilder.Build(world, statics);
        Assert.Equal(expected.Brain[0], Listed(brain.Character));
        Assert.Equal(expected.Brain[1], Listed(brain.Ship));
        Assert.Equal(expected.Brain[2], Listed(brain.Structure));
    }
}

/// <summary>A theory that is skipped, loudly, without a database.</summary>
public sealed class PgTheoryAttribute : TheoryAttribute
{
    public PgTheoryAttribute()
    {
        if (PgFactAttribute.Url is null)
        {
            Skip = "EVE_PG_URL is not set (see tools/dev/pg18.sh)";
        }
    }
}
