using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;
using Xunit;

namespace Eve.Services.Core.Tests;

public sealed class ContractProxyServiceTests
{
    [Fact]
    public async Task Login_info_uses_the_three_client_rowsets_and_session_scope()
    {
        var model = new RecordingReadModel();
        var service = new ContractProxyService(model);
        var session = new ClientSession { CharacterId = 42 };
        session.Set("corpid", new PyInt(84));
        session.Set("allianceid", new PyInt(126));
        var response = await service.GetLoginInfo(new ServiceCall
        {
            Method = "GetLoginInfo",
            Args = [],
            Session = session,
        });

        Assert.Equal((42L, 84L, 126L), (model.CharacterId, model.CorporationId, model.AllianceId));
        var keyVal = Assert.IsType<PyObject>(response);
        var attention = Rowset(keyVal, "needsAttention");
        var progress = Rowset(keyVal, "inProgress");
        var assigned = Rowset(keyVal, "assignedToMe");
        Assert.Equal(new[] { "contractID", "state" }, Header(attention));
        Assert.Equal(new[] { "contractID", "startStationID", "endStationID", "expires" }, Header(progress));
        Assert.Equal(new[] { "contractID", "issuerID" }, Header(assigned));
        Assert.Equal(PyTuple.Of(new PyInt(1001), new PyInt(1)), Assert.Single(Lines(attention).Items));
        Assert.Equal(PyTuple.Of(new PyInt(1002), new PyInt(3001), new PyInt(3002), new PyLong(9876543210123456)), Assert.Single(Lines(progress).Items));
        Assert.Equal(PyTuple.Of(new PyInt(1003), new PyInt(42)), Assert.Single(Lines(assigned).Items));
        Assert.Equal(42L, model.CharacterId);
        Assert.True(model.NowFileTime > 0);
    }

    [Fact]
    public async Task Escrow_read_uses_the_persisted_summary()
    {
        var service = new ContractProxyService(new RecordingReadModel());
        var response = Assert.IsType<PyObject>(await service.GetMyContractEscrow(new ServiceCall
        {
            Method = "GetMyContractEscrow",
            Args = [],
            Session = new ClientSession { CharacterId = 42 },
        }));

        Assert.Equal(new PyInt(1250), Value(response, "iskEscrow"));
        Assert.Equal(new PyInt(3), Value(response, "itemsEscrow"));
    }


    [Fact]
    public async Task Get_my_bids_returns_persisted_contract_rows_and_requires_the_corporation_role()
    {
        var readModel = new RecordingReadModel();
        var service = new ContractProxyService(readModel);
        var denied = new ClientSession { CharacterId = 42 };
        denied.Set("corpid", new PyInt(84));
        await Assert.ThrowsAsync<MachoWrappedException>(() => service.GetMyBids(new ServiceCall
        {
            Method = "GetMyBids", Args = [PyBool.True], Session = denied,
        }));

        var session = new ClientSession { CharacterId = 42 };
        session.Set("corpid", new PyInt(84));
        session.Set("corprole", new PyLong(72057594037927936L));
        var response = Assert.IsType<PyObject>(await service.GetMyBids(new ServiceCall
        {
            Method = "GetMyBids", Args = [PyBool.True], Session = session,
        }));
        Assert.Equal(42, readModel.CharacterId);
        Assert.Equal(84, readModel.CorporationId);
        Assert.True(readModel.ForCorporation);
        var contracts = Assert.IsType<PyList>(Value(response, "contracts"));
        Assert.Single(contracts.Items);
        var contract = Assert.IsType<PyObject>(contracts.Items[0]);
        Assert.Equal(new PyInt(1234), Value(contract, "contractID"));
        Assert.Equal(new PyStr("Auction"), Value(contract, "title"));
        var itemMap = Assert.IsType<PyDict>(Value(response, "items"));
        var itemList = Assert.IsType<PyList>(itemMap.Entries.Single().Value);
        var item = Assert.IsType<PyObject>(itemList.Items[0]);
        Assert.Equal(new PyInt(34), Value(item, "itemTypeID"));
        Assert.Equal(new PyInt(2), Value(item, "quantity"));
    }


