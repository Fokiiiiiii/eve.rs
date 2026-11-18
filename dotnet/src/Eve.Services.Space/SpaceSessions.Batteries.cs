namespace Eve.Services.Space;

public sealed partial class SpaceSessions
{
    /// <summary>
    /// The POS battery targeting of the host's containers (the control grant, the battery's state and its numbers, and the simulation's lock), set when
    /// the containers are made. Null: no battery targeting, so a battery order is refused.
    /// </summary>
    public Func<ClientSession, long, TargetingOrder, long, CancellationToken, Task<TargetingResult>>? BatteryTargetingHandler { get; set; }

    /// <inheritdoc />
    public async Task<TargetingResult?> BatteryOrderAsync(ClientSession session, long batteryId, TargetingOrder order, long target, CancellationToken cancellationToken)
    {
        if (BatteryTargetingHandler is not { } handler)
        {
            return null;
        }
        return await handler(session, batteryId, order, target, cancellationToken).ConfigureAwait(false);
    }
}
