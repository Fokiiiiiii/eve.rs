using Eve.Dogma;
using Eve.Inventory;
using Eve.Services.Space;
using Eve.Ships;
using Eve.Space;

namespace Eve.Services.Space.Tests;

/// <summary>
/// The Standup burst projectors and the Standup HOG through the Host's structure path (ST-8b): a controlled structure's burst is sent to the simulation as a
/// structure burst, each pulse asks the Host for its candidates, and the Host grants only the ships the structure's owner may affect (the owner corporation's facts).
/// </summary>
public sealed class StructureBurstAuthorizationTests
{
    private const long StructureId = 1040000000000301, ModuleId = StructureId + 1, OwnerCorporation = 777000003;

    [PgFact]
    public async Task A_standup_burst_projector_is_sent_as_a_structure_burst_and_authorizes_each_pulse_by_owner_corporation_facts()
    {
        await using var rig = await Rig.CreateAsync();
        var pilot = await DroneHostTests.FlyingWithADroneAsync(rig, solarSystemId: 30002537, characterName: "Burst Controller");
        var war = true;
        var effectId = await ControlStructureAsync(rig, pilot, 47070, "doomsdayAOEDamp", war: () => war);
        Assert.Equal(ModuleResult.Done, await rig.Space.ActivateAsync(pilot.Session, ModuleId, "doomsdayAOEDamp", 0, 1000, CancellationToken.None));

        var activated = Assert.Single(rig.Sim.Heard.OfType<SimActivate>());
        var burst = Assert.IsType<SimAction.StructureBurst>(activated.Effect.Action);
        Assert.True(burst.IsValid);
        Assert.IsType<SimRemote.Damp>(burst.What);
        Assert.True(burst.RangeMeters > 0);

        var target = await DroneHostTests.FlyingWithADroneAsync(rig, solarSystemId: pilot.System, characterName: "Burst Entrant", sessionId: 2);
        var targetId = target.Session.GetInt("shipid")!.Value;
        var granted = await RequestAsync(rig, pilot, effectId, 1, 0, [targetId]);
        Assert.True(granted.AllowedTargetIds.SequenceEqual([targetId]), string.Join("\n", rig.Log));
        war = false;
        Assert.Empty((await RequestAsync(rig, pilot, effectId, 1, 1, [targetId])).AllowedTargetIds);
    }

    [PgFact]
    public async Task The_standup_hog_is_refused_as_unpublished_and_nothing_reaches_the_simulation()
    {
        await using var rig = await Rig.CreateAsync();
        var pilot = await DroneHostTests.FlyingWithADroneAsync(rig, solarSystemId: 30002537, characterName: "Hog Controller");
        await ControlStructureAsync(rig, pilot, 47068, "doomsdayHOG", war: () => true);

        var result = await rig.Space.ActivateAsync(pilot.Session, ModuleId, "doomsdayHOG", 0, 1000, CancellationToken.None);

        Assert.NotNull(result);
        Assert.False(result.Ok);
        Assert.Equal("MODULE_NOT_PUBLISHED", result.Reason);
        Assert.Empty(rig.Sim.Heard.OfType<SimActivate>());
    }

    [PgFact]
    public async Task A_standup_vorton_projector_authorizes_each_hop_by_the_owner_corporation_facts()
    {
        await using var rig = await Rig.CreateAsync();
        var pilot = await DroneHostTests.FlyingWithADroneAsync(rig, solarSystemId: 30002537, characterName: "Vorton Controller");
        var war = true;
        var effectId = await ControlStructureAsync(rig, pilot, 35928, "lightningWeapon", war: () => war);
        // the activation the simulation accepted: the Host's record of the running module (the vorton's target is a ship in reach)
        rig.Space.StartStructureDamageModuleForTest((ulong)pilot.Session.SessionId, ModuleId, "lightningWeapon", new SimEffect(ModuleId, 35928, effectId, "", 600000, 0, 1, 0, null, null, [], []));

        var target = await DroneHostTests.FlyingWithADroneAsync(rig, solarSystemId: pilot.System, characterName: "Vorton Entrant", sessionId: 2);
        var targetId = target.Session.GetInt("shipid")!.Value;
        // the first strike and a chain hop are each authorized afresh
        Assert.True((await RequestAsync(rig, pilot, effectId, 1, 0, [targetId])).AllowedTargetIds.SequenceEqual([targetId]), string.Join("\n", rig.Log));
        Assert.True((await RequestAsync(rig, pilot, effectId, 1, 1, [targetId])).AllowedTargetIds.SequenceEqual([targetId]));
        war = false;
        Assert.Empty((await RequestAsync(rig, pilot, effectId, 1, 2, [targetId])).AllowedTargetIds);
    }

