using Eve.Dogma;
using Eve.Services.Space.Effects;
using Eve.Ships;
using Eve.Space;

namespace Eve.Services.Space;

public sealed partial class SpaceContainers
{
    /// <summary>
    /// The settlement of one command burst's cycle (track C6, protocol 43). The pilot must run the burst in its ship in space, and the burst must carry buffs. The
    /// ships the simulation found in reach are the candidates, with the source's own; the host keeps those of the source's fleet, at most
    /// <see cref="BurstRecipients.MaxRecipients"/> (<see cref="BurstRecipients"/>). Each of them has the burst's buffs for the buff duration, and its pilot is sent the
    /// ship and the buff state.
    /// </summary>
    private async Task<WorkOutcome> SettleBurstAsync(WorkCycle cycle)
    {
        var session = _space.ClientSessionOf(cycle.SessionId);
        if (session is null)
        {
            _log?.Invoke($"command burst cycle {cycle.CycleId} refused: no pilot session for {cycle.SessionId}");
            return WorkOutcome.Refuse("NO_PILOT");
        }
        if (session.CharacterId != cycle.CharacterId)
        {
            _log?.Invoke($"command burst cycle {cycle.CycleId} refused: session character {session.CharacterId} differs from cycle character {cycle.CharacterId}");
            return WorkOutcome.Refuse("NO_PILOT");
        }
        if (session.GetInt("shipid") is not { } shipId || shipId != cycle.ShipId)
        {
            _log?.Invoke($"command burst cycle {cycle.CycleId} refused: session ship {session.GetInt("shipid")} differs from cycle ship {cycle.ShipId}");
            return WorkOutcome.Refuse("NO_PILOT");
        }
        if (!_space.IsInSpace(session))
        {
            _log?.Invoke($"command burst cycle {cycle.CycleId} refused: source pilot is not in space");
            return WorkOutcome.Refuse("NO_PILOT");
        }
        if (_space.SolarSystemOf(session) != cycle.SolarSystemId)
        {
            _log?.Invoke($"command burst cycle {cycle.CycleId} refused: session system {_space.SolarSystemOf(session)} differs from cycle system {cycle.SolarSystemId}");
            return WorkOutcome.Refuse("NO_PILOT");
        }
        if (!_space.IsModuleActive(cycle.SessionId, cycle.OwnerId))
        {
            _log?.Invoke($"command burst cycle {cycle.CycleId} refused: module {cycle.OwnerId} is not active");
            return WorkOutcome.Refuse("NOT_CONTROLLED");
        }
        var ship = await _ships.LoadAsync(cycle.CharacterId);
        if (ship is null || ship.Ship.Id != cycle.ShipId || ship.Ship.LocationId != cycle.SolarSystemId || ship.Ship.Flag != CharacterShips.InSpaceFlag)
        {
            _log?.Invoke($"command burst cycle {cycle.CycleId} refused: source ship {cycle.ShipId} is not in space system {cycle.SolarSystemId}");
            return WorkOutcome.Refuse("NO_SHIP");
        }
        var module = ship.Fitted.FirstOrDefault(item => item.Id == cycle.OwnerId);
        if (module is null)
        {
            return WorkOutcome.Refuse("INVALID_PROFILE");
        }
        var buffs = CommandBurstBuffs.Of(ship.World, _statics, cycle.OwnerId, cycle.ChargeTypeId);
        var duration = CommandBurstBuffs.DurationOf(ship.World, cycle.OwnerId);
        if (buffs.Count == 0 || duration <= 0)
        {
            var charge = ship.ChargeOf(module);
            var ids = new[] { 2468, 2470, 2472, 2536 }.Select(attribute => ship.World.Get(cycle.OwnerId, attribute));
            _log?.Invoke($"command burst cycle {cycle.CycleId} refused: module {cycle.OwnerId} has {buffs.Count} buffs and duration {duration}; fitted flag {module?.Flag}; charge {charge?.TypeId}/{charge?.LocationId}/{charge?.Flag}/{charge?.Quantity}; buff ids [{string.Join(",", ids)}]");
            return WorkOutcome.Refuse("INVALID_PROFILE");
        }
        var now = (double)_nowUnixMs();
        var pilots = new Dictionary<long, ClientSession> { [cycle.ShipId] = session };
        foreach (var candidate in cycle.Recipients ?? [])
        {
            if (_space.SessionOfShip(candidate) is { } pilot)
            {
                pilots.TryAdd(candidate, pilot);
            }
        }
        var controlTowerShields = await OnlineControlTowerShieldsAsync(cycle.SolarSystemId, CancellationToken.None);
        var shieldedShips = new HashSet<long>();
        if (controlTowerShields.Count > 0)
        {
            foreach (var (candidate, pilot) in pilots)
            {
                if (await IsInsideControlTowerShieldAsync(pilot, candidate, cycle.SolarSystemId, controlTowerShields, CancellationToken.None))
                {
                    shieldedShips.Add(candidate);
                }
            }
        }
        if (shieldedShips.Contains(cycle.ShipId))
        {
            return WorkOutcome.Refuse("FORCE_FIELD");
        }
        var fleets = new Dictionary<long, long>();
        foreach (var character in pilots.Values.Select(pilot => pilot.CharacterId ?? 0).Where(id => id > 0).Distinct())
        {
            if (_space.FleetIdOfAsync is { } fleetOf && await fleetOf(character, CancellationToken.None) is { } fleet)
            {
                fleets[character] = fleet;
            }
        }
        var ships = pilots.Select(pair => new BurstShip(pair.Key, pair.Value.CharacterId ?? 0, 0)).ToList();
        // the simulation measured the reach (the ships in range), so the distance here is none: the host only decides which of them are the fleet's
        var reached = BurstRecipients.Resolve(
            new BurstShip(cycle.ShipId, cycle.CharacterId, 0),
            ships,
            Math.Max(0d, ship.World.Get(cycle.OwnerId, CommandBurstEffect.RangeAttribute)),
            _ => 0d,
            (first, second) => fleets.TryGetValue(first, out var fleetOfFirst) && fleets.TryGetValue(second, out var fleetOfSecond) && fleetOfFirst == fleetOfSecond,
            eligible: candidate => !shieldedShips.Contains(candidate.ShipId));
        _log?.Invoke($"command burst cycle {cycle.CycleId} resolved: pilots [{string.Join(",", pilots.Keys)}]; fleet ids [{string.Join(",", fleets.Select(pair => $"{pair.Key}:{pair.Value}"))}]; reached [{string.Join(",", reached)}]");
        foreach (var target in reached)
        {
            foreach (var (collection, value) in buffs)
            {
                _space.Buffs.Apply(target, collection, value, now, duration);
            }
        }
        foreach (var target in reached)
        {
            await TellBuffsAsync(pilots[target], target, now);
        }
        return new WorkOutcome(true, "", ClientNotification: (
            "OnFleetBoosted",
            [new Eve.Marshal.PyInt(checked((int)module.TypeId)), new Eve.Marshal.PyInt(reached.Count)]));
    }

