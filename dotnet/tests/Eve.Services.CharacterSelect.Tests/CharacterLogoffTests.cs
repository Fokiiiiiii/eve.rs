using Eve.Characters;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;

namespace Eve.Services.CharacterSelect.Tests;

/// <summary>
/// Logging the character off to the selection screen (<c>userSvc.UserLogOffCharacter</c>): the session drops the character
/// in one session change, the logoff time is recorded, the character is no longer online, the entry hooks give up their
/// state first, and the connection stays open to select a character again.
/// </summary>
public class CharacterLogoffTests
{
    private static readonly PyValue Doll = new PyDict([new(new PyStr("recorded"), new PyInt(1))]);
    private static readonly PyValue Portrait = new PyDict([new(new PyStr("recorded"), new PyInt(2))]);

    /// <summary>Records what each hook saw: the character, and whether the session still held it when the hook ran.</summary>
    private sealed class LeavingRecorder : ICharacterEntryHook
    {
        public List<(long Character, long? SessionCharacter)> Leaving { get; } = [];

        public ValueTask OnCharacterLeavingAsync(ClientSession session, long characterId, CancellationToken cancellationToken)
        {
            Leaving.Add((characterId, session.CharacterId));
            return ValueTask.CompletedTask;
        }

        public ValueTask OnCharacterEnteredAsync(ClientSession session, long characterId, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;
    }

    private static async Task<long> CreateAsync(Harness h, ClientSession session, string name)
    {
        var id = await h.CallAsync(session, "CreateCharacterWithDoll",
            new PyStr(name), new PyInt(1), new PyInt(1), new PyInt(1), new PyInt(10), Doll, Portrait, new PyInt(35), PyNone.Instance, PyNone.Instance);
        return ((PyInt)id).Value;
    }

    private static CharacterLogoff LogoffFor(Harness h, IReadOnlyList<ICharacterEntryHook> hooks) =>
        new(h.Characters, h.Online, hooks, () => Harness.ClockUnixMs);

    private static List<SessionChange> ChangesOf(PyValue packet)
    {
        var payload = (PyTuple)((PyTuple)((PyObject)packet).Args).Items[4];
        var dict = (PyDict)((PyTuple)payload.Items[1]).Items[1];
        return [.. dict.Entries.Select(e => new SessionChange(JsSemantics.StringValue(e.Key)!, ((PyTuple)e.Value).Items[0], ((PyTuple)e.Value).Items[1]))];
    }

    [PgFact]
    public async Task Logging_off_unbinds_the_session_in_one_change_and_records_the_logoff_time()
    {
        await using var h = await Harness.CreateAsync();
        var (session, sink) = await h.LoginAsync("logoff-pilot");
        var id = await CreateAsync(h, session, "Logoff Pilot");
        await h.CallAsync(session, "SelectCharacterID", new PyInt(id));
        sink.Sent.Clear();

        Assert.True(await LogoffFor(h, []).LogOffAsync(session, CancellationToken.None));

        Assert.Null(session.CharacterId);
        Assert.False(h.Online.IsOnline(id));
        var packet = Assert.Single(sink.Sent);
        var changes = ChangesOf(packet);
        Assert.Equal(PyNone.Instance, changes.Single(change => change.Name == "charid").New);
        Assert.Equal(PyNone.Instance, changes.Single(change => change.Name == "solarsystemid2").New);
        Assert.DoesNotContain(changes, change => change.Name == "userid");
        var saved = await h.Characters.GetAsync(id);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(Harness.ClockUnixMs), saved!.LastLogoffAt);
    }

    [PgFact]
    public async Task The_connection_stays_open_and_the_character_can_be_selected_again()
    {
        await using var h = await Harness.CreateAsync();
        var (session, _) = await h.LoginAsync("again-pilot");
        var id = await CreateAsync(h, session, "Again Pilot");
        await h.CallAsync(session, "SelectCharacterID", new PyInt(id));
        await LogoffFor(h, []).LogOffAsync(session, CancellationToken.None);

        await h.CallAsync(session, "SelectCharacterID", new PyInt(id));

        Assert.Equal(id, session.CharacterId);
        Assert.True(h.Online.IsOnline(id));
    }

    [PgFact]
    public async Task Entry_hooks_see_the_character_still_in_the_session_before_it_is_unbound()
    {
        var recorder = new LeavingRecorder();
        await using var h = await Harness.CreateAsync(entryHooks: [recorder]);
        var (session, _) = await h.LoginAsync("hook-pilot");
        var id = await CreateAsync(h, session, "Hook Pilot");
        await h.CallAsync(session, "SelectCharacterID", new PyInt(id));

        await LogoffFor(h, [recorder]).LogOffAsync(session, CancellationToken.None);

        var leaving = Assert.Single(recorder.Leaving);
        Assert.Equal((id, (long?)id), leaving);
        Assert.Null(session.CharacterId);
    }

    [PgFact]
    public async Task A_session_without_a_character_logs_off_as_done_and_sends_nothing()
    {
        await using var h = await Harness.CreateAsync();
        var (session, sink) = await h.LoginAsync("nobody");

        Assert.True(await LogoffFor(h, []).LogOffAsync(session, CancellationToken.None));

        Assert.Empty(sink.Sent);
    }
}
