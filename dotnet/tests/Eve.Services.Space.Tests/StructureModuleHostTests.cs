using Eve.Crimewatch;
using Eve.Dogma;
using Eve.Inventory;
using Eve.Marshal;
using Eve.Services.Space;
using Eve.Ships;
using Eve.Space;

namespace Eve.Services.Space.Tests;

public sealed class StructureModuleHostTests
{
    private const long StructureId = 1_040_000_000_001;
    private const int AstrahusTypeId = 35_832;
    private const int StandupMultiroleMissileLauncherTypeId = 35_922;
    private const int StandupLightMissileTypeId = 37_848;
    private const int KeepstarTypeId = 35_834;
    private const int PointDefenseTypeId = 35_926;
    private const int PointDefenseChargeTypeId = 63_195;
    private const int PochvenDamavikTypeId = 52_182;
    private const int PochvenWerpostTypeId = 54_577;
    private const int CombatDroneTypeId = 2_454;
    private const int FighterTypeId = 23_057;

    private sealed record Controlled(ClientSession Session, long OwnerCorporationId, long ControllerCorporationId);

    private static async Task<Controlled> ControlAsync(Rig rig, Eve.Characters.Created controller, params long[] moduleIds)
    {
        await rig.Statics.EnsureTypesAsync([AstrahusTypeId, StandupMultiroleMissileLauncherTypeId, StandupLightMissileTypeId]);
        var session = await WeaponHostTests.InSpaceAsync(rig, controller, 1);
        var character = (await rig.Characters.GetAsync(controller.CharacterId))!;
        var controllerCorporationId = character.CorporationId;
        session.Set("corpid", new PyLong(controllerCorporationId));
        var ownerCorporationId = controllerCorporationId + 1;
        var system = rig.Space.SolarSystemOf(session)!.Value;
        rig.Space.StructureDogmaOf = (_, personal, _) =>
            ValueTask.FromResult<ShipSnapshot?>(StructureSnapshot(rig, personal, system, ownerCorporationId, moduleIds));

        await rig.Space.SetStructureObserverAsync(session, system, StructureId, controller.CharacterId, true, controlLeaseGeneration: 1);
        rig.Space.ApplyStructureControlSession(session, system, StructureId, controller.ShipId, system, true);
        await rig.Space.RefreshShipAsync(session, CancellationToken.None);
        return new Controlled(session, ownerCorporationId, controllerCorporationId);
    }

private static async Task<(Controlled Controlled, ShipSnapshot Structure, long ModuleId)> ControlPointDefenseAsync(
        Rig rig,
        Eve.Characters.Created controller,
        int structureTypeId = KeepstarTypeId,
        long moduleId = StructureId + 401)
    {
        await rig.Statics.EnsureTypesAsync([structureTypeId, PointDefenseTypeId, PointDefenseChargeTypeId]);
        var session = await WeaponHostTests.InSpaceAsync(rig, controller, 1);
        var character = (await rig.Characters.GetAsync(controller.CharacterId))!;
        var controllerCorporationId = character.CorporationId;
        session.Set("corpid", new PyLong(controllerCorporationId));
        session.Set("allianceid", new PyLong(character.AllianceId ?? 0));
        session.Set("warfactionid", new PyLong(character.FactionId ?? 0));
        var ownerCorporationId = controllerCorporationId + 1;
        var system = rig.Space.SolarSystemOf(session)!.Value;
        var personal = (await rig.Ships.LoadAsync(controller.CharacterId, CancellationToken.None))!;
        var structure = PointDefenseStructureSnapshot(rig, personal, system, ownerCorporationId, structureTypeId, moduleId);
        rig.Space.StructureDogmaOf = (_, _, _) => ValueTask.FromResult<ShipSnapshot?>(structure);
        rig.Space.StructureControlLeaseGenerationOf = (_, _, _) => Task.FromResult<long?>(1);

        await rig.Space.SetStructureObserverAsync(session, system, StructureId, controller.CharacterId, true,
            controlLeaseGeneration: 1);
        rig.Space.ApplyStructureControlSession(session, system, StructureId, controller.ShipId, system, true);
        await rig.Space.RefreshShipAsync(session, CancellationToken.None);
        return (new Controlled(session, ownerCorporationId, controllerCorporationId), structure, moduleId);
    }