    [Fact]
    public async Task Owner_contract_list_checks_scope_role_and_forwards_page_cursor()
    {
        var readModel = new RecordingReadModel();
        var service = new ContractProxyService(readModel);
        var session = new ClientSession { CharacterId = 42 };
        session.Set("corpid", new PyInt(84));
        var response = Assert.IsType<PyObject>(await service.GetContractListForOwner(new ServiceCall
        {
            Method = "GetContractListForOwner",
            Args = [new PyLong(42), new PyInt(0), new PyInt(1), new PyInt(0)],
            Kwargs = Eve.Services.Py.Dict(("num", new PyInt(25)), ("startContractID", new PyLong(900))),
            Session = session,
        }));
        Assert.Equal(42, readModel.CharacterId);
        Assert.Equal(42, readModel.OwnerId);
        Assert.Equal(0, readModel.OwnerStatus);
        Assert.Equal(25, readModel.OwnerPageSize);
        Assert.Equal(900, readModel.OwnerStartContractId);
        Assert.Single(Assert.IsType<PyList>(Value(response, "contracts")).Items);

        await Assert.ThrowsAsync<MachoWrappedException>(() => service.GetContractListForOwner(new ServiceCall
        {
            Method = "GetContractListForOwner", Args = [new PyInt(84), new PyInt(0), new PyInt(1), new PyInt(0)], Session = session,
        }));
        session.Set("corprole", new PyLong(72057594037927936L));
        await service.GetContractListForOwner(new ServiceCall
        {
            Method = "GetContractListForOwner", Args = [new PyInt(84), new PyInt(0), new PyInt(1), new PyInt(0)], Session = session,
        });
    }

    [Fact]
    public async Task Place_bid_uses_client_argument_order_and_returns_boolean_success()
    {
        var writer = new RecordingWriteModel();
        var service = new ContractProxyService(new RecordingReadModel(), writer);
        var session = new ClientSession { CharacterId = 42 };
        session.Set("corpid", new PyInt(84));
        var response = await service.PlaceBid(new ServiceCall
        {
            Method = "PlaceBid",
            Args = [new PyInt(900), new PyInt(2500), PyBool.False],
            Session = session,
        });

        Assert.Same(PyBool.True, response);
        Assert.Equal((900L, 42L, 84L, false, 1000, 2500m), writer.LastBid);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(1e300)]
    [InlineData(0d)]
    [InlineData(-1d)]
    public async Task Place_bid_refuses_nonfinite_overflow_and_nonpositive_amounts(double amount)
    {
        var writer = new RecordingWriteModel();
        var service = new ContractProxyService(new RecordingReadModel(), writer);
        var session = new ClientSession { CharacterId = 42 };
        var error = await Assert.ThrowsAsync<MachoWrappedException>(() => service.PlaceBid(new ServiceCall
        {
            Method = "PlaceBid",
            Args = [new PyInt(900), new PyReal(amount), PyBool.False],
            Session = session,
        }));

        Assert.Equal(MachoErrors.UserErrorClass, error.Error.ClassName);
        Assert.Equal((0L, 0L, 0L, false, 0, 0m), writer.LastBid);
    }

    [Fact]
    public async Task Accept_contract_returns_the_settled_row_and_uses_session_identity()
    {
        var writer = new RecordingWriteModel();
        var service = new ContractProxyService(new RecordingReadModel(), writer);
        var session = new ClientSession { CharacterId = 42 };
        session.Set("corpid", new PyInt(84));
        session.Set("allianceid", new PyInt(126));
        session.Set("stationid", new PyInt(3001));
        var response = Assert.IsType<PyObject>(await service.AcceptContract(new ServiceCall
        {
            Method = "AcceptContract",
            Args = [new PyInt(999), PyBool.False],
            Session = session,
        }));

        Assert.Equal(new PyInt(999), Value(response, "contractID"));
        Assert.Equal(new PyInt(4), Value(response, "status"));
        Assert.Equal(new PyInt(42), Value(response, "acceptorID"));
        Assert.Equal((999L, 42L, 84L, 126L, false, 1000, 3001L), writer.LastAccept);
    }

