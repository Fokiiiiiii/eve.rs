using System.Text.Json.Nodes;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;
using Npgsql;
using Xunit;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [PgFact]
    public async Task Tale_manager_calls_cross_the_host_wire_and_notify_the_current_system()
    {
        const long systemId = 30_000_142;
        var pgUrl = Environment.GetEnvironmentVariable("EVE_PG_URL")
            ?? throw new InvalidOperationException("EVE_PG_URL is not set");
        await using var dataSource = NpgsqlDataSource.Create(Eve.Data.PgConnection.ForTest(pgUrl));
        await using var connection = await dataSource.OpenConnectionAsync();
        var tale = JsonNode.Parse("""
            {
              "taleID": 1201,
              "templateID": 987,
              "templateNameID": 12345,
              "stagingSolarSystemID": 30000142,
              "systems": [{ "locationID": 30000142, "sceneType": 5 }],
              "incursedSystems": [30000142],
              "state": 2,
              "influence": 0.25,
              "lastUpdated": 1000,
              "decayRate": 0.02,
              "graceTime": 30,
              "hasFinalEncounter": false,
              "effects": [9],
              "rewardGroupID": 4,
              "hasChat": false
            }
            """)!.AsObject();
        await using (var command = new NpgsqlCommand("CREATE TEMP TABLE active_incursions (id bigint PRIMARY KEY, data jsonb NOT NULL)", connection))
            await command.ExecuteNonQueryAsync();
        await using (var insert = new NpgsqlCommand("INSERT INTO active_incursions (id, data) VALUES (1, $1::jsonb)", connection))
        {
            insert.Parameters.AddWithValue(tale.ToJsonString());
            await insert.ExecuteNonQueryAsync();
        }

        async ValueTask<IReadOnlyList<JsonObject>> ReadIncursions(CancellationToken cancellationToken)
        {
            await using var command = new NpgsqlCommand("SELECT data::text FROM active_incursions ORDER BY id", connection);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var rows = new List<JsonObject>();
            while (await reader.ReadAsync(cancellationToken))
                rows.Add(JsonNode.Parse(reader.GetString(0))!.AsObject());
            return rows;
        }

        Start(
            strictRpc: true,
            configure: registry => registry.Register(new IncursionAnomalyService(
                "taleMgr",
                ReadIncursions,
                (_, _) => ValueTask.FromResult<IReadOnlyList<JsonObject>>([]))),
            sessionOpened: session => session.Set("solarsystemid2", new PyInt((int)systemId)));

        var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        _ = await front.ReceiveAsync();

        var kwargs = new PyDict([
            new KeyValuePair<PyValue, PyValue>(
                new PyStr("machoVersion"),
                new PyTuple([new PyInt(0), new PyInt(3396210)])),
        ]);
        async Task<(PyValue Notification, PyValue Reply)> Call(long callId, string method, PyValue[] args)
        {
            var request = Packets.CallWithKwargs(
                open, callId, "taleMgr", method, new PyTuple(args), kwargs);
            await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(request)));
            PyValue? notification = null;
            while (true)
            {
                var packet = MarshalCodec.Decode(Assert.IsType<Send>(await front.ReceiveAsync()).Payload);
                var items = Assert.IsType<PyTuple>(Assert.IsType<PyObject>(packet).Args).Items;
                if (items[0] is PyInt { Value: MachoMessage.Notification })
                {
                    notification = packet;
                    continue;
                }

                return (notification ?? throw new InvalidOperationException("OnTaleData was not sent before the reply."),
                    Result(packet) ?? throw new InvalidOperationException("taleMgr did not return an RPC result."));
            }
        }

        var global = await Call(1, "GetGlobalWorldEventTales", []);
        var notificationState = Assert.IsType<PyTuple>(Assert.IsType<PyObject>(global.Notification).Args).Items;
        Assert.Equal(MachoMessage.Notification, Assert.IsType<PyInt>(notificationState[0]).Value);
        var envelope = Assert.IsType<PyTuple>(notificationState[4]).Items[0];
        var notificationBody = Assert.IsType<PyBuffer>(Assert.IsType<PyTuple>(envelope).Items[1]);
        var decodedNotification = Assert.IsType<PyTuple>(MarshalCodec.DecodeExact(notificationBody.Value));
        var eventCall = Assert.IsType<PyTuple>(decodedNotification.Items[1]);
        var eventArgs = Assert.IsType<PyTuple>(eventCall.Items[1]);
        Assert.Equal(systemId, eventArgs.Items[0] switch
        {
            PyInt value => value.Value,
            PyLong value => value.Value,
            _ => throw new Xunit.Sdk.XunitException("Unexpected system id type."),
        });
        Assert.Single(Assert.IsType<PyDict>(eventArgs.Items[1]).Entries);
        var cached = Assert.IsType<PyObject>(global.Reply);
        Assert.Equal(ObjectCaching.ResultClass, JsSemantics.StringValue(cached.Name));

        var byTemplate = await Call(2, "get_active_tales_by_template", [new PyInt(987)]);
        Assert.Empty(Assert.IsType<PyList>(byTemplate.Reply).Items);
        Assert.NotNull(byTemplate.Notification);
    }
}
