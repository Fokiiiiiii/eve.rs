using Eve.Fitting;
using Eve.Skills;

namespace Eve.Ships.Tests;

/// <summary>
/// A module's skill check honours the levels an active expert system grants (wiki: an expert system gives access to its skills while it is
/// active; EveJS grants them in the ship's skill view). The grant comes from the expert system store, through the ship snapshot the fit reads.
/// </summary>
public class ExpertGrantFitTests
{
    private const int HullUpgrades = 3394;
    private const int CoreShipOperations = 57208; // grants Hull Upgrades at level 5 for seven days

    [PgFact]
    public async Task A_module_refused_for_a_missing_skill_fits_while_an_expert_system_grants_it_and_is_refused_after_the_lapse()
    {
        await using var rig = await Rig.CreateAsync();
        var c = await rig.NewCharacterAsync();
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var store = new PgExpertSystemStore(rig.Source, rig.SkillsSchema);
        var expert = new ExpertSystems(ExpertSystemCatalog.LoadEmbedded(), store, () => now);
        var withExpert = new CharacterShips(rig.Source, rig.Statics, rig.Characters, rig.Items, rig.Skills, rig.Modules, expertSystems: expert);

        // no expert system: Damage Control II needs Hull Upgrades at 4 and the pilot has none
        var control = await rig.GiveAsync(c.CharacterId, Rig.DamageControlII);
        var refused = Assert.IsType<ShipRefused>(await rig.Ships.MoveAsync(c.CharacterId, control, Rig.Station, c.ShipId, 11, 1));
        Assert.Equal(FitError.SkillRequired, refused.Fit!.Error);
        Assert.Equal((HullUpgrades, 4), (refused.Fit.Data["skillTypeID"], refused.Fit.Data["requiredLevel"]));
        Assert.True(Convert.ToInt32(refused.Fit.Data["currentLevel"]) < 4, "the new pilot is below the level the module needs");

        // the expert system is installed: the level it grants is the one the check reads, and the module fits
        await using (var connection = await rig.Source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await store.UpsertAsync(connection, transaction, new ExpertSystemEntry(c.CharacterId, CoreShipOperations, now, now + 7 * ExpertSystemRules.Day, null, "item", now));
            await transaction.CommitAsync();
        }
        Assert.IsType<ShipChanged>(await withExpert.MoveAsync(c.CharacterId, control, Rig.Station, c.ShipId, 11, 1));

        // the same module in another slot is still refused without the grant (the plain ship view ignores expert systems)
        var second = await rig.GiveAsync(c.CharacterId, Rig.DamageControlII);
        Assert.Equal(FitError.SkillRequired, Assert.IsType<ShipRefused>(await rig.Ships.MoveAsync(c.CharacterId, second, Rig.Station, c.ShipId, 12, 1)).Fit!.Error);

        // after the expert system has run out of time the grant is gone: the module in the next slot is refused with the skill
        now += 8 * ExpertSystemRules.Day;
        Assert.Equal(FitError.SkillRequired, Assert.IsType<ShipRefused>(await withExpert.MoveAsync(c.CharacterId, second, Rig.Station, c.ShipId, 12, 1)).Fit!.Error);
    }
}
