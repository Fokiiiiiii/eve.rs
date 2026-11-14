using Eve.Dogma;
using Eve.Fitting;
using Eve.Inventory;
using Eve.Skills;

namespace Eve.Ships.Tests;

/// <summary>
/// An active expert system supplies skill levels to the online check (EveJS exposes them in the ship's skill view). Fitting keeps a module
/// offline without that check; the grant comes from the expert system store through the ship snapshot.
/// </summary>
public class ExpertGrantFitTests
{
    private const int HullUpgrades = 3394;
    private const int CoreShipOperations = 57208; // grants Hull Upgrades at level 5 for seven days

    [PgFact]
    public async Task An_expert_system_allows_a_fitted_module_to_online_and_the_module_is_refused_after_the_lapse()
    {
        await using var rig = await Rig.CreateAsync();
        var c = await rig.NewCharacterAsync();
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var store = new PgExpertSystemStore(rig.Source, rig.SkillsSchema);
        var expert = new ExpertSystems(ExpertSystemCatalog.LoadEmbedded(), store, () => now);
        var withExpert = new CharacterShips(rig.Source, rig.Statics, rig.Characters, rig.Items, rig.Skills, rig.Modules, expertSystems: expert);

        // no expert system: Damage Control II needs Hull Upgrades at 4 and the pilot has none
        var control = await rig.GiveAsync(c.CharacterId, Rig.DamageControlII);
        Assert.IsType<ShipChanged>(await rig.Ships.MoveAsync(c.CharacterId, control, Rig.Station, c.ShipId, 11, 1));
        var refused = Assert.IsType<ShipRefused>(await rig.Ships.SetOnlineAsync(c.CharacterId, c.ShipId, control, online: true));
        Assert.Equal(ShipRefusal.SkillRequired, refused.Reason);
        Assert.Equal((HullUpgrades, 4), (refused.Fit!.Data["skillTypeID"], refused.Fit.Data["requiredLevel"]));
        Assert.True(Convert.ToInt32(refused.Fit!.Data["currentLevel"]) < 4, "the new pilot is below the level the module needs");
        Assert.Equal(ItemState.Offline, (await rig.Modules.StatesAsync([control]))[control]);

        // the expert system is installed: the level it grants is the one the check reads, and the module fits
        await using (var connection = await rig.Source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await store.UpsertAsync(connection, transaction, new ExpertSystemEntry(c.CharacterId, CoreShipOperations, now, now + 7 * ExpertSystemRules.Day, null, "item", now));
            await transaction.CommitAsync();
        }
        Assert.IsType<ShipChanged>(await withExpert.SetOnlineAsync(c.CharacterId, c.ShipId, control, online: true));
        Assert.Equal(ItemState.Online, (await rig.Modules.StatesAsync([control]))[control]);

        // after the expert system has run out of time the grant is gone: the fitted module cannot be reactivated
        now += 8 * ExpertSystemRules.Day;
        Assert.IsType<ShipChanged>(await withExpert.SetOnlineAsync(c.CharacterId, c.ShipId, control, online: false));
        var lapsed = Assert.IsType<ShipRefused>(await withExpert.SetOnlineAsync(c.CharacterId, c.ShipId, control, online: true));
        Assert.Equal((ShipRefusal.SkillRequired, HullUpgrades, 4), (lapsed.Reason, lapsed.Fit!.Data["skillTypeID"], lapsed.Fit!.Data["requiredLevel"]));
        Assert.Equal(ItemState.Offline, (await rig.Modules.StatesAsync([control]))[control]);
    }
}
