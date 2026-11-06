using System.Collections.Concurrent;
using Eve.Marshal;

namespace Eve.Services.Core;

public static class ServerBuildInfo
{
    public const double BootVersion = 24.01;
    public const int BootBuild = 3_396_210;
}

// Compatibility-only manual IService stubs. Do not register these in CoreServices.Create;
// Program registers the backed Eve.Host implementations for runtime dispatch.

public sealed class ShipCosmeticsMgrRpcService(
    Func<ServiceCall, ValueTask<PyValue?>>? handler = null) : IService
{
    public string Name => "shipCosmeticsMgr";
    public bool HasMethod(string method) => method is
        "ActivateSkinLicense" or "ApplySkinToShip" or "GMExpireSkinLicense" or
        "GetEnabledCosmetics" or "GetFirstPartySkinData" or "GiveSkin" or "RemoveSkin";

    public ValueTask<PyValue> CallAsync(ServiceCall call)
    {
        if (!HasMethod(call.Method)) return ValueTask.FromResult<PyValue>(PyNone.Instance);
        return CompatibilityRpc.Invoke(call, handler, () =>
            call.Method == "GetEnabledCosmetics" ? new PyDict([]) : PyNone.Instance);
    }
}

public sealed class CharacterEnergyMgrRpcService(
    Func<long>? nowUnixMs = null,
    Func<ServiceCall, ValueTask<PyValue?>>? handler = null) : IService
{
    private const string StateClass = "characterenergy.characterEnergyState.CharacterEnergyState";
    public string Name => "characterEnergyMgr";
    public bool HasMethod(string method) => method == "GetMyEnergyState";

    public ValueTask<PyValue> CallAsync(ServiceCall call) =>
        !HasMethod(call.Method)
            ? ValueTask.FromResult<PyValue>(PyNone.Instance)
            : CompatibilityRpc.Invoke(call, handler, State);

    private PyValue State()
    {
        var fileTime = Py.FileTimeFromUnixMs((nowUnixMs ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()))());
        return new PyObjectEx(ObjectExKind.Ex1,
            PyTuple.Of(
                new PyToken(StateClass),
                PyTuple.Of(
                    new PyReal(100),
                    new PyLong(fileTime),
                    new PyReal(1),
                    new PyReal(792),
                    new PyReal(0),
                    new PyReal(100))),
            [], []);
    }
}

internal static class CompatibilityRpc
{
    /// <summary>QA operations act only for a session whose server-set account role is staff; anything else skips the handler and any state change.</summary>
    public static bool IsStaff(ServiceCall call) => GameMasterRoles.Has(call.Session, GameMasterRoles.Staff);

    public static async ValueTask<PyValue> Invoke(
        ServiceCall call,
        Func<ServiceCall, ValueTask<PyValue?>>? handler,
        Func<PyValue> fallback)
    {
        call.Cancellation.ThrowIfCancellationRequested();
        return handler is null
            ? fallback()
            : await handler(call) ?? fallback();
    }
}