    private static ShipSnapshot PointDefenseStructureSnapshot(
        Rig rig,
        ShipSnapshot personal,
        int system,
        long ownerCorporationId,
        int structureTypeId,
        long moduleId)
    {
        const int moduleFlag = 27;
        var structure = new Item(StructureId, structureTypeId, ownerCorporationId, system, 0, 1, true, null);
        var module = new Item(moduleId, PointDefenseTypeId, ownerCorporationId, StructureId, moduleFlag, 1, true, null);
        var charge = new Item(moduleId + 10_000, PointDefenseChargeTypeId, ownerCorporationId, StructureId, moduleFlag, 400, false, null);
        var world = new DogmaWorld(rig.Statics, personal.Character.Id, StructureId);
        world.Add(new DogmaItem(personal.Character.Id, personal.Character.TypeId, personal.Character.Id,
            StructureId, ShipSnapshot.PilotFlag, ItemState.Online));
        world.Add(ItemDogma.Of(structure, personal.Character.Id, system, structure.Flag, ItemState.Online));
        world.Add(ItemDogma.Of(module, personal.Character.Id, StructureId, moduleFlag, ItemState.Online));
        world.Add(ItemDogma.Of(charge, personal.Character.Id, StructureId, moduleFlag, ItemState.Online));
        world.SetValue(charge.Id, ShipSnapshot.QuantityCharge, charge.Quantity);
        return new ShipSnapshot
        {
            Character = personal.Character,
            Ship = structure,
            Fitted = [module],
            Charges = [charge],
            Skills = [],
            World = world
        };
    }

