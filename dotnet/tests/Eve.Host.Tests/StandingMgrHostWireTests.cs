using Eve.Marshal;
using Eve.Services.Corporations;
using Eve.ReadSide;
using Npgsql;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [Fact]
    public async Task Standing_history_and_composition_keep_client_wire_shapes_through_host_dispatch()
    {
        await using var source = NpgsqlDataSource.Create("Host=localhost;Timeout=1");
        var service = new StandingMgrService(new Eve.ReadSide.ReadSide(source), null!, () => FixedNowMs);
        Start(configure: registry => registry.Register(service));
        var (front, open) = await OpenFrontAsync();

        var transactions = Assert.IsType<PyList>(await HostRpcAsync(
            front, open, 1, "standingMgr", "GetStandingTransactions", new PyLong(500001), new PyLong(900001)));
        Assert.Empty(transactions.Items);

        var cached = Assert.IsType<PyObject>(await HostRpcAsync(
            front, open, 2, "standingMgr", "GetStandingCompositions", new PyLong(500001), new PyLong(980001)));
        Assert.Equal("carbon.common.script.net.objectCaching.CachedMethodCallResult", WireText(cached.Name));
        var payload = Assert.IsType<PySubStream>(Assert.IsType<PyTuple>(cached.Args).Items[1]).Decoded!;
        var rowset = Assert.IsType<PyObject>(payload);
        Assert.Equal("carbon.common.script.sys.crowset.CRowset", WireText(rowset.Name));
        Assert.Empty(Assert.IsType<PyList>(WireField(rowset, "lines")).Items);
        var descriptorColumns = Assert.IsType<PyObjectEx>(WireField(rowset, "header"));
        var fields = Descriptor.DescriptorColumns(descriptorColumns)!;
        Assert.Equal(["standing", "ownerID"], fields.Select(field =>
            WireText(Assert.IsType<PyTuple>(field).Items[0])).ToArray());
        Assert.Equal([5L, 3L], fields.Select(field =>
            WireInteger(Assert.IsType<PyTuple>(field).Items[1])).ToArray());
    }

    [Fact]
    public async Task Standing_rowsets_and_history_use_client_consumed_wire_shapes_through_host_dispatch()
    {
        const long fromId = 500001;
        const long characterId = 900001;
        var reads = new TestReadSide(characterId);
        var service = new StandingMgrService(reads, null!, () => FixedNowMs);
        Start(
            configure: registry => registry.Register(service),
            sessionOpened: session =>
            {
                session.CharacterId = characterId;
                session.Set("charid", new PyLong(characterId));
            });
        var (front, open) = await OpenFrontAsync();

        var npc = Assert.IsType<PyObject>(await HostRpcAsync(front, open, 1, "standingMgr", "GetNPCNPCStandings"));
        Assert.Equal("eve.common.script.sys.rowset.Rowset", WireText(npc.Name));
        Assert.Equal(["fromID", "toID", "standing"], Header(npc));
        var npcLine = Assert.IsType<PyList>(Assert.Single(Assert.IsType<PyList>(WireField(npc, "lines")).Items));
        Assert.Equal([new PyInt(fromId), new PyInt(characterId), new PyReal(-2.5)], npcLine.Items);

        var character = Assert.IsType<PyObject>(await HostRpcAsync(front, open, 2, "standingMgr", "GetCharStandings"));
        Assert.Equal("eve.common.script.sys.rowset.Rowset", WireText(character.Name));
        Assert.Equal(["fromID", "standing"], Header(character));
        var characterLine = Assert.IsType<PyList>(Assert.Single(Assert.IsType<PyList>(WireField(character, "lines")).Items));
        Assert.Equal([new PyInt(fromId), new PyReal(2.5)], characterLine.Items);

        var transactions = Assert.IsType<PyList>(await HostRpcAsync(front, open, 3, "standingMgr",
            "GetStandingTransactions", new PyLong(fromId), new PyLong(characterId)));
        var transaction = Assert.Single(transactions.Items);
        Assert.Equal(new PyInt(12), WireField(transaction, "eventTypeID"));
        Assert.Equal(new PyLong(Eve.Services.Py.FileTimeFromUnixMs(FixedNowMs)), WireField(transaction, "eventDateTime"));
        Assert.Equal(new PyReal(0.75), WireField(transaction, "modification"));
        Assert.Equal(new PyInt(fromId), WireField(transaction, "fromID"));
        Assert.Equal(new PyInt(characterId), WireField(transaction, "toID"));
        Assert.Equal("Standing adjusted", Assert.IsType<PyWString>(WireField(transaction, "msg")).Value);
        Assert.Same(PyNone.Instance, WireField(transaction, "int_1"));
    }

    private static string[] Header(PyObject rowset) =>
        Assert.IsType<PyList>(WireField(rowset, "header")).Items.Select(WireText).ToArray();

    private sealed class TestReadSide(long characterId) : IRowsetReadSide
    {
        private readonly Dictionary<string, RowsetQuery> _queries = new(StringComparer.Ordinal);

        public void Register(RowsetQuery query) => _queries.Add(query.Name, query);

        public Task<RowSet> RowsAsync(string name, object?[]? args = null, CancellationToken cancellationToken = default)
        {
            object?[][] rows = name switch
            {
                "standing.npcNpc" => [[500001L, characterId, -2.5d]],
                "standing.character" => [[500001L, 2.5d]],
                "standing.transactions" => [[FixedNowMs, 1.5d, 2.25d,
                    "{\"eventTypeID\":12,\"modification\":0.75,\"msg\":\"Standing adjusted\"}"]],
                _ => throw new KeyNotFoundException(name),
            };
            return Task.FromResult(RowSet.FromRows(_queries[name], rows));
        }
    }
}
