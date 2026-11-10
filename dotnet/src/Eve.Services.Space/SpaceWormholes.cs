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
/// starts or a ship arrives in it, publishes the opening and collapse of a connection, and runs a pilot's jump through a wormhole end: the mass
/// of the ship is checked and taken from the wormhole (<see cref="WormholeWorld.TransitAsync"/>), and the simulation takes the jump as it takes a stargate's.
/// </summary>
public sealed partial class SpaceSessions
{

    public Func<int, IReadOnlyList<Eve.Dogma.EffectDef>>? SystemEnvironment { get; set; }

    /// <summary>The environment effects of the system the ship is in, from the pilot that flies it (no pilot, no environment).</summary>
    private IReadOnlyList<Eve.Dogma.EffectDef> EnvironmentOf(long shipId)
    {
        var flown = _pilots.Values.FirstOrDefault(pilot => pilot.ShipId == shipId);
        var offline = _abyssalOfflineResidents.GetValueOrDefault(shipId);
        var scene = flown?.SimulationScene ?? offline?.Scene;
        if (scene is null) return [];
        if (scene.Value.InstanceId > 0)
        {
            var contentId = flown?.AbyssalContentId ?? offline?.ContentId;
            var weather = _abyssalEnvironmentByScene.TryGetValue(scene.Value, out var effects)
                ? effects : contentId is not null ? AbyssalEnvironmentEffects?.Invoke(contentId, scene.Value.LocationId) ?? [] : [];
            return [.. weather, .. LocalAbyssalEffects(scene.Value, shipId)];
        }
        return SystemEnvironment?.Invoke(scene.Value.LocationId) ?? [];
    }

    /// <summary>
    /// The ship is given to the simulation again after it changes system: its buffs are synced to the system it is in now (the environment of its
    /// wormhole), and the simulation flies it with the ship's attributes from then on (its spec is kept from when it entered).
    /// </summary>
    private async Task RefreshEnvironmentAsync(ClientSession session, long characterId, long shipId)
    {
        try
        {
            if (await _ships.LoadAsync(characterId, CancellationToken.None) is not { } flown || flown.Ship.Id != shipId)
                return;
            await SyncBuffsAsync(flown.World, shipId, CancellationToken.None);
            _sim.ShipChanged((ulong)session.SessionId, SimShips.Of(flown, _statics, await SecurityStatusAsync(characterId, CancellationToken.None)));
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            _log?.Invoke($"the environment of ship {shipId} could not be given to the simulation: {error.Message}");
        }
    }

    /// <summary>The wormhole world, or null when the host has no client data for it (then no wormhole exists).</summary>
    public WormholeWorld? Wormholes { get; private set; }

    /// <summary>Connects the Host-owned world to the simulation, including the scheduler's retryable state-delivery path.</summary>
    public void BindWormholes(WormholeWorld world)
    {
        ArgumentNullException.ThrowIfNull(world);
        if (ReferenceEquals(Wormholes, world))
            return;
        if (Wormholes is { } previous)
        {
            previous.Changed -= PublishWormholeChange;
            previous.StateChanged -= PublishWormholeStateChangeAsync;
            previous.SlimStateSyncRequested -= PublishWormholeStateChangeForRetryAsync;
        }
        Wormholes = world;
        world.Changed += PublishWormholeChange;
        world.StateChanged += PublishWormholeStateChangeAsync;
        world.SlimStateSyncRequested += PublishWormholeStateChangeForRetryAsync;
    }

    private readonly ConcurrentDictionary<long, SemaphoreSlim> _wormholeTransitGates = new();

    public static SimPlaceWormhole ToSim(WormholeEnd end) => new(end.BodyId, end.SolarSystemId, end.Name, end.TypeId, end.GroupId, end.Position, end.Radius,
        end.DestinationSystemId, end.DestinationEnd, end.DestinationClass, end.NebulaTypeId, end.AgeState, end.Size,
        end.MaxShipJumpMassCategory, end.DestinationPosition, end.DestinationRadius, end.MaxJumpMass);

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

