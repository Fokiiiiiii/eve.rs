using System.Collections.Concurrent;
using Eve.Dogma;
using Eve.Inventory;
using Eve.Marshal;
using Eve.Services.Ships;
using Eve.Ships;
using Eve.Space;

namespace Eve.Services.Space;

public sealed partial class SpaceContainers
{
    /// <summary>What the host answers a work cycle: accepted or refused with the reason, and what it delivered (the fields of <see cref="SimDroneWorkResult"/>).</summary>
    /// <summary>
    /// The answer of a settled work cycle. <see cref="Fact"/> is announced on the bus in the transaction that records the answer; <see cref="Notice"/> is the
    /// <c>OnRemoteMessage</c> the pilot is told once the answer is kept.
    /// </summary>
    private readonly record struct WorkOutcome(
        bool Accepted, string Reason, long Remaining = 0, long Delivered = 0, bool Complete = true, (string Topic, object Payload, string Key)? Fact = null,
        PyValue? Notice = null, (string Name, PyValue[] Args)? Scan = null)
    {
        public static WorkOutcome Refuse(string reason) => new(false, reason);
    }

    /// <summary>
    /// A work cycle the simulation parks on and the host decides (ADR 0013): a drone's (mining or salvage) or a module's (a mining laser's or a salvager's). The
    /// <see cref="OwnerId"/> is the drone or the module; the answer is recorded for the owner (<see cref="PgWorkCycleLedger"/>).
    /// </summary>
    private sealed record WorkCycle(
        bool IsModule, ulong SessionId, long CharacterId, long ShipId, int SolarSystemId, long OwnerId, long TargetId, ulong CycleId, byte Command,
        double MiningAmountM3, double WasteVolumeMultiplier, double WasteProbability, double AccessDifficultyBonus, double Roll,
        double RoundingRoll, double CritRoll, double CritProbability, double CritQuantityMultiplier, IReadOnlyList<long>? Recipients = null)
    {
        public static WorkCycle OfDrone(SimDroneWorkCycle cycle) => new(
            false, cycle.SessionId, cycle.CharacterId, cycle.ShipId, cycle.SolarSystemId, cycle.DroneId, cycle.TargetId, cycle.CycleId, cycle.Command,
            cycle.MiningAmountM3, cycle.WasteVolumeMultiplier, cycle.WasteProbability, cycle.AccessDifficultyBonus, cycle.Roll, 0, 0, 0, 0);

        public static WorkCycle OfModule(SimModuleWorkCycle cycle) => new(
            true, cycle.SessionId, cycle.CharacterId, cycle.ShipId, cycle.SolarSystemId, cycle.ModuleId, cycle.TargetId, cycle.CycleId, cycle.Command,
            cycle.MiningAmountM3, cycle.WasteVolumeMultiplier, cycle.WasteProbability, cycle.AccessDifficultyBonus, cycle.WasteRoll,
            cycle.RoundingRoll, cycle.CritRoll, cycle.CritProbability, cycle.CritQuantityMultiplier, cycle.Recipients);

        public string Kind => IsModule ? "module" : "drone";
    }

    /// <summary>
    /// The one path for the work cycles of drones and modules. A repeat of the owner's last settled cycle is answered from the record and never settled again, so
    /// a cycle the simulation re-sends (its answer was lost, or the host restarted) is not granted twice.
    /// </summary>
    private async Task HandleWorkCycleAsync(WorkCycle cycle)
    {
        try
        {
            if (cycle.CycleId > long.MaxValue)
            {
                ReplyToWorkCycle(cycle, WorkOutcome.Refuse("INVALID_PROFILE"));
                return;
            }
            // the fuel of a module's cycle is debited and its answer recorded in one transaction, which also answers a repeat (CharacterShips.ConsumeModuleFuelAsync)
            if (cycle.IsModule && cycle.Command == SimModuleWorkCommands.ConsumeFuel)
            {
                ReplyToWorkCycle(cycle, await SettleFuelAsync(cycle));
                return;
            }
            if (await _cycles.LastAsync(cycle.OwnerId) is { } last && last.CycleId == (long)cycle.CycleId)
            {
                ReplyToWorkCycle(cycle, new WorkOutcome(last.Accepted, last.Reason, last.UnitsRemaining, last.DeliveredUnits, last.Complete));
                return;
            }
            var outcome = await SettleWorkCycleAsync(cycle);
            await RecordOutcomeAsync(cycle, outcome);
            ReplyToWorkCycle(cycle, outcome);
        }
        catch (Exception error)
        {
            _log?.Invoke($"{cycle.Kind} work cycle {cycle.CycleId} was not settled: {error.Message}");
            // a module is parked, and the simulation sends the same cycle again (ADR 0013, rule 3): no answer is sent, so the retry settles it. A drone is not
            // re-sent, so it is refused and goes back to idle.
            if (!cycle.IsModule)
            {
                ReplyToWorkCycle(cycle, WorkOutcome.Refuse("WORK_FAILED"));
            }
        }
    }