    [Fact]
    public async Task Complete_contract_passes_status_station_and_corporation_role()
    {
        var writer = new RecordingWriteModel();
        var service = new ContractProxyService(new RecordingReadModel(), writer);
        var session = new ClientSession { CharacterId = 42 };
        session.Set("corpid", new PyInt(84));
        session.Set("stationid", new PyInt(3002));
        session.Set("corprole", new PyLong(72057594037927936L));

        var response = await service.CompleteContract(new ServiceCall
        {
            Method = "CompleteContract", Args = [new PyInt(900), new PyInt(4)], Session = session,
        });

        Assert.Same(PyBool.True, response);
        Assert.Equal((900L, 4, 42L, 84L, 3002L, true), writer.LastComplete);
    }

    [Fact]
    public async Task Delete_contract_passes_session_identity_and_corporation_role()
    {
        var writer = new RecordingWriteModel();
        var service = new ContractProxyService(new RecordingReadModel(), writer);
        var session = new ClientSession { CharacterId = 42 };
        session.Set("corpid", new PyInt(84));
        session.Set("corprole", new PyLong(72057594037927936L));

        var response = await service.DeleteContract(new ServiceCall
        {
            Method = "DeleteContract", Args = [new PyLong(900)], Session = session,
        });

        Assert.Same(PyBool.True, response);
        Assert.Equal((900L, 42L, 84L, true), writer.LastDelete);
    }

    [Fact]
    public async Task Create_contract_passes_client_terms_fees_and_skill_limits_to_the_writer()
    {
        var writer = new RecordingWriteModel();
        var skillLevels = new Dictionary<int, int> { [25235] = 3, [73912] = 2, [16622] = 4, [3446] = 1 };
        var service = new ContractProxyService(new RecordingReadModel(), writer,
            (characterId, typeId, ct) => ValueTask.FromResult(skillLevels.GetValueOrDefault(typeId)));
        var session = new ClientSession { CharacterId = 42 };
        session.Set("stationid", new PyInt(3001));
        session.Set("corpid", new PyInt(84));

        var response = Assert.IsType<PyList>(await service.CreateContract(new ServiceCall
        {
            Method = "CreateContract",
            Args = [ContractInfo(itemList: new PyList([PyTuple.Of(new PyInt(9001), new PyInt(2))]),
                requested: new PyList([PyTuple.Of(new PyInt(35), new PyInt(4))]))],
            Session = session,
        }));

        Assert.Equal(new PyInt(777), Assert.Single(response.Items));
        var request = Assert.Single(writer.Requests);
        Assert.Equal((42L, 84L, 3001L), (request.IssuerCharacterId, request.IssuerCorporationId, request.StartStationId));
        Assert.Equal((0, 0L, 1000), (request.Availability, request.AssigneeId, request.WalletAccountKey));
        Assert.Equal(41, request.OutstandingLimit);
        Assert.Equal((10_000m, 10_000m, 100_000m), (request.BrokerFee, request.SalesTax, request.Deposit));
        Assert.Equal(new[] { 9001L }, request.Items.Select(item => item.ItemId));
        Assert.Equal(2L, Assert.Single(request.Items).Quantity);
        Assert.Equal((35, 4L), (Assert.Single(request.RequestedItems!).TypeId, Assert.Single(request.RequestedItems!).Quantity));
    }

