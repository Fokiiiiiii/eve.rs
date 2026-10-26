using Eve.Data;
using Eve.Events;
using Eve.Host;
using Eve.Macho;
using Eve.Marshal;
using Eve.Relations;
using Eve.Scheduler;
using Eve.Services;
using Eve.Services.Corporations;
using Eve.Services.Space;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [PgFact]
    public async Task Wars_info_rpcs_keep_client_shapes_through_registered_host_with_transport_kwargs()
    {
        await using var rig = await WarsWireRig.CreateAsync();
        await rig.SetAsync("war", 1001, 1002, new
        {
            openForAllies = true,
            warHQID = 7000L,
            timeDeclared = 1_700_000_000_000L,
            timeStarted = 1_700_000_086_400L,
        });
        var war = Assert.Single(await rig.Relations.ListAsync("war"));
        await rig.SetAsync("war.ally", 1003, war.Id, new { side = "defender", timeStarted = 1_700_000_086_400L });

        var service = new WarsInfoMgrService(rig.Relations, () => 1_700_000_000_000L);
        Start(
            configure: registry => registry.Register(service),
            sessionOpened: session =>
            {
                session.CharacterId = 9001;
                session.Set("corpid", new PyLong(1002));
            });

        using var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        _ = await front.ReceiveAsync();

        async Task<PyValue> Call(long callId, string method, params PyValue[] args)
        {
            await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(Packets.CallWithKwargs(
                open,
                callId,
                "warsInfoMgr",
                method,
                new PyTuple(args),
                new PyDict([new KeyValuePair<PyValue, PyValue>(new PyStr("machoVersion"), new PyInt(1))])))));
            return Result(MarshalCodec.Decode(Assert.IsType<Send>(await front.ReceiveAsync()).Payload))!;
        }

        var firstPage = Assert.IsType<PyObjectEx>(UnwrapWarCache(await Call(1, "GetTop50", new PyLong(0))));
        Assert.Equal(1, ReadWarNumber(Assert.IsType<PyPackedRow>(Assert.Single(firstPage.List)).Values[0]));
        var byOwner = Assert.IsType<PyObjectEx>(UnwrapWarCache(await Call(2, "GetWarsByOwnerID", new PyLong(1003))));
        Assert.Equal(war.Id, ReadWarNumber(Assert.IsType<PyPackedRow>(Assert.Single(byOwner.List)).Values[0]));

        var byOwners = Assert.IsType<PyDict>(await Call(
            3,
            "GetWarsByOwners",
            PyList.Of(new PyLong(1001), new PyLong(1003), new PyLong(9999))));
        Assert.Equal(3, byOwners.Entries.Count);
        var allyWars = Assert.IsType<PyDict>(byOwners.Entries.Single(entry => ReadWarNumber(entry.Key) == 1003).Value);
        Assert.Contains(allyWars.Entries, entry => ReadWarNumber(entry.Key) == war.Id);

        var requiringAssistance = Assert.IsType<PyList>(await Call(4, "GetWarsRequiringAssistance"));
        Assert.Equal(war.Id, ReadWarNumber(WarKeyValFields(Assert.Single(requiringAssistance.Items))["warID"]));

        var structureWars = Assert.IsType<PyList>(await Call(5, "GetWarsForStructure", new PyLong(7000)));
        Assert.Equal(war.Id, ReadWarNumber(WarKeyValFields(Assert.Single(structureWars.Items))["warID"]));
    }

    [Fact]
    public async Task Wormhole_jump_returns_the_client_list_shape_through_registered_host_with_transport_kwargs()
    {
        var callbackEndpoint = 0L;
        var service = new WormholeMgrService(
            spaceSessions: null,
            jump: (_, endpointId, _) =>
            {
                callbackEndpoint = endpointId;
                return ValueTask.FromResult<PyValue?>(Py.Tuple(
                    new PyStr("bound-wormhole"),
                    new PyLong(638_000_000_000_000_000L)));
            });
        Start(
            configure: registry => registry.Register(service),
            sessionOpened: session => session.CharacterId = 9001);

        using var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        _ = await front.ReceiveAsync();
        await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(Packets.CallWithKwargs(
            open,
            1,
            "wormholeMgr",
            "WormholeJump",
            new PyTuple([new PyLong(654)]),
            new PyDict([new KeyValuePair<PyValue, PyValue>(new PyStr("machoVersion"), new PyInt(1))])))));

        var result = Assert.IsType<PyList>(
            Result(MarshalCodec.Decode(Assert.IsType<Send>(await front.ReceiveAsync()).Payload)));
        Assert.Equal(654, callbackEndpoint);
        Assert.Equal("bound-wormhole", System.Text.Encoding.UTF8.GetString(Assert.IsType<PyBytes>(result.Items[0]).Value.Span));
        Assert.Equal(new PyLong(638_000_000_000_000_000L), result.Items[1]);
    }

    private static PyValue UnwrapWarCache(PyValue result)
    {
        var wrapper = Assert.IsType<PyObject>(result);
        Assert.Equal(
            "carbon.common.script.net.objectCaching.CachedMethodCallResult",
            JsSemantics.StringValue(wrapper.Name));
        return Assert.IsType<PySubStream>(Assert.IsType<PyTuple>(wrapper.Args).Items[1]).Decoded!;
    }

    private static IReadOnlyDictionary<string, PyValue> WarKeyValFields(PyValue value)
    {
        var keyVal = Assert.IsType<PyObject>(value);
        Assert.Equal("util.KeyVal", Assert.IsType<PyStr>(keyVal.Name).Value);
        return Assert.IsType<PyDict>(keyVal.Args).Entries
            .ToDictionary(entry => Assert.IsType<PyStr>(entry.Key).Value, entry => entry.Value);
    }

    private static long ReadWarNumber(PyValue value) => value switch
    {
        PyInt integer => integer.Value,
        PyLong integer => integer.Value,
        _ => throw new Xunit.Sdk.XunitException($"Expected a marshal integer but received {value.GetType().Name}."),
    };

    private sealed class WarsWireRig : IAsyncDisposable
    {
        private readonly string[] _schemas;

        private WarsWireRig(Npgsql.NpgsqlDataSource source, string[] schemas)
        {
            Source = source;
            _schemas = schemas;
            Scheduler = new DurableScheduler(source, () => 1_700_000_000_000L,
                new SchedulerOptions { Concurrency = 1 }, schemas[0]);
            Bus = new EventBus(source, Scheduler, null, schemas[1]);
            Relations = new RelationStore(source, Scheduler, Bus, schemas[2]);
        }

        private Npgsql.NpgsqlDataSource Source { get; }
        private DurableScheduler Scheduler { get; }
        private EventBus Bus { get; }
        public RelationStore Relations { get; }

        public static async Task<WarsWireRig> CreateAsync()
        {
            var connectionString = Environment.GetEnvironmentVariable("EVE_PG_URL")
                ?? throw new InvalidOperationException("EVE_PG_URL is required for the PostgreSQL-backed Host wire test.");
            var source = Npgsql.NpgsqlDataSource.Create(PgConnection.ForTest(connectionString));
            var id = Guid.NewGuid().ToString("N")[..12];
            string[] schemas = ["wars_host_sched_" + id, "wars_host_events_" + id, "wars_host_rel_" + id];
            await DurableScheduler.MigrateAsync(source, schemas[0]);
            await EventBus.MigrateAsync(source, schemas[1]);
            await RelationStore.MigrateAsync(source, schemas[2]);
            return new WarsWireRig(source, schemas);
        }

        public async Task SetAsync(string kind, long subject, long obj, object attrs)
        {
            await using var connection = await Source.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            await Relations.SetAsync(connection, transaction, kind, subject, obj, 0, "wars host wire test", attrs);
            await transaction.CommitAsync();
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var schema in _schemas)
            {
                await using var command = Source.CreateCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE");
                await command.ExecuteNonQueryAsync();
            }
            await Source.DisposeAsync();
        }
    }
}
