using Eve.Marshal;
using Eve.Services.Slash;
using Eve.Skills;

namespace Eve.Services.Dogma.Tests;

/// <summary>
/// <c>/givestarterskills</c>, <c>/randomizeskills</c> and <c>/skillscopy</c> on the database rig: the pilot's rows are read back from the skill store
/// after each command, and the command's words are the replies the client shows.
/// </summary>
public class SkillBulkCommandTests
{
    private static ClientSession Gm(ClientSession session)
    {
        session.Set("accountRole", new PyLong(GameMasterRoles.Gml));
        return session;
    }

    [PgFact]
    public async Task Give_starter_skills_sets_the_race_starting_set_and_is_repeat_safe()
    {
        await using var rig = await Rig.CreateAsync();
        var pilot = await rig.NewCharacterAsync("Starter Pilot");
        var gm = Gm(Rig.Session(pilot.CharacterId));
        var starter = rig.Catalog.StartingSet(1)!;
        Assert.NotEmpty(starter);
        var lowered = starter.First(skill => skill.Level > 0);
        await using (var connection = await rig.Source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await rig.Skills.GrantAsync(connection, transaction, pilot.CharacterId, [new CharacterSkill(lowered.TypeId, 0, 0)]);
            await transaction.CommitAsync();
        }

        var first = await rig.SlashAsync(gm, "/givestarterskills me");
        Assert.StartsWith("Gave the starting skills of race 1", first);
        var notices = Events.Of(gm).Select(notice => notice.Name).ToList();
        Assert.Contains("OnServerSkillsChanged", notices);
        Assert.Contains("OnNewSkillQueueSaved", notices);
        var rows = await rig.Skills.ForCharacterAsync(pilot.CharacterId);
        foreach (var expected in starter)
        {
            var row = Assert.Single(rows, skill => skill.TypeId == expected.TypeId);
            Assert.Equal(expected.Level, row.Level);
        }

        var repeat = await rig.SlashAsync(gm, "/givestarterskills me");
        Assert.Equal("Gave the starting skills of race 1 to character(" + pilot.CharacterId + "): 0 changed.", repeat);
    }

    [PgFact]
    public async Task Randomize_skills_gives_every_published_skill_a_level_from_0_to_5()
    {
        await using var rig = await Rig.CreateAsync();
        var pilot = await rig.NewCharacterAsync("Random Pilot");
        var gm = Gm(Rig.Session(pilot.CharacterId));

        var reply = await rig.SlashAsync(gm, "/randomizeskills");
        Assert.StartsWith("Randomized the published skills of character(" + pilot.CharacterId + ")", reply);
        var notices = Events.Of(gm).Select(notice => notice.Name).ToList();
        Assert.Contains("OnServerSkillsChanged", notices);
        Assert.Contains("OnNewSkillQueueSaved", notices);
        var rows = (await rig.Skills.ForCharacterAsync(pilot.CharacterId)).ToDictionary(skill => skill.TypeId);
        var published = rig.Catalog.Skills.Where(skill => skill.Published).ToList();
        Assert.NotEmpty(published);
        foreach (var skill in published)
        {
            Assert.True(rows.TryGetValue(skill.TypeId, out var row));
            Assert.InRange(row.Level, 0, SkillPoints.MaxLevel);
            Assert.Equal(SkillPoints.At(skill.Rank, row.Level), row.SkillPoints);
        }
    }

    [PgFact]
    public async Task Skill_copy_makes_the_target_match_the_donor_and_removes_the_rest()
    {
        await using var rig = await Rig.CreateAsync();
        var donor = await rig.NewCharacterAsync("Skill Donor");
        var taker = await rig.NewCharacterAsync("Skill Taker");
        var gm = Gm(Rig.Session(donor.CharacterId));
        var takerSession = Rig.Session(taker.CharacterId, id: 2);
        rig.SetOnline(takerSession);
        Assert.StartsWith("Gave the starting skills", await rig.SlashAsync(gm, "/givestarterskills me"));
        var extra = rig.Catalog.Skills.First(skill => skill.Published && skill.Needs.Count == 0 && rig.Catalog.StartingSkills(1).All(start => start.TypeId != skill.TypeId));
        await using (var connection = await rig.Source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await rig.Skills.GrantAsync(connection, transaction, taker.CharacterId, [new CharacterSkill(extra.TypeId, 0, 0)]);
            await transaction.CommitAsync();
        }
        Assert.IsType<QueueSaved>(await rig.Training.SaveQueueAsync(taker.CharacterId, 1,
            [new QueueEntry(extra.TypeId, 1)], activate: true, [donor.CharacterId, taker.CharacterId]));

        var callerEventCount = Events.Of(gm).Count;
        var reply = await rig.SlashAsync(gm, $"/skillscopy \"Skill Donor\" \"Skill Taker\"");
        Assert.StartsWith($"Copied", reply);
        Assert.Equal(callerEventCount, Events.Of(gm).Count);
        var targetNotices = Events.Of(takerSession).Select(notice => notice.Name).ToList();
        Assert.Contains("OnSkillsChanged", targetNotices);
        Assert.Contains("OnNewSkillQueueSaved", targetNotices);
        var donorRows = (await rig.Skills.ForCharacterAsync(donor.CharacterId)).OrderBy(skill => skill.TypeId).Select(skill => (skill.TypeId, skill.Level, skill.SkillPoints)).ToList();
        var takerRows = (await rig.Skills.ForCharacterAsync(taker.CharacterId)).OrderBy(skill => skill.TypeId).Select(skill => (skill.TypeId, skill.Level, skill.SkillPoints)).ToList();
        Assert.Equal(donorRows, takerRows);
        Assert.DoesNotContain(takerRows, row => row.TypeId == extra.TypeId);
        var queue = await rig.Training.ViewAsync(taker.CharacterId, 1);
        Assert.Empty(queue.State.Entries);
        Assert.False(queue.State.Running);
        Assert.Equal("Usage: /skillscopy \"<from character>\" \"<to character>\"", await rig.SlashAsync(gm, "/skillscopy onlyone"));
    }
}
