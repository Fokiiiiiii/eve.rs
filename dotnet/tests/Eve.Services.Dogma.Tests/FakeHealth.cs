namespace Eve.Services.Dogma.Tests;

/// <summary>The health of a ship in space as a test plays it: what the simulation would say it has taken, and whether a heal was asked for.</summary>
internal sealed class FakeHealth : ISpaceHealth
{
    /// <summary>What the ship in space has taken; null when the session has no ship in space.</summary>
    public SpaceCondition? Condition { get; set; }

    public int Healed { get; private set; }

    public Task<SpaceCondition?> ConditionAsync(ClientSession session, CancellationToken cancellationToken) => Task.FromResult(Condition);

    public Task<bool> HealAsync(ClientSession session, CancellationToken cancellationToken)
    {
        if (Condition is null)
        {
            return Task.FromResult(false);
        }
        Healed++;
        Condition = new SpaceCondition(0, 1, 1, 0);
        return Task.FromResult(true);
    }
}
