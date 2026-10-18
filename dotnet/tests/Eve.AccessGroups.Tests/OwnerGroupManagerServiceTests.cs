using Eve.AccessGroups;
using Eve.Bookmarks;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;

namespace Eve.AccessGroups.Tests;

/// <summary>The client's ownerGroupManager: the RPC shapes, the refusals and the <c>OnOwnerGroupsUpdated</c> notifications.</summary>
public sealed class OwnerGroupManagerServiceTests
{
    private const long Alice = 90000001;
    private const long Bob = 90000002;
    private const long Carol = 90000003;
    private const long Stranger = 90000004;
    private const long CorpA = 98000001;
    private const long CorpB = 98000002;
    private const long Unknown = 99999999;

    private static readonly Dictionary<long, MemberKind> Kinds = new()
    {
        [Bob] = MemberKind.Character,
        [Carol] = MemberKind.Character,
        [Stranger] = MemberKind.Character,
        [CorpA] = MemberKind.Corporation,
        [CorpB] = MemberKind.Corporation,
        [0] = MemberKind.Everyone,
    };

    [PgFact]
    public async Task A_created_group_answers_the_client_keyval_with_the_creator_as_admin()
    {
        await using var rig = await Rig.CreateAsync();
        var sessions = new CharacterSessions();
        var service = Service(rig, sessions);
        var alice = Connect(sessions, Alice, CorpA, new Sink());

        var created = Fields(await Call(service, alice, "Create", new PyWString("Haulers"), new PyWString("fleet")));
        Assert.Equal("Haulers", Text(created["name"]));
        Assert.Equal(2, Num(created["membershipType"]));
        Assert.Equal(Alice, Num(created["creatorID"]));

        var mine = Assert.IsType<PyList>(await Call(service, alice, "GetMyGroups"));
        var listed = mine.Items.Select(Fields).Single(fields => Text(fields["name"]) == "Haulers");
        Assert.Equal(2, Num(listed["membershipType"]));
        Assert.Equal(Num(created["groupID"]), Num(listed["groupID"]));
    }

    [PgFact]
    public async Task A_group_the_character_cannot_see_is_answered_with_none_and_a_character_less_reader_gets_no_groups()
    {
        await using var rig = await Rig.CreateAsync();
        var sessions = new CharacterSessions();
        var service = Service(rig, sessions);
        var alice = Connect(sessions, Alice, CorpA, new Sink());
        var group = Num(Fields(await Call(service, alice, "Create", new PyWString("Private"), new PyWString(""))) ["groupID"]);
        var stranger = Connect(sessions, Stranger, CorpB, new Sink());

        Assert.Equal(PyNone.Instance, await Call(service, stranger, "GetGroup", new PyLong(group)));
        Assert.Empty(Assert.IsType<PyList>(await Call(service, new ClientSession(), "GetMyGroups")).Items);
    }

    [PgFact]
    public async Task Adding_members_answers_the_refused_ones_as_member_and_reason_pairs()
    {
        await using var rig = await Rig.CreateAsync();
        var sessions = new CharacterSessions();
        var service = Service(rig, sessions);
        var alice = Connect(sessions, Alice, CorpA, new Sink());
        var group = Num(Fields(await Call(service, alice, "Create", new PyWString("G"), new PyWString(""))) ["groupID"]);

        var failures = Assert.IsType<PyList>(await Call(service, alice, "AddMembers", new PyLong(group),
            Dict((Bob, 0), (Unknown, 0), (CorpB, 2))));

        var refused = failures.Items.Select(item => (Num(((PyTuple)item).Items[0]), Text(((PyTuple)item).Items[1]))).OrderBy(pair => pair.Item1).ToArray();
        Assert.Equal(new[] { (Unknown, "InvalidMember") }, refused.Where(pair => pair.Item1 == Unknown).ToArray());
        Assert.Equal(new[] { (CorpB, "NotAllowed") }, refused.Where(pair => pair.Item1 == CorpB).ToArray());
        var members = Assert.IsType<PyList>(await Call(service, alice, "GetMembers", new PyLong(group)));
        Assert.Contains(members.Items.Select(Fields), fields => Num(fields["memberID"]) == Bob && Num(fields["membershipType"]) == 0);
        Assert.DoesNotContain(members.Items.Select(Fields), fields => Num(fields["memberID"]) == CorpB);
    }

