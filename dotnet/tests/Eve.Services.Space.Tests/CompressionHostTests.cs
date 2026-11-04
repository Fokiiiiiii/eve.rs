using Eve.Marshal;
using Eve.Space;

namespace Eve.Services.Space.Tests;

/// <summary>
/// runs as an ordinary module cycle, and while it runs its ship is a compression facility for the pilots of its fleet within its range, for the items of its type list.
/// The host decides, replaces the stack in the same hold in one inventory transaction and tells the pilot. Real stores, the real static data, a simulation played by the test.
/// </summary>
public class CompressionHostTests
{
    private const int Porpoise = 42244, MediumOreCompressor = 62622, MediumGasCompressor = 62624, HighSlot = 27;
    private const int Veldspar = 1230, CompressedVeldspar = 62516, ClearIcicle = 16262, FulleriteC28 = 30375, CompressedFulleriteC28 = 62402, Tritanium = 34;
    private const int Radius = 162, MaxRange = 54;
    private const string EffectName = "industrialItemCompression";

    private sealed record Pilot(ClientSession Session, Eve.Characters.Created Character, long Module, long ShipId);

    /// <summary>A Porpoise in space with the compressor fitted and online; running when <paramref name="run"/>.</summary>
    private static async Task<Pilot> CompressorAsync(Rig rig, int moduleTypeId, bool run = true, string name = "Compressor", int sessionId = 1)
    {
        var (session, character, module) = await RepairHostTests.InSpaceWithAsync(rig, moduleTypeId, HighSlot, shipTypeId: Porpoise, characterName: name, sessionId: sessionId);
        if (run)
        {
            Assert.Equal(ModuleResult.Done, await rig.Space.ActivateAsync(session, module, EffectName, 0, 1000, CancellationToken.None));
        }
        return new Pilot(session, character, module, session.GetInt("shipid")!.Value);
    }

    /// <summary>A pilot in space with no compressor (an ordinary ship in a fleet with the compressor's ship).</summary>
    private static async Task<Pilot> MinerAsync(Rig rig, string name = "Miner", int sessionId = 2)
    {
        var (session, character, module) = await RepairHostTests.InSpaceWithAsync(rig, 523, 11, characterName: name, sessionId: sessionId);
        return new Pilot(session, character, module, session.GetInt("shipid")!.Value);
    }

    private static async Task<Eve.Inventory.Item> HoldAsync(Rig rig, Pilot pilot, int typeId, long quantity)
    {
        // made in the hold directly: the pilot is already in space, where the hangar-to-hold move is not the path under test
        await using var connection = await rig.Source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var id = await rig.Items.CreateAsync(connection, transaction, new Eve.Inventory.NewItem(typeId, pilot.Character.CharacterId, pilot.ShipId, Eve.Ships.CharacterShips.CargoFlag, quantity, false));
        await transaction.CommitAsync();
        return (await rig.Items.GetAsync(id))!;
    }

    private static string NoticeOf(MachoWrappedException error)
    {
        var arguments = ((PyTuple)((PyTuple)((PyObjectEx)error.Error.Payload).Header).Items[1]).Items;
        Assert.Equal("CustomNotify", ((PyStr)arguments[0]).Value);
        var values = ((PyDict)arguments[1]).Entries.ToDictionary(e => ((PyStr)e.Key).Value, e => e.Value);
        return ((PyStr)values["notify"]).Value;
    }

    private static async Task<string> RefusedAsync(Rig rig, Pilot pilot, Eve.Inventory.Item item, long facilityShipId) =>
        NoticeOf(await Assert.ThrowsAsync<MachoWrappedException>(() => rig.Containers.CompressInSpaceAsync(pilot.Session, item, facilityShipId, CancellationToken.None)));

    private static async Task<double> RadiusAsync(Rig rig, Pilot pilot) =>
        (await rig.Ships.LoadAsync(pilot.Character.CharacterId, CancellationToken.None))!.World.Get(pilot.ShipId, Radius);

    /// <summary>Puts the two ships so that the surfaces are <paramref name="surfaceMetres"/> apart, and the two pilots in one fleet.</summary>
    private static async Task PlaceInOneFleetAsync(Rig rig, Pilot facility, Pilot miner, double surfaceMetres)
    {
        var centres = surfaceMetres + await RadiusAsync(rig, facility) + await RadiusAsync(rig, miner);
        rig.Sim.PositionAnswers[facility.ShipId] = [0, 0, 0];
        rig.Sim.PositionAnswers[miner.ShipId] = [centres, 0, 0];
        rig.Space.FleetIdOfAsync = (_, _) => Task.FromResult<long?>(900);
    }

