using System.Text.Json.Nodes;

namespace Eve.Characters.Tests;

public class NameTests
{
    private static readonly JsonObject Walk = (JsonObject)JsonNode.Parse(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "scenarios", "login_to_character.json")))!;

    public static IEnumerable<object[]> Recorded() =>
        ((JsonArray)Walk["names"]!).Select(n => new object[] { (string)n!["name"]!, (int)n["ok"]! });

    [Fact]
    public void This_process_can_normalise_unicode()
    {
        CharacterNames.EnsureUnicodeSupport();
    }

    [Fact]
    public void The_recording_has_the_names_it_should()
    {
        Assert.True(((JsonArray)Walk["names"]!).Count >= 20);
    }

    /// <summary>
    /// What the reference answered for each name, before any character existed. One difference is deliberate: it
    /// also reserves its own project's names ("Elysian"), which eve.rs does not (docs/compat/DEVIATIONS.md).
    /// </summary>
    [Theory]
    [MemberData(nameof(Recorded))]
    public void A_name_gets_the_code_the_reference_gave_it(string name, int recorded)
    {
        var expected = name == "Elysian" ? (int)NameCheck.Valid : recorded;
        Assert.Equal(expected, (int)CharacterNames.Validate(name));
    }

    [Theory]
    [InlineData("abc", NameCheck.Valid)]
    [InlineData("ab", NameCheck.TooShort)]
    [InlineData("  ab  ", NameCheck.TooShort)]
    [InlineData("", NameCheck.TooShort)]
    [InlineData("a b", NameCheck.Valid)]
    [InlineData("a b c", NameCheck.Valid)]
    [InlineData("a b c d", NameCheck.TooManySpaces)]
    [InlineData("a  b", NameCheck.ConsecutiveSpaces)]
    [InlineData("a b c d e f g", NameCheck.TooManySpaces)]
    [InlineData("Mary-Jane O'Neil", NameCheck.Valid)]
    [InlineData("R2D2", NameCheck.Valid)]
    [InlineData("Bad.Name", NameCheck.IllegalCharacter)]
    [InlineData("Bad!Name", NameCheck.IllegalCharacter)]
    [InlineData("😀 Smile", NameCheck.IllegalCharacter)]
    [InlineData("Ann\tLee", NameCheck.IllegalCharacter)]
    [InlineData("ccp guy", NameCheck.Reserved)]
    [InlineData("CCP Guy", NameCheck.Reserved)]
    [InlineData("Ｇｍ Guy", NameCheck.Reserved)]
    [InlineData("ccpguy", NameCheck.Valid)]
    [InlineData("gmail", NameCheck.Valid)]
    public void The_rules_in_the_order_the_screen_reports_them(string name, NameCheck expected)
    {
        Assert.Equal(expected, CharacterNames.Validate(name));
    }

    [Fact]
    public void The_length_limits_count_utf16_units_of_the_trimmed_name()
    {
        Assert.Equal(NameCheck.Valid, CharacterNames.Validate(new string('a', 37)));
        Assert.Equal(NameCheck.TooLong, CharacterNames.Validate(new string('a', 38)));
        Assert.Equal(NameCheck.Valid, CharacterNames.Validate("  " + new string('a', 37) + "  "));
        // an astral letter is two UTF-16 units: 18 of them and one letter make 37
        Assert.Equal(NameCheck.Valid, CharacterNames.Validate(string.Concat(Enumerable.Repeat("\U00020000", 18)) + "a"));
        Assert.Equal(NameCheck.TooLong, CharacterNames.Validate(string.Concat(Enumerable.Repeat("\U00020000", 19))));
    }

    [Fact]
    public void Astral_letters_and_composed_accents_are_letters()
    {
        Assert.Equal(NameCheck.Valid, CharacterNames.Validate("\U00020000\U00020001x"));
        Assert.Equal(NameCheck.Valid, CharacterNames.Validate("José Lee")); // an "e" and a combining accent become one letter
        Assert.Equal(NameCheck.IllegalCharacter, CharacterNames.Validate("́́́")); // combining marks alone are not letters
    }

    [Fact]
    public void White_space_is_javascripts_not_dotnets()
    {
        Assert.Equal(NameCheck.Valid, CharacterNames.Validate("﻿Ann Lee﻿"));
        Assert.Equal(NameCheck.Valid, CharacterNames.Validate(" Ann Lee　"));
        Assert.Equal(NameCheck.ConsecutiveSpaces, CharacterNames.Validate("Ann  Lee"));
        // NEL (U+0085) is white space to .NET but not to JavaScript: it is an illegal character here
        Assert.Equal(NameCheck.IllegalCharacter, CharacterNames.Validate("Ann\u0085Lee"));
    }

    [Fact]
    public void The_key_folds_case_width_and_runs_of_spaces()
    {
        Assert.Equal("ann lee", CharacterNames.Key("Ann  Lee"));
        Assert.Equal("ann lee", CharacterNames.Key("  ANN LEE "));
        Assert.Equal("ann lee", CharacterNames.Key("Ａｎｎ Ｌｅｅ"));
        Assert.NotEqual(CharacterNames.Key("Ann Lee"), CharacterNames.Key("Ann Leigh"));
        Assert.Equal("", CharacterNames.Key(null));
    }

    [Fact]
    public void Clean_trims_and_collapse_joins_but_neither_changes_letters()
    {
        Assert.Equal("Ann  Lee", CharacterNames.Clean("  Ann  Lee "));
        Assert.Equal("Ann Lee", CharacterNames.Collapse("Ann  \t Lee"));
        Assert.Equal("Ann Lee", CharacterNames.Collapse("Ann Lee"));
    }

    [Fact]
    public void Random_names_are_always_valid_and_in_bounds()
    {
        var random = new Random(12345);
        foreach (var race in RandomNames.Races.Append(999))
        {
            var seen = new HashSet<string>();
            for (var i = 0; i < 200; i++)
            {
                var name = RandomNames.Generate(race, random);
                Assert.Equal(NameCheck.Valid, CharacterNames.Validate(name));
                seen.Add(name);
            }
            Assert.True(seen.Count > 20, $"race {race} gave only {seen.Count} different names");
        }
    }
}