    private static async Task<SimStructureDamageCandidatesResult> RequestDamageCandidatesAsync(
        Rig rig,
        Controlled controlled,
        long moduleId,
        int effectId,
        ulong activationNonce,
        long leaseGeneration,
        ulong cycleId,
        uint ordinal,
        IReadOnlyList<long> candidateIds)
    {
        var completion = new TaskCompletionSource<SimStructureDamageCandidatesResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Sim.StructureDamageCandidatesGate = completion;
        rig.Sim.Say(new SimStructureDamageCandidatesRequest(
            (ulong)controlled.Session.SessionId, StructureId, moduleId, effectId,
            activationNonce, leaseGeneration, cycleId, ordinal, candidateIds));
        return await completion.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static ShipSnapshot StructureSnapshot(
        Rig rig,
        ShipSnapshot personal,
        int system,
        long ownerCorporationId,
        IReadOnlyList<long> moduleIds)
    {
        var structure = new Item(StructureId, AstrahusTypeId, ownerCorporationId, system, 0, 1, true, null);
        var modules = new List<Item>();
        var charges = new List<Item>();
        var world = new DogmaWorld(rig.Statics, personal.Character.Id, StructureId);
        world.Add(new DogmaItem(personal.Character.Id, personal.Character.TypeId, personal.Character.Id,
            StructureId, ShipSnapshot.PilotFlag, ItemState.Online));
        world.Add(ItemDogma.Of(structure, personal.Character.Id, system, structure.Flag, ItemState.Online));

        for (var i = 0; i < moduleIds.Count; i++)
        {
            var flag = 27 + i;
            var module = new Item(moduleIds[i], StandupMultiroleMissileLauncherTypeId,
                ownerCorporationId, StructureId, flag, 1, true, null);
            var charge = new Item(moduleIds[i] + 10_000, StandupLightMissileTypeId,
                ownerCorporationId, StructureId, flag, 100, false, null);
            modules.Add(module);
            charges.Add(charge);
            world.Add(ItemDogma.Of(module, personal.Character.Id, StructureId, flag, ItemState.Online));
            world.Add(ItemDogma.Of(charge, personal.Character.Id, StructureId, flag, ItemState.Online));
            world.SetValue(charge.Id, ShipSnapshot.QuantityCharge, charge.Quantity);
        }

        return new ShipSnapshot
        {
            Character = personal.Character,
            Ship = structure,
            Fitted = modules,
            Charges = charges,
            Skills = [],
            World = world
        };
    }

    [PgFact]
    public async Task Illegal_neutral_players_and_capsules_are_refused_by_the_structure_owner_without_SIM_or_charge_spend()
    {
        await using var rig = await Rig.CreateAsync();
        var controller = await rig.NewCharacterAsync("Structure Controller");
        var neutral = await rig.NewCharacterAsync("Neutral Pilot", account: 2);
        var capsule = await rig.NewCharacterAsync("Neutral Capsule", account: 3, shipTypeId: CharacterShips.CapsuleTypeId);
        var moduleId = StructureId + 101;
        var controlled = await ControlAsync(rig, controller, moduleId);
        var neutralSession = await WeaponHostTests.InSpaceAsync(rig, neutral, 2);
        var capsuleSession = await WeaponHostTests.InSpaceAsync(rig, capsule, 3);
        List<long> owners = [];
        var personalSafetyCalls = 0;
        var aggressionCalls = 0;
        rig.Space.StructureWeaponTargetContextOf = (owner, _, _) =>
        {
            owners.Add(owner);
            return Task.FromResult(new WeaponTargetContext(AttackerIsPlayer: true, TargetIsPlayerProperty: true));
        };
        rig.Space.SafetyLevelOf = (_, _) =>
        {
            personalSafetyCalls++;
            return Task.FromResult(SafetyLevel.None);
        };
        rig.Space.Aggression = (_, _) =>
        {
            aggressionCalls++;
            return Task.CompletedTask;
        };
        var activationsBefore = rig.Sim.Heard.OfType<SimActivate>().Count();

        var neutralResult = await rig.Space.ActivateAsync(controlled.Session, moduleId, "useMissiles",
            neutralSession.GetInt("shipid")!.Value, 1000, CancellationToken.None);
        var capsuleResult = await rig.Space.ActivateAsync(controlled.Session, moduleId, "useMissiles",
            capsuleSession.GetInt("shipid")!.Value, 1000, CancellationToken.None);

        Assert.Equal("SafetyActivated", neutralResult?.Reason);
        Assert.Equal("SafetyActivated", capsuleResult?.Reason);
        Assert.NotEqual(controlled.ControllerCorporationId, controlled.OwnerCorporationId);
        Assert.Equal([controlled.OwnerCorporationId, controlled.OwnerCorporationId], owners);
        Assert.Equal(activationsBefore, rig.Sim.Heard.OfType<SimActivate>().Count());
        Assert.Empty(rig.Sim.Heard.OfType<SimChargeSpent>());
        Assert.Equal(0, personalSafetyCalls);
        Assert.Equal(0, aggressionCalls);
    }

    [PgFact]
    public async Task Legal_war_players_and_unpiloted_nonplayer_targets_can_activate_without_controller_crimewatch()
    {
        await using var rig = await Rig.CreateAsync();
        var controller = await rig.NewCharacterAsync("Structure Controller");
        var warTarget = await rig.NewCharacterAsync("War Target", account: 2);
        var moduleIds = new[] { StructureId + 201, StructureId + 202 };
        var controlled = await ControlAsync(rig, controller, moduleIds);
        var targetSession = await WeaponHostTests.InSpaceAsync(rig, warTarget, 2);
        var ownerCalls = new List<long>();
        var safetyCalls = 0;
        var aggressionCalls = 0;
        rig.Space.StructureWeaponTargetContextOf = (owner, _, _) =>
        {
            ownerCalls.Add(owner);
            return Task.FromResult(new WeaponTargetContext(
                AttackerIsPlayer: true,
                TargetIsPlayerProperty: true,
                ActiveWar: true));
        };
        rig.Space.SafetyLevelOf = (_, _) =>
        {
            safetyCalls++;
            return Task.FromResult(SafetyLevel.Full);
        };
        rig.Space.Aggression = (_, _) =>
        {
            aggressionCalls++;
            return Task.CompletedTask;
        };

        var warResult = await rig.Space.ActivateAsync(controlled.Session, moduleIds[0], "useMissiles",
            targetSession.GetInt("shipid")!.Value, 1000, CancellationToken.None);
        var nonplayerResult = await rig.Space.ActivateAsync(controlled.Session, moduleIds[1], "useMissiles",
            980_050_000_105, 1000, CancellationToken.None);

        Assert.Equal(ModuleResult.Done, warResult);
        Assert.Equal(ModuleResult.Done, nonplayerResult);
        Assert.Equal([controlled.OwnerCorporationId], ownerCalls);
        Assert.Equal(2, rig.Sim.Heard.OfType<SimActivate>().Count());
        Assert.Equal(0, safetyCalls);
        Assert.Equal(0, aggressionCalls);
    }

    [PgFact]
    public async Task SIM_range_and_capacitor_refusals_do_not_report_structure_charge_spent_on_retry()
    {
        await using var rig = await Rig.CreateAsync();
        var controller = await rig.NewCharacterAsync("Structure Controller");
        var moduleId = StructureId + 301;
        var controlled = await ControlAsync(rig, controller, moduleId);
        rig.Space.StructureWeaponTargetContextOf = (_, _, _) =>
            Task.FromResult(new WeaponTargetContext(AttackerIsPlayer: true, TargetIsPlayerProperty: true));
        var spentBefore = rig.Sim.Heard.OfType<SimChargeSpent>().Count();

        rig.Sim.ActivateAnswer = new CommandReply(false, 0, "TARGET_OUT_OF_RANGE");
        var rangeResult = await rig.Space.ActivateAsync(controlled.Session, moduleId, "useMissiles",
            980_050_000_105, 1000, CancellationToken.None);
        Assert.Equal("TARGET_OUT_OF_RANGE", rangeResult?.Reason);

        rig.Sim.ActivateAnswer = new CommandReply(false, 0, "NO_CAPACITOR");
        var capacitorResult = await rig.Space.ActivateAsync(controlled.Session, moduleId, "useMissiles",
            980_050_000_105, 1000, CancellationToken.None);
        Assert.Equal("NO_CAPACITOR", capacitorResult?.Reason);
        Assert.Equal(spentBefore, rig.Sim.Heard.OfType<SimChargeSpent>().Count());
    }
}