    [PgFact]
    public async Task A_standup_guided_bomb_launcher_is_sent_as_a_bomb_and_its_blast_is_authorized_by_owner_corporation_facts()
    {
        await using var rig = await Rig.CreateAsync();
        var pilot = await DroneHostTests.FlyingWithADroneAsync(rig, solarSystemId: 30002537, characterName: "Bomb Controller");
        var war = true;
        // a Standup Light Guided Bomb in the launcher: the launch is not refused for being a structure's (the blast is authorized when it goes off)
        var effectId = await ControlStructureAsync(rig, pilot, 35923, "useMissiles", war: () => war, chargeTypeId: 37850);
        Assert.Equal(ModuleResult.Done, await rig.Space.ActivateAsync(pilot.Session, ModuleId, "useMissiles", 0, 1000, CancellationToken.None));
        var activated = Assert.Single(rig.Sim.Heard.OfType<SimActivate>());
        // the launcher runs the bomb on its charge (the charged wrapper of the action)
        Assert.IsType<SimAction.Bomb>(Assert.IsType<SimAction.Charged>(activated.Effect.Action).Action);

        var target = await DroneHostTests.FlyingWithADroneAsync(rig, solarSystemId: pilot.System, characterName: "Bomb Entrant", sessionId: 2);
        var targetId = target.Session.GetInt("shipid")!.Value;
        var granted = await RequestAsync(rig, pilot, effectId, 1, 0, [targetId]);
        Assert.True(granted.AllowedTargetIds.SequenceEqual([targetId]), string.Join("\n", rig.Log));
        war = false;
        Assert.Empty((await RequestAsync(rig, pilot, effectId, 1, 0, [targetId])).AllowedTargetIds);
    }

    /// <summary>Fits a structure with one module of the given type and gives the pilot its control. Returns the module's damage effect id for the family's first effect.</summary>
    private static async Task<int> ControlStructureAsync(Rig rig, DroneHostTests.Flying pilot, int moduleTypeId, string effectName, Func<bool> war, int? chargeTypeId = null)
    {
        await rig.Statics.EnsureTypesAsync(chargeTypeId is { } required ? [35834, moduleTypeId, required] : [35834, moduleTypeId]);
        var personal = (await rig.Ships.LoadAsync(pilot.Character, CancellationToken.None))!;
        pilot.Session.Set("corpid", new Eve.Marshal.PyLong(personal.Character.CorporationId));
        var hull = new Item(StructureId, 35834, OwnerCorporation, pilot.System, 0, 1, true, null);
        var module = new Item(ModuleId, moduleTypeId, OwnerCorporation, StructureId, 27, 1, true, null);
        var world = new DogmaWorld(rig.Statics, personal.Character.Id, StructureId);
        world.Add(new DogmaItem(personal.Character.Id, personal.Character.TypeId, personal.Character.Id, StructureId, ShipSnapshot.PilotFlag, ItemState.Online));
        var items = new List<Item> { hull, module };
        Item? charge = null;
        if (chargeTypeId is { } chargeType)
        {
            charge = new Item(ModuleId + 1, chargeType, OwnerCorporation, StructureId, 27, 400, false, null);
            items.Add(charge);
        }
        foreach (var item in items)
            world.Add(ItemDogma.Of(item, personal.Character.Id, item.LocationId, item.Flag, ItemState.Online));
        if (charge is not null)
            world.SetValue(charge.Id, ShipSnapshot.QuantityCharge, charge.Quantity);
        var fit = new ShipSnapshot { Character = personal.Character, Ship = hull, Fitted = [module], Charges = charge is null ? [] : [charge], Skills = [], World = world };
        rig.Space.StructureDogmaOf = (_, _, _) => ValueTask.FromResult<ShipSnapshot?>(fit);
        rig.Space.StructureControlLeaseGenerationOf = (_, _, _) => Task.FromResult<long?>(1);
        rig.Space.StructureWeaponTargetContextOf = (_, _, _) =>
            Task.FromResult(new WeaponTargetContext(AttackerIsPlayer: true, TargetIsPlayerProperty: true, ActiveWar: war()));
        await rig.Space.SetStructureObserverAsync(pilot.Session, pilot.System, StructureId, pilot.Character, true, controlLeaseGeneration: 1);
        rig.Space.ApplyStructureControlSession(pilot.Session, pilot.System, StructureId, personal.Ship.Id, pilot.System, true);
        await rig.Space.RefreshShipAsync(pilot.Session, CancellationToken.None);
        return rig.Statics.Type(moduleTypeId)!.EffectIds.Single(id => rig.Statics.Effect(id)?.Name == effectName);
    }

    private static async Task<SimStructureDamageCandidatesResult> RequestAsync(Rig rig, DroneHostTests.Flying pilot, int effectId, long generation, uint ordinal, IReadOnlyList<long> candidates)
    {
        var gate = new TaskCompletionSource<SimStructureDamageCandidatesResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Sim.StructureDamageCandidatesGate = gate;
        rig.Sim.Say(new SimStructureDamageCandidatesRequest((ulong)pilot.Session.SessionId, StructureId, ModuleId, effectId, 9, generation, 20, ordinal, candidates));
        return await gate.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }
}