    [Fact]
    public async Task Create_contract_applies_the_station_adjustment_to_the_broker_fee()
    {
        var writer = new RecordingWriteModel();
        long callbackCharacterId = 0;
        long callbackSystemId = 0;
        var service = new ContractProxyService(new RecordingReadModel(), writer,
            (characterId, typeId, ct) => ValueTask.FromResult(0),
            (characterId, solarSystemId, ct) =>
            {
                callbackCharacterId = characterId;
                callbackSystemId = solarSystemId;
                return ValueTask.FromResult(0.5m);
            });
        var session = new ClientSession { CharacterId = 42 };
        session.Set("stationid", new PyInt(3001));
        session.Set("solarsystemid2", new PyInt(3002));
        session.Set("corpid", new PyInt(84));
        var contractStruct = new PyObject(new PyStr("contracts.contractStruct.ContractStruct"), PyTuple.Of(
            new PyInt(1), PyBool.False, new PyInt(0), new PyInt(1440), new PyInt(0), new PyInt(3001),
            new PyInt(0), new PyInt(10_000_000), new PyInt(0), new PyInt(0), new PyStr("Parts"), new PyStr(""),
            new PyList([PyTuple.Of(new PyInt(9001), new PyInt(1))]), new PyInt(4),
            new PyList([]), PyBool.False, PyBool.False));

        await service.CreateContract(new ServiceCall
        {
            Method = "CreateContract", Args = [contractStruct], Session = session,
        });

        var request = Assert.Single(writer.Requests);
        Assert.Equal((42L, 3002L), (callbackCharacterId, callbackSystemId));
        Assert.Equal(20_000m, request.BrokerFee);
    }

    [Fact]
    public async Task Create_contract_reads_the_V24_positional_contract_struct()
    {
        var writer = new RecordingWriteModel();
        var service = new ContractProxyService(new RecordingReadModel(), writer,
            (characterId, typeId, ct) => ValueTask.FromResult(0));
        var session = new ClientSession { CharacterId = 42 };
        session.Set("stationid", new PyInt(3001));
        session.Set("corpid", new PyInt(84));
        var contractStruct = new PyObject(new PyStr("contracts.contractStruct.ContractStruct"), PyTuple.Of(
            new PyInt(1), PyBool.False, new PyInt(0), new PyInt(1440), new PyInt(0), new PyInt(3001),
            new PyInt(0), new PyInt(100), new PyInt(50), new PyInt(0),
            new PyBytes(System.Text.Encoding.UTF8.GetBytes("Parts")), new PyBytes(ReadOnlyMemory<byte>.Empty),
            new PyList([PyTuple.Of(new PyInt(9001), new PyInt(2))]), new PyInt(4),
            new PyList([PyTuple.Of(new PyInt(35), new PyInt(4))]), PyBool.False, PyBool.False));

        var response = Assert.IsType<PyList>(await service.CreateContract(new ServiceCall
        {
            Method = "CreateContract", Args = [contractStruct], Session = session,
        }));

        Assert.Equal(new PyInt(777), Assert.Single(response.Items));
        var request = Assert.Single(writer.Requests);
        Assert.Equal((100m, 50m), (request.Price, request.Reward));
        Assert.Equal("Parts", request.Title);
        Assert.Equal(new[] { 9001L }, request.Items.Select(item => item.ItemId));
        Assert.Equal((35, 4L), (Assert.Single(request.RequestedItems!).TypeId, Assert.Single(request.RequestedItems!).Quantity));
    }

    [Fact]
    public async Task Accept_for_corporation_refuses_a_session_without_contract_manager_role()
    {
        var writer = new RecordingWriteModel();
        var service = new ContractProxyService(new RecordingReadModel(), writer);
        var session = new ClientSession { CharacterId = 42 };
        session.Set("corpid", new PyInt(84));
        session.Set("corprole", new PyLong(0));

        await Assert.ThrowsAsync<MachoWrappedException>(() => service.AcceptContract(new ServiceCall
        {
            Method = "AcceptContract",
            Args = [new PyInt(999), PyBool.True],
            Session = session,
        }));

        Assert.Equal(default, writer.LastAccept);
    }

