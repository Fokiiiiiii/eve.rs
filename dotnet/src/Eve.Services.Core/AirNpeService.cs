using Eve.Marshal;
using Eve.Services;

namespace Eve.Services.Core;

public sealed record AirNpeCharacterState(string EntryMode, int StoredState)
{
    public int EffectiveState(string mode) =>
        mode == "tutorial" && EntryMode == "tutorial" ? StoredState : 2;
}

public sealed class AirNpeService(
    Func<string> newCharacterExperienceMode,
    Func<long, CancellationToken, Task<AirNpeCharacterState?>> readCharacterState,
    Func<long, bool, CancellationToken, Task<int?>> markTutorialSkipped) : IService
{
    public string Name => "air_npe";

    public bool HasMethod(string method) => method is
        "is_air_npe_enabled" or
        "get_air_npe_state" or
        "skip_air_npe";

    public ValueTask<PyValue> CallAsync(ServiceCall call)
    {
        if (!HasMethod(call.Method)) return ValueTask.FromResult<PyValue>(PyNone.Instance);
        call.Cancellation.ThrowIfCancellationRequested();
        if (call.Args.Count != 0 || call.Kwargs is not PyNone)
            throw MachoErrors.ThrowUserError("AIR_NPE_ARGUMENTS_INVALID");
        return call.Method switch
        {
            "is_air_npe_enabled" => ValueTask.FromResult<PyValue>(IsEnabled ? PyBool.True : PyBool.False),
            "get_air_npe_state" => GetStateAsync(call),
            "skip_air_npe" => SkipAsync(call),
            _ => ValueTask.FromResult<PyValue>(PyNone.Instance),
        };
    }

    private bool IsEnabled => newCharacterExperienceMode() is "tutorial" or "cinematic";

    private async ValueTask<PyValue> GetStateAsync(ServiceCall call)
    {
        var characterId = CharacterId(call);
        var characterState = characterId > 0
            ? await readCharacterState(characterId, call.Cancellation)
            : null;
        var mode = newCharacterExperienceMode();
        return new PyInt(characterState?.EffectiveState(mode) ?? 2);
    }

    private async ValueTask<PyValue> SkipAsync(ServiceCall call)
    {
        var characterId = CharacterId(call);
        var mode = newCharacterExperienceMode();
        if (characterId > 0 && mode is "tutorial" or "cinematic" &&
            await markTutorialSkipped(characterId, mode == "tutorial", call.Cancellation) is { } state)
            call.Session.SendNotification("OnAirNpeStateChanged", "clientID", new PyInt(characterId), new PyInt(state));
        return PyNone.Instance;
    }

    private static long CharacterId(ServiceCall call)
    {
        var characterId = call.Session.CharacterId.GetValueOrDefault();
        return characterId > 0 ? characterId : Positive(call.Session.Get("charid"));
    }

    private static long Positive(PyValue? value) => value switch
    {
        PyInt integer when integer.Value > 0 => integer.Value,
        PyLong integer when integer.Value > 0 => integer.Value,
        _ => 0,
    };
}
