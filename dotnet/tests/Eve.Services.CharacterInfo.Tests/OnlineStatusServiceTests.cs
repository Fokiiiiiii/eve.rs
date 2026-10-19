using System.Text;
using Eve.Macho;
using Eve.Marshal;

namespace Eve.Services.CharacterInfo.Tests;

public sealed class OnlineStatusServiceTests
{
    private const long Observer = 90_000_001;
    private const long Target = 90_000_002;

    private static ValueTask<PyValue> Call(IService service, ClientSession session, string method, params PyValue[] args) =>
        service.CallAsync(new ServiceCall { Method = method, Session = session, Args = args });

    private static ClientSession Enter(CharacterSessions sessions, long id)
    {
        var client = new ClientSession { CharacterId = id };
        sessions.Enter(id, client);
        return client;
    }

    private static OnlineStatusService WithContacts(CharacterSessions sessions, Dictionary<long, IReadOnlyCollection<long>> contacts) =>
        new(sessions, (id, _) => ValueTask.FromResult(contacts.TryGetValue(id, out var ids) ? ids : (IReadOnlyCollection<long>)[]));

    [Fact]
    public async Task Reference_methods_are_registered_under_onlineStatus()
    {
        IService service = new OnlineStatusService(new CharacterSessions());
        Assert.Equal("onlineStatus", service.Name);
        foreach (var method in new[] { "GetOnlineStatus", "GetInitialState", "Prime" })
            Assert.True(service.HasMethod(method));
        Assert.Equal(PyNone.Instance, await Call(service, new ClientSession(), "UnknownMethod"));
    }

    [Fact]
    public async Task Online_status_tracks_the_current_session_and_its_disconnect()
    {
        var sessions = new CharacterSessions();
        var client = new ClientSession { CharacterId = Observer };
        IService service = new OnlineStatusService(sessions);
        Assert.Equal(PyBool.False, await Call(service, client, "GetOnlineStatus", new PyInt(Observer)));
        sessions.Enter(Observer, client);
        Assert.Equal(PyBool.True, await Call(service, client, "GetOnlineStatus", new PyInt(Observer)));
        client.Close();
        Assert.Equal(PyBool.False, await Call(service, client, "GetOnlineStatus", new PyInt(Observer)));
    }

    [Fact]
    public async Task Switching_character_clears_old_presence_and_closing_old_session_preserves_replacement()
    {
        var sessions = new CharacterSessions();
        var previous = Enter(sessions, Observer);
        var current = Enter(sessions, Observer);
        var service = new OnlineStatusService(sessions);
        previous.Close();
        Assert.Equal(PyBool.True, await Call(service, current, "GetOnlineStatus", new PyLong(Observer)));
        current.CharacterId = Target;
        var query = new ClientSession();
        Assert.Equal(PyBool.False, await Call(service, query, "GetOnlineStatus", new PyInt(Observer)));
        Assert.Equal(PyBool.False, await Call(service, query, "GetOnlineStatus", new PyInt(Target)));
        sessions.Enter(Target, current);
        Assert.Equal(PyBool.True, await Call(service, query, "GetOnlineStatus", new PyInt(Target)));
        current.Close();
        Assert.Equal(PyBool.False, await Call(service, query, "GetOnlineStatus", new PyInt(Target)));
    }

    [Fact]
    public async Task Other_presence_requires_live_mutual_buddy_membership()
    {
        var sessions = new CharacterSessions();
        var observer = Enter(sessions, Observer);
        var target = Enter(sessions, Target);
        var contacts = new Dictionary<long, IReadOnlyCollection<long>>();
        var service = WithContacts(sessions, contacts);
        Assert.Equal(PyBool.False, await Call(service, observer, "GetOnlineStatus", new PyInt(Target)));
        contacts[Target] = [Observer];
        Assert.Equal(PyBool.False, await Call(service, observer, "GetOnlineStatus", new PyInt(Target)));
        contacts[Observer] = [Target];
        Assert.Equal(PyBool.True, await Call(service, observer, "GetOnlineStatus", new PyInt(Target)));
        contacts.Remove(Target);
        Assert.Equal(PyBool.False, await Call(service, observer, "GetOnlineStatus", new PyInt(Target)));
        contacts[Target] = [Observer];
        target.Close();
        Assert.Equal(PyBool.False, await Call(service, observer, "GetOnlineStatus", new PyInt(Target)));
    }

