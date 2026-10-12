using Eve.Marshal;
using Eve.Services;

namespace Eve.Services.Core;

/// <summary>
/// The client's <c>nes_intro</c> (the New Eden store introduction): its state of a character, and skipping it. The state is what the
/// reference gives: unset unless the character's tutorial entry is on, else the stored state. The storyline that moves a character to
/// active is not built, so an introduction is never active here and skipping changes nothing (the reference answers the same).
/// </summary>
[Service("nes_intro")]
public sealed partial class NesIntroService(
    Func<long, CancellationToken, Task<int>> readStoredState,
    Func<long, CancellationToken, Task<bool>> skipActive,
    Func<long, CancellationToken, Task<AirNpeCharacterState?>> readCharacter,
    Func<string> newCharacterExperienceMode,
    CharacterSessions? sessions)
{
    [Method("get_nes_intro_state")]
    public async ValueTask<PyValue> GetNesIntroState(ServiceCall call)
    {
        var characterId = CharacterId(call);
        var character = characterId > 0 ? await readCharacter(characterId, call.Cancellation) : null;
        var enabled = character is not null && newCharacterExperienceMode() == "tutorial" && character.EntryMode == "tutorial";
        return new PyInt(enabled ? await readStoredState(characterId, call.Cancellation) : Unset);
    }

    /// <summary>Answers whether the character exists; a skip of an introduction that is not active is answered as done, as the reference does.</summary>
    [Method("skip_nes_intro")]
    public async ValueTask<PyValue> SkipNesIntro(ServiceCall call)
    {
        var characterId = CharacterId(call);
        if (characterId <= 0 || await readCharacter(characterId, call.Cancellation) is null) return PyValue.Of(false);
        if (await skipActive(characterId, call.Cancellation))
            sessions?.Of(characterId)?.SendNotification("OnNesIntroStateChanged", "clientID", new PyInt(characterId), new PyInt(Skipped));
        return PyValue.Of(true);
    }

    /// <summary>The reference's states (<c>storylines/common/nes_intro_constants.py</c>).</summary>
    private const int Unset = 0;
    private const int Skipped = 3;

    private static long CharacterId(ServiceCall call)
    {
        var direct = Positive(call.Arg(0));
        return direct > 0 ? direct : Positive(call.Session.Get("charid"));
    }

    private static long Positive(PyValue? value) => value switch
    {
        PyInt integer when integer.Value > 0 => integer.Value,
        PyLong integer when integer.Value > 0 => integer.Value,
        _ => 0,
    };
}
