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
    public async Task Contract_reads_and_mutations_are_dispatched_through_the_host_with_client_shapes()
    {
        var writer = new RecordingContractWriter();
        var reads = new RecordingContractReadModel();
        StartContractHost(writer, reads);
        var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        _ = await front.ReceiveAsync();

        async Task<PyValue> Rpc(uint id, string method, params PyValue[] args)
        {
            await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
                Packets.Call(open, id, "contractProxy", method, args))));
            var reply = Assert.IsType<Send>(await front.ReceiveAsync());
            return Result(MarshalCodec.Decode(reply.Payload))!;
        }

        var deleteResult = Assert.IsType<PyTuple>(await Rpc(1, "DeleteMultipleContracts",
            new PyList([new PyInt(1001), new PyLong(1002), new PyInt(1003), new PyStr("ignored")])));
        Assert.Equal([new PyInt(1001), new PyInt(1003)], Assert.IsType<PyList>(deleteResult.Items[0]).Items);
        Assert.Equal([new PyInt(1002)], Assert.IsType<PyList>(deleteResult.Items[1]).Items);
        Assert.Equal([1001L, 1002L, 1003L], writer.Deleted);

        var counts = Fields(Assert.IsType<PyObject>(await Rpc(2, "NumOutstandingContracts")));
        Assert.Equal(new PyInt(101), counts["nonCorpForMyChar"]);
        Assert.Equal(new PyInt(1), counts["myCorpTotal"]);
        Assert.Equal(new PyInt(1), counts["nonCorpForMyCorp"]);
        Assert.Equal(new PyInt(101), counts["myCharTotal"]);
        Assert.Equal([42L, 42L, 84L], reads.OwnerIds);

        var page = Fields(Assert.IsType<PyObject>(await Rpc(12, "CollectMyPageInfo")));
        Assert.Equal(new PyInt(101), page["numOutstandingContractsNonCorp"]);
        Assert.Equal(new PyInt(1), page["numOutstandingContractsForCorp"]);
        Assert.Equal(new PyInt(102), page["numOutstandingContracts"]);
        Assert.Equal(new PyInt(1), page["numInProgress"]);
        Assert.Equal(new PyInt(1), page["numInProgressCorp"]);
        Assert.Equal(new PyInt(1), page["numBiddingOn"]);
        Assert.Equal(new PyInt(1), page["numBiddingOnCorp"]);
        Assert.Equal(new PyInt(2), page["numRequiresAttention"]);
        Assert.Equal(new PyInt(1), page["numRequiresAttentionCorp"]);
        var assignments = Assert.IsType<PyList>(page["outstandingContracts"]);
        Assert.Equal([new PyInt(42), new PyInt(84), new PyInt(42), new PyInt(1)],
            Assert.IsType<PyTuple>(Assert.Single(assignments.Items)).Items);

        var login = Fields(Assert.IsType<PyObject>(await Rpc(3, "GetLoginInfo")));
        Assert.Equal(new[] { "assignedToMe", "inProgress", "needsAttention" }, login.Keys.Order().ToArray());

        var escrow = Fields(Assert.IsType<PyObject>(await Rpc(4, "GetMyContractEscrow")));
        Assert.Equal(new PyInt(1250), escrow["iskEscrow"]);
        Assert.Equal(new PyInt(3), escrow["itemsEscrow"]);

        var bids = Fields(Assert.IsType<PyObject>(await Rpc(5, "GetMyBids", PyBool.False)));
        Assert.Single(Assert.IsType<PyList>(bids["contracts"]).Items);

        var ownerContracts = Fields(Assert.IsType<PyObject>(await Rpc(6, "GetContractListForOwner",
            new PyInt(42), new PyInt(0), new PyInt(1), new PyInt(0))));
        Assert.Equal(2, Assert.IsType<PyList>(ownerContracts["contracts"]).Items.Count);

        var current = Fields(Assert.IsType<PyObject>(await Rpc(7, "GetMyCurrentContractList", PyBool.False, PyBool.False)));
        Assert.Single(Assert.IsType<PyList>(current["contracts"]).Items);

        var expired = Fields(Assert.IsType<PyObject>(await Rpc(8, "GetMyExpiredContractList", PyBool.False)));
        Assert.Single(Assert.IsType<PyList>(expired["contracts"]).Items);

        var accepted = Fields(Assert.IsType<PyObject>(await Rpc(9, "AcceptContract", new PyInt(5003), PyBool.False)));
        Assert.Equal(new PyInt(5003), accepted["contractID"]);
        Assert.Same(PyBool.True, await Rpc(10, "CompleteContract", new PyInt(5001), new PyInt(4)));
        Assert.Same(PyBool.True, await Rpc(11, "DeleteContract", new PyInt(5002)));

        Assert.Equal((5001L, 4L, 42L, 84L, 3001L, true), writer.LastComplete);
        Assert.Equal((5002L, 42L, 84L, true), writer.LastDelete);
        Assert.Equal((5003L, 42L, 84L, false, 1000, 3001L), writer.LastAccept);
    }

    private void StartContractHost(RecordingContractWriter writer, IContractReadModel reads)
    {
        var registry = new ServiceRegistry();
        Func<long> now = () => FixedNowMs;
        foreach (var service in CoreServices.Create(
            new NoAccounts(), now, contractReadModel: reads, contractWriteModel: writer,
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
                    session.Set("corprole", new PyLong(72_057_594_037_927_936));
                    session.Set("stationid", new PyInt(3001));
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
        public (long ContractId, long CharacterId, long CorporationId, bool HasRole) LastDelete { get; private set; }
        public (long ContractId, long Status, long CharacterId, long CorporationId, long StationId, bool HasRole) LastComplete { get; private set; }
        public (long ContractId, long CharacterId, long CorporationId, bool ForCorp, int WalletKey, long StationId) LastAccept { get; private set; }

        public ValueTask<CreatedContract> CreateContractAsync(CreateItemExchangeRequest request, CancellationToken cancellationToken) =>
            ValueTask.FromException<CreatedContract>(new NotSupportedException());

        public ValueTask<AcceptedContract> AcceptContractAsync(long contractId, long characterId, long corporationId,
            long allianceId, bool acceptForCorporation, int acceptorWalletKey, long currentStationId,
            CancellationToken cancellationToken)
        {
            LastAccept = (contractId, characterId, corporationId, acceptForCorporation, acceptorWalletKey, currentStationId);
            return ValueTask.FromResult(new AcceptedContract(contractId, 1, 4, 100, corporationId,
                false, characterId, currentStationId, 0, 123456, 0));
        }

        public ValueTask<AcceptedContract> AcceptItemExchangeAsync(long contractId, long characterId, long corporationId,
            long allianceId, bool acceptForCorporation, int acceptorWalletKey, CancellationToken cancellationToken) =>
            ValueTask.FromException<AcceptedContract>(new NotSupportedException());

        public ValueTask<bool> CompleteContractAsync(long contractId, int targetStatus, long characterId, long corporationId,
            long currentStationId, bool hasCorpContractManager, CancellationToken cancellationToken)
        {
            LastComplete = (contractId, targetStatus, characterId, corporationId, currentStationId, hasCorpContractManager);
            return ValueTask.FromResult(true);
        }

        public ValueTask<bool> DeleteContractAsync(long contractId, long characterId, long corporationId,
            bool hasCorpContractManager, CancellationToken cancellationToken)
        {
            Deleted.Add(contractId);
            LastDelete = (contractId, characterId, corporationId, hasCorpContractManager);
            return ValueTask.FromResult(contractId != 1002);
        }

        public ValueTask<bool> PlaceBidAsync(long contractId, long characterId, long corporationId, long allianceId,
            bool bidForCorporation, int bidderWalletKey, decimal amount, CancellationToken cancellationToken) =>
            ValueTask.FromException<bool>(new NotSupportedException());
    }

    private sealed class RecordingContractReadModel : IContractReadModel
    {
        public List<long> OwnerIds { get; } = [];

        public ValueTask<ContractLoginInfo> GetLoginInfoAsync(
            long characterId, long corporationId, long allianceId, long nowFileTime, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new ContractLoginInfo(
                [new ContractNeedsAttentionRow(1001, 0), new ContractNeedsAttentionRow(1002, 0),
                    new ContractNeedsAttentionRow(1003, 1)],
                [new ContractInProgressRow(1002, 3001, 3002, 9876543210123456)],
                [new ContractAssignedToMeRow(1003, characterId, corporationId, characterId, 1)]));

        public ValueTask<ContractEscrowSummary> GetMyContractEscrowAsync(
            long characterId, long corporationId, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new ContractEscrowSummary(1250, 3));

        public ValueTask<ContractReadBundle> GetMyBidsAsync(
            long characterId, long corporationId, bool forCorporation, long nowFileTime, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new ContractReadBundle([Contract(4001, characterId, corporationId, false)]));

        public ValueTask<ContractReadBundle> GetContractListForOwnerAsync(
            long characterId, long corporationId, long ownerId, int status, int contractType, long issuedBy,
            int pageSize, long? startContractId, long nowFileTime, CancellationToken cancellationToken)
        {
            OwnerIds.Add(ownerId);
            if (status == 0 && contractType == 0 && ownerId == characterId)
            {
                var rows = startContractId is null
                    ? Enumerable.Range(0, 100).Select(offset => Contract(2100 + offset, characterId, corporationId, false)).ToArray()
                    : [Contract(2000, characterId, corporationId, false)];
                return ValueTask.FromResult(new ContractReadBundle(rows));
            }

            return ValueTask.FromResult(new ContractReadBundle(ownerId == characterId
                ? [Contract(2001, characterId, corporationId, false), Contract(2002, characterId, corporationId, false)]
                : [Contract(3001, corporationId, corporationId, true)]));
        }

        public ValueTask<ContractSearchResult> SearchContractsAsync(
            ContractSearchQuery query, long nowFileTime, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new ContractSearchResult([], 0, 0));

        public ValueTask<ContractReadBundle> GetMyCurrentContractListAsync(
            long characterId, long corporationId, bool isAccepted, bool forCorporation,
            long nowFileTime, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new ContractReadBundle([Contract(5004, characterId, corporationId, false)]));

        public ValueTask<ContractReadBundle> GetMyExpiredContractListAsync(
            long characterId, long corporationId, bool forCorporation, long nowFileTime,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new ContractReadBundle([Contract(5005, characterId, corporationId, false)]));

        private static ContractReadSnapshot Contract(long id, long issuer, long corporation, bool issuedByCorporation) =>
            new(id, 1, 0, 0, issuer, corporation, issuedByCorporation, 0, 0, 1, long.MaxValue, 0, 0,
                0, 3001, 0, 5000m, 0m, 0m, "Contract", "Description",
                [new ContractReadItemSnapshot(7001, 34, 2, true, false)], []);
    }

    private static IReadOnlyDictionary<string, PyValue> Fields(PyObject value) =>
        Assert.IsType<PyDict>(value.Args).Entries.ToDictionary(
            pair => T(pair.Key)!, pair => pair.Value);
}
