using Eve.Macho;

namespace Eve.Services.Dogma.Tests;

/// <summary>The locks of space as a test plays them: what the dogma service asks is kept, and what it is answered is what the test says.</summary>
internal sealed class FakeTargeting : ISpaceTargeting
{
    public List<(TargetingOrder Order, long[] Targets)> Asked { get; } = [];

    /// <summary>What every order is answered with; null when the session has no ship in space.</summary>
    public TargetingResult? Answer { get; set; } = new(true, false, []);

    public Task<TargetingResult?> OrderAsync(ClientSession session, TargetingOrder order, IReadOnlyList<long> targets, CancellationToken cancellationToken)
    {
        Asked.Add((order, [.. targets]));
        return Task.FromResult(Answer);
    }
}
