using Eve.Macho;
using Eve.Marshal;
using Eve.Ships;
using Eve.Space;
using Eve.Universe;

namespace Eve.Services.Space.Tests;

/// <summary>
/// The capital jump drive to a lit cynosural field (EVE University wiki, "Jump drive" and "Jump fatigue"): a Revelation in a low-security system spools to a field
/// lit by its fleet in another system within range, takes its fuel and sets its cooldown; a field that is not lit, a field of another fleet and a drive that cools
/// down are refused, and the fuel and the fatigue are written together or not at all.
/// </summary>
public class CynoJumpHostTests
{
    private const int Revelation = 19720, Helium = 16274, FuelBayFlag = CharacterShips.FuelBayFlag;
    private const long Field = 4_250_000_000_000_201, OtherField = 4_250_000_000_000_202;
    private const int Origin = 30100032, Destination = 30100099;
    private const long Minute = 60_000, Spool = 300_000;

    /// <summary>
    /// The origin is low-security in this catalogue (the rig's station system is high-security, and a capital does not jump from there), and the destination is one
    /// light year off along x (the test's own geometry).
    /// </summary>
    private static SolarSystemCatalog Systems() => new([
        new SolarSystemInfo(Origin, "Test Origin", 1, 1, -0.5, 0, 0, 0, 0),
        new SolarSystemInfo(Destination, "Test Destination", 1, 1, -0.4, 0, SolarSystemCatalog.MetersPerLightYear, 0, 0),
    ]);

    private static SpaceJumpDrive Drive(Rig rig, Func<long, ClientSession?>? sessionOf = null) => new(
        rig.Ships, rig.Statics, rig.Deployed, new PgJumpFatigueStore(rig.Source, rig.SpaceSchema), Systems(), rig.Skills, rig.Source, rig.Space,
        rig.FleetStore, () => rig.Now, null, sessionOf);

    /// <summary>A Revelation (a jump drive hull) in space, with its fleet, its fuel in the fuel bay and the given fuel units.</summary>
    private static async Task<(ClientSession Session, Eve.Characters.Created Character, long Ship, string Bound)> JumperAsync(Rig rig, long fuel)
    {
        var character = await rig.NewCharacterAsync(shipTypeId: Revelation);
        var ship = character.ShipId;
        // the fuel goes into the fuel bay while the ship is docked, as the client moves it
        if (fuel > 0)
        {
            var stack = (await rig.Ships.GrantAsync(character.CharacterId, Helium, fuel, singleton: false))!.Single().After;
            Assert.IsType<Eve.Ships.ShipChanged>(await rig.Ships.MoveAsync(character.CharacterId, stack.Id, Rig.Station, ship, FuelBayFlag, fuel));
        }
        var session = await rig.SessionAsync(character, 1);
        var oid = await rig.BindShipAsync(session);
        await rig.ShipCallAsync(session, "Undock", oid, new PyLong(session.GetInt("shipid")!.Value), PyBool.False);
        await rig.BindBeyonceAsync(session);
        return (session, character, ship, oid);
    }

    /// <summary>The fleet a character is in, or a new one they lead (the login may already have put them in one).</summary>
    private static async Task FleetOfAsync(Rig rig, long characterId)
    {
        var fleet = await rig.FleetStore.FleetIdForCharacterAsync(characterId) ?? await rig.FleetStore.CreateAsync(characterId);
        Assert.True(fleet > 0);
    }

    /// <summary>A field lit by <paramref name="owner"/> in the destination system, its life running out at ten minutes from now.</summary>
    private static async Task LightAsync(Rig rig, long field, long owner, long ship)
    {
        await rig.Deployed.SaveAsync(new DeployedRecord(
            field, "cyno_field", Destination, "Cynosural Field", 21_094, 2_020, [SolarSystemCatalog.MetersPerLightYear, 0, 0], 250, owner, 0, 0, ship, rig.Now + 10 * Minute));
    }

