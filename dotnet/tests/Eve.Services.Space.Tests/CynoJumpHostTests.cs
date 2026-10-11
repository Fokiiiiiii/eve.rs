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
    /// <summary>A wormhole system and a Pochven system, one light year from the origin each: a jump drive may not enter either (the wiki).</summary>
    private const int Wormhole = 31_000_500, Pochven = 30_003_500;
    private const long Minute = 60_000, Spool = 300_000;

    /// <summary>
    /// The origin is low-security in this catalogue (the rig's station system is high-security, and a capital does not jump from there), and the destination is one
    /// light year off along x (the test's own geometry).
    /// </summary>
    private static SolarSystemCatalog Systems() => new([
        new SolarSystemInfo(Origin, "Test Origin", 1, 1, -0.5, 0, 0, 0, 0),
        new SolarSystemInfo(Destination, "Test Destination", 1, 1, -0.4, 0, SolarSystemCatalog.MetersPerLightYear, 0, 0),
        new SolarSystemInfo(Wormhole, "Test Wormhole", 1, 11_000_001, -0.99, 0, 0, SolarSystemCatalog.MetersPerLightYear, 0),
        new SolarSystemInfo(Pochven, "Test Pochven", 1, 10_000_070, -0.9, 0, 0, 0, SolarSystemCatalog.MetersPerLightYear),
    ]);

    /// <summary>A spool of the jump to Destination that takes the given fuel, ending as the drive would end it from now.</summary>
    private static JumpSpool SpoolOf(Rig rig, long characterId, long ship, long quantity) =>
        new(characterId, ship, Field, Destination, rig.Now + (long)Spool, Helium, quantity, JumpFatigueState.None);

    private static SpaceJumpDrive Drive(Rig rig, Func<long, ClientSession?>? sessionOf = null) => new(
        rig.Ships, rig.Statics, rig.Deployed, new PgJumpFatigueStore(rig.Source, rig.SpaceSchema), new PgJumpSpoolStore(rig.Source, rig.SpaceSchema), Systems(), rig.Skills, rig.Source, rig.Space,
        rig.FleetStore, () => rig.Now, null, sessionOf);

    /// <summary>The Rorqual (SDE 28352), the jump freighter's group 883 hull that the economizers fit, and the oxygen isotopes it burns (SDE 866 of the hull).</summary>
    private const int Rorqual = 28_352, Oxygen = 17_887;

    /// <summary>A jump drive hull (a Revelation by default) in space, with its fleet, its fuel in the fuel bay and the given fuel units.</summary>
    private static async Task<(ClientSession Session, Eve.Characters.Created Character, long Ship, string Bound)> JumperAsync(
        Rig rig, long fuel, int? economizer = null, int hull = Revelation, int fuelType = Helium, int? generator = null, long strontium = 0, int account = 1, string name = "Space Tester", int sessionId = 1)
    {
        var character = await rig.NewCharacterAsync(name: name, account: account, shipTypeId: hull);
        var ship = character.ShipId;
        if (economizer is not null)
        {
            // an economizer needs Jump Fuel Conservation at level one (the SDE: its requiredSkill1 is 21610, at 277 = 1)
            await using var connection = await rig.Source.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            await rig.Skills.GrantAsync(connection, transaction, character.CharacterId, [new Eve.Skills.CharacterSkill(21_610, 1, 0)]);
            await transaction.CommitAsync();
        }
        // the fuel goes into the fuel bay, and a module into a high slot, while the ship is docked, as the client moves them
        if (fuel > 0)
        {
            var stack = (await rig.Ships.GrantAsync(character.CharacterId, fuelType, fuel, singleton: false))!.Single().After;
            Assert.IsType<Eve.Ships.ShipChanged>(await rig.Ships.MoveAsync(character.CharacterId, stack.Id, Rig.Station, ship, FuelBayFlag, fuel));
        }
        if (economizer is { } module)
        {
            var fitted = (await rig.Ships.GrantAsync(character.CharacterId, module, 1, singleton: true))!.Single().After;
            var fit = await rig.Ships.MoveAsync(character.CharacterId, fitted.Id, Rig.Station, ship, Eve.Inventory.ItemFlags.LowSlot0, 1);
            Assert.True(fit is Eve.Ships.ShipChanged, fit is Eve.Ships.ShipRefused refused ? $"the economizer is refused: {refused.Reason} {refused.Fit}" : fit.ToString());
        }
        if (generator is { } portal)
        {
            // a jump portal generator needs Jump Portal Generation at level one, and it fits a high slot
            await using (var connection = await rig.Source.OpenConnectionAsync())
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                await rig.Skills.GrantAsync(connection, transaction, character.CharacterId, [new Eve.Skills.CharacterSkill(24_562, 1, 0)]);
                await transaction.CommitAsync();
            }
            var fitted = (await rig.Ships.GrantAsync(character.CharacterId, portal, 1, singleton: true))!.Single().After;
            var fit = await rig.Ships.MoveAsync(character.CharacterId, fitted.Id, Rig.Station, ship, Eve.Inventory.ItemFlags.HiSlot0, 1);
            Assert.True(fit is Eve.Ships.ShipChanged, fit is Eve.Ships.ShipRefused refused ? $"the generator is refused: {refused.Reason} {refused.Fit}" : fit.ToString());
        }
        if (strontium > 0)
        {
            var stack = (await rig.Ships.GrantAsync(character.CharacterId, 16_275, strontium, singleton: false))!.Single().After;
            Assert.IsType<Eve.Ships.ShipChanged>(await rig.Ships.MoveAsync(character.CharacterId, stack.Id, Rig.Station, ship, FuelBayFlag, strontium));
        }
        var session = await rig.SessionAsync(character, sessionId);
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
    private static async Task LightAsync(Rig rig, long field, long owner, long ship, int type = 21_094)
    {
        await rig.Deployed.SaveAsync(new DeployedRecord(
            field, "cyno_field", Destination, "Cynosural Field", type, 2_020, [SolarSystemCatalog.MetersPerLightYear, 0, 0], 250, owner, 0, 0, ship, rig.Now + 10 * Minute));
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

        // the spool is asked of the simulation: the field, its system, the drive's spool and the capacitor share (the Revelation's 95 %, no operation skill)
        var spool = Assert.Single(rig.Sim.Heard.OfType<SimCommand>(), command => command.Code == SimCommands.CynoJump);
        Assert.Equal(SimCommands.CynoJump, spool.Code);
        Assert.Equal(new double[] { Field, Destination, Spool, 0.95 }, spool.Args.ToArray());
        Assert.IsType<PyTuple>(answer);
        // one light year: 3 000 units of helium (the hull's 868), and the fatigue and the cooldown of two steps
        Assert.Equal(47_000, await rig.Ships.FuelHeldAsync(character.CharacterId, ship, Helium));
        var state = await new PgJumpFatigueStore(rig.Source, rig.SpaceSchema).GetAsync(character.CharacterId);
        Assert.Equal((rig.Now + 20 * Minute, rig.Now + 2 * Minute), (state.FatigueUntilMs, state.CooldownUntilMs));
    }

    [PgFact]
    public async Task A_spool_in_progress_is_kept_and_a_restart_refunds_its_fuel_and_restores_the_fatigue()
    {
        await using var rig = await Rig.CreateAsync();
        var (session, character, ship, oid) = await JumperAsync(rig, fuel: 50_000);
        await FleetOfAsync(rig, character.CharacterId);
        await LightAsync(rig, Field, character.CharacterId, ship);
        var spools = new PgJumpSpoolStore(rig.Source, rig.SpaceSchema);
        var start = rig.Now;
        await Drive(rig).JumpToFieldAsync(session, Field, character.CharacterId, Destination, oid, CancellationToken.None);

        // the spool is kept with its end, its fuel and the fatigue state it replaced
        var pending = Assert.Single(await spools.ListAsync());
        Assert.Equal((Field, Destination, start + (long)Spool), (pending.FieldId, pending.DestinationSystem, pending.EndsAtMs));
        Assert.Equal(JumpFatigueState.None, pending.Prior);
        Assert.Equal(47_000, await rig.Ships.FuelHeldAsync(character.CharacterId, ship, Helium));

        // the host restarts before the ship leaves: the spool is refunded and the fatigue it replaced is back
        await Drive(rig).CancelInterruptedAsync();
        Assert.Equal(50_000, await rig.Ships.FuelHeldAsync(character.CharacterId, ship, Helium));
        Assert.Equal(JumpFatigueState.None, await new PgJumpFatigueStore(rig.Source, rig.SpaceSchema).GetAsync(character.CharacterId));
        Assert.Empty(await spools.ListAsync());
    }

    [PgFact]
    public async Task A_completed_spool_is_forgotten_and_a_sweep_refunds_only_a_spool_the_simulation_lost()
    {
        await using var rig = await Rig.CreateAsync();
        var (session, character, ship, oid) = await JumperAsync(rig, fuel: 50_000);
        await FleetOfAsync(rig, character.CharacterId);
        await LightAsync(rig, Field, character.CharacterId, ship);
        var spools = new PgJumpSpoolStore(rig.Source, rig.SpaceSchema);
        var drive = Drive(rig);
        var start = rig.Now;
        await drive.JumpToFieldAsync(session, Field, character.CharacterId, Destination, oid, CancellationToken.None);

        // within the grace after the spool ends the simulation may still be jumping the ship: nothing is taken back
        rig.Now = start + (long)Spool + 30_000;
        await drive.SweepAsync();
        Assert.Single(await spools.ListAsync());

        // the ship jumped: the spool is forgotten, and a later sweep has nothing to refund
        await drive.ArrivedAsync(character.CharacterId);
        rig.Now = start + (long)Spool + 120_000;
        await drive.SweepAsync();
        Assert.Empty(await spools.ListAsync());
        Assert.Equal(47_000, await rig.Ships.FuelHeldAsync(character.CharacterId, ship, Helium));

        // a second spool the simulation lost (the pilot left before it ended) is refunded once the grace is over
        var second = rig.Now;
        await drive.JumpToFieldAsync(session, Field, character.CharacterId, Destination, oid, CancellationToken.None);
        Assert.Equal(44_000, await rig.Ships.FuelHeldAsync(character.CharacterId, ship, Helium));
        rig.Now = second + (long)Spool + 61_000;
        await drive.SweepAsync();
        Assert.Empty(await spools.ListAsync());
        Assert.Equal(47_000, await rig.Ships.FuelHeldAsync(character.CharacterId, ship, Helium));
    }

    [PgFact]
    public async Task A_fitted_economizer_takes_its_share_of_the_fuel_before_the_rounding()
    {
        await using var rig = await Rig.CreateAsync();
        // a Limited Jump Drive Economizer (SDE 34122, a bonus of 4 percent) fitted in a low slot; the economizers fit only a Rorqual or a jump freighter (SDE 1298, 1299)
        var (session, character, ship, oid) = await JumperAsync(rig, fuel: 50_000, economizer: 34_122, hull: Rorqual, fuelType: Oxygen);
        await FleetOfAsync(rig, character.CharacterId);
        await LightAsync(rig, Field, character.CharacterId, ship);
        await Drive(rig).JumpToFieldAsync(session, Field, character.CharacterId, Destination, oid, CancellationToken.None);
        // one light year is 4 000 units of the Rorqual (SDE 868); level one of Jump Fuel Conservation takes 10 percent and the economizer 4 percent: 3 456
        Assert.Equal(50_000 - 3_456, await rig.Ships.FuelHeldAsync(character.CharacterId, ship, Oxygen));
    }

    /// <summary>The Avatar (SDE 11567), a Titan, and its Jump Portal Generator (SDE 23953): the bridging hull of the bridge tests.</summary>
    private const int Avatar = 11_567, PortalGenerator = 23_953, Ibis = 601;

    /// <summary>The bridges of a rig (the fleet and the simulation of the rig; no session is told of them).</summary>
    private static SpaceBridges Bridges(Rig rig) => new(
        rig.Ships, rig.Statics, rig.Deployed, new PgJumpFatigueStore(rig.Source, rig.SpaceSchema), Systems(), rig.Skills, rig.Source, rig.Space,
        rig.FleetStore, () => rig.Now, null, null);

    [PgFact]
    public async Task A_titan_bridge_opens_to_the_fleets_field_and_a_fleetmate_passes_through_for_the_isotopes_and_the_fatigue()
    {
        await using var rig = await Rig.CreateAsync();
        var (session, character, ship, oid) = await JumperAsync(rig, fuel: 50_000, hull: Avatar, fuelType: Helium, generator: PortalGenerator, strontium: 500);
        await FleetOfAsync(rig, character.CharacterId);
        await LightAsync(rig, Field, character.CharacterId, ship);
        var fleet = (await rig.FleetStore.FleetIdForCharacterAsync(character.CharacterId))!.Value;
        var (passengerSession, passengerCharacter, passengerShip, passengerOid) = await JumperAsync(rig, fuel: 0, hull: Ibis, account: 2, name: "Fleetmate", sessionId: 2);
        // a fleetmate joins by an invitation to a wing and a squad (the fleet tests' pattern)
        var wing = await rig.FleetStore.CreateWingAsync(fleet, character.CharacterId);
        var squad = await rig.FleetStore.CreateSquadAsync(fleet, character.CharacterId, wing);
        Assert.True(await rig.FleetStore.InviteAsync(fleet, character.CharacterId, passengerCharacter.CharacterId, wing, squad, Eve.Services.Space.FleetStore.Member));
        Assert.NotNull(await rig.FleetStore.JoinAsync(fleet, passengerCharacter.CharacterId));
        Assert.Equal(fleet, await rig.FleetStore.FleetIdForCharacterAsync(passengerCharacter.CharacterId));
        var bridges = Bridges(rig);
        rig.Sim.Heard.Clear();

        await bridges.OpenAsync(session, Field, Destination, CancellationToken.None);

        // the field, its system, the bridge's minute, the Titan's hold of the same minute, and the module's 1 000 GJ of capacitor
        var opened = Assert.Single(rig.Sim.Heard.OfType<SimCommand>(), command => command.Code == SimCommands.OpenBridge);
        Assert.Equal(new double[] { Field, Destination, 60_000, 60_000, 2_500, 1_000 }, opened.Args.ToArray());
        // the activation takes 450 Strontium (500 less 10 percent at Jump Portal Generation level one, SDE 1775)
        Assert.Equal(50, await rig.Ships.FuelHeldAsync(character.CharacterId, ship, 16_275));
        Assert.Single(bridges.Open);

        // the fleetmate passes through: the isotopes come from the Titan (one light year, the Ibis's mass, the portal's mass factor 1.5e-9 and the Titan's 3 000)
        await rig.Statics.EnsureTypesAsync([Ibis], CancellationToken.None);
        var mass = rig.Statics.Type(Ibis)!.Attributes[JumpDriveRules.MassAttribute];
        var expectedFuel = (long)Math.Ceiling(Math.Round(1 * mass * 1.5e-9 * 3_000, 6));
        rig.Sim.Heard.Clear();
        var answer = await bridges.JumpThroughAsync(passengerSession, ship, Field, Destination, passengerOid, CancellationToken.None);
        Assert.IsType<PyTuple>(answer);
        var passage = Assert.Single(rig.Sim.Heard.OfType<SimCommand>(), command => command.Code == SimCommands.BridgeJump);
        Assert.Equal(new double[] { ship, Field, Destination }, passage.Args.ToArray());
        Assert.Equal(50_000 - expectedFuel, await rig.Ships.FuelHeldAsync(character.CharacterId, ship, Helium));
        // the passenger\x27s fatigue is the wiki\x27s for one light year: twenty minutes, and a two minute cooldown
        var state = await new PgJumpFatigueStore(rig.Source, rig.SpaceSchema).GetAsync(passengerCharacter.CharacterId);
        Assert.Equal((rig.Now + 20 * Minute, rig.Now + 2 * Minute), (state.FatigueUntilMs, state.CooldownUntilMs));
    }

    [PgFact]
    public async Task A_bridge_is_closed_when_its_minute_is_up_and_a_passenger_is_then_refused()
    {
        await using var rig = await Rig.CreateAsync();
        var (session, character, ship, oid) = await JumperAsync(rig, fuel: 50_000, hull: Avatar, fuelType: Helium, generator: PortalGenerator, strontium: 500);
        await FleetOfAsync(rig, character.CharacterId);
        await LightAsync(rig, Field, character.CharacterId, ship);
        var bridges = Bridges(rig);
        await bridges.OpenAsync(session, Field, Destination, CancellationToken.None);
        Assert.Single(bridges.Open);

        rig.Now += 61_000;
        await bridges.SweepAsync();
        Assert.Empty(bridges.Open);
        await Assert.ThrowsAnyAsync<MachoWrappedException>(() => bridges.JumpThroughAsync(session, ship, Field, Destination, oid, CancellationToken.None));
    }

    /// <summary>The Redeemer (SDE 22428), a Black Ops battleship, and its Covert Jump Portal Generator (SDE 28652): the conduit of the conduit tests.</summary>
    private const int Redeemer = 22_428, CovertGenerator = 28_652;

    /// <summary>The conduit service of a rig; the passenger's session is the one the fleet is told of.</summary>
    private static SpaceConduits Conduits(Rig rig, long passengerCharacter, ClientSession passengerSession) => new(
        rig.Ships, rig.Statics, rig.Deployed, new PgJumpFatigueStore(rig.Source, rig.SpaceSchema), Systems(), rig.Skills, rig.Source, rig.Space,
        rig.FleetStore, () => rig.Now, null, id => id == passengerCharacter ? passengerSession : null);

    /// <summary>A fleetmate joins a fleet by an invitation to its wing and squad (the fleet tests' pattern).</summary>
    private static async Task JoinFleetAsync(Rig rig, long fleet, long leader, long character)
    {
        var wing = await rig.FleetStore.CreateWingAsync(fleet, leader);
        var squad = await rig.FleetStore.CreateSquadAsync(fleet, leader, wing);
        Assert.True(await rig.FleetStore.InviteAsync(fleet, leader, character, wing, squad, Eve.Services.Space.FleetStore.Member));
        Assert.NotNull(await rig.FleetStore.JoinAsync(fleet, character));
    }

    [PgFact]
    public async Task A_black_ops_conduit_takes_the_fleetmate_in_range_through_with_it_at_once()
    {
        await using var rig = await Rig.CreateAsync();
        var (session, character, ship, oid) = await JumperAsync(rig, fuel: 50_000, hull: Redeemer, fuelType: Helium, generator: CovertGenerator);
        await FleetOfAsync(rig, character.CharacterId);
        await LightAsync(rig, Field, character.CharacterId, ship);
        var fleet = (await rig.FleetStore.FleetIdForCharacterAsync(character.CharacterId))!.Value;
        var (passengerSession, passengerCharacter, passengerShip, _) = await JumperAsync(rig, fuel: 0, hull: Redeemer, account: 2, name: "Fleetmate", sessionId: 2);
        await JoinFleetAsync(rig, fleet, character.CharacterId, passengerCharacter.CharacterId);
        var conduits = Conduits(rig, passengerCharacter.CharacterId, passengerSession);
        rig.Sim.Heard.Clear();

        await conduits.GroupJumpAsync(session, Field, Destination, oid, CancellationToken.None);

        // the field, its system, the conduit's 10 km, and the passenger's ship: the anchor and the passenger leave at once
        var passage = Assert.Single(rig.Sim.Heard.OfType<SimCommand>(), command => command.Code == SimCommands.ConduitJump);
        Assert.Equal(new double[] { Field, Destination, 10_000, passengerShip }, passage.Args.ToArray());
        // one light year of the hull's 3131 (1 000 isotopes) comes from the anchor
        Assert.Equal(50_000 - 1_000, await rig.Ships.FuelHeldAsync(character.CharacterId, ship, Helium));
        // the fatigue of one light year at a Black Ops' 0.25 (SDE 1971), for the anchor and for the passenger
        var (fatigueMs, cooldownMs) = JumpDriveRules.AfterJump(0, 0.25);
        var store = new PgJumpFatigueStore(rig.Source, rig.SpaceSchema);
        var anchorState = await store.GetAsync(character.CharacterId);
        var passengerState = await store.GetAsync(passengerCharacter.CharacterId);
        Assert.Equal((rig.Now + fatigueMs, rig.Now + cooldownMs), (anchorState.FatigueUntilMs, anchorState.CooldownUntilMs));
        Assert.Equal((rig.Now + fatigueMs, rig.Now + cooldownMs), (passengerState.FatigueUntilMs, passengerState.CooldownUntilMs));
    }

    [PgFact]
    public async Task A_bridge_does_not_survive_a_restart_of_the_host()
    {
        await using var rig = await Rig.CreateAsync();
        var (session, character, ship, _) = await JumperAsync(rig, fuel: 50_000, hull: Avatar, fuelType: Helium, generator: PortalGenerator, strontium: 500);
        await FleetOfAsync(rig, character.CharacterId);
        await LightAsync(rig, Field, character.CharacterId, ship);
        var fleet = (await rig.FleetStore.FleetIdForCharacterAsync(character.CharacterId))!.Value;
        var (passengerSession, passengerCharacter, _, passengerOid) = await JumperAsync(rig, fuel: 0, hull: Ibis, account: 2, name: "Fleetmate", sessionId: 2);
        await JoinFleetAsync(rig, fleet, character.CharacterId, passengerCharacter.CharacterId);
        await Bridges(rig).OpenAsync(session, Field, Destination, CancellationToken.None);

        // the host restarts: a new service holds no bridge, so the passage is refused
        var restarted = Bridges(rig);
        Assert.Empty(restarted.Open);
        await Assert.ThrowsAnyAsync<MachoWrappedException>(() => restarted.JumpThroughAsync(passengerSession, ship, Field, Destination, passengerOid, CancellationToken.None));
    }

    /// <summary>The Rorqual (SDE 28352), its Industrial Jump Portal Generator (SDE 63140), an industrial cynosural field (SDE 52696) and a Porpoise (SDE 42244).</summary>
    private const int IndustrialGenerator = 63_140, IndustrialField = 52_696, Porpoise = 42_244;

    [PgFact]
    public async Task A_rorqual_bridges_to_an_industrial_field_and_a_porpoise_passes_through_for_the_isotopes_and_the_fatigue()
    {
        await using var rig = await Rig.CreateAsync();
        var (session, character, ship, oid) = await JumperAsync(rig, fuel: 50_000, hull: Rorqual, fuelType: Oxygen, generator: IndustrialGenerator);
        await FleetOfAsync(rig, character.CharacterId);
        await LightAsync(rig, Field, character.CharacterId, ship, IndustrialField);
        var fleet = (await rig.FleetStore.FleetIdForCharacterAsync(character.CharacterId))!.Value;
        var (passengerSession, passengerCharacter, _, passengerOid) = await JumperAsync(rig, fuel: 0, hull: Porpoise, account: 2, name: "Fleetmate", sessionId: 2);
        await JoinFleetAsync(rig, fleet, character.CharacterId, passengerCharacter.CharacterId);
        var bridges = Bridges(rig);
        rig.Sim.Heard.Clear();

        await bridges.OpenAsync(session, Field, Destination, CancellationToken.None);

        // the industrial generator: 20 seconds (SDE 1002), 2 500 metres (the wiki), 100 GJ of capacitor (SDE 6), and no hold (the wiki gives none)
        var opened = Assert.Single(rig.Sim.Heard.OfType<SimCommand>(), command => command.Code == SimCommands.OpenBridge);
        Assert.Equal(new double[] { Field, Destination, 20_000, 0, 2_500, 100 }, opened.Args.ToArray());
        Assert.Single(bridges.Open);

        // the Porpoise passes: the isotopes are the mass times 1e-9 (SDE 1001) times the Rorqual's 4 000 (SDE 868) for one light year; its fatigue at 0.1 (SDE 1971)
        await rig.Statics.EnsureTypesAsync([Porpoise], CancellationToken.None);
        var mass = rig.Statics.Type(Porpoise)!.Attributes[JumpDriveRules.MassAttribute];
        var expectedFuel = (long)Math.Ceiling(Math.Round(1 * mass * 1e-9 * 4_000, 6));
        await bridges.JumpThroughAsync(passengerSession, ship, Field, Destination, passengerOid, CancellationToken.None);
        Assert.Equal(50_000 - expectedFuel, await rig.Ships.FuelHeldAsync(character.CharacterId, ship, Oxygen));
        var (fatigueMs, cooldownMs) = JumpDriveRules.AfterJump(0, 0.1);
        var passengerState = await new PgJumpFatigueStore(rig.Source, rig.SpaceSchema).GetAsync(passengerCharacter.CharacterId);
        Assert.Equal((rig.Now + fatigueMs, rig.Now + cooldownMs), (passengerState.FatigueUntilMs, passengerState.CooldownUntilMs));
    }

    [PgFact]
    public async Task An_industrial_bridge_is_refused_for_a_standard_field()
    {
        await using var rig = await Rig.CreateAsync();
        var (session, character, ship, _) = await JumperAsync(rig, fuel: 50_000, hull: Rorqual, fuelType: Oxygen, generator: IndustrialGenerator);
        await FleetOfAsync(rig, character.CharacterId);
        await LightAsync(rig, Field, character.CharacterId, ship);
        await Assert.ThrowsAnyAsync<MachoWrappedException>(() => Bridges(rig).OpenAsync(session, Field, Destination, CancellationToken.None));
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
        Assert.True(await rig.Ships.DebitJumpAsync(fatigue, new PgJumpSpoolStore(rig.Source, rig.SpaceSchema), SpoolOf(rig, character.CharacterId, ship, 1), new JumpFatigueState(rig.Now + 20 * Minute, rig.Now + 2 * Minute)));
        // the spool of that jump is over by the time the next one is asked for (the simulation would have jumped the ship)
        await new PgJumpSpoolStore(rig.Source, rig.SpaceSchema).ForgetAsync(character.CharacterId);
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

        Assert.False(await rig.Ships.DebitJumpAsync(fatigue, new PgJumpSpoolStore(rig.Source, rig.SpaceSchema), SpoolOf(rig, character.CharacterId, ship, 3_000), next));
        Assert.Equal(1_000, await rig.Ships.FuelHeldAsync(character.CharacterId, ship, Helium));
        Assert.Equal(JumpFatigueState.None, await fatigue.GetAsync(character.CharacterId));

        Assert.True(await rig.Ships.DebitJumpAsync(fatigue, new PgJumpSpoolStore(rig.Source, rig.SpaceSchema), SpoolOf(rig, character.CharacterId, ship, 400), next));
        Assert.Equal(600, await rig.Ships.FuelHeldAsync(character.CharacterId, ship, Helium));
        Assert.Equal(next, await fatigue.GetAsync(character.CharacterId));
    }

    /// <summary>A Mobile Cynosural Beacon of <paramref name="type"/> in the destination system, owned by <paramref name="owner"/>: anchored (its activation time is
    /// past) or still waiting to anchor (the record the placement keeps: the activation is two minutes, a covert one a minute, after the drop).</summary>
    private static async Task BeaconAsync(Rig rig, long beacon, long owner, long ship, int type, bool anchored)
    {
        await rig.Deployed.SaveAsync(new DeployedRecord(
            beacon, "mobile_cyno_beacon", Destination, "Mobile Cynosural Beacon", type, 4_093, [SolarSystemCatalog.MetersPerLightYear, 0, 0], 100, owner, 0, 0, ship,
            rig.Now + 60 * Minute, anchored ? rig.Now - Minute : rig.Now + 2 * Minute));
    }

    /// <summary>The conduit of the rig's Black Ops with no passenger (the passengers are the fleet tests' concern).</summary>
    private static SpaceConduits Conduits(Rig rig) => new(
        rig.Ships, rig.Statics, rig.Deployed, new PgJumpFatigueStore(rig.Source, rig.SpaceSchema), Systems(), rig.Skills, rig.Source, rig.Space,
        rig.FleetStore, () => rig.Now, null, null);

    [PgFact]
    public async Task A_revelation_jumps_to_an_anchored_beacon_of_its_fleet_as_it_does_to_a_field()
    {
        await using var rig = await Rig.CreateAsync();
        var (session, character, ship, oid) = await JumperAsync(rig, fuel: 50_000);
        await FleetOfAsync(rig, character.CharacterId);
        await BeaconAsync(rig, Field, character.CharacterId, ship, 57_319, anchored: true);
        rig.Sim.Heard.Clear();

        await Drive(rig).JumpToFieldAsync(session, Field, character.CharacterId, Destination, oid, CancellationToken.None);

        var spool = Assert.Single(rig.Sim.Heard.OfType<SimCommand>(), command => command.Code == SimCommands.CynoJump);
        Assert.Equal(new double[] { Field, Destination, Spool, 0.95 }, spool.Args.ToArray());
        Assert.Equal(47_000, await rig.Ships.FuelHeldAsync(character.CharacterId, ship, Helium));
    }

    [PgFact]
    public async Task A_titan_bridge_opens_to_an_anchored_beacon_and_is_refused_while_the_beacon_is_still_anchoring()
    {
        await using var rig = await Rig.CreateAsync();
        var (session, character, ship, _) = await JumperAsync(rig, fuel: 50_000, hull: Avatar, fuelType: Helium, generator: PortalGenerator, strontium: 500);
        await FleetOfAsync(rig, character.CharacterId);
        await BeaconAsync(rig, Field, character.CharacterId, ship, 57_319, anchored: false);
        var bridges = Bridges(rig);
        rig.Sim.Heard.Clear();

        // not lit yet: refused by the host, and the simulation is not asked
        await Assert.ThrowsAnyAsync<MachoWrappedException>(() => bridges.OpenAsync(session, Field, Destination, CancellationToken.None));
        Assert.DoesNotContain(rig.Sim.Heard.OfType<SimCommand>(), command => command.Code == SimCommands.OpenBridge);
        Assert.Empty(bridges.Open);

        await BeaconAsync(rig, Field, character.CharacterId, ship, 57_319, anchored: true);
        await bridges.OpenAsync(session, Field, Destination, CancellationToken.None);

        var opened = Assert.Single(rig.Sim.Heard.OfType<SimCommand>(), command => command.Code == SimCommands.OpenBridge);
        Assert.Equal((double)Field, opened.Args.ToArray()[0]);
        Assert.Single(bridges.Open);
    }

    [PgFact]
    public async Task A_titan_bridge_is_refused_to_a_covert_beacon_which_only_a_black_ops_takes()
    {
        await using var rig = await Rig.CreateAsync();
        var (session, character, ship, _) = await JumperAsync(rig, fuel: 50_000, hull: Avatar, fuelType: Helium, generator: PortalGenerator, strontium: 500);
        await FleetOfAsync(rig, character.CharacterId);
        await BeaconAsync(rig, Field, character.CharacterId, ship, 59_630, anchored: true);
        rig.Sim.Heard.Clear();

        // the covert beacon is a covert field for the cyno rule (the wiki: only black ops battleships)
        await Assert.ThrowsAnyAsync<MachoWrappedException>(() => Bridges(rig).OpenAsync(session, Field, Destination, CancellationToken.None));
        Assert.DoesNotContain(rig.Sim.Heard.OfType<SimCommand>(), command => command.Code == SimCommands.OpenBridge);
    }

    [PgFact]
    public async Task A_black_ops_conduit_jumps_to_an_anchored_covert_beacon()
    {
        await using var rig = await Rig.CreateAsync();
        var (session, character, ship, oid) = await JumperAsync(rig, fuel: 50_000, hull: Redeemer, fuelType: Helium, generator: CovertGenerator);
        await FleetOfAsync(rig, character.CharacterId);
        await BeaconAsync(rig, Field, character.CharacterId, ship, 59_630, anchored: true);
        rig.Sim.Heard.Clear();

        await Conduits(rig).GroupJumpAsync(session, Field, Destination, oid, CancellationToken.None);

        var passage = Assert.Single(rig.Sim.Heard.OfType<SimCommand>(), command => command.Code == SimCommands.ConduitJump);
        Assert.Equal((double)Field, passage.Args.ToArray()[0]);
        Assert.Equal(50_000 - 1_000, await rig.Ships.FuelHeldAsync(character.CharacterId, ship, Helium));
    }

    [PgFact]
    public async Task A_beacon_that_anchors_is_told_to_its_fleet_and_its_end_is_told_too()
    {
        await using var rig = await Rig.CreateAsync();
        var (session, character, ship, _) = await JumperAsync(rig, fuel: 50_000);
        await FleetOfAsync(rig, character.CharacterId);
        rig.Containers.FieldAnnounced = Drive(rig, characterId => characterId == character.CharacterId ? session : null).AnnounceFieldAsync;
        await BeaconAsync(rig, Field, character.CharacterId, ship, 57_319, anchored: false);
        Events.Of(session);

        rig.Now += 2 * Minute;
        await rig.Containers.AnchorAsync(Field);

        // the beacon is told as a deployable: its id, its system, its id as the beacon, its owner, and lit
        var lit = Assert.Single(Events.Of(session));
        Assert.Equal("OnFleetJumpBeaconDeployableChange", lit.Name);
        Assert.Equal(new long[] { Field, Destination, Field, character.CharacterId }, lit.Args.Take(4).Select(Events.Number).ToArray());
        Assert.True(lit.Args[4] is PyBool { Value: true }, "lit");

        rig.Now += 60 * Minute + 1;
        await rig.Containers.SweepAsync();
        var gone = Assert.Single(Events.Of(session));
        Assert.Equal("OnFleetJumpBeaconDeployableChange", gone.Name);
        Assert.True(gone.Args[4] is PyBool { Value: false }, "gone");
    }

    [PgFact]
    public async Task A_titan_bridge_tells_its_opener_the_anchor_is_activated_with_no_passenger_yet()
    {
        await using var rig = await Rig.CreateAsync();
        var (session, character, ship, _) = await JumperAsync(rig, fuel: 50_000, hull: Avatar, fuelType: Helium, generator: PortalGenerator, strontium: 500);
        await FleetOfAsync(rig, character.CharacterId);
        await LightAsync(rig, Field, character.CharacterId, ship);
        Events.Of(session);

        await Bridges(rig).OpenAsync(session, Field, Destination, CancellationToken.None);

        var anchor = Assert.Single(Events.Of(session), e => e.Name == "OnGroupJumpAnchorActivated");
        Assert.Equal(new long[] { Destination, 0 }, anchor.Args.Select(Events.Number).ToArray());
    }

    /// <summary>Every text inside a wrapped error's payload, joined (the refusal's words).</summary>
    private static string Texts(PyValue value) => value switch
    {
        PyStr text => text.Value,
        PyWString text => text.Value,
        PyBytes text => System.Text.Encoding.UTF8.GetString(text.Value.Span),
        PyTuple tuple => string.Join("|", tuple.Items.Select(Texts)),
        PyList list => string.Join("|", list.Items.Select(Texts)),
        PyDict dict => string.Join("|", dict.Entries.SelectMany(entry => new[] { Texts(entry.Key), Texts(entry.Value) })),
        PyObjectEx ex => string.Join("|", new[] { Texts(ex.Header) }.Concat(ex.List.Select(Texts)).Concat(ex.Dict.SelectMany(entry => new[] { Texts(entry.Key), Texts(entry.Value) }))),
        PyObject obj => Texts(obj.Args),
        _ => "",
    };

    /// <summary>A cynosural field lit by <paramref name="owner"/> in <paramref name="system"/> (the field's own system, not the destination), ten minutes of life.</summary>
    private static async Task FieldInAsync(Rig rig, long field, int system, long owner, long ship)
    {
        await rig.Deployed.SaveAsync(new DeployedRecord(
            field, "cyno_field", system, "Cynosural Field", 21_094, 2_020, [0, 0, SolarSystemCatalog.MetersPerLightYear], 250, owner, 0, 0, ship, rig.Now + 10 * Minute));
    }

    [PgFact]
    public async Task A_revelation_cannot_jump_into_wormhole_space_or_into_pochven_even_with_a_field_lit_there()
    {
        await using var rig = await Rig.CreateAsync();
        var (session, character, ship, oid) = await JumperAsync(rig, fuel: 50_000);
        await FleetOfAsync(rig, character.CharacterId);
        await FieldInAsync(rig, Field, Wormhole, character.CharacterId, ship);
        await FieldInAsync(rig, OtherField, Pochven, character.CharacterId, ship);
        var drive = Drive(rig);
        rig.Sim.Heard.Clear();

        // the wiki: jump drives cannot jump to systems in wormhole space, and cannot be used to enter Pochven (only to exit)
        var wormhole = await Assert.ThrowsAsync<MachoWrappedException>(() => drive.JumpToFieldAsync(session, Field, character.CharacterId, Wormhole, oid, CancellationToken.None));
        Assert.Contains("wormhole space", Texts(wormhole.Error.Payload));
        var pochven = await Assert.ThrowsAsync<MachoWrappedException>(() => drive.JumpToFieldAsync(session, OtherField, character.CharacterId, Pochven, oid, CancellationToken.None));
        Assert.Contains("Pochven", Texts(pochven.Error.Payload));
        Assert.DoesNotContain(rig.Sim.Heard.OfType<SimCommand>(), command => command.Code == SimCommands.CynoJump);
        Assert.Equal(50_000, await rig.Ships.FuelHeldAsync(character.CharacterId, ship, Helium));
    }

    [PgFact]
    public async Task A_titan_bridge_is_refused_into_pochven_and_nothing_is_opened()
    {
        await using var rig = await Rig.CreateAsync();
        var (session, character, ship, _) = await JumperAsync(rig, fuel: 50_000, hull: Avatar, fuelType: Helium, generator: PortalGenerator, strontium: 500);
        await FleetOfAsync(rig, character.CharacterId);
        await FieldInAsync(rig, OtherField, Pochven, character.CharacterId, ship);
        rig.Sim.Heard.Clear();

        var refused = await Assert.ThrowsAsync<MachoWrappedException>(() => Bridges(rig).OpenAsync(session, OtherField, Pochven, CancellationToken.None));

        Assert.Contains("Pochven", Texts(refused.Error.Payload));
        Assert.DoesNotContain(rig.Sim.Heard.OfType<SimCommand>(), command => command.Code == SimCommands.OpenBridge);
    }
}