    /// <summary>
    /// Records a settled cycle's answer, with its fact when it has one (in one transaction), and then tells the pilot the notice of the answer, once it is kept.
    /// </summary>
    private async Task RecordOutcomeAsync(WorkCycle cycle, WorkOutcome outcome)
    {
        var record = new WorkCycleRecord(cycle.OwnerId, (long)cycle.CycleId, outcome.Accepted, outcome.Reason, outcome.Remaining, outcome.Delivered, outcome.Complete);
        if (outcome.Fact is { } fact)
        {
            await _cycles.RecordWithFactAsync(record, _events, fact.Topic, fact.Payload, fact.Key);
        }
        else
        {
            await _cycles.RecordAsync(record);
        }
        if (outcome.Notice is { } notice && _space.ClientSessionOf(cycle.SessionId) is { } session)
        {
            session.SendNotification("OnRemoteMessage", "clientID", notice);
        }
        // a scan's reveal is the pilot's alone, sent once the grant is kept (a repeated cycle is answered from the record and reveals nothing again)
        if (outcome.Scan is { } scan && _space.ClientSessionOf(cycle.SessionId) is { } scanner)
        {
            scanner.SendNotification(scan.Name, "clientID", scan.Args);
        }
    }

    /// <summary>
    /// The fuel of one cycle of a module that burns fuel: the module must be running in the pilot's ship, and the fuel is the module's consumption (713, 714) taken
    /// from the ship. A refusal before the debit is not recorded (nothing was taken, and a repeat is checked again).
    /// </summary>
    private async Task<WorkOutcome> SettleFuelAsync(WorkCycle cycle)
    {
        if (cycle.OwnerId <= 0)
        {
            return WorkOutcome.Refuse("INVALID_PROFILE");
        }
        var session = _space.ClientSessionOf(cycle.SessionId);
        if (session is null || session.CharacterId != cycle.CharacterId
            || session.GetInt("shipid") is not { } shipId || shipId != cycle.ShipId
            || !_space.IsInSpace(session) || _space.SolarSystemOf(session) != cycle.SolarSystemId)
        {
            return WorkOutcome.Refuse("NO_PILOT");
        }
        if (!_space.IsModuleActive(cycle.SessionId, cycle.OwnerId))
        {
            return WorkOutcome.Refuse("NOT_CONTROLLED");
        }
        var ship = await _ships.LoadAsync(cycle.CharacterId);
        if (ship is null || ship.Ship.Id != cycle.ShipId
            || ship.Ship.LocationId != cycle.SolarSystemId || ship.Ship.Flag != CharacterShips.InSpaceFlag)
        {
            return WorkOutcome.Refuse("NO_SHIP");
        }
        var fuelType = ship.World.Get(cycle.OwnerId, Eve.Services.Space.Effects.ModuleAttributes.ConsumptionType);
        var fuelQuantity = ship.World.Get(cycle.OwnerId, Eve.Services.Space.Effects.ModuleAttributes.ConsumptionQuantity);
        if (!double.IsFinite(fuelType) || fuelType <= 0 || fuelType > int.MaxValue
            || !double.IsFinite(fuelQuantity) || fuelQuantity < 0)
        {
            return WorkOutcome.Refuse("INVALID_PROFILE");
        }
        var record = await _ships.ConsumeModuleFuelAsync(
            _cycles, cycle.CharacterId, cycle.ShipId, cycle.OwnerId, (long)cycle.CycleId, (int)fuelType, (long)Math.Round(fuelQuantity));
        return new WorkOutcome(record.Accepted, record.Reason, record.UnitsRemaining, record.DeliveredUnits, record.Complete);
    }