    private async Task<IReadOnlyList<(double[] Position, double Radius)>> OnlineControlTowerShieldsAsync(
        int solarSystemId,
        CancellationToken cancellationToken)
    {
        if (_deployed is null)
        {
            return [];
        }
        var towers = (await _deployed.InSystemAsync(solarSystemId, cancellationToken))
            .Where(record => record.Kind == ControlTowerKind && record.ActivateAtMs <= _nowUnixMs())
            .ToArray();
        if (towers.Length == 0)
        {
            return [];
        }
        await _statics.EnsureTypesAsync(towers.Select(tower => tower.TypeId), cancellationToken);
        var shieldRadiusAttribute = _statics.AttributeId("shieldRadius") ?? 680;
        var shields = new List<(double[] Position, double Radius)>(towers.Length);
        foreach (var tower in towers)
        {
            if (await _deployed.TowerStateAsync(tower.DeployableId, cancellationToken) != TowerOnline)
            {
                continue;
            }
            var radius = _statics.Type(tower.TypeId)?.Attributes.GetValueOrDefault(shieldRadiusAttribute) ?? 0d;
            if (radius > 0 && double.IsFinite(radius) && tower.Position.Length >= 3 && tower.Position.Take(3).All(double.IsFinite))
            {
                shields.Add((tower.Position, radius));
            }
        }
        return shields;
    }

