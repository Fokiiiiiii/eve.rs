using Eve.Services.Slash;
using Eve.Skills;

namespace Eve.Services.Skills.Tests;

/// <summary><c>/removeskill &lt;target&gt; &lt;skill|all&gt;</c> of the game master: the skill rows go, the words are the reference's.</summary>
public class RemoveSkillTests
{
    private static SkillCommands CommandsOf(Rig rig) => new(Rig.Catalog, rig.Characters, rig.Training, rig.Announcer, _ => null);

    [PgFact]
    public async Task A_skill_is_removed_and_the_pilot_is_told_and_a_second_removal_says_so()
    {
        await using var rig = await Rig.CreateAsync();
        var character = rig.CharacterId;
        var commands = CommandsOf(rig);
        var (session, sink) = Rig.Session(character);
        await commands.GiveAsync(session, "me Gunnery 5", default);
        await commands.GiveAsync(session, "me Small Hybrid Turret 3", default);
        var told = sink.Sent.Count;

        Assert.Equal($"Removed Gunnery(3300) from character({character}).", await commands.RemoveAsync(session, "me Gunnery", default));
        Assert.Equal([new CharacterSkill(3301, 3, 8000)], await rig.Store.ForCharacterAsync(character));
        Assert.True(sink.Sent.Count > told);
        Assert.Equal($"character({character}) does not have Gunnery(3300).", await commands.RemoveAsync(session, "me 3300", default));
    }

    [PgFact]
    public async Task All_removes_every_skill_row_and_what_cannot_be_done_is_answered_in_the_words_of_the_reference()
    {
        await using var rig = await Rig.CreateAsync();
        var character = rig.CharacterId;
        var commands = CommandsOf(rig);
        var (session, _) = Rig.Session(character);
        var (nobody, _) = Rig.Session(null);
        await commands.GiveAsync(session, "me all 4", default);
        var before = (await rig.Store.ForCharacterAsync(character)).Count;

        Assert.Equal($"Removed all skills from character({character}). Deleted {before} skill records.", await commands.RemoveAsync(session, "me all", default));
        Assert.Empty(await rig.Store.ForCharacterAsync(character));
        Assert.Equal($"character({character}) already has no skills.", await commands.RemoveAsync(session, "me all", default));
        Assert.Equal("Usage: /removeskill <target> <skill name|typeID|all>", await commands.RemoveAsync(session, "me", default));
        Assert.Equal("Select a character before using /removeskill me.", await commands.RemoveAsync(nobody, "me Gunnery", default));
        Assert.Equal("Character not found: Someone. Use me, a character ID, or an exact character name.", await commands.RemoveAsync(session, "Someone Gunnery", default));
        Assert.Equal("Skill not found: Piloting.", await commands.RemoveAsync(session, "me Piloting", default));
    }
}