    private void ReplyToWorkCycle(WorkCycle cycle, WorkOutcome outcome)
    {
        try
        {
            if (cycle.IsModule)
            {
                _sim.ModuleWorkResult(new SimModuleWorkResult(
                    cycle.SessionId, cycle.OwnerId, cycle.TargetId, cycle.CycleId, outcome.Accepted, outcome.Reason, outcome.Remaining, outcome.Delivered, outcome.Complete));
            }
            else
            {
                _sim.DroneWorkResult(new SimDroneWorkResult(
                    cycle.SessionId, cycle.OwnerId, cycle.TargetId, cycle.CycleId, outcome.Accepted, outcome.Reason, outcome.Remaining, outcome.Delivered, outcome.Complete));
            }
        }
        catch (Exception error)
        {
            _log?.Invoke($"{cycle.Kind} work cycle {cycle.CycleId} response was not sent: {error.Message}");
        }
    }

    /// <summary>
    /// An entosis link's cycle with its hub in reach (the simulation checked the lock and the reach): the pilot must still be in the system, in an alliance, and
    /// the capture service settles the hub. Its answer is the cycle's answer; a refusal by name ends the link.
    /// </summary>
    private async Task<WorkOutcome> SettleEntosisAsync(WorkCycle cycle)
    {
        if (EntosisCaptures is null)
        {
            return WorkOutcome.Refuse("ENTOSIS_NOT_WIRED");
        }
        var session = _space.ClientSessionOf(cycle.SessionId);
        if (session is null || session.CharacterId != cycle.CharacterId || !_space.IsInSpace(session) || _space.SolarSystemOf(session) != cycle.SolarSystemId)
        {
            return WorkOutcome.Refuse("NO_PILOT");
        }
        var allianceId = session.GetInt("allianceid") ?? 0;
        if (allianceId <= 0)
        {
            return WorkOutcome.Refuse("ALLIANCE_MEMBERSHIP_REQUIRED");
        }
        var corporationId = session.GetInt("corpid") ?? 0;
        if (corporationId <= 0)
        {
            return WorkOutcome.Refuse("NO_PILOT");
        }
        var settled = await EntosisCaptures.SettleCycleAsync(
            cycle.OwnerId, cycle.CharacterId, corporationId, allianceId, cycle.TargetId, cycle.SolarSystemId, CancellationToken.None);
        return settled.Accepted ? new WorkOutcome(true, "") : WorkOutcome.Refuse(settled.Reason);
    }