    [PgFact]
    public async Task A_member_who_renames_the_list_is_refused_with_the_notice()
    {
        await using var rig = await Rig.CreateAsync();
        var sessions = new CharacterSessions();
        var service = Service(rig, sessions);
        var alice = Connect(sessions, Alice, CorpA, new Sink());
        var group = Num(Fields(await Call(service, alice, "Create", new PyWString("G"), new PyWString(""))) ["groupID"]);
        await Call(service, alice, "AddMembers", new PyLong(group), Dict((Bob, 0)));
        var bob = Connect(sessions, Bob, CorpB, new Sink());

        var error = await Assert.ThrowsAsync<MachoWrappedException>(() => Call(service, bob, "UpdateName", new PyLong(group), new PyWString("Mine")));
        Assert.Equal("CustomNotify", error.Error.UserErrorMessage);
    }

    [PgFact]
    public async Task A_change_is_heard_after_the_response_by_the_sessions_it_names_only()
    {
        await using var rig = await Rig.CreateAsync();
        var sessions = new CharacterSessions();
        var service = Service(rig, sessions);
        var aliceSink = new Sink();
        var bobSink = new Sink();
        var carolSink = new Sink();
        var alice = Connect(sessions, Alice, CorpA, aliceSink);
        var bob = Connect(sessions, Bob, CorpB, bobSink);
        var carol = Connect(sessions, Carol, CorpB, carolSink);

        var group = Num(Fields(await Call(service, alice, "Create", new PyWString("G"), new PyWString(""))) ["groupID"]);
        Assert.Empty(bobSink.Packets);
        alice.RunAfterResponse();
        var created = Assert.Single(Notifications(aliceSink));
        Assert.Equal(new[] { 1 }, Codes(created, group));
        Assert.Empty(Notifications(bobSink));

        await Call(service, alice, "AddMembers", new PyLong(group), Dict((Bob, 0)));
        alice.RunAfterResponse();
        bob.RunAfterResponse();
        Assert.Equal(new[] { 2, 8 }, Codes(Assert.Single(Notifications(bobSink)), group));
        Assert.Equal(new[] { 2, 8 }, Codes(Assert.Single(Notifications(aliceSink).Skip(1)), group));
        Assert.Empty(Notifications(carolSink));
        carol.RunAfterResponse();
        Assert.Empty(Notifications(carolSink));
    }

    [PgFact]
    public async Task Removing_a_member_whose_list_gave_a_shared_folder_its_view_sends_the_loss_after_the_response()
    {
        await using var rig = await Rig.CreateAsync();
        var bookmarkSchema = "bm_" + Guid.NewGuid().ToString("N")[..12];
        await BookmarkStore.MigrateAsync(rig.Source, bookmarkSchema);
        try
        {
            var bookmarks = new BookmarkStore(rig.Source, bookmarkSchema, () => rig.Now);
            var sessions = new CharacterSessions();
            async ValueTask<AccessLists> ListsOf(Affiliation who, CancellationToken token) =>
                new(await rig.Store.MemberRolesAsync(who, token), who.CorporationId);
            var service = new OwnerGroupRefusals(new OwnerGroupManagerService(rig.Store,
                id => ValueTask.FromResult<MemberKind?>(Kinds.TryGetValue(id, out var kind) ? kind : null), sessions,
                new SharedFolderNotifier(bookmarks, ListsOf, sessions)));
            var alice = Connect(sessions, Alice, CorpA, new Sink());
            var bobSink = new Sink();
            var bob = Connect(sessions, Bob, CorpB, bobSink);
            var admin = Num(Fields(await Call(service, alice, "Create", new PyWString("Admin"), new PyWString(""))) ["groupID"]);
            var view = Num(Fields(await Call(service, alice, "Create", new PyWString("View"), new PyWString(""))) ["groupID"]);
            await Call(service, alice, "AddMembers", new PyLong(view), Dict((Bob, 0)));
            var folder = await bookmarks.AddFolderAsync(Alice, false, "Shared", "", new SharedGroups(admin, admin, admin, view),
                await ListsOf(new Affiliation(Alice, CorpA, 0), default));
            await bookmarks.AddKnownFolderAsync(Bob, folder.Folder.FolderId, true, await ListsOf(new Affiliation(Bob, CorpB, 0), default));
            alice.RunAfterResponse();
            bob.RunAfterResponse();

            await Call(service, alice, "DeleteMembers", new PyLong(view), new PyList([new PyLong(Bob)]));
            Assert.DoesNotContain(SharedNotices(bobSink), heard => heard.Name == "OnSharedBookmarksFoldersAccessLost");
            alice.RunAfterResponse();
            var lost = Assert.Single(SharedNotices(bobSink), heard => heard.Name == "OnSharedBookmarksFoldersAccessLost");
            Assert.Equal(folder.Folder.FolderId, Num(Assert.Single(Assert.IsType<PyList>(Assert.IsType<PyTuple>(lost.Args).Items[0]).Items)));
        }
        finally
        {
            await using var command = rig.Source.CreateCommand($"DROP SCHEMA IF EXISTS {bookmarkSchema} CASCADE");
            await command.ExecuteNonQueryAsync();
        }
    }