    private async Task<bool> IsInsideControlTowerShieldAsync(
        ClientSession pilot,
        long shipId,
        int solarSystemId,
        IReadOnlyList<(double[] Position, double Radius)> shields,
        CancellationToken cancellationToken)
    {
        var position = await _space.PositionOfAsync(pilot, shipId, cancellationToken);
        if (position is not { } at || at.SolarSystemId != solarSystemId)
        {
            _log?.Invoke($"command burst ship {shipId} shield position is unavailable in system {solarSystemId}; excluding it");
            return true;
        }
        var radius = Math.Max(0d, at.Radius.GetValueOrDefault());
        return shields.Any(shield => MetersBetween(
            [at.X, at.Y, at.Z], shield.Position) + radius < shield.Radius);
    }

    private async Task<bool> IsCommandBurstSourceInForceFieldAsync(
        ClientSession pilot,
        long shipId,
        int solarSystemId,
        CancellationToken cancellationToken)
    {
        var shields = await OnlineControlTowerShieldsAsync(solarSystemId, cancellationToken);
        return shields.Count > 0 && await IsInsideControlTowerShieldAsync(
            pilot, shipId, solarSystemId, shields, cancellationToken);
    }

    /// <summary>
    /// The grant of a P.A.N.I.C. (track C6, protocol 43): the core starts when the host has given its fleet the buffs. The source is always granted; another ship the
    /// simulation found in reach is granted when it is the source's fleet's and a subcapital mining or hauling ship (<see cref="PanicGroups"/>). The grant lasts the
    /// core's duration (panicDuration), and a granted ship's running modules that its buffs block (activationBlocked) are stopped.
    /// </summary>
    private async Task<WorkOutcome> SettlePanicAsync(WorkCycle cycle)
    {
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
        if (ship is null || ship.Ship.Id != cycle.ShipId || ship.Ship.LocationId != cycle.SolarSystemId || ship.Ship.Flag != CharacterShips.InSpaceFlag)
        {
            return WorkOutcome.Refuse("NO_SHIP");
        }
        var duration = ship.World.Get(cycle.OwnerId, Eve.Services.Space.Effects.ModuleAttributes.PanicDuration);
        var buffs = PanicBuffs.Of(_statics);
        if (buffs.Count == 0 || !(duration > 0))
        {
            return WorkOutcome.Refuse("INVALID_PROFILE");
        }
        var now = (double)_nowUnixMs();
        var pilots = new Dictionary<long, ClientSession> { [cycle.ShipId] = session };
        foreach (var candidate in cycle.Recipients ?? [])
        {
            if (_space.SessionOfShip(candidate) is { } pilot)
            {
                pilots.TryAdd(candidate, pilot);
            }
        }
        var fleets = new Dictionary<long, long>();
        foreach (var character in pilots.Values.Select(pilot => pilot.CharacterId ?? 0).Where(id => id > 0).Distinct())
        {
            if (_space.FleetIdOfAsync is { } fleetOf && await fleetOf(character, CancellationToken.None) is { } fleet)
            {
                fleets[character] = fleet;
            }
        }
        // the group of each other ship: the grant reaches only the subcapital mining and hauling ships
        var groups = new Dictionary<long, int>();
        foreach (var (candidate, pilot) in pilots.Where(pair => pair.Key != cycle.ShipId))
        {
            if (await _ships.LoadAsync(pilot.CharacterId ?? 0) is { } other)
            {
                groups[candidate] = _statics.Type(other.Ship.TypeId)?.GroupId ?? 0;
            }
        }
        var ships = pilots.Select(pair => new BurstShip(pair.Key, pair.Value.CharacterId ?? 0, 0)).ToList();
        var reached = BurstRecipients.Resolve(
            new BurstShip(cycle.ShipId, cycle.CharacterId, 0),
            ships,
            Math.Max(0d, ship.World.Get(cycle.OwnerId, CommandBurstEffect.RangeAttribute)),
            _ => 0d,
            (first, second) => fleets.TryGetValue(first, out var fleetOfFirst) && fleets.TryGetValue(second, out var fleetOfSecond) && fleetOfFirst == fleetOfSecond,
            eligible: candidate => candidate.ShipId == cycle.ShipId || (groups.TryGetValue(candidate.ShipId, out var group) && PanicGroups.Allows(group)));
        foreach (var target in reached)
        {
            foreach (var (collection, value) in buffs)
            {
                _space.Buffs.Apply(target, collection, value, now, duration);
            }
        }
        foreach (var target in reached)
        {

            _space.Buffs.Immobilize(target, now + duration);
            _sim.Command((ulong)pilots[target].SessionId, SimCommands.Stop, []);
            await TellBuffsAsync(pilots[target], target, now);
            StopBlockedLater(pilots[target]);
        }
        return new WorkOutcome(true, "");
    }

