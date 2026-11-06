using Eve.Dogma;
using Eve.Inventory;
using Eve.Services.Space;
using Eve.Ships;
using Eve.Space;

namespace Eve.Services.Space.Tests;

public sealed class StructureAreaAuthorizationTests
{
    [PgFact]
    public async Task A_public_point_defense_module_authorizes_fresh_targets_with_owner_corporation_facts()
    {
        await using var rig = await Rig.CreateAsync();
        var pilot = await DroneHostTests.FlyingWithADroneAsync(rig, solarSystemId: 30002537, characterName: "Area Controller");
        const long structureId = 1040000000000101, moduleId = structureId + 1, ownerCorporation = 777000001;
        await rig.Statics.EnsureTypesAsync([35834, 35926, 63195]);
        var personal = (await rig.Ships.LoadAsync(pilot.Character, CancellationToken.None))!;
        pilot.Session.Set("corpid", new Eve.Marshal.PyLong(personal.Character.CorporationId));
        var hull = new Item(structureId, 35834, ownerCorporation, pilot.System, 0, 1, true, null);
        var module = new Item(moduleId, 35926, ownerCorporation, structureId, 27, 1, true, null);
        var charge = new Item(moduleId + 1, 63195, ownerCorporation, structureId, 27, 400, false, null);
        var world = new DogmaWorld(rig.Statics, personal.Character.Id, structureId);
        world.Add(new DogmaItem(personal.Character.Id, personal.Character.TypeId, personal.Character.Id, structureId, ShipSnapshot.PilotFlag, ItemState.Online));
        foreach (var item in new[] { hull, module, charge })
            world.Add(ItemDogma.Of(item, personal.Character.Id, item.LocationId, item.Flag, ItemState.Online));
        world.SetValue(charge.Id, ShipSnapshot.QuantityCharge, charge.Quantity);
        var fit = new ShipSnapshot { Character = personal.Character, Ship = hull, Fitted = [module], Charges = [charge], Skills = [], World = world };
        rig.Space.StructureDogmaOf = (_, _, _) => ValueTask.FromResult<ShipSnapshot?>(fit);
        rig.Space.StructureControlLeaseGenerationOf = (_, _, _) => Task.FromResult<long?>(1);
        var war = true;
        var queriedCorporations = new List<long>();
        rig.Space.StructureWeaponTargetContextOf = (owner, _, _) =>
        {
            queriedCorporations.Add(owner);
            return Task.FromResult(new WeaponTargetContext(AttackerIsPlayer: true, TargetIsPlayerProperty: true, ActiveWar: war));
        };
        await rig.Space.SetStructureObserverAsync(pilot.Session, pilot.System, structureId, pilot.Character, true, controlLeaseGeneration: 1);
        rig.Space.ApplyStructureControlSession(pilot.Session, pilot.System, structureId, personal.Ship.Id, pilot.System, true);
        await rig.Space.RefreshShipAsync(pilot.Session, CancellationToken.None);
        Assert.Equal(ModuleResult.Done, await rig.Space.ActivateAsync(pilot.Session, moduleId, "pointDefense", 0, 1000, CancellationToken.None));

        // The target enters after activation; its current pilot record is used by this cycle.
        var target = await DroneHostTests.FlyingWithADroneAsync(rig, solarSystemId: pilot.System, characterName: "Area Entrant", sessionId: 2);
        var targetId = target.Session.GetInt("shipid")!.Value;
        var effectId = rig.Statics.Type(35926)!.EffectIds.Single(id => rig.Statics.Effect(id)?.Name == "pointDefense");
        async Task<SimStructureDamageCandidatesResult> Request(long generation, uint ordinal, IReadOnlyList<long>? candidates = null)
        {
            var gate = new TaskCompletionSource<SimStructureDamageCandidatesResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            rig.Sim.StructureDamageCandidatesGate = gate;
            rig.Sim.Say(new SimStructureDamageCandidatesRequest((ulong)pilot.Session.SessionId, structureId, moduleId, effectId, 9, generation, 20, ordinal, candidates ?? [targetId]));
            return await gate.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        var granted = await Request(1, 0);
        Assert.Equal([targetId], granted.AllowedTargetIds);
        Assert.Equal((structureId, moduleId, 9UL, 1L, 20UL, 0U), (granted.StructureId, granted.ModuleId, granted.ActivationNonce, granted.ControlLeaseGeneration, granted.CycleId, granted.PulseHopOrdinal));
        Assert.Equal([ownerCorporation], queriedCorporations);
        war = false;
        Assert.Empty((await Request(1, 1)).AllowedTargetIds);
        Assert.Empty((await Request(2, 2)).AllowedTargetIds);
        const long sentryId = 9800500001939;
        Assert.Null(rig.Statics.Type(54577));
        rig.Sim.Say(new SimDamageableEntityUpserted(pilot.System, sentryId, 54577, 0, Eve.Services.Space.Pochven.PochvenRules.TriglavianCorporationId, 0, 0));
        Assert.Equal([sentryId], (await Request(1, 3, [sentryId])).AllowedTargetIds);
    }
}
