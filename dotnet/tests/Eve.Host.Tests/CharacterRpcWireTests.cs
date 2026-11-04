using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using Eve.Host;
using Eve.Identity;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;
using Eve.Services.CharacterInfo;

namespace Eve.Host.Tests;

public sealed class CharacterRpcWireTests : IAsyncLifetime
{
    private const long FixedNowMs = 1_790_646_377_000;
    private readonly CancellationTokenSource _cancel = new();
    private Socket _listener = null!;
    private HostServer _server = null!;
    private Task _serving = Task.CompletedTask;
    private readonly List<TestFront> _fronts = [];
    private static readonly ClusterOptions Options = new();

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        foreach (var front in _fronts) front.Dispose();
        _cancel.Cancel();
        _listener.Dispose();
        return _serving;
    }

    [Fact]
    public async Task Character_and_support_rpc_contracts_reach_registered_host_services()
    {
        var notifications = new JsonObject
        {
            ["byID"] = new JsonObject
            {
                ["101"] = new JsonObject
                {
                    ["notificationID"] = 101, ["typeID"] = 7, ["senderID"] = 12,
                    ["receiverID"] = 42, ["groupID"] = 4, ["processed"] = false,
                    ["created"] = 132_000_000_000_000_000L, ["data"] = new JsonObject { ["message"] = "notice" },
                },
                ["100"] = new JsonObject
                {
                    ["notificationID"] = 100, ["typeID"] = 8, ["senderID"] = 13,
                    ["receiverID"] = 42, ["groupID"] = 5, ["processed"] = true,
                    ["created"] = 132_000_000_000_000_000L, ["data"] = new JsonObject(),
                },
            },
        };
        var notificationMutations = new List<(string Operation, long? GroupId, IReadOnlyList<long> Ids)>();
        var sessions = new CharacterSessions();
        ServiceCall? extractionCall = null;
        var registry = new ServiceRegistry();
        foreach (var service in CoreServices.Create(new NoAccounts(), () => FixedNowMs,
                     readNonDiminishingInjections: (_, _) => ValueTask.FromResult(7)))
            registry.Register(service);
        registry.Register(new PaperDollServerRpcService());
        registry.Register(new MoonExtractionsRpcService(
            () => Task.FromResult<JsonObject?>(new JsonObject
            {
                ["resourcesByStructureID"] = new JsonObject
                {
                    ["700"] = new JsonObject { ["resources"] = new JsonObject { ["34"] = 0.75, ["35"] = 0.25 } },
                },
            }),
            call =>
            {
                extractionCall = call;
                return ValueTask.FromResult<PyValue>(new PyInt(1));
            }));
        registry.Register(new OnlineStatusService(sessions,
            (id, _) => ValueTask.FromResult<IReadOnlyCollection<long>>(id == 42 ? new long[] { 12 } : new long[] { 42 })));
        registry.Register(new NotificationMgrService(
            (_, _) => ValueTask.FromResult<JsonObject?>(notifications),
            () => FixedNowMs,
            mutate: (characterId, operation, groupId, ids, _, _) =>
            {
                Assert.Equal(42, characterId);
                notificationMutations.Add((operation, groupId, ids));
                return ValueTask.CompletedTask;
            }));

        var config = GlobalConfig.Build(Options);
        var encoded = MarshalCodec.Encode(config);
        var dispatcher = new PacketDispatcher(registry, () => FixedNowMs);
        _server = new HostServer(
            new HostOptions { MaxFrontConnections = 8, SessionOpened = session =>
            {
                session.Set("allianceID", new PyInt(0));
                session.Set("corporationID", new PyInt(90_000_001));
                session.Set("structureid", new PyInt(700));
                session.Set("charid", new PyInt(42));
                sessions.Enter(42, session);
                var other = new ClientSession();
                other.Set("charid", new PyInt(12));
                sessions.Enter(12, other);
            }},
            dispatcher,
            new InMemoryAccountDirectory(new AccountOptions { AutoCreate = true }),
            () => encoded);
        _listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        _listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        _listener.Listen(16);
        _serving = _server.ServeAsync(_listener, _cancel.Token);

        var front = await TestFront.ConnectAsync((IPEndPoint)_listener.LocalEndPoint!);
        _fronts.Add(front);
        await front.SendAsync(new Hello(HostCodec.ProtocolVersion, 0xFFAA));
        Assert.IsType<HelloAck>(await front.ReceiveAsync());
        await front.ReceiveAsync();
        var open = new SessionOpen(1_790_646_377_000UL * 15, (42UL * 1_000_000UL) + 0xFFAA, 42, 0, "127.0.0.1", "EN", "GB");
        await front.SendAsync(open);
        await front.ReceiveAsync();
        uint callId = 1;

        async Task<PyValue?> CallAsync(string service, string method, PyValue args, PyValue kwargs)
        {
            await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
                Packets.CallWithKwargs(open, callId++, service, method, args, kwargs))));
            var reply = Assert.IsType<Send>(await front.ReceiveAsync());
            var state = ((PyTuple)((PyObject)MarshalCodec.Decode(reply.Payload)).Args).Items;
            Assert.Equal(7, ((PyInt)state[0]).Value);
            var payload = (PyTuple)state[4];
            return payload.Items[0] is PySubStream { Decoded: { } inner } ? inner : payload.Items[0];
        }

        Assert.IsType<PyNone>(await CallAsync("paperDollServer", "GetPaperDollData",
            new PyTuple([new PyInt(90_000_001)]), PyNone.Instance));
        var portrait = Assert.IsType<PyTuple>(await CallAsync("paperDollServer", "GetPaperDollPortraitDataFor",
            new PyTuple([new PyInt(90_000_001)]), PyNone.Instance));
        Assert.Empty(Assert.IsType<PyList>(portrait.Items[0]).Items);
        Assert.IsType<PyNone>(portrait.Items[1]);

        Assert.IsType<PyNone>(await CallAsync("paperDollServer", "UpdateExistingCharacterFull",
            new PyTuple([new PyInt(90_000_001), PyNone.Instance, PyNone.Instance, PyBool.True]),
            new PyDict([new(new PyStr("flush"), PyBool.True)])));
        Assert.IsType<PyNone>(await CallAsync("paperDollServer", "UpdateExistingCharacterLimited",
            new PyTuple([new PyInt(90_000_001), PyNone.Instance, PyNone.Instance, PyBool.False]),
            PyNone.Instance));

        var treaties = Assert.IsType<PyTuple>(await CallAsync("peaceTreatyMgr", "GetPeaceTreatiesForSession",
            new PyTuple([]), PyNone.Instance));
        Assert.Equal(2, treaties.Items.Count);
        Assert.All(treaties.Items, value => Assert.IsType<PyObjectEx>(value));

        var petitionId = new PyInt(1234);
        var petition = new PyTuple([
            new PyStr("subject"), new PyStr("body"), new PyInt(1), PyNone.Instance,
            new PyInt(90_000_001), new PyList([]), new PyList([]), new PyDict([])]);
        Assert.IsType<PyNone>(await CallAsync("petitioner", "AddPetitionRating",
            new PyTuple([petitionId, new PyInt(5), new PyInt(5), new PyInt(5), new PyStr("thanks"), new PyInt(0)]),
            PyNone.Instance));
        Assert.IsType<PyNone>(await CallAsync("petitioner", "CancelPetition",
            new PyTuple([petitionId]), PyNone.Instance));
        Assert.Equal(new PyInt(0), await CallAsync("petitioner", "ClaimPetition",
            new PyTuple([petitionId]), PyNone.Instance));
        Assert.IsType<PyNone>(await CallAsync("petitioner", "ClosePetition",
            new PyTuple([petitionId]), PyNone.Instance));
        Assert.Same(PyBool.False, await CallAsync("petitioner", "CreatePetition", petition, PyNone.Instance));

        var resources = Assert.IsType<PyDict>(await CallAsync("moonExtractions", "GetMoonResourcesForStructure",
            new PyTuple([]), PyNone.Instance));
        Assert.Contains(resources.Entries, pair => pair.Key == new PyInt(34) && pair.Value == new PyReal(0.75));
        var extractionResult = await CallAsync("moonExtractions", "StartNewExtraction",
            new PyTuple([new PyInt(700), new PyInt(604_800), PyBool.True]),
            new PyDict([new(new PyStr("machoVersion"), new PyInt(1))]));
        Assert.Equal(new PyInt(1), extractionResult);
        Assert.Equal(3, extractionCall!.Args.Count);
        Assert.Equal(new PyInt(604_800), extractionCall.Args[1]);

        Assert.Equal(new PyInt(7), await CallAsync("nonDiminishingInjectionMgr", "GetAvailableNonDiminishingInjections",
            new PyTuple([]), new PyDict([new(new PyStr("machoVersion"), new PyInt(1))])));

        Assert.Same(PyBool.True, await CallAsync("onlineStatus", "GetOnlineStatus",
            new PyTuple([new PyInt(12)]), PyNone.Instance));
        var initialOnline = Assert.IsType<PyObjectEx>(await CallAsync("onlineStatus", "GetInitialState",
            new PyTuple([]), PyNone.Instance));
        var presence = Assert.IsType<PyPackedRow>(Assert.Single(initialOnline.List));
        Assert.Equal(new PyInt(12), presence.Values[0]);
        Assert.Same(PyBool.True, presence.Values[1]);

        var allNotifications = Assert.IsType<PyList>(await CallAsync("notificationMgr", "GetAllNotifications",
            new PyTuple([]), new PyDict([new(new PyStr("fromID"), new PyInt(0))])));
        Assert.Equal(101, Assert.IsType<PyInt>(NotificationField(allNotifications.Items[0], "notificationID")).Value);
        var groupNotifications = Assert.IsType<PyList>(await CallAsync("notificationMgr", "GetByGroupID",
            new PyTuple([new PyInt(4)]), PyNone.Instance));
        Assert.Single(groupNotifications.Items);
        var unprocessed = Assert.IsType<PyList>(await CallAsync("notificationMgr", "GetUnprocessed",
            new PyTuple([]), PyNone.Instance));
        Assert.Single(unprocessed.Items);

        var burstIds = new PyList(Enumerable.Range(1, 100).Select(id => (PyValue)new PyInt(id)).ToArray());
        foreach (var (method, args) in new (string Method, PyValue Args)[]
        {
            ("DeleteAllNotifications", new PyTuple([])),
            ("DeleteGroupNotifications", new PyTuple([new PyInt(4)])),
            ("DeleteNotifications", new PyTuple([burstIds])),
            ("MarkAllAsProcessed", new PyTuple([])),
            ("MarkAsProcessed", new PyTuple([burstIds])),
            ("MarkGroupAsProcessed", new PyTuple([new PyInt(4)])),
        })
            Assert.IsType<PyNone>(await CallAsync("notificationMgr", method, args, PyNone.Instance));
        Assert.Equal(
            ["DeleteAllNotifications", "DeleteGroupNotifications", "DeleteNotifications", "MarkAllAsProcessed", "MarkAsProcessed", "MarkGroupAsProcessed"],
            notificationMutations.Select(mutation => mutation.Operation));
        Assert.Equal(100, notificationMutations[2].Ids.Count);
        Assert.Equal(100, notificationMutations[4].Ids.Count);
    }

    private static PyValue NotificationField(PyValue value, string name)
    {
        var dictionary = Assert.IsType<PyDict>(Assert.IsType<PyObject>(value).Args);
        return dictionary.Entries.Single(entry => entry.Key switch
        {
            PyStr key => key.Value,
            PyBytes key => System.Text.Encoding.UTF8.GetString(key.Value.Span),
            _ => string.Empty,
        } == name).Value;
    }

    private sealed class NoAccounts : IOmegaAccountStore
    {
        public string? GetOmegaExpiry(long accountId) => null;
    }
}