using Eve.Marshal;
using Eve.Services.Station;
using Npgsql;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [Fact]
    public async Task Map_reads_return_the_registered_wire_shapes_through_the_host()
    {
        await using var source = NpgsqlDataSource.Create("Host=127.0.0.1;Port=5432;Database=eve;Username=postgres");
        var map = new MapService(new Eve.ReadSide.ReadSide(source), () => FixedNowMs);
        Start(configure: registry => registry.Register(map));

        var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        _ = await front.ReceiveAsync();

        var calls = new (string Method, PyValue[] Args, string[]? Columns)[]
        {
            ("GetIndustryCostModifier", [new PyInt(1), new PyInt(1)], null),
            ("GetIndustryJobsOverLast24Hours", [new PyInt(1), new PyInt(1)], ["solarSystemID", "noOfJobs"]),
            ("GetMyExtraMapInfo", [], ["characterID", "locationID"]),
            ("GetMyExtraMapInfoAgents", [], ["fromID", "rank"]),
            ("GetRecentSovActivity", [], ["solarSystemID", "ownerID", "oldOwnerID", "stationID", "changeTime"]),
            ("GetSolarSystemVisits", [], ["lastDateTime", "solarSystemID", "visits"]),
        };

        for (var i = 0; i < calls.Length; i++)
        {
            var (method, args, columns) = calls[i];
            await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
                Packets.Call(open, i + 1, "map", method, args))));

            var reply = Assert.IsType<Send>(await front.ReceiveAsync());
            var value = Result(MarshalCodec.Decode(reply.Payload))
                ?? throw new Xunit.Sdk.XunitException("The map RPC returned no value.");

            if (columns is null)
            {
                Assert.IsType<PyDict>(value);
                continue;
            }

            var rowset = Assert.IsType<PyObject>(value);
            Assert.Equal("eve.common.script.sys.rowset.Rowset", T(rowset.Name));
            var fields = Assert.IsType<PyDict>(rowset.Args);
            var fieldValues = fields.Entries.ToDictionary(entry => T(entry.Key)!, entry => entry.Value);
            Assert.Equal(columns, Assert.IsType<PyList>(fieldValues["header"]).Items.Select(T).ToArray());
            Assert.Empty(Assert.IsType<PyList>(fieldValues["lines"]).Items);
        }
    }
}
