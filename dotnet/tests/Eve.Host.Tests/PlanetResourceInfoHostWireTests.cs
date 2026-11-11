using Eve.Data;
using Eve.Macho;
using Eve.Marshal;
using Eve.Planets;
using Eve.Services;
using Eve.Services.Planets;
using Npgsql;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [PgFact]
    public async Task Planet_resource_info_returns_the_client_resource_set_through_the_registered_bound_host_wire()
    {
        const long planetId = 40_486_618;
        var url = Environment.GetEnvironmentVariable("EVE_PG_URL")!;
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(url));
        var schema = "priw" + Guid.NewGuid().ToString("N")[..10];
        await PlanetStore.MigrateAsync(source, schema);
        try
        {
            var notifications = 0;
            var service = new PlanetMgrService(
                source,
                new PlanetStore(source, schema),
                new PlanetCatalog(new Dictionary<int, TypeInfo>(), new Dictionary<int, Schematic>()),
                new DeterministicResourceField(),
                null!, null!, null!, () => FixedNowMs,
                (_, _) => { notifications++; return Task.CompletedTask; },
                _ => { notifications++; return Task.CompletedTask; }, "unused");
            Start(configure: registry => registry.Register(service));

            var front = await Handshaken(await ConnectAsync());
            var open = Open();
            await front.SendAsync(open);
            _ = await front.ReceiveAsync();
            var kwargs = new PyDict([
                new KeyValuePair<PyValue, PyValue>(new PyStr("machoVersion"), new PyInt(1)),
            ]);
            await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(Packets.CallWithKwargs(
                open, 1, "planetMgr", "MachoBindObject", new PyTuple([new PyLong(planetId), PyNone.Instance]), kwargs))));
            var bindReply = Assert.IsType<Send>(await front.ReceiveAsync());
            var bindResult = Assert.IsType<PyTuple>(Result(MarshalCodec.Decode(bindReply.Payload)));
            var bound = Assert.IsType<PySubStruct>(bindResult.Items[0]);
            var reference = Assert.IsType<PyTuple>(Assert.IsType<PySubStream>(bound.Value).Decoded);
            var objectId = Assert.IsType<string>(JsSemantics.StringValue(reference.Items[0]));

            await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(Packets.BoundCall(
                open, 2, objectId, "GetPlanetResourceInfo", new PyTuple([]), kwargs))));
            var reply = Assert.IsType<Send>(await front.ReceiveAsync());
            var resources = Assert.IsType<PyDict>(Result(MarshalCodec.Decode(reply.Payload)));

            Assert.Equal(
                new long[] { 2_073, 2_267, 2_268, 2_270, 2_288 },
                resources.Entries.Select(entry => Assert.IsType<PyInt>(entry.Key).Value).Order().ToArray());
            Assert.All(resources.Entries, entry => Assert.IsType<PyReal>(entry.Value));
            Assert.Equal(0, notifications);
        }
        finally
        {
            await using var cleanup = source.CreateCommand($"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE");
            await cleanup.ExecuteNonQueryAsync();
        }
    }
}