    [Fact]
    public async Task Create_contract_refuses_corporation_issue_without_contract_manager_and_undocked_issue()
    {
        var service = new ContractProxyService(new RecordingReadModel(), new RecordingWriteModel(),
            (characterId, typeId, ct) => ValueTask.FromResult(0));
        var notDocked = new ClientSession { CharacterId = 42 };
        notDocked.Set("corpid", new PyInt(84));
        var notManager = new ClientSession { CharacterId = 42 };
        notManager.Set("stationid", new PyInt(3001));
        notManager.Set("corpid", new PyInt(84));
        notManager.Set("corprole", new PyLong(0));

        await Assert.ThrowsAsync<MachoWrappedException>(() => service.CreateContract(new ServiceCall
        {
            Method = "CreateContract", Args = [ContractInfo()], Session = notDocked,
        }));
        await Assert.ThrowsAsync<MachoWrappedException>(() => service.CreateContract(new ServiceCall
        {
            Method = "CreateContract", Args = [ContractInfo(forCorp: true)], Session = notManager,
        }));
    }

    private static PyObject ContractInfo(
        PyValue? itemList = null,
        PyValue? requested = null,
        bool forCorp = false) => new(new PyStr("util.KeyVal"), new PyDict(
        [
            Entry("contractType", new PyInt(1)),
            Entry("isPrivate", PyBool.False),
            Entry("assignedToID", new PyInt(0)),
            Entry("minutesExpire", new PyInt(1440)),
            Entry("numDays", new PyInt(0)),
            Entry("startStationID", new PyInt(3001)),
            Entry("destinationID", new PyInt(0)),
            Entry("price", new PyInt(100)),
            Entry("reward", new PyInt(50)),
            Entry("collateral", new PyInt(0)),
            Entry("title", new PyStr("Parts")),
            Entry("description", new PyStr("")),
            Entry("itemList", itemList ?? new PyList([])),
            Entry("startStationDivision", new PyInt(4)),
            Entry("requestItemTypeList", requested ?? new PyList([])),
            Entry("forCorp", forCorp ? PyBool.True : PyBool.False),
        ]));

    private static KeyValuePair<PyValue, PyValue> Entry(string name, PyValue value) => new(new PyStr(name), value);

    private sealed class RecordingWriteModel : IContractWriteModel
    {
        public List<CreateItemExchangeRequest> Requests { get; } = [];

        public (long ContractId, long CharacterId, long CorporationId, long AllianceId, bool ForCorporation, int WalletKey, long StationId) LastAccept { get; private set; }

        public (long ContractId, int Status, long CharacterId, long CorporationId, long StationId, bool IsManager) LastComplete { get; private set; }

        public (long ContractId, long CharacterId, long CorporationId, bool IsManager) LastDelete { get; private set; }

        public (long ContractId, long CharacterId, long CorporationId, bool ForCorporation, int WalletKey, decimal Amount) LastBid { get; private set; }

        public ValueTask<CreatedContract> CreateContractAsync(CreateItemExchangeRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return ValueTask.FromResult(new CreatedContract(777, [5001]));
        }

        public ValueTask<AcceptedContract> AcceptContractAsync(long contractId, long characterId, long corporationId,
            long allianceId, bool acceptForCorporation, int acceptorWalletKey, long currentStationId, CancellationToken cancellationToken)
        {
            LastAccept = (contractId, characterId, corporationId, allianceId, acceptForCorporation, acceptorWalletKey, currentStationId);
            return ValueTask.FromResult(new AcceptedContract(contractId, 1, 4, 100, 200, false, characterId, 3001, 0, 123, 0));
        }

        public ValueTask<AcceptedContract> AcceptItemExchangeAsync(long contractId, long characterId, long corporationId,
            long allianceId, bool acceptForCorporation, int acceptorWalletKey, CancellationToken cancellationToken) =>
            AcceptContractAsync(contractId, characterId, corporationId, allianceId, acceptForCorporation, acceptorWalletKey, 3001, cancellationToken);

        public ValueTask<bool> CompleteContractAsync(long contractId, int targetStatus, long characterId, long corporationId,
            long currentStationId, bool hasCorpContractManager, CancellationToken cancellationToken)
        {
            LastComplete = (contractId, targetStatus, characterId, corporationId, currentStationId, hasCorpContractManager);
            return ValueTask.FromResult(true);
        }