    [Fact]
    public async Task Default_contacts_are_empty_self_is_visible_and_other_characters_are_hidden()
    {
        var sessions = new CharacterSessions();
        var observer = Enter(sessions, Observer);
        Enter(sessions, Target);
        var service = new OnlineStatusService(sessions);
        Assert.Equal(PyBool.True, await Call(service, observer, "GetOnlineStatus", new PyInt(Observer)));
        Assert.Equal(PyBool.False, await Call(service, observer, "GetOnlineStatus", new PyInt(Target)));
        Assert.Empty(Rows(await Call(service, observer, "GetInitialState")));
    }

    [Theory]
    [InlineData("GetInitialState")]
    [InlineData("Prime")]
    public async Task Initial_state_is_sorted_descriptor_backed_and_keeps_offline_and_nonmutual_buddies(string method)
    {
        var sessions = new CharacterSessions();
        var observer = Enter(sessions, Observer);
        Enter(sessions, Target);
        Enter(sessions, Target + 1);
        var contacts = new Dictionary<long, IReadOnlyCollection<long>>
        {
            [Observer] = [Target + 2, Target + 1, Target, Target, 0, -1],
            [Target] = [Observer], [Target + 2] = [Observer],
        };
        var response = await Call(WithContacts(sessions, contacts), observer, method, new PyInt(123));
        var expected = new[] { (Target, true), (Target + 1, false), (Target + 2, false) };
        Assert.Equal(expected, Rows(response));
        Assert.Equal(expected, Rows(MarshalCodec.DecodeExact(MarshalCodec.Encode(response))));
    }

    [Fact]
    public async Task Repriming_reads_contact_and_presence_changes()
    {
        var sessions = new CharacterSessions();
        var observer = Enter(sessions, Observer);
        var target = Enter(sessions, Target);
        var contacts = new Dictionary<long, IReadOnlyCollection<long>>
        {
            [Observer] = [Target], [Target] = [Observer],
        };
        var service = WithContacts(sessions, contacts);
        Assert.Equal(new[] { (Target, true) }, Rows(await Call(service, observer, "Prime")));
        target.Close();
        Assert.Equal(new[] { (Target, false) }, Rows(await Call(service, observer, "Prime")));
        contacts[Observer] = [];
        Assert.Empty(Rows(await Call(service, observer, "Prime")));
    }

    [Fact]
    public async Task Unselected_observer_gets_empty_initial_state_and_reads_raw_presence()
    {
        var sessions = new CharacterSessions();
        Enter(sessions, Target);
        var service = new OnlineStatusService(sessions, (_, _) => throw new InvalidOperationException("No observer lookup expected."));
        var observer = new ClientSession();
        Assert.Empty(Rows(await Call(service, observer, "GetInitialState")));
        Assert.Empty(Rows(await Call(service, observer, "Prime")));
        Assert.Equal(PyBool.True, await Call(service, observer, "GetOnlineStatus", new PyInt(Target)));
        Assert.Equal(PyBool.False, await Call(service, observer, "GetOnlineStatus", new PyInt(Target + 1)));
        Assert.Equal(PyBool.False, await Call(service, observer, "GetOnlineStatus"));
    }

    public static IEnumerable<object[]> InvalidIds()
    {
        yield return [PyNone.Instance];
        yield return [new PyInt(0)];
        yield return [new PyLong(-1)];
        yield return [new PyReal(Target + 0.5)];
        yield return [new PyReal(double.NaN)];
        yield return [new PyReal(double.PositiveInfinity)];
        yield return [new PyStr("not-a-character")];
        yield return [new PyInt(Target + 1)];
    }

    [Theory]
    [MemberData(nameof(InvalidIds))]
    public async Task Invalid_and_unknown_ids_are_false(PyValue id)
    {
        var sessions = new CharacterSessions();
        Enter(sessions, Target);
        Assert.Equal(PyBool.False, await Call(new OnlineStatusService(sessions), new ClientSession(), "GetOnlineStatus", id));
    }

