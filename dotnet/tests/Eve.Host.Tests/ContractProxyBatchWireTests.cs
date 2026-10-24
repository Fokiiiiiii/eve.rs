using System.Net;
using System.Net.Sockets;
using Eve.Host;
using Eve.Identity;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;
using Xunit;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [Fact]
    public async Task Delete_multiple_contracts_is_dispatched_through_the_host_and_reports_each_outcome()
    {
        var writer = new RecordingContractWriter();
        StartContractHost(writer);
        var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        _ = await front.ReceiveAsync();

        await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
            Packets.Call(open, 1, "contractProxy", "DeleteMultipleContracts",
                new PyList([new PyInt(1001), new PyLong(1002), new PyInt(1003), new PyStr("ignored")])))));
        var reply = Assert.IsType<Send>(await front.ReceiveAsync());
        var result = Assert.IsType<PyTuple>(Result(MarshalCodec.Decode(reply.Payload)));
        var deleted = Assert.IsType<PyList>(result.Items[0]);
        var failed = Assert.IsType<PyList>(result.Items[1]);

        Assert.Equal([new PyInt(1001), new PyInt(1003)], deleted.Items);
        Assert.Equal([new PyInt(1002)], failed.Items);
        Assert.Equal([1001L, 1002L, 1003L], writer.Deleted);
    }

    private void StartContractHost(RecordingContractWriter writer)
    {
        var registry = new ServiceRegistry();
        Func<long> now = () => FixedNowMs;
        foreach (var service in CoreServices.Create(
            new NoAccounts(), now, contractWriteModel: writer,
            readCurrentCalendarGroups: (_, _) => ValueTask.FromResult((84L, 0L))))
            registry.Register(service);

        var config = GlobalConfig.Build(Options);
        registry.Register(new MachoNetService(new Info(registry, config), now));
        var dispatcher = new PacketDispatcher(registry, now);
        var encoded = MarshalCodec.Encode(config);
        _server = new HostServer(
            new HostOptions
            {
                MaxFrontConnections = 1024,
                SessionOpened = session =>
                {
                    session.Set("charid", new PyInt(42));
                    session.Set("corpid", new PyInt(84));
                },
            },
            dispatcher,
            new InMemoryAccountDirectory(new AccountOptions { AutoCreate = true }),
            () => encoded);
        _listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        _listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        _listener.Listen(16);
        _serving = _server.ServeAsync(_listener, _cancel.Token);
    }

    private sealed class RecordingContractWriter : IContractWriteModel
    {
        public List<long> Deleted { get; } = [];

        public ValueTask<CreatedContract> CreateContractAsync(CreateItemExchangeRequest request, CancellationToken cancellationToken) =>
            ValueTask.FromException<CreatedContract>(new NotSupportedException());

        public ValueTask<AcceptedContract> AcceptContractAsync(long contractId, long characterId, long corporationId,
            long allianceId, bool acceptForCorporation, int acceptorWalletKey, long currentStationId,
            CancellationToken cancellationToken) =>
            ValueTask.FromException<AcceptedContract>(new NotSupportedException());

        public ValueTask<AcceptedContract> AcceptItemExchangeAsync(long contractId, long characterId, long corporationId,
            long allianceId, bool acceptForCorporation, int acceptorWalletKey, CancellationToken cancellationToken) =>
            ValueTask.FromException<AcceptedContract>(new NotSupportedException());

        public ValueTask<bool> CompleteContractAsync(long contractId, int targetStatus, long characterId, long corporationId,
            long currentStationId, bool hasCorpContractManager, CancellationToken cancellationToken) =>
            ValueTask.FromException<bool>(new NotSupportedException());

        public ValueTask<bool> DeleteContractAsync(long contractId, long characterId, long corporationId,
            bool hasCorpContractManager, CancellationToken cancellationToken)
        {
            Deleted.Add(contractId);
            return ValueTask.FromResult(contractId != 1002);
        }

        public ValueTask<bool> PlaceBidAsync(long contractId, long characterId, long corporationId, long allianceId,
            bool bidForCorporation, int bidderWalletKey, decimal amount, CancellationToken cancellationToken) =>
            ValueTask.FromException<bool>(new NotSupportedException());
    }
}
