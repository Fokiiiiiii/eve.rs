using Eve.Characters;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;

namespace Eve.Services.Core.Tests;

/// <summary>The <c>nes_intro</c> answers: the stored state only under the tutorial entry, and the skip of an active introduction.</summary>
public sealed class NesIntroServiceTests
{
    private const long Pilot = 90000100;

    [Fact]
    public async Task The_state_is_the_stored_one_under_the_tutorial_entry_and_unset_otherwise()
    {
        var tutorial = Service(stored: NesIntroStore.Active, entryMode: "tutorial", newMode: "tutorial");
        Assert.Equal(new PyInt(NesIntroStore.Active), await tutorial.CallAsync(Call("get_nes_intro_state", Pilot)));

        var disabled = Service(stored: NesIntroStore.Active, entryMode: "tutorial", newMode: "disabled");
        Assert.Equal(new PyInt(0), await disabled.CallAsync(Call("get_nes_intro_state", Pilot)));

        var notTutorial = Service(stored: NesIntroStore.Active, entryMode: "disabled", newMode: "tutorial");
        Assert.Equal(new PyInt(0), await notTutorial.CallAsync(Call("get_nes_intro_state", Pilot)));
    }

    [Fact]
    public async Task A_skip_of_an_active_introduction_is_done_and_a_missing_character_is_refused()
    {
        var skipped = new List<long>();
        IService service = new NesIntroService(
            (id, _) => Task.FromResult(NesIntroStore.Active),
            (id, _) => { skipped.Add(id); return Task.FromResult(true); },
            (id, _) => Task.FromResult<AirNpeCharacterState?>(new AirNpeCharacterState("tutorial", 1)),
            () => "tutorial",
            null);

        Assert.Equal(PyValue.Of(true), await service.CallAsync(Call("skip_nes_intro", Pilot)));
        Assert.Equal([Pilot], skipped);

        IService missing = new NesIntroService(
            (id, _) => Task.FromResult(0),
            (id, _) => Task.FromResult(false),
            (id, _) => Task.FromResult<AirNpeCharacterState?>(null),
            () => "tutorial",
            null);
        Assert.Equal(PyValue.Of(false), await missing.CallAsync(Call("skip_nes_intro", Pilot)));
    }

    private static IService Service(int stored, string entryMode, string newMode) => new NesIntroService(
        (id, _) => Task.FromResult(stored),
        (id, _) => Task.FromResult(false),
        (id, _) => Task.FromResult<AirNpeCharacterState?>(new AirNpeCharacterState(entryMode, stored)),
        () => newMode,
        null);

    private static ServiceCall Call(string method, long characterId) => new()
    {
        Method = method,
        Args = [new PyInt(characterId)],
        Session = new ClientSession(),
    };
}
