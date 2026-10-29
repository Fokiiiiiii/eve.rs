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
    private string? _lastContractRpcProblem;

    [Fact]
    public async Task Contract_reads_and_mutations_are_dispatched_through_the_host_with_client_shapes()
    {
        var writer = new RecordingContractWriter();
        var reads = new RecordingContractReadModel();
        var inventory = new RecordingContractInventoryReader();
        StartContractHost(writer, reads, inventory,
            isCharacterInCurrentCorporation: (characterId, _, _) => ValueTask.FromResult(characterId == 43));
        var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        _ = await front.ReceiveAsync();

        async Task<PyValue> Rpc(uint id, string method, params PyValue[] args)
        {
            // These client calls send application kwargs in addition to the transport version.
            var extraKwargs = method switch
            {
                "CreateContract" => new (string Name, PyValue Value)[] { ("confirm", PyNone.Instance) },
                "GetContractListForOwner" => new (string Name, PyValue Value)[]
                {
                    ("num", new PyInt(100)), ("startContractID", PyNone.Instance)
                },
                _ => Array.Empty<(string Name, PyValue Value)>(),
            };
            return await RpcWithKwargs(id, method, args, extraKwargs);
        }

        async Task<PyValue> RpcWithKwargs(uint id, string method, PyValue[] args,
            params (string Name, PyValue Value)[] extraKwargs)
        {
            var entries = new List<KeyValuePair<PyValue, PyValue>>
            {
                new(new PyStr("machoVersion"), new PyInt(1))
            };
            entries.AddRange(extraKwargs.Select(entry =>
                new KeyValuePair<PyValue, PyValue>(new PyStr(entry.Name), entry.Value)));
            await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
                Packets.CallWithKwargs(open, id, "contractProxy", method, new PyTuple(args), new PyDict(entries)))));
            // The client link can carry notifications before a pending RPC response.
            while (true)
            {
                var reply = Assert.IsType<Send>(await front.ReceiveAsync());
                var response = MarshalCodec.Decode(reply.Payload);
                if (response is not PyObject { Args: PyTuple responseArgs } || responseArgs.Items.Count < 3 ||
                    MachoAddress.Decode(responseArgs.Items[2]).CallId != new PyInt(id))
                    continue;
                var responseState = responseArgs.Items;
                Assert.True(((PyInt)responseState[0]).Value == 7,
                    $"{method} returned an error response: {FormatRpcValue(responseState[4])} {_lastContractRpcProblem}");
                return Result(response)!;
            }
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

        var internalAssignment = Assert.IsType<PyList>(await Rpc(31, "CreateContract", new PyObject(new PyStr("contracts.contractStruct.ContractStruct"), new PyTuple([
            new PyInt(1), PyBool.True, new PyInt(43), new PyInt(1440), new PyInt(0),
            new PyInt(3001), new PyInt(0), new PyInt(1000), new PyInt(0), new PyInt(0),
            new PyStr("internal assignment"), new PyStr(""), new PyList([]),
            new PyInt(4), new PyList([]), PyBool.False, PyBool.False]))));
        Assert.Equal([new PyInt(6001)], internalAssignment.Items);
        Assert.Equal(500, writer.CreatedRequest?.OutstandingLimit);
        var externalAssignment = Assert.IsType<PyList>(await Rpc(34, "CreateContract", new PyObject(new PyStr("contracts.contractStruct.ContractStruct"), new PyTuple([
            new PyInt(1), PyBool.True, new PyInt(44), new PyInt(1440), new PyInt(0),
            new PyInt(3001), new PyInt(0), new PyInt(1000), new PyInt(0), new PyInt(0),
            new PyStr("external assignment"), new PyStr(""), new PyList([]),
            new PyInt(4), new PyList([]), PyBool.False, PyBool.False]))));
        Assert.Equal([new PyInt(6001)], externalAssignment.Items);
        Assert.Equal(28, writer.CreatedRequest?.OutstandingLimit);

        var typeFilter = new PyDict([
            new KeyValuePair<PyValue, PyValue>(new PyInt(34), PyNone.Instance),
            new KeyValuePair<PyValue, PyValue>(new PyLong(35), PyNone.Instance),
        ]);
        _ = await RpcWithKwargs(32, "SearchContracts", [], ("searchHint", new PyInt(0)), ("itemTypes", typeFilter));
        Assert.Equal(0, reads.LastSearchQuery?.SearchHint);
        _ = await RpcWithKwargs(35, "SearchContracts", [], ("searchHint", new PyInt(1)), ("itemTypes", typeFilter));
        Assert.Equal(1, reads.LastSearchQuery?.SearchHint);
        Assert.Equal([34, 35], reads.LastSearchQuery?.ItemTypeIds);
        _ = await RpcWithKwargs(33, "SearchContracts", [], ("searchHint", new PyInt(2)), ("itemTypes", typeFilter));
        Assert.Equal(2, reads.LastSearchQuery?.SearchHint);

        var login = Fields(Assert.IsType<PyObject>(await Rpc(3, "GetLoginInfo")));
        Assert.Equal(new[] { "assignedToMe", "inProgress", "needsAttention" }, login.Keys.Order().ToArray());

        var escrow = Fields(Assert.IsType<PyObject>(await Rpc(4, "GetMyContractEscrow")));
        Assert.Equal(new PyInt(1250), escrow["iskEscrow"]);
        Assert.Equal(new PyInt(3), escrow["itemsEscrow"]);

        var bids = Fields(Assert.IsType<PyObject>(await Rpc(5, "GetMyBids", PyBool.False)));
        Assert.Single(Assert.IsType<PyList>(bids["contracts"]).Items);
        var corporationBids = Fields(Assert.IsType<PyObject>(await Rpc(18, "GetMyBids", PyBool.True)));
        Assert.Single(Assert.IsType<PyList>(corporationBids["contracts"]).Items);
        Assert.Equal([false, true, false, true], reads.BidForCorporationRequests);

        var ownerContracts = Fields(Assert.IsType<PyObject>(await Rpc(6, "GetContractListForOwner",
            new PyInt(42), new PyInt(0), new PyInt(1), new PyInt(0))));
        Assert.Equal(2, Assert.IsType<PyList>(ownerContracts["contracts"]).Items.Count);
        Assert.Contains((42L, 0, 1, 0L, 100, (long?)null), reads.OwnerRequests);

        var current = Fields(Assert.IsType<PyObject>(await Rpc(7, "GetMyCurrentContractList", PyBool.False, PyBool.False)));
        Assert.Single(Assert.IsType<PyList>(current["contracts"]).Items);
        var corporationCurrent = Fields(Assert.IsType<PyObject>(await Rpc(19, "GetMyCurrentContractList", PyBool.False, PyBool.True)));
        Assert.Single(Assert.IsType<PyList>(corporationCurrent["contracts"]).Items);
        Assert.Equal([(true, false), (true, true), (false, false), (false, true)], reads.CurrentContractRequests);

        var expired = Fields(Assert.IsType<PyObject>(await Rpc(8, "GetMyExpiredContractList", PyBool.False)));
        Assert.Single(Assert.IsType<PyList>(expired["contracts"]).Items);
        var corporationExpired = Fields(Assert.IsType<PyObject>(await Rpc(20, "GetMyExpiredContractList", PyBool.True)));
        Assert.Single(Assert.IsType<PyList>(corporationExpired["contracts"]).Items);
        Assert.Equal([false, true], reads.ExpiredForCorporationRequests);

        var accepted = Fields(Assert.IsType<PyObject>(await Rpc(9, "AcceptContract", new PyInt(5003), PyBool.False)));
        Assert.Equal(new PyInt(5003), accepted["contractID"]);
        Assert.Same(PyBool.True, await Rpc(10, "CompleteContract", new PyInt(5001), new PyInt(4)));
        Assert.Equal((5001L, 4L, 42L, 84L, 126L, 3001L, true), writer.LastComplete);
        Assert.Same(PyBool.True, await Rpc(21, "CompleteContract", new PyInt(5001), new PyInt(7)));
        Assert.Equal((5001L, 7L, 42L, 84L, 126L, 3001L, true), writer.LastComplete);
        Assert.Same(PyBool.True, await Rpc(22, "CompleteContract", new PyInt(5001), new PyInt(6)));
        Assert.Equal((5001L, 6L, 42L, 84L, 126L, 3001L, true), writer.LastComplete);
        Assert.Same(PyBool.True, await Rpc(11, "DeleteContract", new PyInt(6001)));
        Assert.Equal((6001L, 42L, 84L, true), writer.LastDelete);
        Assert.Equal((5003L, 42L, 84L, false, 1000, 3001L), writer.LastAccept);

        var detail = Fields(Assert.IsType<PyObject>(await Rpc(23, "GetContract", new PyInt(5002))));
        var detailContract = Assert.IsType<PyPackedRow>(detail["contract"]);
        Assert.Equal(new PyInt(5002), detailContract.Fields().Single(field => field.Key == "contractID").Value);
        Assert.Single(Assert.IsType<PyList>(detail["items"]).Items);
        Assert.Single(Assert.IsType<PyList>(detail["bids"]).Items);
        var courier = Fields(Assert.IsType<PyObject>(await Rpc(24, "GetCourierContractFromItemID", new PyInt(9001))));
        Assert.Equal(new PyInt(5003), courier["contractID"]);

        Assert.Same(PyBool.True, await Rpc(25, "DeleteNotification", new PyInt(5004), PyBool.False));
        Assert.Same(PyNone.Instance, await Rpc(26, "DeleteContractNotification", new PyInt(5005), PyBool.True));
        Assert.Same(PyBool.True, await Rpc(27, "FinishAuction", new PyInt(5002), PyBool.True));
        Assert.Same(PyBool.True, await Rpc(28, "FinishAuction", new PyInt(5002), PyBool.False));
        Assert.Same(PyBool.True, await Rpc(30, "FinishAuction", new PyInt(5007), PyBool.False));
        Assert.Same(PyBool.True, await Rpc(29, "GM_ExpireContract", new PyInt(5006)));
        Assert.Equal([
            (5004L, 42L, 84L, false),
            (5005L, 42L, 84L, true)
        ], writer.Dismissed);
        Assert.Equal([5002L, 5002L], writer.Finished);
        Assert.Equal([5006L], writer.Expired);
    }

    [Fact]
    public async Task Gm_contract_expiry_is_refused_for_a_player_account()
    {
        var writer = new RecordingContractWriter();
        StartContractHost(writer, staff: false);
        var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        _ = await front.ReceiveAsync();
        await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
            Packets.CallWithKwargs(open, 1, "contractProxy", "GM_ExpireContract",
                new PyTuple([new PyInt(5006)]), Eve.Services.Py.Dict(("machoVersion", new PyInt(1)))))));

        var reply = Assert.IsType<Send>(await front.ReceiveAsync());
        var response = Assert.IsType<PyObject>(MarshalCodec.Decode(reply.Payload));
        var state = Assert.IsType<PyTuple>(response.Args);
        Assert.Equal(new PyInt(MachoMessage.ErrorResponse), state.Items[0]);
        Assert.Empty(writer.Expired);
    }

    [Fact]
    public async Task Contract_notification_dismissal_domain_errors_remain_user_errors_over_host_wire()
    {
        var writer = new RecordingContractWriter { ThrowOnDismissal = true };
        StartContractHost(writer);
        var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        _ = await front.ReceiveAsync();

        async Task AssertUserError(uint id, string method, long contractId, bool forCorporation)
        {
            await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
                Packets.CallWithKwargs(open, id, "contractProxy", method,
                    new PyTuple([new PyInt(contractId), forCorporation ? PyBool.True : PyBool.False]),
                    Eve.Services.Py.Dict(("machoVersion", new PyInt(1)))))));
            var reply = Assert.IsType<Send>(await front.ReceiveAsync());
            var response = Assert.IsType<PyObject>(MarshalCodec.Decode(reply.Payload));
            var state = Assert.IsType<PyTuple>(response.Args);
            Assert.Equal(new PyInt(MachoMessage.ErrorResponse), state.Items[0]);
            var callError = Assert.IsType<PyTuple>(state.Items[4]);
            var wrapped = Assert.IsType<PyTuple>(callError.Items[2]);
            var payload = Assert.IsType<PyObjectEx>(Assert.IsType<PySubStream>(wrapped.Items[0]).Decoded);
            var header = Assert.IsType<PyTuple>(payload.Header);
            Assert.Equal(new PyToken(MachoErrors.UserErrorClass), header.Items[0]);
            var errorArgs = Assert.IsType<PyTuple>(header.Items[1]);
            Assert.Equal("ConContractNotAvailable", System.Text.Encoding.UTF8.GetString(
                Assert.IsType<PyBytes>(errorArgs.Items[0]).Value.Span));
        }

        await AssertUserError(1, "DeleteNotification", 5004, false);
        await AssertUserError(2, "DeleteContractNotification", 5005, true);
        Assert.Equal(2, writer.Dismissed.Count);
    }

    [Fact]
    public async Task Contract_missing_detail_and_keyval_multicontract_return_client_user_errors()
    {
        var writer = new RecordingContractWriter();
        StartContractHost(writer, new RecordingContractReadModel());
        var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        _ = await front.ReceiveAsync();

        await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
            Packets.CallWithKwargs(open, 1, "contractProxy", "GetContract",
                new PyTuple([new PyInt(9999)]), Eve.Services.Py.Dict(("machoVersion", new PyInt(1)))))));
        var missing = Assert.IsType<Send>(await front.ReceiveAsync());
        Assert.Equal("ConContractNotFound", UserErrorKey(missing));

        var contractInfo = new PyObject(new PyStr("util.KeyVal"), new PyDict([
            new KeyValuePair<PyValue, PyValue>(new PyStr("contractType"), new PyInt(1)),
            new KeyValuePair<PyValue, PyValue>(new PyStr("minutesExpire"), new PyInt(1440)),
            new KeyValuePair<PyValue, PyValue>(new PyStr("startStationID"), new PyInt(3001)),
            new KeyValuePair<PyValue, PyValue>(new PyStr("multiContract"), PyBool.True),
        ]));
        await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
            Packets.CallWithKwargs(open, 2, "contractProxy", "CreateContract",
                new PyTuple([contractInfo]),
                Eve.Services.Py.Dict(("machoVersion", new PyInt(1)), ("confirm", PyNone.Instance))))));
        var multi = Assert.IsType<Send>(await front.ReceiveAsync());
        Assert.Equal("CustomInfo", UserErrorKey(multi));
        Assert.Null(writer.CreatedRequest);

        var tupleInfo = new PyObject(new PyStr("contracts.contractStruct.ContractStruct"), new PyTuple([
            new PyInt(1), PyBool.False, new PyInt(0), new PyInt(1440), new PyInt(0),
            new PyInt(3001), new PyInt(0), new PyInt(0), new PyInt(0), new PyInt(0),
            new PyStr("multi contract"), new PyStr(""), new PyList([]),
            new PyInt(4), new PyList([]), PyBool.False, PyBool.True]));
        await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
            Packets.CallWithKwargs(open, 3, "contractProxy", "CreateContract",
                new PyTuple([tupleInfo]),
                Eve.Services.Py.Dict(("machoVersion", new PyInt(1)), ("confirm", PyNone.Instance))))));
        var tupleMulti = Assert.IsType<Send>(await front.ReceiveAsync());
        Assert.Equal("CustomInfo", UserErrorKey(tupleMulti));
        Assert.Null(writer.CreatedRequest);
    }

    private void StartContractHost(RecordingContractWriter writer, IContractReadModel? readModel = null,
        IContractInventoryReader? inventory = null, bool staff = true,
        Func<long, long, CancellationToken, ValueTask<bool>>? isCharacterInCurrentCorporation = null)
    {
        var registry = new ServiceRegistry();
        Func<long> now = () => FixedNowMs;
        foreach (var service in CoreServices.Create(
            new NoAccounts(), now, contractReadModel: readModel, contractWriteModel: writer,
            contractSkillLevel: (_, _, _) => ValueTask.FromResult(0), contractInventoryReader: inventory,
            readCurrentCalendarGroups: (_, _) => ValueTask.FromResult((84L, 126L)),
            isCharacterInCurrentCorporation: isCharacterInCurrentCorporation))
            registry.Register(service);

        var config = GlobalConfig.Build(Options);
        registry.Register(new MachoNetService(new Info(registry, config), now));
        var dispatcher = new PacketDispatcher(registry, now)
        {
            // This RPC batch tests contract wire shapes; budget refusal has separate coverage.
            MaxExpensiveRpcUnitsPerMinute = 100_000,
            OnProblem = (message, _) => _lastContractRpcProblem = message,
        };
        var encoded = MarshalCodec.Encode(config);
        _server = new HostServer(
            new HostOptions
            {
                MaxFrontConnections = 1024,
                SessionOpened = session =>
                {
                    session.Set("charid", new PyInt(42));
                    session.Set("corpid", new PyInt(84));
                    session.Set("allianceid", new PyInt(126));
                    session.Set("stationid", new PyInt(3001));
                    session.Set("corprole", new PyLong(72_057_594_037_927_936));
                    if (staff) session.Set("accountRole", new PyLong(GameMasterRoles.Gml));
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
        public (long ContractId, long Status, long CharacterId, long CorporationId, long AllianceId, long StationId, bool HasRole) LastComplete { get; private set; }
        public (long ContractId, long CharacterId, long CorporationId, bool ForCorp, int WalletKey, long StationId) LastAccept { get; private set; }
        public List<(long ContractId, long CharacterId, long CorporationId, bool ForCorporation)> Dismissed { get; } = [];
        public List<long> Finished { get; } = [];
        public List<long> Expired { get; } = [];
        public bool ThrowOnDismissal { get; set; }
        public ContractUserErrorException? NextCreateError { get; set; }
        public ContractUserErrorException? NextBidError { get; set; }

        public ValueTask<CreatedContract> CreateContractAsync(CreateItemExchangeRequest request, CancellationToken cancellationToken)
        {
            var error = NextCreateError;
            NextCreateError = null;
            if (error is not null)
                return ValueTask.FromException<CreatedContract>(error);
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
            long allianceId, long currentStationId, bool hasCorpContractManager, CancellationToken cancellationToken)
        {
            LastComplete = (contractId, targetStatus, characterId, corporationId, allianceId, currentStationId, hasCorpContractManager);
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
            var error = NextBidError;
            NextBidError = null;
            return error is null ? ValueTask.FromResult(true) : ValueTask.FromException<bool>(error);
        }

        public ValueTask<bool> SplitStackAsync(long itemId, long quantity, long characterId, long corporationId,
            long stationId, bool forCorporation, int flag, CancellationToken cancellationToken)
        {
            LastSplit = (itemId, quantity, characterId, corporationId, stationId, forCorporation, flag);
            return ValueTask.FromResult(true);
        }

        public ValueTask<bool> DismissNotificationAsync(long contractId, long characterId, long corporationId,
            bool forCorporation, bool hasCorpContractManager, CancellationToken cancellationToken)
        {
            Dismissed.Add((contractId, characterId, corporationId, forCorporation));
            return ThrowOnDismissal
                ? ValueTask.FromException<bool>(new ContractUserErrorException("ConContractNotAvailable"))
                : ValueTask.FromResult(true);
        }

        public ValueTask<bool> FinishAuctionAsync(long contractId, CancellationToken cancellationToken)
        {
            Finished.Add(contractId);
            return ValueTask.FromResult(true);
        }

        public ValueTask<bool> ExpireContractNowAsync(long contractId, CancellationToken cancellationToken)
        {
            Expired.Add(contractId);
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
        public List<(long OwnerId, int Status, int ContractType, long IssuedBy, int PageSize, long? StartContractId)> OwnerRequests { get; } = [];
        public ContractSearchQuery? LastSearchQuery { get; private set; }

        public ValueTask<ContractDetailRead?> GetContractAsync(long contractId, CancellationToken cancellationToken)
        {
            if (contractId == 9999)
                return ValueTask.FromResult<ContractDetailRead?>(null);
            var contract = Contract(contractId, 42, 84, false) with
            {
                Type = 2,
                Status = contractId == 5007 ? (short)4 : (short)0,
                Bids = [new ContractReadBidSnapshot(1, 42, 5000m, 1500, false)],
            };
            return ValueTask.FromResult<ContractDetailRead?>(new ContractDetailRead(contract,
                30_000_001, 10_000_001, 30_000_002, 10_000_002, 20_000_001, 20_000_002,
                "Solar System", [5], [6], [34]));
        }

        public ValueTask<ContractReadSnapshot?> GetCourierContractFromItemIdAsync(long itemId, CancellationToken cancellationToken) =>
            ValueTask.FromResult<ContractReadSnapshot?>(Contract(5003, 42, 84, false));

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

        public List<bool> BidForCorporationRequests { get; } = [];
        public List<(bool IsAccepted, bool ForCorporation)> CurrentContractRequests { get; } = [];
        public List<bool> ExpiredForCorporationRequests { get; } = [];

        public ValueTask<ContractReadBundle> GetMyBidsAsync(
            long characterId, long corporationId, bool forCorporation, long nowFileTime, CancellationToken cancellationToken)
        {
            BidForCorporationRequests.Add(forCorporation);
            return ValueTask.FromResult(new ContractReadBundle([
                Contract(forCorporation ? 4002 : 4001, forCorporation ? corporationId : characterId,
                    corporationId, forCorporation)]));
        }

        public ValueTask<ContractReadBundle> GetContractListForOwnerAsync(
            long characterId, long corporationId, long ownerId, int status, int contractType, long issuedBy,
            int pageSize, long? startContractId, long nowFileTime, CancellationToken cancellationToken)
        {
            OwnerIds.Add(ownerId);
            OwnerRequests.Add((ownerId, status, contractType, issuedBy, pageSize, startContractId));
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
            ContractSearchQuery query, long nowFileTime, CancellationToken cancellationToken)
        {
            LastSearchQuery = query;
            return ValueTask.FromResult(new ContractSearchResult([], 0, 0));
        }

        public ValueTask<ContractReadBundle> GetMyCurrentContractListAsync(
            long characterId, long corporationId, bool isAccepted, bool forCorporation,
            long nowFileTime, CancellationToken cancellationToken)
        {
            CurrentContractRequests.Add((isAccepted, forCorporation));
            return ValueTask.FromResult(new ContractReadBundle([
                Contract(forCorporation ? 5006 : 5004, forCorporation ? corporationId : characterId,
                    corporationId, forCorporation)]));
        }

        public ValueTask<ContractReadBundle> GetMyExpiredContractListAsync(
            long characterId, long corporationId, bool forCorporation, long nowFileTime,
            CancellationToken cancellationToken)
        {
            ExpiredForCorporationRequests.Add(forCorporation);
            return ValueTask.FromResult(new ContractReadBundle([
                Contract(forCorporation ? 5007 : 5005, forCorporation ? corporationId : characterId,
                    corporationId, forCorporation)]));
        }

        private static ContractReadSnapshot Contract(long id, long issuer, long corporation, bool issuedByCorporation) =>
            new(id, 1, 0, 0, issuer, corporation, issuedByCorporation, 0, 0, 1, long.MaxValue, 0, 0,
                0, 3001, 0, 5000m, 0m, 0m, "Contract", "Description",
                [new ContractReadItemSnapshot(7001, 34, 2, true, false)], []);
    }

    private static string FormatRpcValue(PyValue? value) => value switch
    {
        null => "<null>",
        PyNone => "None",
        PyBool boolean => boolean.Value ? "True" : "False",
        PyInt integer => integer.Value.ToString(),
        PyLong integer => integer.Value.ToString(),
        PyReal number => number.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
        PyStr text => text.Value,
        PyBytes bytes => System.Text.Encoding.UTF8.GetString(bytes.Value.Span),
        PyWString text => text.Value,
        PyToken token => token.Value,
        PyTuple tuple => $"({string.Join(", ", tuple.Items.Select(FormatRpcValue))})",
        PyList list => $"[{string.Join(", ", list.Items.Select(FormatRpcValue))}]",
        PyDict dict => $"{{{string.Join(", ", dict.Entries.Select(entry => $"{FormatRpcValue(entry.Key)}: {FormatRpcValue(entry.Value)}"))}}}",
        PySubStream { Decoded: { } decoded } => FormatRpcValue(decoded),
        PyObject obj => FormatRpcValue(obj.Args),
        PyObjectEx objectEx => $"object(header={FormatRpcValue(objectEx.Header)}, dict={FormatRpcValue(new PyDict(objectEx.Dict))})",
        _ => value.GetType().Name,
    };

    private static IReadOnlyDictionary<string, PyValue> Fields(PyObject value) =>
        Assert.IsType<PyDict>(value.Args).Entries.ToDictionary(
            pair => T(pair.Key)!, pair => pair.Value);
}