    [PgFact]
    public async Task A_compressor_runs_as_a_cycle_of_its_duration_and_needs_the_industrial_core_the_simulation_enforces()
    {
        await using var rig = await Rig.CreateAsync();
        var pilot = await CompressorAsync(rig, MediumOreCompressor);

        var effect = Assert.IsType<SimActivate>(rig.Sim.Heard.Last()).Effect;

        // the SDE: duration 60 000 ms (attribute 73 after dogma), capacitorNeed 100 (attribute 6 of type 62622)
        Assert.Equal((pilot.Module, MediumOreCompressor, 60_000d, 100d), (effect.ModuleId, effect.ModuleType, effect.DurationMs, effect.CapacitorNeed));
        Assert.True(effect.Rules!.RequiresIndustrialCore);
        Assert.False(effect.Rules.ProvidesIndustrialCore);
        Assert.True(rig.Space.IsModuleActive((ulong)pilot.Session.SessionId, pilot.Module));
    }

    [PgFact]
    public async Task A_running_compressor_makes_its_ship_a_facility_for_its_type_list_with_the_range_dogma_gives_it()
    {
        await using var rig = await Rig.CreateAsync();
        var idle = await CompressorAsync(rig, MediumOreCompressor, run: false, name: "Idle", sessionId: 3);
        var pilot = await CompressorAsync(rig, MediumOreCompressor);
        var snapshot = (await rig.Ships.LoadAsync(pilot.Character.CharacterId, CancellationToken.None))!;

        Assert.Null(await rig.Space.CompressionFacilityOfAsync(idle.ShipId, CancellationToken.None));
        var facility = (await rig.Space.CompressionFacilityOfAsync(pilot.ShipId, CancellationToken.None))!;

        Assert.Equal(pilot.Character.CharacterId, facility.CharacterId);
        // the SDE: the medium ore compressor works type list 334 (all asteroid ore) over maxRange 44 000 m, plus what the pilot's skills add (dogma, not a table here)
        var range = Assert.Single(facility.TypeLists);
        Assert.Equal(334, range.Key);
        Assert.Equal((int)Math.Round(snapshot.World.Get(pilot.Module, MaxRange)), range.Value);
        Assert.True(range.Value >= 44_000);
    }

    [PgFact]
    public async Task A_pilot_compresses_ore_of_its_own_hold_at_its_own_compressor_and_gets_the_six_values_and_the_item_changes()
    {
        await using var rig = await Rig.CreateAsync();
        var pilot = await CompressorAsync(rig, MediumOreCompressor);
        var ore = await HoldAsync(rig, pilot, Veldspar, 4_000);
        Events.Of(pilot.Session);

        var made = await rig.Containers.CompressInSpaceAsync(pilot.Session, ore, pilot.ShipId, CancellationToken.None);

        // the units stay and each one is a hundredth of the volume (the SDE: Veldspar 0.1 m3, Compressed Veldspar 0.001 m3): a new stack replaces the old one
        Assert.Equal(ore.Id, made.SourceItemId);
        Assert.Equal((Veldspar, 4_000L, CompressedVeldspar, 4_000L), (made.SourceTypeId, made.SourceQuantity, made.OutputTypeId, made.OutputQuantity));
        Assert.NotEqual(ore.Id, made.OutputItemId);
        Assert.Null(await rig.Items.GetAsync(ore.Id));
        var output = (await rig.Items.GetAsync(made.OutputItemId))!;
        Assert.Equal((CompressedVeldspar, 4_000L, pilot.ShipId, Eve.Ships.CharacterShips.CargoFlag, pilot.Character.CharacterId), (output.TypeId, output.Quantity, output.LocationId, output.Flag, output.OwnerId));
        Assert.Contains("OnItemChange", Events.Of(pilot.Session).Select(e => e.Name));
    }

    [PgFact]
    public async Task Compressed_ore_joins_the_stack_of_its_type_the_hold_already_has()
    {
        await using var rig = await Rig.CreateAsync();
        var pilot = await CompressorAsync(rig, MediumOreCompressor);
        var held = await HoldAsync(rig, pilot, CompressedVeldspar, 25);
        var ore = await HoldAsync(rig, pilot, Veldspar, 300);

        var made = await rig.Containers.CompressInSpaceAsync(pilot.Session, ore, pilot.ShipId, CancellationToken.None);

        Assert.Equal(held.Id, made.OutputItemId);
        Assert.Equal(300, made.OutputQuantity);
        Assert.Equal(325, (await rig.Items.GetAsync(held.Id))!.Quantity);
        Assert.Null(await rig.Items.GetAsync(ore.Id));
    }

    [PgFact]
    public async Task A_compressor_that_does_not_run_is_no_facility()
    {
        await using var rig = await Rig.CreateAsync();
        var pilot = await CompressorAsync(rig, MediumOreCompressor, run: false);
        var ore = await HoldAsync(rig, pilot, Veldspar, 100);

        Assert.Equal(SpaceContainers.CompressNoFacilityNotice, await RefusedAsync(rig, pilot, ore, pilot.ShipId));
        Assert.NotNull(await rig.Items.GetAsync(ore.Id));
        Assert.Equal(Veldspar, (await rig.Items.GetAsync(ore.Id))!.TypeId);
    }

