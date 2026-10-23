using Eve.Marshal;
using Eve.Services;

namespace Eve.Host;

public sealed class IndustryInventoryChangePublisher(IService inner, IndustryMonitorRpcService monitors) : IService, IBoundObjectReleaseHandler
{
    private static readonly HashSet<string> MutationMethods = new(StringComparer.Ordinal)
    {
        "Add",
        "MultiAdd",
        "MultiMerge",
        "StackAll",
        "RepackageItems",
    };

    internal IService Inner => inner;

    public string Name => inner.Name;
    public bool HasMethod(string method) => inner.HasMethod(method);

    public async ValueTask<PyValue> CallAsync(ServiceCall call)
    {
        var result = await inner.CallAsync(call);
        if (MutationMethods.Contains(call.Method))
            await monitors.InventoryChangedAsync(call.Session, call.Cancellation);
        return result;
    }

    public void OnBoundObjectReleased(string oid, ClientSession? session)
    {
        if (inner is IBoundObjectReleaseHandler releaseHandler)
            releaseHandler.OnBoundObjectReleased(oid, session);
    }
}
