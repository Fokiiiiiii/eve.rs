using Eve.Services.Slash;
using Eve.Skills;

namespace Eve.Services.Skills.Tests;

/// <summary><c>/giveskill me &lt;skill&gt; [level]</c> of the game master: the words of the reference, the skills in the store and what the client is told.</summary>
public class GiveSkillTests
{
    private const long Character = 140_000_038;

    private static SkillCommands CommandsOf(Rig rig) => new(Rig.Catalog, rig.Store, rig.Source, null, () => Rig.ClockMs);

    [PgFact]
    public async Task A_skill_is_set_to_the_level_asked_for_and_the_client_is_told_what_training_tells_it()
    {
        await using var rig = await Rig.CreateAsync();
        var (session, sink) = Rig.Session(Character);

        var said = await CommandsOf(rig).GiveAsync(session, "me Small Hybrid Turret 3", default);

        Assert.Equal($"Set Small Hybrid Turret(3301) for character({Character}) to level III.", said);
        var skill = Assert.Single(await rig.Store.ForCharacterAsync(Character));
        // the points of the level for a skill of its rank (1): 250 * sqrt(32)^2
        Assert.Equal(new CharacterSkill(3301, 3, 8000), skill);
        Assert.Equal(3, sink.Sent.Count); // OnServerSkillsChanged, OnSkillLevelsTrained, OnSkillsChanged
    }

    [PgFact]
    public async Task A_skill_that_is_there_at_the_level_is_left_alone_and_a_level_out_of_range_is_clamped()
    {
        await using var rig = await Rig.CreateAsync();
        var commands = CommandsOf(rig);
        var (session, sink) = Rig.Session(Character);

        Assert.Equal($"Set Gunnery(3300) for character({Character}) to level V. Requested level 9 was clamped to 5.", await commands.GiveAsync(session, "me 3300 9", default));
        var told = sink.Sent.Count;
        Assert.Equal($"character({Character}) already has Gunnery(3300) at level V.", await commands.GiveAsync(session, "me Gunnery", default));
        Assert.Equal(told, sink.Sent.Count);
        Assert.Equal($"Set Gunnery(3300) for character({Character}) to level 0.", await commands.GiveAsync(session, "me Gunnery 0", default));
        Assert.Equal(0, (await rig.Store.ForCharacterAsync(Character)).Single().Level);
    }

    [PgFact]
    public async Task All_sets_the_published_skills_and_super_every_one()
    {
        await using var rig = await Rig.CreateAsync();
        var commands = CommandsOf(rig);
        var (session, _) = Rig.Session(Character);

        Assert.Equal($"Set all published skills for character({Character}) to level IV. Updated 2 skills.", await commands.GiveAsync(session, "me all 4", default));
        Assert.Equal([3300, 3301], (await rig.Store.ForCharacterAsync(Character)).Select(s => s.TypeId));
        Assert.Equal($"Set all published and unpublished skills for character({Character}) to level IV. Updated 1 skills.", await commands.GiveAsync(session, "me super 4", default));
        Assert.Equal(3, (await rig.Store.ForCharacterAsync(Character)).Count);
    }

    [PgFact]
    public async Task What_cannot_be_done_is_answered_in_the_words_of_the_reference()
    {
        await using var rig = await Rig.CreateAsync();
        var commands = CommandsOf(rig);
        var (session, _) = Rig.Session(Character);
        var (nobody, _) = Rig.Session(null);

        Assert.Equal("Usage: /giveskill <target> <skill name|typeID|all|super> [level]", await commands.GiveAsync(session, "me", default));
        Assert.Equal("Select a character before using /giveskill me.", await commands.GiveAsync(nobody, "me Gunnery", default));
        Assert.Equal("Character not found: Someone. Use me, a character ID, or an exact character name.", await commands.GiveAsync(session, "Someone Gunnery", default));
        Assert.Equal("Skill not found: Piloting.", await commands.GiveAsync(session, "me Piloting", default));
        Assert.Equal("Skill not found: Hybrid. Suggestions: Small Hybrid Turret(3301)", await commands.GiveAsync(session, "me Hybrid", default));
        Assert.Empty(await rig.Store.ForCharacterAsync(Character));
    }
}
