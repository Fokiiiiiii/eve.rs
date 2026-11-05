using Eve.Space;

namespace Eve.Services.Space;

public sealed partial class SpaceSessions
{
    /// <summary>
    /// The walk of a pilot through an acceleration gate into the room the gate leads to (<see cref="SimCommands.AccelerationGateTransit"/>): the simulation
    /// checks the pilot's reach to the gate and that the ship can warp, then begins the warp with the modules stopped and the locks let go.
    /// </summary>
    public async Task<CommandReply> AccelerationGateTransitAsync(ClientSession session, long gateId, double[] destination, CancellationToken cancellationToken)
    {
        var reply = await _sim.CommandAsync((ulong)session.SessionId, SimCommands.AccelerationGateTransit, [gateId, destination[0], destination[1], destination[2]], cancellationToken);
        if (!reply.Accepted)
            _log?.Invoke($"gate transit of session {session.SessionId} through {gateId} refused: {reply.Reason}");
        return reply;
    }
}