        public ValueTask<bool> DeleteContractAsync(long contractId, long characterId, long corporationId,
            bool hasCorpContractManager, CancellationToken cancellationToken)
        {
            LastDelete = (contractId, characterId, corporationId, hasCorpContractManager);
            return ValueTask.FromResult(true);
        }

        public ValueTask<bool> PlaceBidAsync(long contractId, long characterId, long corporationId, long allianceId,
            bool bidForCorporation, int bidderWalletKey, decimal amount, CancellationToken cancellationToken)
        {
            LastBid = (contractId, characterId, corporationId, bidForCorporation, bidderWalletKey, amount);
            return ValueTask.FromResult(true);
        }
    }

    private static PyObject Rowset(PyObject keyVal, string name) => Assert.IsType<PyObject>(Value(keyVal, name));

    private static string[] Header(PyObject rowset) =>
        Assert.IsType<PyList>(Value(rowset, "header")).Items.Select(value => Assert.IsType<PyStr>(value).Value).ToArray();

    private static PyList Lines(PyObject rowset) => Assert.IsType<PyList>(Value(rowset, "lines"));

    private static PyValue Value(PyObject value, string name)
    {
        var dict = Assert.IsType<PyDict>(value.Args);
        return dict.Entries.Single(entry => Assert.IsType<PyStr>(entry.Key).Value == name).Value;
    }

    private sealed class RecordingReadModel : IContractReadModel
    {
        public long CharacterId { get; private set; }
        public long CorporationId { get; private set; }
        public long AllianceId { get; private set; }
        public long NowFileTime { get; private set; }
        public bool ForCorporation { get; private set; }
        public long OwnerId { get; private set; }
        public int OwnerStatus { get; private set; }
        public int OwnerPageSize { get; private set; }
        public long? OwnerStartContractId { get; private set; }

        public ValueTask<ContractLoginInfo> GetLoginInfoAsync(
            long characterId, long corporationId, long allianceId, long nowFileTime, CancellationToken cancellationToken)
        {
            CharacterId = characterId;
            CorporationId = corporationId;
            AllianceId = allianceId;
            NowFileTime = nowFileTime;
            return ValueTask.FromResult(new ContractLoginInfo(
                [new ContractNeedsAttentionRow(1001, 1)],
                [new ContractInProgressRow(1002, 3001, 3002, 9876543210123456)],
                [new ContractAssignedToMeRow(1003, 42)]));
        }

        public ValueTask<ContractEscrowSummary> GetMyContractEscrowAsync(
            long characterId, long corporationId, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new ContractEscrowSummary(1250m, 3));

        public ValueTask<ContractReadBundle> GetMyBidsAsync(
            long characterId, long corporationId, bool forCorporation, long nowFileTime, CancellationToken cancellationToken)
        {
            CharacterId = characterId;
            CorporationId = corporationId;
            ForCorporation = forCorporation;
            return ValueTask.FromResult(new ContractReadBundle([Contract()]));
        }

        public ValueTask<ContractReadBundle> GetContractListForOwnerAsync(
            long characterId, long corporationId, long ownerId, int status, int contractType, long issuedBy,
            int pageSize, long? startContractId, long nowFileTime, CancellationToken cancellationToken)
        {
            CharacterId = characterId;
            CorporationId = corporationId;
            OwnerId = ownerId;
            OwnerStatus = status;
            OwnerPageSize = pageSize;
            OwnerStartContractId = startContractId;
            return ValueTask.FromResult(new ContractReadBundle([Contract()]));
        }

        private static ContractReadSnapshot Contract() => new(
            1234, 2, 0, 0, 100, 200, false, 0, 0, 1000, 2000, 0, 0, 0,
            3001, 0, 5000m, 0m, 0m, "Auction", "Item exchange",
            [new ContractReadItemSnapshot(8, 34, 2, true, false)],
            [new ContractReadBidSnapshot(1, 1000, 5000m, 1500, false)]);
    }
}
