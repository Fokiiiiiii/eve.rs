using System.Collections.Concurrent;
using Eve.Crimewatch;
using Eve.Events;
using Eve.Inventory;
using Eve.Scheduler;
using Eve.Dogma;
using Eve.Fitting;
using Eve.Macho;
using Eve.Marshal;
using Eve.Npc;
using Eve.Services.Ships;
using Eve.Ships;
using Eve.Space;
using Eve.Services.Space.Pochven;
using Eve.Universe;
using ModuleAttributes = Eve.Services.Space.Effects.ModuleAttributes;
using Eve.Wormholes;

namespace Eve.Services.Space;

/// <summary>
/// The wormholes of J-space in the space service (slice 7): the host places both ends of every connection of a system when the system's scene
/// starts or a ship arrives in it, publishes the opening and collapse of a connection, and runs a pilot's jump through a wormhole end: the mass
/// of the ship is checked and taken from the wormhole (<see cref="WormholeWorld.TransitAsync"/>), and the simulation takes the jump as it takes a stargate's.
/// </summary>
public sealed partial class SpaceSessions
{
    /// <summary>The polarization of a wormhole end in one direction (wiki "Wormhole": five minutes per pilot).</summary>
    private static readonly long PolarizationMs = (long)WormholeRules.Polarization.TotalMilliseconds;

    /// <summary>The wormhole world, or null when the host has no client data for it (then no wormhole exists).</summary>
    public WormholeWorld? Wormholes { get; set; }

    /// <summary>Until when each pilot of a character may not go into a wormhole end again (in memory; the rule is the pilot's, not the account's).</summary>
    private readonly Dictionary<(long Character, long EndId), long> _polarizedUntil = new();

    public static SimPlaceWormhole ToSim(WormholeEnd end) => new(end.BodyId, end.SolarSystemId, end.Name, end.TypeId, end.GroupId, end.Position, end.Radius,
        end.DestinationSystemId, end.DestinationEnd, end.DestinationClass, end.DestinationPosition, end.DestinationRadius, end.MaxJumpMass);

    /// <summary>Places the ends of every active connection with an end in the system (the simulation makes the system's scene for them when it has none).</summary>
    internal async Task PlaceWormholeEndsAsync(int systemId, CancellationToken cancellationToken)
    {
        if (Wormholes is null || _sim is null)
            return;
        foreach (var end in await Wormholes.EndsInSystemAsync(systemId, cancellationToken))
            _sim.PlaceWormhole(ToSim(end));
    }

    /// <summary>A connection opened: both ends are placed; a connection collapsed: both ends are taken out (the simulation ignores an end it does not have).</summary>
    public void PublishWormholeChange(WormholeChange change)
    {
        if (_sim is null || Wormholes is null)
            return;
        if (change.Opened is { } stored)
        {
            _sim.PlaceWormhole(ToSim(Wormholes.EndOf(stored, far: false)));
            _sim.PlaceWormhole(ToSim(Wormholes.EndOf(stored, far: true)));
        }
        else
        {
            _sim.RemoveWormhole(change.ConnectionId * 2);
            _sim.RemoveWormhole(change.ConnectionId * 2 + 1);
        }
    }

    /// <summary>The polarization of an end for a character, or null when it has none (<c>GetWormholePolarization</c>).</summary>
    public WormholePolarization? PolarizationOf(long endId, long characterId)
    {
        var now = _nowUnixMs();
        lock (_polarizedUntil)
        {
            return _polarizedUntil.TryGetValue((characterId, endId), out var until) && until > now
                ? new WormholePolarization(until, (int)Math.Ceiling((until - now) / 1000.0))
                : null;
        }
    }

    /// <summary>
    /// <c>wormholeMgr.WormholeJump(itemID)</c>: the pilot goes into the wormhole end it picked. The ship's mass is checked against the wormhole (refused
    /// over its individual limit) and taken from it when the simulation takes the jump; a wormhole that empties collapses behind the ship, and both its
    /// ends are taken out. The simulation takes the jump as it takes a stargate's (<see cref="JumpAsync"/>).
    /// shortcut: the mass is checked before the simulation is asked and taken after it says yes; another ship that transits in between is not
    /// seen by the first check, so the second transit can be refused after the jump is taken (logged).
    /// </summary>
    public async Task<PyValue> WormholeJumpAsync(ClientSession session, long endId, string? boundObjectId, CancellationToken cancellationToken)
    {
        if (!_pilots.TryGetValue((ulong)session.SessionId, out var pilot))
        {
            throw JumpRefused("NOT_IN_SPACE");
        }
        if (await ShipAttributeAsync(pilot, Eve.Services.Space.Effects.ModuleAttributes.DisallowDriveJumping, cancellationToken))
        {
            throw Notify("You cannot jump at this time.");
        }
        var policy = await MovementPolicyAsync(pilot, PolicyAction.GateJump, cancellationToken);
        if (!policy.Allowed)
        {
            throw MovementRefused(policy, "jump");
        }
        var world = Wormholes ?? throw JumpRefused("STARGATE_NOT_FOUND");
        var end = await world.EndAsync(endId, cancellationToken) ?? throw JumpRefused("STARGATE_NOT_FOUND");
        if (end.SolarSystemId != pilot.SolarSystemId)
        {
            throw JumpRefused("STARGATE_NOT_FOUND");
        }
        var now = _nowUnixMs();
        if (PolarizationOf(endId, pilot.CharacterId) is not null)
        {
            throw Notify("You are polarized to this wormhole: you cannot go into it from this side yet.");
        }
        var connectionId = endId / 2;
        var mass = await ShipMassAsync(pilot, cancellationToken);
        var preview = await world.PreviewAsync(connectionId, mass, cancellationToken) ?? throw JumpRefused("STARGATE_NOT_FOUND");
        if (preview.Outcome == TransitOutcome.Refused)
        {
            throw Notify("Your ship is too large for this wormhole.");
        }

        CommandReply reply;
        try
        {
            reply = await _sim.CommandAsync((ulong)session.SessionId, SimCommands.Jump, [endId, 0], cancellationToken);
        }
        catch (SimUnavailableException error)
        {
            _log?.Invoke($"wormhole jump of session {session.SessionId}: {error.Message}");
            throw JumpRefused("SCENE_NOT_FOUND");
        }
        if (!reply.Accepted || reply.DestinationSystem is not { } destination)
        {
            _log?.Invoke($"wormhole jump of session {session.SessionId} through {endId} refused: {reply.Reason}");
            throw JumpRefused(reply.Reason);
        }

        var changes = new List<WormholeChange>();
        var transit = await world.TransitAsync(connectionId, mass, now, changes.Add, cancellationToken);
        if (transit.Outcome == TransitOutcome.Refused)
        {
            _log?.Invoke($"wormhole {connectionId}: the transit of session {session.SessionId} was refused after the jump was taken (a race, see the shortcut)");
        }
        foreach (var change in changes)
        {
            PublishWormholeChange(change);
        }
        lock (_polarizedUntil)
        {
            _polarizedUntil[(pilot.CharacterId, endId)] = now + PolarizationMs;
        }
        await PlaceWormholeEndsAsync(destination, cancellationToken);
        SendJumpQueueUpdate(session, pilot.CharacterId, destination);
        return Py.Tuple(boundObjectId is null ? None : new PyStr(boundObjectId), new PyLong(Py.FileTimeFromUnixMs(now + SessionChangeCooldownMs)));
    }
}