    /// <summary>The checks and the settlement of one work cycle: the owner must be the pilot's (a drone flown, or a module that runs), the target there, in reach.</summary>
    private async Task<WorkOutcome> SettleWorkCycleAsync(WorkCycle cycle)
    {
        // a command burst's cycle applies its buffs to the ships it reaches (track C6, SpaceContainers.Burst.cs)
        if (cycle.IsModule && cycle.Command == SimModuleWorkCommands.Burst)
        {
            return await SettleBurstAsync(cycle);
        }
        // a P.A.N.I.C.'s start is granted its fleet's buffs before the effect starts (track C6)
        if (cycle.IsModule && cycle.Command == SimModuleWorkCommands.Panic)
        {
            return await SettlePanicAsync(cycle);
        }
        if (cycle.IsModule && cycle.Command is SimModuleWorkCommands.ScanCargo or SimModuleWorkCommands.ScanShip)
        {
            return await SettleScanAsync(cycle);
        }
        // an entosis link's cycle moves the capture of its hub to now (ST-9 c, EntosisCaptureService; durable, in its own transaction)
        if (cycle.IsModule && cycle.Command == SimModuleWorkCommands.Entosis)
        {
            return await SettleEntosisAsync(cycle);
        }
        if (cycle.Command is not (SimDroneCommands.MineRepeatedly or SimDroneCommands.Salvage))
        {
            return WorkOutcome.Refuse("UNSUPPORTED_COMMAND");
        }
        if (cycle.OwnerId <= 0 || cycle.TargetId <= 0
            || !double.IsFinite(cycle.Roll) || cycle.Roll < 0 || cycle.Roll >= 1
            || !double.IsFinite(cycle.AccessDifficultyBonus) || cycle.AccessDifficultyBonus < 0)
        {
            return WorkOutcome.Refuse("INVALID_PROFILE");
        }
        if (cycle.Command == SimDroneCommands.MineRepeatedly
            && (!double.IsFinite(cycle.MiningAmountM3) || cycle.MiningAmountM3 <= 0
                || !double.IsFinite(cycle.WasteVolumeMultiplier) || !double.IsFinite(cycle.WasteProbability)))
        {
            return WorkOutcome.Refuse("INVALID_PROFILE");
        }

        var session = _space.ClientSessionOf(cycle.SessionId);
        if (session is null || session.CharacterId != cycle.CharacterId
            || session.GetInt("shipid") is not { } shipId || shipId != cycle.ShipId
            || !_space.IsInSpace(session) || _space.SolarSystemOf(session) != cycle.SolarSystemId)
        {
            return WorkOutcome.Refuse("NO_PILOT");
        }

        var ship = await _ships.LoadAsync(cycle.CharacterId);
        if (ship is null || ship.Ship.Id != cycle.ShipId
            || ship.Ship.LocationId != cycle.SolarSystemId || ship.Ship.Flag != CharacterShips.InSpaceFlag)
        {
            return WorkOutcome.Refuse("NO_SHIP");
        }

        // how far the work reaches: a drone's control range, or a module's own range (its maxRange)
        double controlRange;
        if (cycle.IsModule)
        {
            if (!_space.IsModuleActive(cycle.SessionId, cycle.OwnerId))
            {
                return WorkOutcome.Refuse("NOT_CONTROLLED");
            }
            controlRange = ship.World.Get(cycle.OwnerId, ModuleMaxRangeAttribute);
            if (!double.IsFinite(controlRange) || controlRange <= 0)
            {
                return WorkOutcome.Refuse("INVALID_PROFILE");
            }
        }
        else
        {
            controlRange = 20_000d;
            var range = ship.World.Get(cycle.CharacterId, DroneControlDistanceAttribute);
            if (double.IsFinite(range) && range > 0)
            {
                controlRange = range;
            }
        }

        var drone = cycle.IsModule ? null : await _items.GetAsync(cycle.OwnerId);
        if (!cycle.IsModule && (drone is null || drone.OwnerId != cycle.CharacterId || drone.LocationId != cycle.SolarSystemId
            || drone.Flag != CharacterShips.InSpaceFlag))
        {
            return WorkOutcome.Refuse("NOT_CONTROLLED");
        }

        var target = await _items.GetAsync(cycle.TargetId);
        var targetSpace = await _spaceItems.GetAsync(cycle.TargetId);
        if (target is null || target.LocationId != cycle.SolarSystemId || target.Flag != CharacterShips.InSpaceFlag
            || targetSpace is null || targetSpace.SolarSystemId != cycle.SolarSystemId)
        {
            return WorkOutcome.Refuse("NO_TARGET");
        }

        if (drone is not null)
        {
            await _statics.EnsureTypesAsync([drone.TypeId, target.TypeId]);
            var droneType = _statics.Type(drone.TypeId);
            var expectedDroneGroup = cycle.Command == SimDroneCommands.MineRepeatedly ? "Mining Drone" : "Salvage Drone";
            if (droneType is null || _statics.CategoryName(droneType.CategoryId) != "Drone"
                || !_statics.GroupName(droneType.GroupId).Equals(expectedDroneGroup, StringComparison.OrdinalIgnoreCase))
            {
                return WorkOutcome.Refuse("NOT_CONTROLLED");
            }
        }
        else
        {
            await _statics.EnsureTypesAsync([target.TypeId]);
        }

        var targetType = _statics.Type(target.TypeId);
        if (targetType is null
            || (cycle.Command == SimDroneCommands.MineRepeatedly
                ? !MiningTargets.IsMineable(_statics, targetType)
                : !_statics.GroupName(targetType.GroupId).Contains("Wreck", StringComparison.OrdinalIgnoreCase)))
        {
            return WorkOutcome.Refuse(cycle.Command == SimDroneCommands.MineRepeatedly ? "NO_TARGET" : "NO_WRECK");
        }

        var measured = await _sim.CommandAsync(cycle.SessionId, SimCommands.Measure, [cycle.TargetId]);
        if (!measured.Accepted || measured.Distance is not { } distance || !double.IsFinite(distance))
        {
            return WorkOutcome.Refuse(cycle.Command == SimDroneCommands.MineRepeatedly ? "NO_TARGET" : "NO_WRECK");
        }
        if (distance > controlRange)
        {
            return WorkOutcome.Refuse("OUT_OF_RANGE");
        }

        if (cycle.Command == SimDroneCommands.MineRepeatedly)
        {
            // a module's rolls are the simulation's three draws; a drone rounds down and never crits, so its crit draw is unused
            var mined = await _ships.MineAsteroidAsync(
                cycle.CharacterId, cycle.ShipId, cycle.SolarSystemId, cycle.OwnerId, cycle.TargetId,
                cycle.IsModule ? MiningSource.Module : MiningSource.Drone,
                new MiningNumbers(cycle.MiningAmountM3, cycle.WasteVolumeMultiplier, cycle.WasteProbability, cycle.CritProbability, cycle.CritQuantityMultiplier),
                cycle.IsModule
                    ? MiningRolls.ForModule(cycle.Roll, cycle.RoundingRoll, cycle.CritRoll, cycle.WasteProbability, cycle.CritProbability)
                    : new MiningRolls(cycle.Roll, 0, 0, 1, 0),
                (long)cycle.CycleId, _cycles, _spaceItems, _events, (int)(session.GetInt("corporationID") ?? session.GetInt("corpid") ?? 0),
                nowUnixMs: _nowUnixMs());
            if (mined.Accepted && mined.Depleted)
            {
                _placed.TryRemove(cycle.TargetId, out _);
            }
            if (mined.Accepted && mined.Change is { } changed)
            {
                try
                {
                    await _statics.EnsureTypesAsync([.. changed.Items.Select(item => item.After.TypeId).Distinct()]);
                    Eve.Services.Ships.ShipNotifications.Send(
                        session, cycle.CharacterId, changed, RowOf, Py.FileTimeFromUnixMs(_nowUnixMs()));
                }
                catch (Exception error)
                {
                    _log?.Invoke($"mined cargo for cycle {cycle.CycleId} was saved but its item notification failed: {error.Message}");
                }
            }
            // the miner is told what it mined, after the grant is committed (the cycle is granted once, so this is sent once)
            if (mined.Accepted && cycle.IsModule && mined.Mined is { } yield)
            {
                session.SendNotification(
                    "OnOreMined", "charid", Eve.Services.Ships.ShipNotifications.OreMined(cycle.TargetId, cycle.OwnerId, mined.OreTypeId, yield));
            }
            // a laser with a crystal loaded rolls the crystal's volatility once the grant is kept (EVE.js applyCrystalVolatility)
            if (mined.Accepted && cycle.IsModule && ship.Fitted.FirstOrDefault(m => m.Id == cycle.OwnerId) is { } laser
                && ship.ChargeOf(laser) is { Quantity: > 0 } crystal)
            {
                await _statics.EnsureTypesAsync([crystal.TypeId]);
                if (_statics.Type(crystal.TypeId) is { } crystalType
                    && _statics.AttributeId("crystalVolatilityChance") is { } chanceId
                    && _statics.AttributeId("crystalVolatilityDamage") is { } damageId)
                {
                    await _ships.ApplyCrystalVolatilityAsync(
                        crystal.Id, VolatilityRoll(), crystalType.Attributes.GetValueOrDefault(chanceId, 0d), crystalType.Attributes.GetValueOrDefault(damageId, 0d));
                }
            }
            return new WorkOutcome(mined.Accepted, mined.Reason, mined.UnitsRemaining, mined.DeliveredUnits, mined.Depleted);
        }

        var difficultyId = _statics.AttributeId("accessDifficulty");
        var baseChance = difficultyId is { } id ? targetType.Attributes.GetValueOrDefault(id, 0d) : 0d;
        var chance = Math.Clamp(baseChance + cycle.AccessDifficultyBonus, 0d, 100d);
        if (!double.IsFinite(chance))
        {
            return WorkOutcome.Refuse("INVALID_PROFILE");
        }
        if (cycle.Roll * 100d >= chance)
        {
            // a failed roll takes nothing from the wreck: the answer that records it carries the fact, and the pilot is told
            var failed = new SalvageFailedEvent(
                cycle.CharacterId, cycle.SolarSystemId, cycle.ShipId, cycle.OwnerId, cycle.IsModule ? MiningSource.Module : MiningSource.Drone,
                cycle.TargetId, targetType.TypeId);
            return new WorkOutcome(
                true, "", 1, 0, false, (SalvageFailedEvent.Topic, failed, $"char:{cycle.CharacterId}"),
                Py.Tuple(new PyStr("SalvagingFailure"), new PyDict([
                    new(new PyStr("itemID"), new PyLong(cycle.TargetId)),
                    new(new PyStr("type"), Py.Tuple(new PyInt(4), new PyInt(targetType.TypeId))),
                ])));
        }

        var salvageProducts = await _statics.TypesInGroupsAsync(["Salvaged Materials"]);
        var salvageTypeId = SelectSalvageProductTypeId(targetType, targetSpace, salvageProducts);
        if (salvageTypeId is null)
        {
            return WorkOutcome.Refuse("NO_SALVAGE_PRODUCTS");
        }
        var salvaged = await _ships.SalvageWreckAsync(
            cycle.CharacterId, cycle.ShipId, cycle.SolarSystemId, cycle.IsModule ? MiningSource.Module : MiningSource.Drone,
            cycle.OwnerId, cycle.TargetId, salvageTypeId,
            targetSpace, _nowUnixMs() + (long)CanLifetime.TotalMilliseconds, _spaceItems, (long)cycle.CycleId, _cycles, _events);
        if (!salvaged.Accepted)
        {
            return WorkOutcome.Refuse(salvaged.Reason);
        }

        _placed.TryRemove(cycle.TargetId, out _);
        if (salvaged.Change is { } salvageChange)
        {
            try
            {
                await _statics.EnsureTypesAsync([.. salvageChange.Items.Select(item => item.After.TypeId).Distinct()]);
                Eve.Services.Ships.ShipNotifications.Send(
                    session, cycle.CharacterId, salvageChange, RowOf, Py.FileTimeFromUnixMs(_nowUnixMs()));
            }
            catch (Exception error)
            {
                _log?.Invoke($"salvage for cycle {cycle.CycleId} was saved but its item notification failed: {error.Message}");
            }
        }
        if (salvaged.Container is { } container && salvaged.ContainerSpaceItem is { } containerSpace)
        {
            _placed[container.Id] = 0;
            var containerType = _statics.Type(container.TypeId);
            try
            {
                _sim.PlaceWreck(new SimPlaceWreck(
                    cycle.SolarSystemId, container.Id, containerSpace.GroupId,
                    new SimWreckKind(container.TypeId, container.CustomName ?? containerType?.Name ?? "Cargo Container", containerType?.GraphicId ?? 0),
                    containerSpace.Position, containerSpace.Direction, containerSpace.Radius, containerSpace.LauncherId,
                    new SimCredit(containerSpace.CharacterId, containerSpace.CorporationId, containerSpace.AllianceId, containerSpace.WarFactionId), false));
            }
            catch (Exception error)
            {
                _placed.TryRemove(container.Id, out _);
                _log?.Invoke($"loot container {container.Id} was saved but not placed in the simulation: {error.Message}");
            }
        }
        // the pilot is told what was recovered once the grant is kept; a replayed grant has no change and is not told again
        var recovered = _statics.Type(salvageTypeId.Value)?.Name ?? string.Empty;
        return new WorkOutcome(true, "", 0, 1, true, Notice: salvaged.Change is null ? null : Py.Tuple(new PyStr("CustomNotify"), new PyDict([
            new(new PyStr("notify"), new PyStr($"Salvaging succeeded. Recovered 1 x {recovered} and placed it in your cargo hold."))])));
    }