    /// <summary>Publishes a committed age or mass-state change to the slim item of both active ends.</summary>
    public async Task PublishWormholeStateChangeAsync(long connectionId, CancellationToken cancellationToken = default)
    {
        if (_sim is null || Wormholes is not { } world)
            return;
        try
        {
            await ApplyWormholeStateChangeAsync(world, connectionId, waitForWrite: false, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            _log?.Invoke($"the slim state of wormhole connection {connectionId} could not be sent to the simulation: {error.Message}");
        }
    }

    /// <summary>A durable state job uses this path so a failed DB read or simulation send reaches the Scheduler for retry.</summary>
    public Task PublishWormholeStateChangeForRetryAsync(long connectionId, CancellationToken cancellationToken = default) =>
        Wormholes is { } world ? ApplyWormholeStateChangeAsync(world, connectionId, waitForWrite: true, cancellationToken) : Task.CompletedTask;

    private async Task ApplyWormholeStateChangeAsync(WormholeWorld world, long connectionId, bool waitForWrite, CancellationToken cancellationToken)
    {
        if (_sim is null)
            return;
        foreach (var end in await world.EndsForConnectionAsync(connectionId, cancellationToken))
        {
            var age = new SimSlimInt(end.AgeState);
            var size = new SimSlimReal(end.Size);
            if (waitForWrite)
            {
                if (!await _sim.TrySetSlimFieldAsync(end.BodyId, "wormholeAge", age, cancellationToken).ConfigureAwait(false))
                    throw new SimUnavailableException($"the simulation link could not queue the wormholeAge update for end {end.BodyId}");
                if (!await _sim.TrySetSlimFieldAsync(end.BodyId, "wormholeSize", size, cancellationToken).ConfigureAwait(false))
                    throw new SimUnavailableException($"the simulation link could not queue the wormholeSize update for end {end.BodyId}");
            }
            else
            {
                _sim.SetSlimField(end.BodyId, "wormholeAge", age);
                _sim.SetSlimField(end.BodyId, "wormholeSize", size);
            }
        }
    }

    /// <summary>The polarization of an end for a character, or null when it has none (<c>GetWormholePolarization</c>).</summary>
    public async Task<WormholePolarization?> PolarizationOfAsync(long endId, long characterId, CancellationToken cancellationToken = default)
    {
        var now = _nowUnixMs();
        var until = Wormholes is { } world
            ? await world.PolarizedUntilAsync(endId, characterId, now, cancellationToken)
            : null;
        return until is > 0
            ? new WormholePolarization(until.Value, (int)Math.Ceiling((until.Value - now) / 1000.0))
            : null;
    }

    /// <summary>
    /// <c>wormholeMgr.WormholeJump(itemID)</c>: the pilot goes into the wormhole end it picked. The ship's mass is checked against the wormhole (refused
    /// over its individual limit) and taken from it when the simulation takes the jump; a wormhole that empties collapses behind the ship, and both its
    /// ends are taken out. The simulation takes the jump as it takes a stargate's (<see cref="JumpAsync"/>).
    /// Jumps through this connection are serialized until the accepted jump's mass debit and collapse state are committed.
    /// </summary>
    public async Task<PyValue> WormholeJumpAsync(ClientSession session, long endId, string? boundObjectId, CancellationToken cancellationToken)
    {
        RequirePublicMoveSettled(session);
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
        var connectionId = endId / 2;
        var transitGate = _wormholeTransitGates.GetOrAdd(connectionId, static _ => new SemaphoreSlim(1, 1));
        // This gate avoids duplicate database work within one Host. AttemptJumpAsync also locks the connection row, which serializes other Hosts.
        await transitGate.WaitAsync(cancellationToken);
        try
        {
            var now = _nowUnixMs();
            var mass = await ShipMassAsync(pilot, cancellationToken);
            CommandReply? reply = null;
            WormholeJumpAttempt attempt;
            try
            {
                attempt = await world.AttemptJumpAsync(endId, pilot.SolarSystemId, pilot.CharacterId, mass, now, async token =>
                {
                    reply = await _sim.CommandAsync((ulong)session.SessionId, SimCommands.Jump, [endId, 0], token);
                    return reply.Accepted && reply.DestinationSystem is not null;
                }, cancellationToken);
            }
            catch (SimUnavailableException error)
            {
                _log?.Invoke($"wormhole jump of session {session.SessionId}: {error.Message}");
                throw JumpRefused("SCENE_NOT_FOUND");
            }

            switch (attempt.Rejection)
            {
                case WormholeJumpRejection.Collapsed:
                case WormholeJumpRejection.Expired:
                    session.SendNotification("OnWormholeJumpCancel", "clientID");
                    return PyNone.Instance;
                case WormholeJumpRejection.Polarized:
                    throw Notify("You are polarized to this wormhole: you cannot go into it from this side yet.");
                case WormholeJumpRejection.ShipTooMassive:
                    throw Notify("Your ship is too large for this wormhole.");
                case WormholeJumpRejection.UnknownEnd:
                case WormholeJumpRejection.WrongSystem:
                    throw JumpRefused("STARGATE_NOT_FOUND");
                case WormholeJumpRejection.SimulationRefused:
                    _log?.Invoke($"wormhole jump of session {session.SessionId} through {endId} refused: {reply?.Reason}");
                    throw JumpRefused(string.IsNullOrWhiteSpace(reply?.Reason) ? "SCENE_NOT_FOUND" : reply.Reason);
            }

            var destination = reply?.DestinationSystem ?? throw JumpRefused("SCENE_NOT_FOUND");
            await CancelShipSelfDestructAsync(pilot.ShipId, cancellationToken);
            await PlaceWormholeEndsAsync(destination, cancellationToken);
            SendJumpQueueUpdate(session, pilot.CharacterId, destination);
            return Py.Tuple(boundObjectId is null ? None : new PyStr(boundObjectId), new PyLong(Py.FileTimeFromUnixMs(now + SessionChangeCooldownMs)));
        }
        finally
        {
            transitGate.Release();
        }
    }
}
