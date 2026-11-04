using Eve.Services.Ships;
using Eve.Ships;

namespace Eve.Services.Dogma.Tests;

/// <summary>The rounds of the guns that run as a test plays them: what the dogma service asks is kept, and what it is answered is what the test says.</summary>
internal sealed class FakeRounds : ISpaceRounds
{
    public event Action<SpentGun>? GunRanDry;

    /// <summary>A reloading gun ran dry (what the space layer raises).</summary>
    public void RunDry(SpentGun gun) => GunRanDry?.Invoke(gun);

    public List<string> Asked { get; } = [];

    private readonly TaskCompletionSource _completed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task AdjustmentCompleted => _completed.Task;

    /// <summary>What every change of rounds is answered with: a gun that does not run, unless the test says it does.</summary>
    public GunRounds Answer { get; set; } = GunRounds.Idle;

    public async Task<T> AdjustAsync<T>(ClientSession session, long moduleId, int chargeType, RoundsChange change, Func<GunRounds, Task<T>> afterwards, CancellationToken cancellationToken)
    {
        Asked.Add($"{moduleId} {chargeType} {change}");
        try
        {
            return await afterwards(Answer);
        }
        finally
        {
            _completed.TrySetResult();
        }
    }

    public void GiveBack(ClientSession session, long moduleId, long rounds) => Asked.Add($"give back {moduleId} {rounds}");
}

/// <summary>The time a reload takes, as a test has it pass: it passes for every reload at once when the test releases it.</summary>
internal sealed class ManualDelay
{
    private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public List<TimeSpan> Asked { get; } = [];

    public Task WaitAsync(TimeSpan time, CancellationToken cancellationToken)
    {
        lock (Asked)
        {
            Asked.Add(time);
        }
        return _gate.Task;
    }

    public void Release() => _gate.TrySetResult();
}
