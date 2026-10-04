using Eve.Marshal;

namespace Eve.Services.Core;

/// <summary>
/// Read-only callbacks for the structure directory. The host owns the persisted data source and
/// returns its client-compatible PyValue without this RPC adapter changing row order or field types.
/// </summary>
public interface IStructureDirectoryReadCallbacks
{
    ValueTask<PyValue> GetJumpBridgesWithMyAccessAsync(ServiceCall call);

    ValueTask<PyValue> GetNearbyJumpBridgesAsync(ServiceCall call);

    ValueTask<PyValue> GetStructureDescriptionAsync(ServiceCall call);
}

/// <summary>Read-only RPC adapter for the client's structureDirectory service.</summary>
public sealed class StructureDirectoryService(IStructureDirectoryReadCallbacks read) : IService
{
    private readonly IStructureDirectoryReadCallbacks _read =
        read ?? throw new ArgumentNullException(nameof(read));

    public string Name => "structureDirectory";

    public bool HasMethod(string method) => method is
        "GetJumpBridgesWithMyAccess" or
        "GetNearbyJumpBridges" or
        "GetStructureDescription";

    public ValueTask<PyValue> CallAsync(ServiceCall call) => call.Method switch
    {
        "GetJumpBridgesWithMyAccess" => _read.GetJumpBridgesWithMyAccessAsync(call),
        "GetNearbyJumpBridges" => _read.GetNearbyJumpBridgesAsync(call),
        "GetStructureDescription" => _read.GetStructureDescriptionAsync(call),
        _ => ValueTask.FromResult<PyValue>(PyNone.Instance),
    };
}