    [PgFact]
    public async Task A_revelation_jumps_to_its_fleets_lit_field_pays_the_fuel_and_cools_down()
    {
        await using var rig = await Rig.CreateAsync();
        var (session, character, ship, oid) = await JumperAsync(rig, fuel: 50_000);
        await FleetOfAsync(rig, character.CharacterId);
        await LightAsync(rig, Field, character.CharacterId, ship);
        var drive = Drive(rig);
        rig.Sim.Heard.Clear();

        var answer = await drive.JumpToFieldAsync(session, Field, character.CharacterId, Destination, oid, CancellationToken.None);

        // the spool is asked of the simulation: the field, its system and the drive's spool
        var spool = Assert.Single(rig.Sim.Heard.OfType<SimCommand>(), command => command.Code == SimCommands.CynoJump);
        Assert.Equal(SimCommands.CynoJump, spool.Code);
        Assert.Equal(new double[] { Field, Destination, Spool }, spool.Args.ToArray());
        Assert.IsType<PyTuple>(answer);
        // one light year: 3 000 units of helium (the hull's 868), and the fatigue and the cooldown of two steps
        Assert.Equal(47_000, await rig.Ships.FuelHeldAsync(character.CharacterId, ship, Helium));
        var state = await new PgJumpFatigueStore(rig.Source, rig.SpaceSchema).GetAsync(character.CharacterId);
        Assert.Equal((rig.Now + 20 * Minute, rig.Now + 2 * Minute), (state.FatigueUntilMs, state.CooldownUntilMs));
    }

    [PgFact]
    public async Task A_field_lit_for_a_fleet_is_told_to_its_members_and_its_end_is_told_too()
    {
        await using var rig = await Rig.CreateAsync();
        var (session, character, ship, _) = await JumperAsync(rig, fuel: 50_000);
        await FleetOfAsync(rig, character.CharacterId);
        var drive = Drive(rig, characterId => characterId == character.CharacterId ? session : null);
        rig.Containers.FieldAnnounced = drive.AnnounceFieldAsync;
        Events.Of(session);

        rig.Sim.Say(new SimDeployed(
            Destination, Field, "Cynosural Field", 21_094, 2_020, [SolarSystemCatalog.MetersPerLightYear, 0, 0], 250, character.CharacterId, 0, 0, ship, 600_000));
        for (var wait = 0; wait < 200 && !Events.Of(session, clear: false).Any(e => e.Name == "OnFleetJumpBeaconModuleChange"); wait++)
        {
            await Task.Delay(25);
        }
        var lit = Events.Of(session).Single(e => e.Name == "OnFleetJumpBeaconModuleChange");
        Assert.Equal(
            new[] { character.CharacterId, Destination, Field, 21_094 },
            lit.Args.Take(4).Select(Events.Number).ToArray());
        Assert.True(lit.Args[4] is PyBool { Value: true }, "lit");

        rig.Now += 600_001;
        await rig.Containers.SweepAsync();
        var gone = Events.Of(session).Last(e => e.Name == "OnFleetJumpBeaconModuleChange");
        Assert.True(gone.Args[4] is PyBool { Value: false }, "gone");
        Assert.Equal(Field, Events.Number(gone.Args[2]));
    }

    [PgFact]
    public async Task A_field_that_is_not_lit_or_not_of_the_fleet_is_refused_and_nothing_is_spooled()
    {
        await using var rig = await Rig.CreateAsync();
        var (session, character, ship, oid) = await JumperAsync(rig, fuel: 50_000);
        var drive = Drive(rig);
        rig.Sim.Heard.Clear();

        // no field of that id is lit
        await Assert.ThrowsAnyAsync<MachoWrappedException>(() => drive.JumpToFieldAsync(session, OtherField, 0, Destination, oid, CancellationToken.None));

        // a field lit by a character who leads a fleet of their own: the jumper is in no fleet
        var lighter = await rig.NewCharacterAsync(name: "Lighter", account: 2);
        await rig.FleetStore.CreateAsync(lighter.CharacterId);
        await LightAsync(rig, OtherField, lighter.CharacterId, ship);
        await Assert.ThrowsAnyAsync<MachoWrappedException>(() => drive.JumpToFieldAsync(session, OtherField, lighter.CharacterId, Destination, oid, CancellationToken.None));

        Assert.DoesNotContain(rig.Sim.Heard.OfType<SimCommand>(), command => command.Code == SimCommands.CynoJump);
        Assert.Equal(50_000, await rig.Ships.FuelHeldAsync(character.CharacterId, ship, Helium));
    }

