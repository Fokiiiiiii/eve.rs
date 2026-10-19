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
        var buffs = CommandBurstBuffs.Of(ship.World, _statics, cycle.OwnerId);
        var duration = CommandBurstBuffs.DurationOf(ship.World, cycle.OwnerId);
        if (buffs.Count == 0 || duration <= 0)
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
        var ships = pilots.Select(pair => new BurstShip(pair.Key, pair.Value.CharacterId ?? 0, 0)).ToList();
        // the simulation measured the reach (the ships in range), so the distance here is none: the host only decides which of them are the fleet's
        var reached = BurstRecipients.Resolve(
            new BurstShip(cycle.ShipId, cycle.CharacterId, 0),
            ships,
            Math.Max(0d, ship.World.Get(cycle.OwnerId, CommandBurstEffect.RangeAttribute)),
            _ => 0d,
            (first, second) => fleets.TryGetValue(first, out var fleetOfFirst) && fleets.TryGetValue(second, out var fleetOfSecond) && fleetOfFirst == fleetOfSecond);
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
        return new WorkOutcome(true, "");
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
            // the wiki: the protected ships cannot move, so each is held still for the grant's duration and stopped now
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
        _sim.BuffState((ulong)pilot.SessionId, shipId, entries);
    }
}