    private int? SelectSalvageProductTypeId(TypeDef wreckType, SpaceItem wreckSpace, IReadOnlyList<TypeDef> products)
    {
        if (products.Count == 0)
        {
            return null;
        }

        var factionName = wreckSpace.WarFactionId > 0 && wreckSpace.WarFactionId <= int.MaxValue
            ? _statics.FactionName((int)wreckSpace.WarFactionId)
            : "";
        var identity = $"{factionName} {wreckType.Name}";
        var difficultyId = _statics.AttributeId("accessDifficulty");
        var advanced = identity.Contains("Tech II", StringComparison.OrdinalIgnoreCase)
            || identity.Contains("T2", StringComparison.OrdinalIgnoreCase)
            || identity.Contains("Officer", StringComparison.OrdinalIgnoreCase)
            || identity.Contains("Faction Wreck", StringComparison.OrdinalIgnoreCase);
        if (difficultyId is { } attributeId
            && wreckType.Attributes.TryGetValue(attributeId, out var baseChance)
            && baseChance <= 0)
        {
            advanced = true;
        }

        // EVE University maps salvage families by wreck faction; unknown families fall back to published SDE salvage types.
        var materialNames = SalvageMaterialNames(identity, advanced);
        IReadOnlyList<TypeDef> pool = materialNames is null
            ? products
            : products.Where(product => materialNames.Contains(product.Name, StringComparer.OrdinalIgnoreCase)).ToArray();
        if (pool.Count == 0)
        {
            pool = products;
        }
        var ordered = pool.OrderBy(product => product.TypeId).ToArray();
        return ordered[(int)(wreckSpace.ItemId % ordered.Length)].TypeId;
    }

