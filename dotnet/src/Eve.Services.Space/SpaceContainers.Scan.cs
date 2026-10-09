using Eve.Inventory;
using Eve.Marshal;
using Eve.Services.Space.Effects;
using Eve.Services.Space.Scanning;
using Eve.Ships;
using Eve.Space;

namespace Eve.Services.Space;

/// <summary>
/// The scanners' work cycles (protocol 40): a Cargo Scanner's cycle reveals the locked ship's hold, a Ship Scanner's its fitted hardware, to the pilot who ran it. The
/// simulation has decided the lock and the reach (a cycle that ends out of reach never reaches here); the host decides the rows and the notification, through the same
/// dispatcher as the mining and salvage cycles (ADR 0013). The reveal is announced to the pilot only, and the target is told nothing.
/// </summary>
public sealed partial class SpaceContainers
{
    // the scanners' attributes: the range and falloff the scanner effects name in the SDE (cargoScanRange 126; shipScanRange 125 and shipScanFalloff 510), the
    // resistance a target has to a scan (cargoScanResistance 188, shipScanResistance 511)
    private const int CargoScanRangeAttribute = 126;
    private const int ShipScanRangeAttribute = 125;
    private const int ShipScanFalloffAttribute = 510;
    private const int CargoScanResistanceAttribute = 188;
    private const int ShipScanResistanceAttribute = 511;
    private const int CapacitorCapacityAttribute = ModuleAttributes.CapacitorCapacity;

    /// <summary>The draws of a ship scan's per-item reveal (and of a target's resistance is the cycle's own roll). Replaced by a test to fix the draws.</summary>
    internal Func<double> ScanRandom { get; set; } = Random.Shared.NextDouble;

    /// <summary>
    /// Settles one scan. A refusal is an answer like any other: the host's reason goes back to the simulation, which ends the scan. A grant is recorded with its answer,
    /// and the pilot is sent the reveal once the record is kept (<see cref="RecordOutcomeAsync"/>), so a repeated cycle is never revealed twice.
    /// </summary>
    private async Task<WorkOutcome> SettleScanAsync(WorkCycle cycle)
    {
        var cargo = cycle.Command == Eve.Space.SimModuleWorkCommands.ScanCargo;
        if (cycle.OwnerId <= 0 || cycle.TargetId <= 0 || !double.IsFinite(cycle.Roll) || cycle.Roll < 0 || cycle.Roll >= 1)
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
        if (!_space.IsModuleActive(cycle.SessionId, cycle.OwnerId))
        {
            return WorkOutcome.Refuse("NOT_CONTROLLED");
        }

        // the reach of the scanner, from its own attributes after dogma (a cargo scanner has no falloff)
        var optimal = ship.World.Get(cycle.OwnerId, cargo ? CargoScanRangeAttribute : ShipScanRangeAttribute);
        var falloff = cargo ? 0d : ship.World.Get(cycle.OwnerId, ShipScanFalloffAttribute);
        if (!double.IsFinite(optimal) || optimal <= 0 || !double.IsFinite(falloff) || falloff < 0)
        {
            return WorkOutcome.Refuse("INVALID_PROFILE");
        }

        var target = await _items.GetAsync(cycle.TargetId);
        var targetSpace = await _spaceItems.GetAsync(cycle.TargetId);
        if (target is null || target.LocationId != cycle.SolarSystemId || target.Flag != CharacterShips.InSpaceFlag
            || targetSpace is null || targetSpace.SolarSystemId != cycle.SolarSystemId)
        {
            return WorkOutcome.Refuse("NO_TARGET");
        }
        await _statics.EnsureTypesAsync([target.TypeId]);
        var targetType = _statics.Type(target.TypeId);
        if (target.Id == cycle.ShipId)
        {
            return WorkOutcome.Refuse("TARGET_SELF");
        }
        if (targetType is null || _statics.CategoryName(targetType.CategoryId) != "Ship")
        {
            return WorkOutcome.Refuse("NO_TARGET");
        }

        var measured = await _sim.CommandAsync(cycle.SessionId, SimCommands.Measure, [cycle.TargetId]);
        if (!measured.Accepted || measured.Distance is not { } distance || !double.IsFinite(distance))
        {
            return WorkOutcome.Refuse("TARGET_NOT_FOUND");
        }
        var chance = ScanRows.RevealChance(distance, optimal, falloff);
        if (chance <= 0)
        {
            return WorkOutcome.Refuse("TARGET_OUT_OF_RANGE");
        }

        // a ship with a scan resistance is not revealed when the cycle's roll lands under it. The reference refuses the start (no cycle is spent); the host has the
        // target's resistance only at the cycle's end, so the refusal comes there, with the reference's words (DEVIATIONS: module rules, track C-scanners).
        var resistance = Math.Clamp(targetType.Attributes.GetValueOrDefault(cargo ? CargoScanResistanceAttribute : ShipScanResistanceAttribute, 0d), 0d, 1d);
        if (ScanRows.IsResisted(cycle.Roll, resistance))
        {
            return new WorkOutcome(false, "TARGET_RESISTS_SCAN", Notice: Py.Tuple(new PyStr("CustomNotify"), new PyDict([
                new(new PyStr("notify"), new PyStr("That ship is resistant to your scan.")),
            ])));
        }

        var scanned = await ScannedInventoryAsync(target);
        var targetShipId = target.Id;
        if (cargo)
        {
            var contents = new Dictionary<long, IReadOnlyList<ScanRows.Scanned>>();
            foreach (var container in scanned.Where(item => ScanRows.IsCargoFlag(item.Flag) && ScanRows.IsContainer(item)))
            {
                contents[container.Id] = await ScannedInventoryInContainerAsync(container.Id);
            }
            var rows = ScanRows.CargoRows(scanned, id => contents.TryGetValue(id, out var inside) ? inside : []);
            return Revealed("OnCargoScanComplete", [new PyLong(targetShipId), RowsOf(rows)]);
        }

        var hardware = ScanRows.ShipRows(scanned, chance, ScanRandom);
        var (charge, capacity) = await CapacitorOfAsync(target);
        if (charge is null || capacity is null)
        {
            return WorkOutcome.Refuse("NO_TARGET");
        }
        return Revealed("OnShipScanCompleted", [new PyLong(targetShipId), new PyReal(charge.Value), new PyReal(capacity.Value), RowsOf(hardware)]);
    }

