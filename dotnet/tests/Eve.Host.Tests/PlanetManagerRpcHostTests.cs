using Eve.Data;
using Npgsql;
using Eve.Planets;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Planets;
using Xunit;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [Fact]
    public async Task Planet_launch_tax_and_colony_reads_use_registered_host_routes()
    {
        var planetMgr = new PlanetMgrService(
            null!, null!, null!, null!, null!, null!, null!, () => FixedNowMs,
            (_, _) => Task.CompletedTask, _ => Task.CompletedTask, "eve");
        var taxRates = new PlanetOrbitalRegistryService(null!, () => FixedNowMs);
        Start(configure: registry =>
        {
            registry.Register(planetMgr);
            registry.Register(taxRates);
        });

        var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        _ = await front.ReceiveAsync();
        var kwargs = new PyDict([
            new KeyValuePair<PyValue, PyValue>(new PyStr("machoVersion"), new PyInt(1)),
        ]);

        async Task<PyValue?> Call(uint callId, string service, string method, PyTuple args)
        {
            await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
                Packets.CallWithKwargs(open, callId, service, method, args, kwargs))));
            return Result(MarshalCodec.Decode(Assert.IsType<Send>(await front.ReceiveAsync()).Payload));
        }

        var planets = Assert.IsType<PyObjectEx>(await Call(1, "planetMgr", "GetPlanetsForChar", Py.Tuple()));
        Assert.Equal(ObjectExKind.Ex2, planets.Kind);
        Assert.Empty(planets.List);

        var launches = Assert.IsType<PyObjectEx>(await Call(2, "planetMgr", "GetMyLaunchesDetails", Py.Tuple()));
        Assert.Equal(ObjectExKind.Ex2, launches.Kind);
        Assert.Empty(launches.List);

        Assert.Equal(PyBool.False, await Call(3, "planetMgr", "DeleteLaunch", Py.Tuple(new PyInt(7001))));
        Assert.Equal(PyNone.Instance, await Call(
            4, "planetOrbitalRegistryBroker", "GetTaxRate", Py.Tuple(new PyInt(30000142))));
    }

    [PgFact]
    public async Task Planet_launch_list_and_delete_use_persisted_owner_state_through_registered_host_wire()
    {
        var url = Environment.GetEnvironmentVariable("EVE_PG_URL")!;
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(url));
        var schema = "pmrw" + Guid.NewGuid().ToString("N")[..10];
        await PlanetStore.MigrateAsync(source, schema);
        try
        {
            var store = new PlanetStore(source, schema);
            var owner = 90_001L;
            var now = Py.FileTimeFromUnixMs(FixedNowMs);
            long launchId;
            await using (var connection = await source.OpenConnectionAsync())
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                launchId = await store.InsertLaunchAsync(connection, transaction,
                    new LaunchRecord(0, owner, 40_001, 30_000_142, 50_001, true, now, now + 3_600_000_000,
                        1.25, 2.5, 3.75, "{}"));
                await store.InsertLaunchAsync(connection, transaction,
                    new LaunchRecord(0, owner + 1, 40_002, 30_000_142, 50_002, false, now, now + 3_600_000_000,
                        4.5, 5.5, 6.5, "{}"));
                await transaction.CommitAsync();
            }

            var service = new PlanetMgrService(
                source, store, null!, null!, null!, null!, null!, () => FixedNowMs,
                (_, _) => Task.CompletedTask, _ => Task.CompletedTask, "unused");
            Start(
                configure: registry => registry.Register(service),
                sessionOpened: session => session.Set("charid", new PyInt(owner)));

            var front = await Handshaken(await ConnectAsync());
            var open = Open();
            await front.SendAsync(open);
            _ = await front.ReceiveAsync();
            var kwargs = new PyDict([
                new KeyValuePair<PyValue, PyValue>(new PyStr("machoVersion"), new PyInt(1)),
            ]);

            async Task<PyValue?> Call(uint callId, string method, PyTuple args)
            {
                await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
                    Packets.CallWithKwargs(open, callId, "planetMgr", method, args, kwargs))));
                return Result(MarshalCodec.Decode(Assert.IsType<Send>(await front.ReceiveAsync()).Payload));
            }

            var launches = Assert.IsType<PyObjectEx>(await Call(1, "GetMyLaunchesDetails", Py.Tuple()));
            Assert.Equal(ObjectExKind.Ex2, launches.Kind);
            Assert.Single(launches.List);
            Assert.Equal(PyBool.True, await Call(2, "DeleteLaunch", Py.Tuple(new PyLong(launchId))));
            Assert.Empty(Assert.IsType<PyObjectEx>(await Call(3, "GetMyLaunchesDetails", Py.Tuple())).List);
            Assert.False((await store.LaunchesOfAsync(owner, now)).Any());
            Assert.Single(await store.LaunchesOfAsync(owner + 1, now));
        }
        finally
        {
            await using var cleanup = source.CreateCommand($"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE");
            await cleanup.ExecuteNonQueryAsync();
        }
    }
}
