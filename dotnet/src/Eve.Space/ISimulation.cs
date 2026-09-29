namespace Eve.Space;

/// <summary>
/// The answer to a <c>Dock</c>: accepted (with the time it was), refused (with why), or not possible from here, in which case the ship approaches
/// the station and <paramref name="ApproachDistance"/> is how far off it was.
/// </summary>
public sealed record DockReply(bool Accepted, long At, string? Reason, double? ApproachDistance = null);

/// <summary>The simulation cannot be reached (it is not running, or the link broke).</summary>
public sealed class SimUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>What the service host asks of the space simulation and hears back from it.</summary>
public interface ISimulation
{
    /// <summary>Whether the simulation is reachable now.</summary>
    bool IsConnected { get; }

    /// <summary>Something the simulation says on its own: a notification for a client, a finished docking, a refused command.</summary>
    event Action<SimMessage>? Received;

    /// <summary>The link broke: every ship the simulation held is gone.</summary>
    event Action? Lost;

    /// <summary>Puts a ship in a solar system. Returns null when it was done and the reason when it was not; throws <see cref="SimUnavailableException"/> when the simulation cannot be reached.</summary>
    Task<string?> EnterAsync(SimEnter enter, CancellationToken cancellationToken = default);

    void Leave(ulong sessionId, LeaveWhy why);

    void Bind(ulong sessionId);

    void UpdateState(ulong sessionId);

    /// <summary>A movement command (<see cref="SimCommands"/>); the simulation answers it with <see cref="SimAccepted"/> or <see cref="SimRefused"/> (<see cref="Received"/>).</summary>
    void Command(ulong sessionId, byte code, params double[] args);

    /// <summary>Asks the ship to dock and waits for the simulation to accept or refuse.</summary>
    Task<DockReply> DockAsync(ulong sessionId, long stationId, CancellationToken cancellationToken = default);

    void ShipChanged(ulong sessionId, SimShip ship);
}