    [PgFact]
    public async Task A_drive_that_is_cooling_down_is_refused_until_the_cooldown_has_passed()
    {
        await using var rig = await Rig.CreateAsync();
        var (session, character, ship, oid) = await JumperAsync(rig, fuel: 50_000);
        await FleetOfAsync(rig, character.CharacterId);
        await LightAsync(rig, Field, character.CharacterId, ship);
        var drive = Drive(rig);
        // a jump that has just been made: its cooldown runs two minutes
        var fatigue = new PgJumpFatigueStore(rig.Source, rig.SpaceSchema);
        Assert.True(await rig.Ships.DebitJumpAsync(fatigue, character.CharacterId, ship, Helium, 1, new JumpFatigueState(rig.Now + 20 * Minute, rig.Now + 2 * Minute)));
        rig.Sim.Heard.Clear();

        await Assert.ThrowsAnyAsync<MachoWrappedException>(() => drive.JumpToFieldAsync(session, Field, character.CharacterId, Destination, oid, CancellationToken.None));
        Assert.DoesNotContain(rig.Sim.Heard.OfType<SimCommand>(), command => command.Code == SimCommands.CynoJump);

        rig.Now += 3 * Minute;
        await drive.JumpToFieldAsync(session, Field, character.CharacterId, Destination, oid, CancellationToken.None);
        Assert.Single(rig.Sim.Heard.OfType<SimCommand>(), command => command.Code == SimCommands.CynoJump);
    }

    [PgFact]
    public async Task A_ship_short_of_the_fuel_for_the_jump_is_refused_with_nothing_spooled_or_taken()
    {
        await using var rig = await Rig.CreateAsync();
        var (session, character, ship, oid) = await JumperAsync(rig, fuel: 2_000);
        await FleetOfAsync(rig, character.CharacterId);
        await LightAsync(rig, Field, character.CharacterId, ship);
        rig.Sim.Heard.Clear();

        // three thousand units are needed for one light year and two thousand are held
        await Assert.ThrowsAnyAsync<MachoWrappedException>(() => Drive(rig).JumpToFieldAsync(session, Field, character.CharacterId, Destination, oid, CancellationToken.None));
        Assert.DoesNotContain(rig.Sim.Heard.OfType<SimCommand>(), command => command.Code == SimCommands.CynoJump);
        Assert.Equal(2_000, await rig.Ships.FuelHeldAsync(character.CharacterId, ship, Helium));
        Assert.Equal(JumpFatigueState.None, await new PgJumpFatigueStore(rig.Source, rig.SpaceSchema).GetAsync(character.CharacterId));
    }

    [PgFact]
    public async Task The_debit_takes_the_fuel_and_writes_the_fatigue_together_or_neither()
    {
        await using var rig = await Rig.CreateAsync();
        var (_, character, ship, _) = await JumperAsync(rig, fuel: 1_000);
        var fatigue = new PgJumpFatigueStore(rig.Source, rig.SpaceSchema);
        var next = new JumpFatigueState(rig.Now + 20 * Minute, rig.Now + 2 * Minute);

        Assert.False(await rig.Ships.DebitJumpAsync(fatigue, character.CharacterId, ship, Helium, 3_000, next));
        Assert.Equal(1_000, await rig.Ships.FuelHeldAsync(character.CharacterId, ship, Helium));
        Assert.Equal(JumpFatigueState.None, await fatigue.GetAsync(character.CharacterId));

        Assert.True(await rig.Ships.DebitJumpAsync(fatigue, character.CharacterId, ship, Helium, 400, next));
        Assert.Equal(600, await rig.Ships.FuelHeldAsync(character.CharacterId, ship, Helium));
        Assert.Equal(next, await fatigue.GetAsync(character.CharacterId));
    }
}