    private static IService Service(Rig rig, CharacterSessions sessions) =>
        new OwnerGroupRefusals(new OwnerGroupManagerService(rig.Store,
            id => ValueTask.FromResult<MemberKind?>(Kinds.TryGetValue(id, out var kind) ? kind : null), sessions));

    private static ClientSession Connect(CharacterSessions sessions, long characterId, long corporationId, Sink sink)
    {
        var session = new ClientSession(sink) { CharacterId = characterId };
        session.Set("corpid", new PyInt((int)corporationId));
        sessions.Enter(characterId, session);
        return session;
    }

    private static PyDict Dict(params (long Member, int Type)[] entries) => new(entries.Select(entry =>
        new KeyValuePair<PyValue, PyValue>(new PyLong(entry.Member), new PyInt(entry.Type))).ToArray());

    private static async Task<PyValue> Call(IService service, ClientSession session, string method, params PyValue[] args) =>
        await service.CallAsync(new ServiceCall { Method = method, Args = args, Session = session });

    private static Dictionary<string, PyValue> Fields(PyValue keyVal) =>
        ((PyDict)((PyObject)keyVal).Args).Entries.ToDictionary(e => Eve.Macho.JsSemantics.StringValue(e.Key)!, e => e.Value);

    private static long Num(PyValue value) => value switch
    {
        PyInt number => number.Value,
        PyLong number => number.Value,
        _ => throw new InvalidOperationException($"not a number: {value}"),
    };

    private static string Text(PyValue value) => Eve.Macho.JsSemantics.StringValue(value) ?? "";

    /// <summary>The change codes a notification names for one group.</summary>
    private static int[] Codes(PyValue arguments, long group)
    {
        var dict = Assert.IsType<PyDict>(Assert.IsType<PyTuple>(arguments).Items[0]);
        var entry = Assert.Single(dict.Entries, e => Num(e.Key) == group);
        return ((PyList)entry.Value).Items.Select(code => (int)Num(code)).ToArray();
    }

    private static List<(string Name, PyValue Args)> SharedNotices(Sink sink)
    {
        var heard = new List<(string Name, PyValue Args)>();
        foreach (var packet in sink.Packets)
        {
            var nodes = Walk(packet).ToList();
            if (nodes.OfType<PyStr>().FirstOrDefault(node => node.Value.StartsWith("OnShared", StringComparison.Ordinal)) is not { } name) continue;
            var stream = Assert.IsType<PyTuple>(MarshalCodec.Decode(nodes.OfType<PyBuffer>().Single().Value));
            heard.Add((name.Value, Assert.IsType<PyTuple>(Assert.IsType<PyTuple>(stream.Items[1]).Items[1])));
        }
        return heard;
    }

    /// <summary>The arguments of each <c>OnOwnerGroupsUpdated</c> the session received.</summary>
    private static List<PyValue> Notifications(Sink sink)
    {
        var heard = new List<PyValue>();
        foreach (var packet in sink.Packets)
        {
            if (!Walk(packet).Any(node => node is PyStr { Value: "OnOwnerGroupsUpdated" })) continue;
            var buffer = Walk(packet).OfType<PyBuffer>().Single();
            var stream = Assert.IsType<PyTuple>(MarshalCodec.Decode(buffer.Value));
            var args = Assert.IsType<PyTuple>(Assert.IsType<PyTuple>(stream.Items[1]).Items[1]);
            heard.Add(args);
        }
        return heard;
    }

    private static IEnumerable<PyValue> Walk(PyValue value)
    {
        yield return value;
        switch (value)
        {
            case PyTuple tuple:
                foreach (var item in tuple.Items) foreach (var node in Walk(item)) yield return node;
                break;
            case PyList list:
                foreach (var item in list.Items) foreach (var node in Walk(item)) yield return node;
                break;
            case PyObject obj:
                foreach (var node in Walk(obj.Args)) yield return node;
                break;
        }
    }

    private sealed class Sink : IPacketSink
    {
        public List<PyValue> Packets { get; } = [];

        public void Send(PyValue packet) => Packets.Add(packet);
    }
}
