using Eve.Space;

namespace Eve.Services.Space.Tests;

/// <summary>The simulation as a test plays it: it records what the host asks and says what the test tells it to.</summary>
internal sealed class FakeSim : ISimulation
{
    public bool IsConnected { get; set; } = true;

    public event Action<SimMessage>? Received;

    public event Action? Lost;

    public List<SimMessage> Heard { get; } = [];

    /// <summary>What an <c>Enter</c> is answered with: null (done) or a reason.</summary>
    public string? EnterAnswer { get; set; }

    public DockReply DockAnswer { get; set; } = new(true, 134351527136350000, null);

    public Task<string?> EnterAsync(SimEnter enter, CancellationToken cancellationToken = default)
    {
        if (!IsConnected)
        {
            throw new SimUnavailableException("the simulation is not running");
        }
        Heard.Add(enter);
        return Task.FromResult(EnterAnswer);
    }

    public Task<DockReply> DockAsync(ulong sessionId, long stationId, CancellationToken cancellationToken = default)
    {
        Heard.Add(new SimCommand(sessionId, SimCommands.Dock, [stationId]));
        return Task.FromResult(DockAnswer);
    }

    public void Leave(ulong sessionId, LeaveWhy why) => Heard.Add(new SimLeave(sessionId, why));

    public void Bind(ulong sessionId) => Heard.Add(new SimBind(sessionId));

    public void UpdateState(ulong sessionId) => Heard.Add(new SimUpdateState(sessionId));

    public void Command(ulong sessionId, byte code, params double[] args) => Heard.Add(new SimCommand(sessionId, code, args));

    public void ShipChanged(ulong sessionId, SimShip ship) => Heard.Add(new SimShipChanged(sessionId, ship));

    public void Say(SimMessage message) => Received?.Invoke(message);

    public void Lose() => Lost?.Invoke();
}
