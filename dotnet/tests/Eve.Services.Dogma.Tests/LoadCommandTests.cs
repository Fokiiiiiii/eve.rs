using Eve.Services.Slash;

namespace Eve.Services.Dogma.Tests;

/// <summary><c>/load me</c> and <c>/unload me</c>: a charge goes from the station hangar into the fitted module that takes it, and back.</summary>
public class LoadCommandTests
{
    [PgFact]
    public async Task A_charge_is_loaded_into_the_turret_and_unloaded_to_the_hangar()
    {
        await using var rig = await Rig.CreateAsync();
        var pilot = await rig.NewCharacterAsync("Load Pilot", shipTypeId: 587);
        var docked = Rig.Session(pilot.CharacterId);
        Assert.StartsWith("Created and fitted", await rig.SlashAsync(docked, "/fit me Light Neutron Blaster I"));

        Assert.Equal("Loaded 10x Antimatter Charge S into your ship's modules.", await rig.SlashAsync(docked, "/load me Antimatter Charge S 10"));
        Assert.Single((await rig.Ships.LoadAsync(pilot.CharacterId))!.Charges);

        Assert.Equal("Unloaded 1 module to your station hangar.", await rig.SlashAsync(docked, "/unload me all"));
        Assert.Empty((await rig.Ships.LoadAsync(pilot.CharacterId))!.Charges);
        Assert.Equal("Nothing to unload.", await rig.SlashAsync(docked, "/unload me all"));
    }

    [PgFact]
    public async Task Load_and_unload_need_a_character_and_a_usage()
    {
        await using var rig = await Rig.CreateAsync();
        var pilot = await rig.NewCharacterAsync("Load Usage");

        Assert.Equal("Select a character before using /load.", await rig.SlashAsync(Rig.Session(null), "/load me Antimatter Charge S"));
        Assert.Equal("Usage: /load me <item name|typeID> [amount]", await rig.SlashAsync(Rig.Session(pilot.CharacterId), "/load Antimatter Charge S"));
        Assert.Equal("Usage: /unload me <all|item name|typeID|itemID>", await rig.SlashAsync(Rig.Session(pilot.CharacterId), "/unload"));
    }
}
