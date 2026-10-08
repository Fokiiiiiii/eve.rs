using Eve.Marshal;
using Eve.Services;

namespace Eve.Services.Core;

/// <summary>Character login milestones are not persisted in this host yet; callbacks can own the timer and reward state.</summary>
public sealed class MilestoneMgrService(
    Func<ServiceCall, CancellationToken, ValueTask<PyValue?>>? handler = null) : IService
{
    public string Name => "milestoneMgr";
    public bool HasMethod(string method) => method is "ProcessCharacterLogon" or "ClaimRewards";

    public async ValueTask<PyValue> CallAsync(ServiceCall call)
    {
        if (!HasMethod(call.Method)) return PyNone.Instance;
        call.Cancellation.ThrowIfCancellationRequested();
        return handler is null
            ? PyNone.Instance
            : await handler(call, call.Cancellation) ?? PyNone.Instance;
    }
}