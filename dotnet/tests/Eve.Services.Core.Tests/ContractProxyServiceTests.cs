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
    public async Task Accept_contract_returns_the_settled_row_and_uses_session_identity()
    {
        var writer = new RecordingWriteModel();
        var service = new ContractProxyService(new RecordingReadModel(), writer);
        var session = new ClientSession { CharacterId = 42 };
        session.Set("corpid", new PyInt(84));
        session.Set("allianceid", new PyInt(126));
        var response = Assert.IsType<PyObject>(await service.AcceptContract(new ServiceCall
        {
            Method = "AcceptContract",
            Args = [new PyInt(999), PyBool.False],
            Session = session,
        }));

        Assert.Equal(new PyInt(999), Value(response, "contractID"));
        Assert.Equal(new PyInt(4), Value(response, "status"));
        Assert.Equal(new PyInt(42), Value(response, "acceptorID"));
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

        public ValueTask<CreatedContract> CreateContractAsync(CreateItemExchangeRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return ValueTask.FromResult(new CreatedContract(777, [5001]));
        }

        public ValueTask<AcceptedContract> AcceptItemExchangeAsync(long contractId, long characterId, long corporationId,
            long allianceId, bool acceptForCorporation, int acceptorWalletKey, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new AcceptedContract(contractId, 1, 4, 100, 200, false, characterId, 3001, 0, 123, 0));
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
    }
}