    [PgFact]
    public async Task An_ore_compressor_does_not_compress_ice_or_what_has_no_compressed_form()
    {
        await using var rig = await Rig.CreateAsync();
        var pilot = await CompressorAsync(rig, MediumOreCompressor);
        var ice = await HoldAsync(rig, pilot, ClearIcicle, 1);
        var minerals = await HoldAsync(rig, pilot, Tritanium, 100);

        Assert.Equal(SpaceContainers.CompressWrongKindNotice, await RefusedAsync(rig, pilot, ice, pilot.ShipId));
        Assert.Equal(SpaceContainers.CompressNotCompressibleNotice, await RefusedAsync(rig, pilot, minerals, pilot.ShipId));
        Assert.Equal(ClearIcicle, (await rig.Items.GetAsync(ice.Id))!.TypeId);
        Assert.Equal(Tritanium, (await rig.Items.GetAsync(minerals.Id))!.TypeId);
    }

    [PgFact]
    public async Task A_gas_compressor_compresses_gas_and_not_ore()
    {
        await using var rig = await Rig.CreateAsync();
        var pilot = await CompressorAsync(rig, MediumGasCompressor);
        var gas = await HoldAsync(rig, pilot, FulleriteC28, 10);
        var ore = await HoldAsync(rig, pilot, Veldspar, 100);

        var made = await rig.Containers.CompressInSpaceAsync(pilot.Session, gas, pilot.ShipId, CancellationToken.None);

        Assert.Equal((FulleriteC28, CompressedFulleriteC28, 10L), (made.SourceTypeId, made.OutputTypeId, made.OutputQuantity));
        Assert.Equal(SpaceContainers.CompressWrongKindNotice, await RefusedAsync(rig, pilot, ore, pilot.ShipId));
    }

    [PgFact]
    public async Task A_fleet_member_within_range_compresses_at_the_compressor_of_another_ship()
    {
        await using var rig = await Rig.CreateAsync();
        var facility = await CompressorAsync(rig, MediumOreCompressor);
        var miner = await MinerAsync(rig);
        var ore = await HoldAsync(rig, miner, Veldspar, 500);
        await PlaceInOneFleetAsync(rig, facility, miner, surfaceMetres: 40_000);

        var made = await rig.Containers.CompressInSpaceAsync(miner.Session, ore, facility.ShipId, CancellationToken.None);

        Assert.Equal((CompressedVeldspar, 500L), (made.OutputTypeId, made.OutputQuantity));
        var output = (await rig.Items.GetAsync(made.OutputItemId))!;
        // the miner's own hold changed, the facility pilot's did not
        Assert.Equal((miner.ShipId, miner.Character.CharacterId), (output.LocationId, output.OwnerId));
    }

    [PgFact]
    public async Task A_fleet_member_past_the_range_of_the_compressor_is_refused_and_keeps_its_ore()
    {
        await using var rig = await Rig.CreateAsync();
        var facility = await CompressorAsync(rig, MediumOreCompressor);
        var miner = await MinerAsync(rig);
        var ore = await HoldAsync(rig, miner, Veldspar, 500);
        var reach = (await rig.Space.CompressionFacilityOfAsync(facility.ShipId, CancellationToken.None))!.TypeLists[334];
        await PlaceInOneFleetAsync(rig, facility, miner, surfaceMetres: reach + 500);

        Assert.Equal(SpaceContainers.CompressOutOfRangeNotice, await RefusedAsync(rig, miner, ore, facility.ShipId));
        Assert.Equal(Veldspar, (await rig.Items.GetAsync(ore.Id))!.TypeId);
    }

    [PgFact]
    public async Task A_pilot_outside_the_fleet_of_the_compressor_is_refused_though_it_is_in_range()
    {
        await using var rig = await Rig.CreateAsync();
        var facility = await CompressorAsync(rig, MediumOreCompressor);
        var outsider = await MinerAsync(rig, "Outsider");
        var ore = await HoldAsync(rig, outsider, Veldspar, 500);
        await PlaceInOneFleetAsync(rig, facility, outsider, surfaceMetres: 1_000);
        rig.Space.FleetIdOfAsync = (character, _) => Task.FromResult<long?>(character == facility.Character.CharacterId ? 900 : null);

        Assert.Equal(SpaceContainers.CompressNotInFleetNotice, await RefusedAsync(rig, outsider, ore, facility.ShipId));
        Assert.Equal(Veldspar, (await rig.Items.GetAsync(ore.Id))!.TypeId);
    }

    [PgFact]
    public async Task A_stack_that_is_not_in_the_pilots_active_ship_is_never_compressed()
    {
        await using var rig = await Rig.CreateAsync();
        var facility = await CompressorAsync(rig, MediumOreCompressor);
        var miner = await MinerAsync(rig);
        var theirs = await HoldAsync(rig, facility, Veldspar, 500);
        await PlaceInOneFleetAsync(rig, facility, miner, surfaceMetres: 1_000);

        // the fleet member names the compressor pilot's own stack: it is not in the miner's ship
        var error = await Assert.ThrowsAsync<MachoWrappedException>(() => rig.Containers.CompressInSpaceAsync(miner.Session, theirs, facility.ShipId, CancellationToken.None));

        Assert.Equal(SpaceContainers.CompressFailedNotice, NoticeOf(error));
        Assert.Equal(Veldspar, (await rig.Items.GetAsync(theirs.Id))!.TypeId);
    }
}