    private static string[]? SalvageMaterialNames(string identity, bool advanced)
    {
        static bool Has(string text, string value) => text.Contains(value, StringComparison.OrdinalIgnoreCase);

        if (advanced)
        {
            if (Has(identity, "Angel"))
            {
                return ["Artificial Neural Network", "Impetus Console", "Interface Circuit", "Logic Circuit",
                    "Micro Circuit", "Power Circuit", "Single-crystal Superalloy I-beam", "Trigger Unit"];
            }
            if (Has(identity, "Blood"))
            {
                return ["Artificial Neural Network", "Capacitor Console", "Interface Circuit", "Logic Circuit",
                    "Micro Circuit", "Power Circuit", "Power Conduit"];
            }
            if (Has(identity, "Guristas"))
            {
                return ["Artificial Neural Network", "Enhanced Ward Console", "Intact Shield Emitter",
                    "Interface Circuit", "Logic Circuit", "Micro Circuit", "Power Circuit", "Telemetry Processor"];
            }
            if (Has(identity, "Sansha"))
            {
                return ["Artificial Neural Network", "Current Pump", "Intact Armor Plates", "Interface Circuit",
                    "Logic Circuit", "Micro Circuit", "Nanite Compound", "Power Circuit"];
            }
            if (Has(identity, "Serpentis"))
            {
                return ["Conductive Thermoplastic", "Drone Transceiver", "Interface Circuit", "Logic Circuit",
                    "Lorentz Fluid", "Micro Circuit", "Power Circuit"];
            }
            return null;
        }

        if (Has(identity, "Rogue Drone"))
        {
            return ["Alloyed Tritanium Bar", "Armor Plates", "Broken Drone Transceiver", "Burned Logic Circuit",
                "Charred Micro Circuit", "Conductive Polymer", "Contaminated Lorentz Fluid", "Contaminated Nanite Compound",
                "Defective Current Pump", "Fried Interface Circuit", "Malfunctioning Shield Emitter", "Melted Capacitor Console",
                "Scorched Telemetry Processor", "Smashed Trigger Unit", "Tangled Power Conduit", "Thruster Console",
                "Tripped Power Circuit", "Ward Console"];
        }
        if (Has(identity, "Minmatar") || Has(identity, "Angel"))
        {
            return ["Alloyed Tritanium Bar", "Armor Plates", "Burned Logic Circuit", "Charred Micro Circuit",
                "Contaminated Nanite Compound", "Fried Interface Circuit", "Malfunctioning Shield Emitter",
                "Smashed Trigger Unit", "Thruster Console", "Tripped Power Circuit"];
        }
        if (Has(identity, "Caldari") || Has(identity, "Guristas"))
        {
            return ["Burned Logic Circuit", "Charred Micro Circuit", "Conductive Polymer",
                "Damaged Artificial Neural Network", "Fried Interface Circuit", "Malfunctioning Shield Emitter",
                "Scorched Telemetry Processor", "Tripped Power Circuit", "Ward Console"];
        }
        if (Has(identity, "Gallente") || Has(identity, "Serpentis"))
        {
            return ["Armor Plates", "Broken Drone Transceiver", "Burned Logic Circuit", "Charred Micro Circuit",
                "Conductive Polymer", "Contaminated Lorentz Fluid", "Contaminated Nanite Compound",
                "Damaged Artificial Neural Network", "Fried Interface Circuit", "Tripped Power Circuit"];
        }
        if (Has(identity, "Amarr") || Has(identity, "Blood") || Has(identity, "Sansha"))
        {
            return ["Armor Plates", "Burned Logic Circuit", "Charred Micro Circuit",
                "Contaminated Nanite Compound", "Defective Current Pump", "Fried Interface Circuit",
                "Melted Capacitor Console", "Tangled Power Conduit", "Tripped Power Circuit"];
        }
        return null;
    }
}