    /// <summary>
    /// Stops the modules a grant blocks, off the settlement: the pilot whose core asked for the grant may still hold its request gate until the grant is answered.
    /// </summary>
    private void StopBlockedLater(ClientSession pilot) => _ = Task.Run(async () =>
    {
        try
        {
            await _space.EndBlockedModulesAsync(pilot, CancellationToken.None);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            _log?.Invoke($"the modules a grant blocks were not stopped for session {pilot.SessionId}: {error.Message}");
        }
    });

    /// <summary>
    /// The buffs whose time ran out: each ship that lost one is sent again with what it still has, and its pilot is told its buff state (the periodic sweep
    /// of the containers, <see cref="SweepAsync"/>).
    /// </summary>
    internal async Task ExpireBuffsAsync(long nowUnixMs)
    {
        await _space.PruneBuffRecordAsync(nowUnixMs);
        foreach (var shipId in _space.Buffs.Prune(nowUnixMs))
        {
            if (_space.SessionOfShip(shipId) is { } pilot)
            {
                await TellBuffsAsync(pilot, shipId, nowUnixMs);
            }
        }
    }

    /// <summary>
    /// A ship whose buffs changed is sent again with them, its running modules are refreshed (their numbers change while they run), and its pilot is told the
    /// buff state the ship has at <paramref name="now"/> (<c>OnDbuffUpdated</c>, the simulation tells the client).
    /// </summary>
    private async Task TellBuffsAsync(ClientSession pilot, long shipId, double now)
    {
        await _space.KeepBuffsAsync(shipId, now);
        await _space.BuffedAsync(pilot, CancellationToken.None);
        var entries = _space.Buffs.StateOf(shipId, now)
            .Select(state => new SimBuffEntry(state.CollectionId, state.Value, Math.Max(0d, state.ExpiresAtMs - now)))
            .ToArray();
        _log?.Invoke($"command burst buff state sent: session {pilot.SessionId}; ship {shipId}; entries [{string.Join(",", entries.Select(entry => $"{entry.CollectionId}:{entry.Value}:{entry.RemainingMs:F0}"))}]");
        _sim.BuffState((ulong)pilot.SessionId, shipId, entries);
    }
}
