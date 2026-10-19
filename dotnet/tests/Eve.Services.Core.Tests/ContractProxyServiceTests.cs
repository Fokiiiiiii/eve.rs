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
