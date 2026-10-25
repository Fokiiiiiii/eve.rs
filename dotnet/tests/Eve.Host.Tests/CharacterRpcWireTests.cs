using System.Net;
using System.Net.Sockets;
using Eve.Host;
using Eve.Identity;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;

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
        var registry = new ServiceRegistry();
        foreach (var service in CoreServices.Create(new NoAccounts(), () => FixedNowMs))
            registry.Register(service);
        registry.Register(new PaperDollServerRpcService());

        var config = GlobalConfig.Build(Options);
        var encoded = MarshalCodec.Encode(config);
        var dispatcher = new PacketDispatcher(registry, () => FixedNowMs);
        _server = new HostServer(
            new HostOptions { MaxFrontConnections = 8, SessionOpened = session =>
            {
                session.Set("allianceID", new PyInt(0));
                session.Set("corporationID", new PyInt(90_000_001));
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
    }

    private sealed class NoAccounts : IOmegaAccountStore
    {
        public string? GetOmegaExpiry(long accountId) => null;
    }
}
