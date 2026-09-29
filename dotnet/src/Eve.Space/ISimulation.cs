namespace Eve.Space;

/// <summary>
/// The answer to a command the caller waits for (a <c>Dock</c>, a warp): accepted (with the time it was), refused (with why), or, for a dock from
/// too far, not possible from here: the ship approaches the station and <paramref name="ApproachDistance"/> is how far off it was. A jump that
/// was accepted says where the ship is going (<paramref name="DestinationSystem"/>, <paramref name="DestinationGate"/>).
/// </summary>
public sealed record CommandReply(bool Accepted, long At, string? Reason, double? ApproachDistance = null, int? DestinationSystem = null, long DestinationGate = 0)
{
    /// <summary>The answer to a targeting order: what the ship has locked (or who has it locked) and whether a lock is going.</summary>
    public IReadOnlyList<long> Targets { get; init; } = [];

    public bool Pending { get; init; }

    /// <summary>What a refused targeting order says of the target: its type, and (out of range only) the range and the distance.</summary>
    public int TargetType { get; init; }

    public double DesiredRange { get; init; }

    public double ActualDistance { get; init; }
}

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

    /// <summary>Gives a command that is answered to the caller (a dock, a warp) and waits for the simulation to accept or refuse it.</summary>
    Task<CommandReply> CommandAsync(ulong sessionId, byte code, double[] args, CancellationToken cancellationToken = default);

    void ShipChanged(ulong sessionId, SimShip ship);
}
