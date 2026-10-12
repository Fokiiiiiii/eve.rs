using Eve.Data;
using Eve.Dogma;
using Eve.Inventory;
using Eve.Marshal;
using Eve.Sde;
using Eve.Ships;
using Eve.Space;
using Npgsql;

namespace Eve.Services.Space.Tests;

/// <summary>
/// A reactive armor hardener (type 4403) on a ship: what the host sends the simulation (its seed, its step and the armor's attributes, the ship's own
/// resonances), what the client is told, and the Polarized weapon that pins the armor. The types, the attributes and the effect are the real SDE's; the
/// fitting is set up here.
/// </summary>
public class ReactiveArmorHardenerTests
{
    private const long CharacterId = 70101, ShipId = 70102, Station = 60003760;
    private const int Rifter = 587, Polarized = 34274, AdaptiveArmorHardener = 4403;
    private const int LowSlot = 11, HighSlot = 27;
    private const string Effect = "adaptiveArmorHardener";
    private const int CapacitorNeed = 6, Duration = 73, ResistanceShiftAmount = 1849;
    /// <summary>The armor's resonance attributes in the simulation's order: em, thermal, kinetic, explosive.</summary>
    private static readonly int[] ArmorIds = [267, 270, 269, 268];

    /// <summary>A Rifter with the modules fitted (id, type, flag, state), as the snapshot of a pilot in space.</summary>
    private static async Task<(ShipSnapshot Snapshot, DogmaStatic Statics)> ShipAsync(params (long Id, int TypeId, int Flag, ItemState State)[] fitted)
    {
        var source = NpgsqlDataSource.Create(PgConnection.ForTest(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        var statics = await DogmaStatic.LoadAsync(new SdeReader(source));
        await statics.EnsureTypesAsync([Rifter, .. fitted.Select(module => module.TypeId)]);
        var world = new DogmaWorld(statics, CharacterId, ShipId);
        world.Add(new DogmaItem(ShipId, Rifter, CharacterId, Station, 0, ItemState.Online));
        foreach (var (id, typeId, flag, state) in fitted)
        {
            world.Add(new DogmaItem(id, typeId, CharacterId, ShipId, flag, state));
        }
        var snapshot = new ShipSnapshot
        {
            Character = null!,
            Ship = new Eve.Inventory.Item(ShipId, Rifter, CharacterId, Station, 0, 1, true, null),
            Fitted = [.. fitted.Select(module => new Eve.Inventory.Item(module.Id, module.TypeId, CharacterId, ShipId, module.Flag, 1, true, null))],
            Charges = [],
            Skills = [],
            World = world,
        };
        return (snapshot, statics);
    }

    private static SimEffect PackageOf(ShipSnapshot snapshot, DogmaStatic statics, long moduleId, int repeat = 1)
    {
        var result = ModulePackages.Of(snapshot, statics, moduleId, Effect, 0, repeat);
        Assert.Null(result.Reason);
        return Assert.IsType<SimEffect>(result.Effect);
    }

    private static double Told(IEnumerable<SimChange> changes, int attribute) =>
        changes.Single(change => change.ItemId == ShipId && change.AttributeId == attribute).Value;

    [PgFact]
    public async Task The_hardener_is_sent_its_own_seed_its_step_and_the_armor_attributes_it_moves()
    {
        var (snapshot, statics) = await ShipAsync((1, AdaptiveArmorHardener, LowSlot, ItemState.Active));
        var package = PackageOf(snapshot, statics, 1, repeat: 1000);
        var type = statics.Type(AdaptiveArmorHardener)!;

        var shift = Assert.IsType<SimAction.ResistanceShift>(package.Action);
        // the seed is the module's own resonances (the SDE's 0.85 each), in the simulation's order, and the step is the SDE's resistanceShiftAmount (6 points)
        Assert.Equal(ArmorIds.Select(id => type.Attributes[id]), shift.Seed);
        Assert.Equal(ArmorIds, shift.AttributeIds);
        Assert.Equal(type.Attributes[ResistanceShiftAmount] / 100.0, shift.Shift, 12);
        Assert.Equal(0.06, shift.Shift, 9);
        // a toggle that runs until it is stopped, for the SDE's cycle and capacitor
        Assert.Equal(1000, package.Repeat);
        Assert.Equal(type.Attributes[Duration], package.DurationMs, 6);
        Assert.Equal(type.Attributes[CapacitorNeed], package.CapacitorNeed, 6);
    }

    [PgFact]
    public async Task The_client_is_told_the_ships_armor_times_the_seed_and_its_own_armor_when_the_hardener_stops()
    {
        var (snapshot, statics) = await ShipAsync((1, AdaptiveArmorHardener, LowSlot, ItemState.Online));
        var package = PackageOf(snapshot, statics, 1);
        var type = statics.Type(AdaptiveArmorHardener)!;

        foreach (var attribute in ArmorIds)
        {
            var bare = snapshot.World.Get(ShipId, attribute);
            Assert.Equal(Eve.Services.Space.Effects.ModuleAttributes.Six(bare * type.Attributes[attribute]), Told(package.OnChanges, attribute), 9);
            Assert.Equal(bare, Told(package.OffChanges, attribute), 9);
        }
    }

    [PgFact]
    public async Task The_ship_is_sent_its_own_armor_resonances_not_the_hardeners_so_the_simulation_multiplies_them_once()
    {
        var (snapshot, statics) = await ShipAsync((1, AdaptiveArmorHardener, LowSlot, ItemState.Active));
        var vitals = SimShips.VitalsOf(snapshot);
        var bare = statics.Type(Rifter)!;

        foreach (var (attribute, index) in ArmorIds.Select((attribute, index) => (attribute, index)))
        {
            Assert.Equal(bare.Attributes[attribute], vitals.Resonances.Armor[index], 9);
        }
        Assert.False(vitals.ArmorPinned);
    }

    [PgFact]
    public async Task A_fitted_polarized_weapon_pins_the_armor_the_ship_is_sent_pinned_and_the_hardener_is_told_one()
    {
        var (snapshot, statics) = await ShipAsync((2, Polarized, HighSlot, ItemState.Online), (1, AdaptiveArmorHardener, LowSlot, ItemState.Active));

        var vitals = SimShips.VitalsOf(snapshot);
        Assert.True(vitals.ArmorPinned);
        Assert.All(vitals.Resonances.Armor, resonance => Assert.Equal(1.0, resonance));

        // whichever way it is told, the pinned armor stays at one (the pin wins over the hardener's multiplier)
        var package = PackageOf(snapshot, statics, 1);
        foreach (var attribute in ArmorIds)
        {
            Assert.Equal(1.0, Told(package.OnChanges, attribute));
            Assert.Equal(1.0, Told(package.OffChanges, attribute));
        }
    }

    [PgFact]
    public async Task A_hardener_on_a_ship_without_the_weapon_is_not_pinned()
    {
        var (snapshot, _) = await ShipAsync((1, AdaptiveArmorHardener, LowSlot, ItemState.Active));

        Assert.False(SimShips.ArmorPinned(snapshot));
    }
}

/// <summary>A reactive armor hardener in space, through the host: started, stopped, a second one refused, and a Polarized weapon fitted.</summary>
public class ReactiveArmorHardenerHostTests
{
    private const int AdaptiveArmorHardener = 4403, Polarized = 34274;
    private const int LowSlot = 11, HighSlot = 27;
    private static readonly int[] ArmorIds = [267, 270, 269, 268];

    /// <summary>A ship outcome must be a change; a refusal is shown with its reason.</summary>
    private static void Changed(Eve.Ships.ShipOutcome outcome, string what = "") =>
        Assert.True(outcome is Eve.Ships.ShipChanged, outcome is Eve.Ships.ShipRefused refused ? $"{what}: {refused.Reason} {refused.Fit} {refused.Detail}" : outcome.ToString());

    /// <summary>A pilot in space with the modules fitted (type, flag) to the ship it has: each fitted and online before undocking.</summary>
    private static async Task<(ClientSession Session, Eve.Characters.Created Character, long[] Modules)> InSpaceWithAsync(Rig rig, params (int TypeId, int Flag)[] fits)
    {
        var character = await rig.NewCharacterAsync("Hardener Tester");
        long shipId = character.ShipId;
        // the skills the fitting needs are granted before anything is fitted (what is tested is not the fitting rules)
        await using (var connection = await rig.Source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            // the modules' skills (the Polarized weapon needs 3306, 3300 and 12214 at level 5, its SDE skill attributes), and the repair and armor ones
            await rig.Skills.GrantAsync(connection, transaction, character.CharacterId, new[] { 3392, 3393, 3416, 3419, 3413, 3426, 3318, 3422, 3423, 16069, 27902, 3435, 3449, 3433, 19921, 3427, 3434, 3421, 3306, 3300, 12214, 3394 }.Select(type => new Eve.Skills.CharacterSkill(type, 5, 0)));
            await transaction.CommitAsync();
        }
        Assert.IsType<Eve.Ships.ShipChanged>(
            await rig.Ships.MoveAsync(character.CharacterId, character.ShipId + 3, character.ShipId, Rig.Station, ItemFlags.Hangar, null));
        var modules = new List<long>();
        foreach (var (typeId, flag) in fits)
        {
            // a module already in the slot goes to the hangar first, as the pilot would do
            foreach (var occupant in (await rig.Items.InLocationAsync(shipId, character.CharacterId)).Where(i => i.Flag == flag))
            {
                Assert.IsType<Eve.Ships.ShipChanged>(await rig.Ships.MoveAsync(character.CharacterId, occupant.Id, shipId, Rig.Station, ItemFlags.Hangar, null));
            }
            var granted = (await rig.Ships.GrantAsync(character.CharacterId, typeId, 1, singleton: true))!.Single().After;
            Changed(await rig.Ships.MoveAsync(character.CharacterId, granted.Id, Rig.Station, shipId, flag, 1), $"type {typeId} flag {flag}");
            Changed(await rig.Ships.SetOnlineAsync(character.CharacterId, shipId, granted.Id, true), $"type {typeId} online");
            modules.Add(granted.Id);
        }
        var session = await rig.SessionAsync(character, 1);
        var oid = await rig.BindShipAsync(session);
        await rig.ShipCallAsync(session, "Undock", oid, new PyLong(session.GetInt("shipid")!.Value), PyBool.False);
        await rig.BindBeyonceAsync(session);
        Events.Of(session);
        return (session, character, [.. modules]);
    }

    [PgFact]
    public async Task Starting_a_hardener_sends_the_simulation_the_shift_and_the_ship_as_it_is_and_the_client_the_armor_times_its_seed()
    {
        await using var rig = await Rig.CreateAsync();
        var (session, character, modules) = await InSpaceWithAsync(rig, (AdaptiveArmorHardener, LowSlot));
        var hardener = modules.Single();
        var world = (await rig.Ships.LoadAsync(character.CharacterId, CancellationToken.None))!.World;
        var bare = ArmorIds.Select(attribute => world.Get(character.ShipId, attribute)).ToArray();
        var seeds = ArmorIds.Select(attribute => world.Get(hardener, attribute)).ToArray();
        var sent = rig.Sim.Heard.Count;

        Assert.Equal(ModuleResult.Done, await rig.Space.ActivateAsync(session, hardener, Effect, 0, 1000, CancellationToken.None));

        var effect = Assert.IsType<SimActivate>(rig.Sim.Heard.Last()).Effect;
        var shift = Assert.IsType<SimAction.ResistanceShift>(effect.Action);
        Assert.Equal(seeds, shift.Seed);
        Assert.Equal(ArmorIds, shift.AttributeIds);
        Assert.Equal(0.06, shift.Shift, 9);
        // the client is told the armor's resonances as the hardener sets them: the ship's own times the seed
        for (var i = 0; i < ArmorIds.Length; i++)
        {
            var told = effect.OnChanges.Single(change => change.AttributeId == ArmorIds[i]);
            Assert.Equal(Eve.Services.Space.Effects.ModuleAttributes.Six(bare[i] * seeds[i]), told.Value, 9);
        }
        // the simulation keeps the ship's own resonances and multiplies them by its profile: a ship sent after the start carries none of the hardener's multiplier
        Assert.All(rig.Sim.Heard.Skip(sent).OfType<SimShipChanged>(), changed => Assert.Equal(bare, changed.Ship.Vitals!.Resonances.Armor));

        Assert.Equal(ModuleResult.Done, await rig.Space.DeactivateAsync(session, hardener, Effect, CancellationToken.None));
    }

    [PgFact]
    public async Task A_polarized_weapon_fitted_pins_the_ship_the_simulation_is_sent_and_the_hardener_runs_on_the_pin()
    {
        await using var rig = await Rig.CreateAsync();
        var (session, _, modules) = await InSpaceWithAsync(rig, (Polarized, HighSlot), (AdaptiveArmorHardener, LowSlot));
        var hardener = modules[1];

        // the ship entered space with the weapon fitted: the simulation is sent the pinned armor
        var entered = rig.Sim.Heard.OfType<SimEnter>().Last().Ship.Vitals!;
        Assert.True(entered.ArmorPinned);
        Assert.All(entered.Resonances.Armor, resonance => Assert.Equal(1.0, resonance));

        Assert.Equal(ModuleResult.Done, await rig.Space.ActivateAsync(session, hardener, Effect, 0, 1000, CancellationToken.None));
        var effect = Assert.IsType<SimActivate>(rig.Sim.Heard.Last()).Effect;
        Assert.IsType<SimAction.ResistanceShift>(effect.Action);
        Assert.All(ArmorIds, attribute => Assert.Equal(1.0, effect.OnChanges.Single(change => change.AttributeId == attribute).Value));
    }

    private const string Effect = "adaptiveArmorHardener";
}
