namespace Eve.Ships.Tests;

/// <summary>
/// The role bonuses of the special hulls reach the ship's dogma world (SF-11): a command destroyer (group 1534), a blockade runner (1202), a black ops
/// ship (898) and a jump freighter (902) each has its passive bonus effects on the hull, and they change the hull's numbers as the static data says. The
/// rules that the bonuses feed (command bursts, the cloak and the jump drive) are modules' and are not in this test.
/// </summary>
public class SpecialHullBonusTests
{
    private const int CommandDestroyerBifrost = 37480, BlockadeRunnerCrane = 12729, BlackOpsRedeemer = 22428, JumpFreighterRhea = 28844;

    private static async Task AssertTheHullNumbersAreBonusedAsync(int hullTypeId)
    {
        await using var rig = await Rig.CreateAsync();
        var c = await rig.NewCharacterAsync(shipTypeId: hullTypeId);
        var snapshot = await rig.Ships.LoadAsync(c.CharacterId);
        Assert.NotNull(snapshot);
        await rig.Statics.EnsureTypesAsync([hullTypeId]);
        var hull = rig.Statics.Type(hullTypeId)!;
        var numbers = snapshot.World.Attributes(snapshot.Ship.Id);
        var bonused = numbers.Where(pair => Math.Abs(pair.Value - hull.Attributes.GetValueOrDefault(pair.Key)) > 1e-9).Select(pair => pair.Key).ToList();
        Assert.NotEmpty(bonused);
    }

    [PgFact]
    public async Task The_command_destroyer_bonuses_reach_the_hull() => await AssertTheHullNumbersAreBonusedAsync(CommandDestroyerBifrost);

    [PgFact]
    public async Task The_blockade_runner_bonuses_reach_the_hull() => await AssertTheHullNumbersAreBonusedAsync(BlockadeRunnerCrane);

    [PgFact]
    public async Task The_black_ops_bonuses_reach_the_hull() => await AssertTheHullNumbersAreBonusedAsync(BlackOpsRedeemer);

    [PgFact]
    public async Task The_jump_freighter_bonuses_reach_the_hull() => await AssertTheHullNumbersAreBonusedAsync(JumpFreighterRhea);
}
