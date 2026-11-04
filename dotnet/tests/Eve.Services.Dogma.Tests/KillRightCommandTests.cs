using Eve.Marshal;
using Eve.Services.Slash;

namespace Eve.Services.Dogma.Tests;

/// <summary>
/// <c>/killright</c> on the database rig: the grant is the crimewatch store's, read back from the table; a second open right, a right against oneself and a
/// missing name are answered as the reply says.
/// </summary>
public class KillRightCommandTests
{
    private static ClientSession Gm(ClientSession session)
    {
        session.Set("accountRole", new PyLong(GameMasterRoles.Gml));
        return session;
    }

    [PgFact]
    public async Task Gm_kill_right_is_made_once_and_refused_for_a_second_open_right_or_oneself()
    {
        await using var rig = await Rig.CreateAsync();
        var owner = await rig.NewCharacterAsync("Right Owner");
        var target = await rig.NewCharacterAsync("Right Target");
        var gm = Gm(Rig.Session(owner.CharacterId));

        var reply = await rig.SlashAsync(gm, "/killright \"Right Target\"");

        Assert.StartsWith("Made kill right ", reply);
        var right = Assert.Single(await rig.Crimewatch.KillRightsOfAsync(owner.CharacterId));
        Assert.Equal(target.CharacterId, right.TargetCharacterId);
        Assert.Equal((await rig.Characters.GetAsync(owner.CharacterId))!.SolarSystemId, right.SolarSystemId);
        Assert.StartsWith("No kill right was made", await rig.SlashAsync(gm, $"/killright {target.CharacterId}"));
        Assert.StartsWith("No kill right was made", await rig.SlashAsync(gm, "/killright me"));
        Assert.Single(await rig.Crimewatch.KillRightsOfAsync(owner.CharacterId));
    }

    [PgFact]
    public async Task Kill_right_without_a_target_answers_the_usage()
    {
        await using var rig = await Rig.CreateAsync();
        var owner = await rig.NewCharacterAsync("Usage Owner");

        Assert.Equal(KillRightCommands.Usage, await rig.SlashAsync(Gm(Rig.Session(owner.CharacterId)), "/killright"));
    }
}
