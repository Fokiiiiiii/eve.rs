using Eve.Macho;
using Eve.Space;

namespace Eve.Services.Space;

public sealed partial class SpaceSessions
{
    /// <summary>Read the live public-scene pose atomically for a connected pilot.</summary>
    public async Task<(int SolarSystemId, IReadOnlyList<double> Position, IReadOnlyList<double> Direction)?> PilotPoseAsync(
        ClientSession session, CancellationToken cancellationToken)
    {
        if (!_pilots.TryGetValue((ulong)session.SessionId, out var pilot) || !ReferenceEquals(pilot.Session, session) ||
            pilot.CharacterId != session.CharacterId || pilot.SimulationScene.InstanceId != 0)
            return null;
        var scene = SimSceneKey.SolarSystem(pilot.SolarSystemId);
        var request = new SimGetAbyssalParticipantPose(SimClient.NextRequestId(), scene, pilot.ShipId);
        var reply = await _sim.GetAbyssalParticipantPoseAsync(request, cancellationToken);
        if (reply.RequestId != request.RequestId || reply.Scene != scene || reply.ShipId != pilot.ShipId || reply.Error is not null ||
            reply.Position.Count != 3 || reply.Direction.Count != 3 || reply.Position.Any(x => !double.IsFinite(x)) ||
            reply.Direction.Any(x => !double.IsFinite(x)) || reply.Direction.All(x => x == 0) ||
            !_pilots.TryGetValue((ulong)session.SessionId, out var current) || !ReferenceEquals(current, pilot))
            return null;
        return (pilot.SolarSystemId, reply.Position, reply.Direction);
    }

    /// <summary>Return a tournament pilot to the recorded dock after moving them to its system.</summary>
    public async Task<string?> ReturnTournamentPilotToDockAsync(ClientSession session, int solarSystemId, long dockId,
        long expectedShipId, CancellationToken cancellationToken, string? operationId = null)
    {
        if (session.CharacterId is not > 0 || dockId <= 0 || expectedShipId <= 0)
            return "The recorded tournament destination is invalid.";
        if (session.GetInt("stationid") == dockId || session.GetInt("structureid") == dockId)
        {
            var snapshot = await _ships.LoadAsync(session.CharacterId.Value, cancellationToken);
            return session.GetInt("shipid") == expectedShipId && snapshot is not null &&
                snapshot.Ship.Id == expectedShipId && snapshot.Ship.LocationId == dockId &&
                snapshot.Character.SolarSystemId == solarSystemId
                ? null : "The participant dock state could not be confirmed.";
        }
        if (session.GetInt("shipid") != expectedShipId)
            return "The participant is in a different ship.";
        if (session.GetInt("solarsystemid2") != solarSystemId || !IsInSpace(session))
        {
            var refusal = await RelocateAsync(session, solarSystemId, null, null, cancellationToken, operationId);
            if (refusal is not null) return refusal;
        }
        var id = (ulong)session.SessionId;
        if (!_pilots.TryGetValue(id, out var pilot) || pilot.ShipId != expectedShipId || pilot.SolarSystemId != solarSystemId)
            return "The participant's active ship changed.";
        var condition = await _sim.CommandAsync(id, SimCommands.GetCondition, [], cancellationToken);
        if (!condition.Accepted || condition.Condition is null)
            return "The participant ship condition could not be saved.";
        await FinishDockingAsync(pilot, dockId, condition.Condition);
        if (session.GetInt("stationid") != dockId && session.GetInt("structureid") != dockId)
            return "The participant could not be returned to their dock.";
        _sim.Leave(id, LeaveWhy.Jumped);
        return null;
    }
}
