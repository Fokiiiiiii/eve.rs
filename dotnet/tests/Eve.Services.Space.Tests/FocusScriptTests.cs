using Eve.Data;
using Eve.Dogma;
using Eve.Sde;
using Eve.Space;
using Npgsql;

namespace Eve.Services.Space.Tests;

/// <summary>
/// The focus scripts of a warp disruption field generator (group 908): loaded as the generator's charge, a script's effect runs on a target ship. The disruption
/// script reaches the generator's warp scramble range (16 km) with its own range bonus (+50 %, so 24 km), with the generator's scramble strength (100); the
/// scrambling script (-20 %) has the same strength and blocks a microwarpdrive.
/// </summary>
public class FocusScriptTests
{
    private const int Ship = 587;
    private const int FieldGeneratorI = 28654;
    private const int FocusedDisruptionScript = 29003;
    private const int FocusedScramblingScript = 45010;

    [PgFact]
    public async Task A_focus_script_loaded_in_a_field_generator_projects_its_scramble_over_its_own_range()
    {
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        var sde = new SdeReader(source);
        var statics = await DogmaStatic.LoadAsync(sde);
        await statics.EnsureTypesAsync([Ship, FieldGeneratorI, FocusedDisruptionScript, FocusedScramblingScript]);

        var disruption = Package(statics, FocusedDisruptionScript, "shipModuleFocusedWarpDisruptionScript");
        Assert.Null(disruption.Reason);
        var disrupt = Assert.IsType<SimAction.Remote>(disruption.Effect!.Action);
        Assert.Equal(new SimRemote.Scramble(100.0, false), disrupt.What);
        Assert.Equal(16_000.0 * 1.5, disrupt.Optimal, 3);

        var scrambling = Package(statics, FocusedScramblingScript, "shipModuleFocusedWarpScramblingScript");
        Assert.Null(scrambling.Reason);
        var scramble = Assert.IsType<SimAction.Remote>(scrambling.Effect!.Action);
        Assert.Equal(new SimRemote.Scramble(100.0, true), scramble.What);
        Assert.Equal(16_000.0 * 0.8, scramble.Optimal, 3);
    }

    /// <summary>A Generator I, fitted and online, with the script loaded in it (the script is the charge, in the module's slot), asked for its effect at a target ship.</summary>
    private static ModulePackages.Result Package(DogmaStatic statics, int scriptTypeId, string effectName)
    {
        const long shipId = 73100, characterId = 73103, moduleId = 73101, scriptId = 73102, targetId = 73105;
        const int moduleFlag = 11;
        var ship = new Eve.Inventory.Item(shipId, Ship, characterId, 60003760, 0, 1, true, null);
        var module = new Eve.Inventory.Item(moduleId, FieldGeneratorI, characterId, shipId, moduleFlag, 1, true, null);
        var script = new Eve.Inventory.Item(scriptId, scriptTypeId, characterId, shipId, moduleFlag, 1, false, null);
        var world = new DogmaWorld(statics, characterId, shipId);
        world.Add(new DogmaItem(shipId, Ship, characterId, ship.LocationId, ship.Flag, ItemState.Online));
        world.Add(new DogmaItem(moduleId, FieldGeneratorI, characterId, shipId, moduleFlag, ItemState.Online));
        world.Add(new DogmaItem(scriptId, scriptTypeId, characterId, shipId, moduleFlag, ItemState.Online));
        var snapshot = new Eve.Ships.ShipSnapshot { Character = null!, Ship = ship, Fitted = [module], Charges = [script], Skills = [], World = world };
        return ModulePackages.Of(snapshot, statics, moduleId, effectName, targetId, 1);
    }
}