    [Fact]
    public async Task Unselected_sessions_read_raw_presence()
    {
        var sessions = new CharacterSessions();
        Enter(sessions, Target);

        Assert.Equal(PyBool.True, await Call(new OnlineStatusService(sessions), new ClientSession(),
            "GetOnlineStatus", new PyInt(Target)));
    }

    public static IEnumerable<object[]> NumericIds()
    {
        yield return [new PyInt(Target)];
        yield return [new PyLong(Target)];
        yield return [new PyReal(Target)];
        yield return [new PyStr("90000002")];
        yield return [new PyBytes(Encoding.UTF8.GetBytes("90000002"))];
        yield return [new PyBuffer(Encoding.UTF8.GetBytes("90000002"))];
    }

    [Theory]
    [MemberData(nameof(NumericIds))]
    public async Task Ids_follow_reference_number_conversion(PyValue id)
    {
        var sessions = new CharacterSessions();
        var target = Enter(sessions, Target);
        Assert.Equal(PyBool.True, await Call(new OnlineStatusService(sessions), target, "GetOnlineStatus", id));
    }

    [Fact]
    public async Task Presence_is_read_after_asynchronous_buddy_lookup()
    {
        var sessions = new CharacterSessions();
        var observer = Enter(sessions, Observer);
        var target = Enter(sessions, Target);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new OnlineStatusService(sessions, async (id, cancellation) =>
        {
            if (id == Observer) return [Target];
            started.SetResult();
            await release.Task.WaitAsync(cancellation);
            return [Observer];
        });
        var pending = Call(service, observer, "GetOnlineStatus", new PyInt(Target)).AsTask();
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            target.Close();
        }
        finally
        {
            release.TrySetResult();
        }
        Assert.Equal(PyBool.False, await pending);
    }

    [Fact]
    public async Task Store_errors_propagate_and_call_cancellation_is_forwarded()
    {
        var sessions = new CharacterSessions();
        var observer = Enter(sessions, Observer);
        Enter(sessions, Target);
        using var cancellation = new CancellationTokenSource();
        var seen = default(CancellationToken);
        IService service = new OnlineStatusService(sessions, (_, token) =>
        {
            seen = token;
            return ValueTask.FromException<IReadOnlyCollection<long>>(new InvalidOperationException("contact-store-failed"));
        });
        var call = new ServiceCall
        {
            Method = "GetOnlineStatus", Session = observer, Args = [new PyInt(Target)], Cancellation = cancellation.Token,
        };
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.CallAsync(call).AsTask());
        Assert.Equal("contact-store-failed", error.Message);
        Assert.Equal(cancellation.Token, seen);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.CallAsync(call).AsTask());
    }

    private static (long Id, bool Online)[] Rows(PyValue value)
    {
        var rowset = Assert.IsType<PyObjectEx>(value);
        Assert.Equal(ObjectExKind.Ex2, rowset.Kind);
        Assert.Empty(rowset.Dict);
        var header = Assert.IsType<PyTuple>(rowset.Header);
        Assert.Equal(2, header.Items.Count);
        var type = Assert.Single(Assert.IsType<PyTuple>(header.Items[0]).Items);
        Assert.Equal("carbon.common.script.sys.crowset.CRowset", Assert.IsType<PyToken>(type).Value);
        var keyword = Assert.Single(Assert.IsType<PyDict>(header.Items[1]).Entries);
        Assert.Equal("header", JsSemantics.StringValue(keyword.Key));
        var descriptor = Descriptor.FromHeader(keyword.Value);
        Assert.Equal(new[] { new PackedColumn("contactID", 3), new PackedColumn("online", 11) }, descriptor.Columns);
        return rowset.List.Select(item =>
        {
            var row = Assert.IsType<PyPackedRow>(item);
            Assert.Equal(descriptor, row.Descriptor);
            Assert.Equal(2, row.Values.Count);
            return (Assert.IsType<PyInt>(row.Values[0]).Value, Assert.IsType<PyBool>(row.Values[1]).Value);
        }).ToArray();
    }
}