    /// <summary>A granted reveal: no change to the target, the cycle kept as a grant that the target stays (not complete), and the pilot's notice after the record.</summary>
    private static WorkOutcome Revealed(string name, PyValue[] args) =>
        new(true, "", 0, 0, false, Scan: (name, args));

    private static PyValue RowsOf(IReadOnlyList<(int TypeId, long Quantity)> rows) =>
        new PyList([.. rows.Select(row => (PyValue)Py.Tuple(new PyInt(row.TypeId), new PyLong(row.Quantity)))]);

    private async Task<IReadOnlyList<ScanRows.Scanned>> ScannedInventoryAsync(Item ship)
    {
        var items = await _items.InLocationAsync(ship.Id, ship.OwnerId);
        return await ToScannedAsync(items);
    }

    private async Task<IReadOnlyList<ScanRows.Scanned>> ScannedInventoryInContainerAsync(long containerId) =>
        await ToScannedAsync(await _items.InContainerAsync(containerId));

    private async Task<IReadOnlyList<ScanRows.Scanned>> ToScannedAsync(IReadOnlyList<Item> items)
    {
        await _statics.EnsureTypesAsync([.. items.Select(item => item.TypeId).Distinct()]);
        return [.. items.Select(item =>
        {
            var type = _statics.Type(item.TypeId);
            return new ScanRows.Scanned(
                item.Id, item.TypeId, type?.GroupId ?? 0, type?.CategoryId ?? 0, item.Flag, item.Quantity,
                !item.Singleton ? 0 : item.BlueprintCopy ? 2 : 1);
        })];
    }

    /// <summary>The target's capacitor in GJ: its charge (the simulation's fraction of its capacity) and the capacity the target's dogma gives it.</summary>
    private async Task<(double? Charge, double? Capacity)> CapacitorOfAsync(Item target)
    {
        var targetSession = _space.SessionOfShip(target.Id);
        if (targetSession is null || await _space.ConditionAsync(targetSession, CancellationToken.None) is not { } condition)
        {
            return (null, null);
        }
        var targetShip = await _ships.LoadAsync(target.OwnerId);
        if (targetShip is null || targetShip.Ship.Id != target.Id)
        {
            return (null, null);
        }
        var capacity = targetShip.World.Get(target.Id, CapacitorCapacityAttribute);
        if (!double.IsFinite(capacity) || capacity < 0)
        {
            return (null, null);
        }
        return (Math.Round(Math.Clamp(condition.Charge, 0d, 1d) * capacity, 2), Math.Round(capacity, 2));
    }
}
