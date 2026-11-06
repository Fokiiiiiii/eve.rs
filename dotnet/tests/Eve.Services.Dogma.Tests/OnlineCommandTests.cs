using Eve.Dogma;
using Eve.Sde;
using Eve.Services.Slash;

namespace Eve.Services.Dogma.Tests;

/// <summary>
/// <c>/online me</c>: the fitted modules that have the online effect go online (the client's online-all); a passive module is not touched and a module already
/// online is left alone. The fits are made by <c>/fit</c>; the offline state is set by the ship's own switch.
/// </summary>
public class OnlineCommandTests
{
    [PgFact]
    public async Task Offline_modules_with_the_online_effect_go_online_and_an_online_one_is_left_as_it_is()
    {
        await using var rig = await Rig.CreateAsync();
        var pilot = await rig.NewCharacterAsync("Online Pilot", shipTypeId: 587);
        var docked = Rig.Session(pilot.CharacterId);
        var names = new ItemTypeNames(new SdeReader(rig.Source));
        var afterburner = (await names.FindAsync("1MN Afterburner I")).Type!.TypeId;
        Assert.Equal("Created and fitted 1MN Afterburner I to Rifter (medium slot, flag 19).", await rig.SlashAsync(docked, "/fit me 1MN Afterburner I"));
        await rig.SlashAsync(docked, "/fit me Damage Control I");
        var moduleId = (await rig.Ships.LoadAsync(pilot.CharacterId))!.Fitted.Single(module => module.TypeId == afterburner).Id;

        await rig.Ships.SetOnlineAsync(pilot.CharacterId, pilot.ShipId, moduleId, false);
        Assert.True((await rig.Ships.LoadAsync(pilot.CharacterId))!.World.Item(moduleId)!.State < ItemState.Online);

        Assert.Equal("Online: 1MN Afterburner I.", await rig.SlashAsync(docked, "/online me"));
        Assert.True((await rig.Ships.LoadAsync(pilot.CharacterId))!.World.Item(moduleId)!.State >= ItemState.Online);

        Assert.Equal("Every fitted module with an online effect is online already.", await rig.SlashAsync(docked, "/online me"));
    }

    [PgFact]
    public async Task Online_needs_a_selected_character_a_usage_and_the_active_ship()
    {
        await using var rig = await Rig.CreateAsync();
        var pilot = await rig.NewCharacterAsync("Online Usage");

        Assert.Equal("Select a character before using /online.", await rig.SlashAsync(Rig.Session(null), "/online me"));
        Assert.Equal("Usage: /online <me|shipID>", await rig.SlashAsync(Rig.Session(pilot.CharacterId), "/online"));
        Assert.Equal("Ship not found: 999.", await rig.SlashAsync(Rig.Session(pilot.CharacterId), "/online 999"));
    }
}
