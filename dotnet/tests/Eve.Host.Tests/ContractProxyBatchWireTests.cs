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
        var inventory = new RecordingContractInventoryReader();
        StartContractHost(writer, reads, inventory);
        var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        _ = await front.ReceiveAsync();

        async Task<PyValue> Rpc(uint id, string method, params PyValue[] args)
        {
            await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
                Packets.Call(open, id, "contractProxy", method, args))));
            var reply = Assert.IsType<Send>(await front.ReceiveAsync());
            var response = MarshalCodec.Decode(reply.Payload);
            var responseState = ((PyTuple)((PyObject)response).Args).Items;
            static string Describe(PyValue value) => value switch
            {
                PyBytes bytes => System.Text.Encoding.UTF8.GetString(bytes.Value.Span),
                PyStr text => text.Value,
                PyInt number => number.Value.ToString(),
                PyLong number => number.Value.ToString(),
                PyBool boolean => boolean.Value.ToString(),
                PyReal real => real.Value.ToString(),
                PyToken token => token.Value,
                PyTuple tuple => $"({string.Join(", ", tuple.Items.Select(Describe))})",
                PyList list => $"[{string.Join(", ", list.Items.Select(Describe))}]",
                PyObject obj => $"{Describe(obj.Name)} {Describe(obj.Args)}",
                PyObjectEx obj => $"{obj.Kind} header={Describe(obj.Header)} list=[{string.Join(", ", obj.List.Select(Describe))}] dict={Describe(new PyDict(obj.Dict))}",
                PySubStream { Decoded: { } decoded } => Describe(decoded),
                PySubStruct substruct => Describe(substruct.Value),
                PyChecksummed checkedValue => Describe(checkedValue.Value),
                PyDict dict => $"{{{string.Join(", ", dict.Entries.Select(entry => $"{Describe(entry.Key)}: {Describe(entry.Value)}"))}}}",
                PyNone => "None",
                _ => value.GetType().Name,
            };
            Assert.True(((PyInt)responseState[0]).Value == 7,
                $"{method} returned an error response: {Describe(response)}");
            return Result(response)!;
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

        var personalItems = Assert.IsType<PyObjectEx>(await Rpc(14, "GetItemsInDockableLocation", new PyInt(3001), PyBool.False));
        Assert.Equal("__builtin__.set", ((PyToken)((PyTuple)personalItems.Header).Items[0]).Value);
        var personalSet = Assert.IsType<PyList>(((PyTuple)((PyTuple)personalItems.Header).Items[1]).Items[0]);
        Assert.Single(personalSet.Items);
        var personalRow = Assert.IsType<PyPackedRow>(Assert.Single(personalSet.Items));
        var personalFields = personalRow.Fields().ToDictionary(field => field.Key, field => field.Value);
        Assert.Equal(new PyInt(7001), personalFields["itemID"]);
        Assert.Equal(new PyInt(34), personalFields["typeID"]);
        var corporateItems = Assert.IsType<PyObjectEx>(await Rpc(15, "GetItemsInDockableLocation", new PyInt(3001), PyBool.True));
        var corporateSet = Assert.IsType<PyList>(((PyTuple)((PyTuple)corporateItems.Header).Items[1]).Items[0]);
        var corporateRow = Assert.IsType<PyPackedRow>(Assert.Single(corporateSet.Items));
        var corporateFields = corporateRow.Fields().ToDictionary(field => field.Key, field => field.Value);
        Assert.Equal(new PyInt(8001), corporateFields["itemID"]);
        Assert.Equal(new PyInt(34), corporateFields["typeID"]);

        var containerItems = Assert.IsType<PyList>(await Rpc(16, "GetItemsInContainer",
            new PyInt(3001), new PyInt(7001), PyBool.False, new PyInt(4)));
        Assert.Equal(2, containerItems.Items.Count);
        Assert.All(containerItems.Items, row => Assert.IsType<PyPackedRow>(row));
        var containerCounts = Assert.IsType<PyDict>(await Rpc(17, "GetNumItemsInContainers",
            new PyInt(3001), new PyList([new PyInt(7001), new PyInt(7002), new PyInt(7003)]),
            PyBool.False, new PyInt(4)));
        Assert.Equal(2, containerCounts.Entries.Count);
        Assert.Contains(containerCounts.Entries, entry => entry.Key == new PyInt(7001) && entry.Value == new PyInt(2));
        Assert.Contains(containerCounts.Entries, entry => entry.Key == new PyInt(7003) && entry.Value == new PyInt(1));
        Assert.DoesNotContain(containerCounts.Entries, entry => entry.Key == new PyInt(7002));
        Assert.Equal([
            ("dockable", 3001L, 0L, false, 0),
            ("dockable", 3001L, 0L, true, 0),
            ("container", 3001L, 7001L, false, 4),
            ("container", 3001L, 7001L, false, 4),
            ("container", 3001L, 7002L, false, 4),
            ("container", 3001L, 7003L, false, 4)
        ], inventory.Requests);

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

        var created = Assert.IsType<PyList>(await Rpc(13, "CreateContract", new PyObject(new PyStr("contracts.contractStruct.ContractStruct"), new PyTuple([
            new PyInt(1), PyBool.False, new PyInt(0), new PyInt(1440), new PyInt(0),
            new PyInt(3001), new PyInt(0), new PyInt(1000), new PyInt(0), new PyInt(0),
            new PyStr("wire contract"), new PyStr(""),
            new PyList([new PyTuple([new PyInt(7001), new PyInt(1)])]),
            new PyInt(4), new PyList([]), PyBool.False, PyBool.False]))));
        Assert.Equal([new PyInt(6001)], created.Items);
        Assert.Equal((42L, 3001L, 1440, 1000m, "wire contract", (short)1), writer.CreatedRequest is { } request
            ? (request.IssuerCharacterId, request.StartStationId, request.ExpiryMinutes, request.Price, request.Title, request.ContractType)
            : default);

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
        Assert.Same(PyBool.True, await Rpc(11, "DeleteContract", new PyInt(6001)));

        Assert.Equal((5001L, 4L, 42L, 84L, 3001L, true), writer.LastComplete);
        Assert.Equal((6001L, 42L, 84L, true), writer.LastDelete);
        Assert.Equal((5003L, 42L, 84L, false, 1000, 3001L), writer.LastAccept);
    }

    private void StartContractHost(RecordingContractWriter writer, IContractReadModel? readModel = null,
        IContractInventoryReader? inventory = null)
    {
        var registry = new ServiceRegistry();
        Func<long> now = () => FixedNowMs;
        foreach (var service in CoreServices.Create(
            new NoAccounts(), now, contractReadModel: readModel, contractWriteModel: writer,
            contractSkillLevel: (_, _, _) => ValueTask.FromResult(0), contractInventoryReader: inventory,
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
                    session.Set("stationid", new PyInt(3001));
                    session.Set("corprole", new PyLong(72_057_594_037_927_936));
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
        public (long ContractId, long CharacterId, long CorporationId, bool ForCorporation, int WalletKey, decimal Amount) LastBid { get; private set; }
        public (long ItemId, long Quantity, long CharacterId, long CorporationId, long StationId, bool ForCorporation, int Flag) LastSplit { get; private set; }
        public CreateItemExchangeRequest? CreatedRequest { get; private set; }
        public (long ContractId, long CharacterId, long CorporationId, bool HasRole) LastDelete { get; private set; }
        public (long ContractId, long Status, long CharacterId, long CorporationId, long StationId, bool HasRole) LastComplete { get; private set; }
        public (long ContractId, long CharacterId, long CorporationId, bool ForCorp, int WalletKey, long StationId) LastAccept { get; private set; }

        public ValueTask<CreatedContract> CreateContractAsync(CreateItemExchangeRequest request, CancellationToken cancellationToken)
        {
            CreatedRequest = request;
            return ValueTask.FromResult(new CreatedContract(6001, request.Items.Select(item => item.ItemId).ToArray()));
        }

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
            bool bidForCorporation, int bidderWalletKey, decimal amount, CancellationToken cancellationToken)
        {
            LastBid = (contractId, characterId, corporationId, bidForCorporation, bidderWalletKey, amount);
            return ValueTask.FromResult(true);
        }

        public ValueTask<bool> SplitStackAsync(long itemId, long quantity, long characterId, long corporationId,
            long stationId, bool forCorporation, int flag, CancellationToken cancellationToken)
        {
            LastSplit = (itemId, quantity, characterId, corporationId, stationId, forCorporation, flag);
            return ValueTask.FromResult(true);
        }
    }

    private sealed class RecordingContractInventoryReader : IContractInventoryReader
    {
        public List<(string Kind, long StationId, long ContainerId, bool ForCorporation, int Flag)> Requests { get; } = [];

        public ValueTask<IReadOnlyList<ItemRow>> GetContractDockableItemsAsync(
            ClientSession session, long stationId, bool forCorporation, CancellationToken cancellationToken)
        {
            Requests.Add(("dockable", stationId, 0, forCorporation, 0));
            return ValueTask.FromResult<IReadOnlyList<ItemRow>>(
                [new ItemRow(forCorporation ? 8001 : 7001, 34, forCorporation ? 84 : 42, stationId,
                    forCorporation ? 115 : 4, 2, 25, 8)]);
        }

        public ValueTask<IReadOnlyList<ItemRow>> GetContractContainerItemsAsync(
            ClientSession session, long stationId, long containerId, bool forCorporation, int flagId,
            CancellationToken cancellationToken)
        {
            Requests.Add(("container", stationId, containerId, forCorporation, flagId));
            IReadOnlyList<ItemRow> rows = containerId switch
            {
                7001 => [
                    new ItemRow(9001, 35, 42, containerId, 4, 1, 26, 8),
                    new ItemRow(9002, 36, 42, containerId, 4, 3, 27, 8)],
                7003 => [new ItemRow(9003, 37, 42, containerId, 4, 1, 28, 8)],
                _ => []
            };
            return ValueTask.FromResult(rows);
        }
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
