using Eve.FactionWarfare;
using Eve.Host;
using Eve.Marshal;
using Eve.Macho;
using Eve.Services;
using Eve.Services.Core;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [PgFact]
    public async Task Registered_host_warzone_read_returns_occupation_persisted_in_postgres()
    {
        var connectionString = Eve.Data.PgConnection.ForTest(
            Environment.GetEnvironmentVariable("EVE_PG_URL") ?? throw new InvalidOperationException("EVE_PG_URL is required."));
        await using var source = Npgsql.NpgsqlDataSource.Create(connectionString);
        var schema = "fw_wire_" + Guid.NewGuid().ToString("N")[..12];
        var walletSchema = "wallet_wire_" + Guid.NewGuid().ToString("N")[..12];
        await FwStore.MigrateAsync(source, schema);
        var store = new FwStore(source, schema);
        var systemId = FwWarzones.SystemsOf(FwWarzones.AmarrMinmatar)[0];
        await using (var connection = await source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await store.SeedSystemsAsync(connection, transaction,
                [new FwSystem(systemId, FwWarzones.AmarrMinmatar, FwFactions.AmarrEmpire, 0, 0, 0)]);
            await transaction.CommitAsync();
        }

        var factionWarfare = new FwService(store, new Eve.Wallet.LoyaltyPointStore(source, walletSchema));
        var service = new FwWarzoneSolarsystemHostService(
            async call =>
            {
                var occupied = await factionWarfare.OccupiedSystemsAsync(call.Cancellation);
                return SovFwReadSnapshot.Empty with
                {
                    FwOccupationStates = occupied.Select(system =>
                        new FwOccupationRecord(system.WarzoneId, system.SolarSystemId, system.OccupierId)).ToArray(),
                };
            },
            _ => ValueTask.FromResult<IReadOnlyDictionary<long, IReadOnlyCollection<long>>>(
                new Dictionary<long, IReadOnlyCollection<long>>()),
            _ => ValueTask.FromResult<IReadOnlyDictionary<long, long>>(new Dictionary<long, long>()));

        Start(
            configure: registry => registry.Register(service),
            sessionOpened: session => session.Set("solarsystemid2", new PyInt(systemId)));
        var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        _ = await front.ReceiveAsync();
        await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
            Packets.Call(open, 1, "fwWarzoneSolarsystem", "GetLocalOccupationState", []))));
        var reply = Assert.IsType<Send>(await front.ReceiveAsync());
        var local = Assert.IsType<PyTuple>(Result(MarshalCodec.Decode(reply.Payload)));
        Assert.Equal(new PyInt(systemId), local.Items[0]);
        Assert.Equal(new PyInt(FwFactions.AmarrEmpire), FwGet(FwFields(local.Items[1]), "occupierID"));
    }

    [Fact]
    public async Task Faction_warfare_warzone_calls_use_real_host_wire_shapes_and_sde_adjacency_values()
    {
        const long amarrFrontline = 30_000_142;
        const long minmatarFrontline = 30_000_143;
        const long amarrCommand = 30_000_144;
        const long amarrRearguard = 30_000_145;
        var snapshot = SovFwReadSnapshot.Empty with
        {
            FwOccupationStates =
            [
                new(FwWarzones.AmarrMinmatar, amarrFrontline, FwFactions.AmarrEmpire),
                new(FwWarzones.AmarrMinmatar, minmatarFrontline, FwFactions.MinmatarRepublic),
                new(FwWarzones.AmarrMinmatar, amarrCommand, FwFactions.AmarrEmpire),
                new(FwWarzones.AmarrMinmatar, amarrRearguard, FwFactions.AmarrEmpire),
            ],
        };
        var neighbors = new Dictionary<long, IReadOnlyCollection<long>>
        {
            [amarrFrontline] = [minmatarFrontline, amarrCommand],
            [minmatarFrontline] = [amarrFrontline],
            [amarrCommand] = [amarrFrontline, amarrRearguard],
            [amarrRearguard] = [amarrCommand],
        };
        var service = new FwWarzoneSolarsystemHostService(
            _ => ValueTask.FromResult(snapshot),
            _ => ValueTask.FromResult<IReadOnlyDictionary<long, IReadOnlyCollection<long>>>(neighbors),
            _ => ValueTask.FromResult<IReadOnlyDictionary<long, long>>(new Dictionary<long, long>
            {
                [FwFactions.AmarrEmpire] = 30_002_974,
                [FwFactions.CaldariState] = 30_045_324,
                [FwFactions.GallenteFederation] = 30_003_788,
                [FwFactions.MinmatarRepublic] = 30_002_055,
            }));

        ClientSession? hostSession = null;
        Start(
            configure: registry => registry.Register(service),
            sessionOpened: session =>
            {
                hostSession = session;
                session.Set("solarsystemid2", new PyInt(amarrFrontline));
            });
        var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        _ = await front.ReceiveAsync();

        long callId = 0;
        async Task<PyValue?> Call(string method, params PyValue[] args)
        {
            await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
                Packets.Call(open, ++callId, "fwWarzoneSolarsystem", method, args))));
            var reply = Assert.IsType<Send>(await front.ReceiveAsync());
            return Result(MarshalCodec.Decode(reply.Payload));
        }

        var warzones = Assert.IsType<PyDict>(await Call("GetAllWarzones"));
        Assert.Equal(new PyList([new PyInt(FwFactions.AmarrEmpire), new PyInt(FwFactions.MinmatarRepublic)]),
            FwGet(warzones, new PyInt(FwWarzones.AmarrMinmatar)));
        var headquarters = Assert.IsType<PyDict>(await Call("GetHQSystemIDs"));
        Assert.Equal(new PyInt(30_002_974), FwGet(headquarters, new PyInt(FwFactions.AmarrEmpire)));

        var all = Assert.IsType<PyDict>(await Call("GetAllWarzonesOccupationStates"));
        var amarrStates = Assert.IsType<PyDict>(FwGet(all, new PyInt(FwWarzones.AmarrMinmatar)));
        Assert.Equal("FL", T(FwGet(FwFields(amarrStates, amarrFrontline), "adjacencyState")));
        Assert.Equal("FL", T(FwGet(FwFields(amarrStates, minmatarFrontline), "adjacencyState")));
        Assert.Equal("SL", T(FwGet(FwFields(amarrStates, amarrCommand), "adjacencyState")));
        Assert.Equal("BL", T(FwGet(FwFields(amarrStates, amarrRearguard), "adjacencyState")));

        var uncached = Assert.IsType<PyDict>(await Call("GetAllWarzonesOccupationStatesUncached"));
        var uncachedAmarrStates = Assert.IsType<PyDict>(FwGet(uncached, new PyInt(FwWarzones.AmarrMinmatar)));
        Assert.Equal(amarrStates.Entries.Count, uncachedAmarrStates.Entries.Count);
        Assert.Equal("SL", T(FwGet(FwFields(uncachedAmarrStates, amarrCommand), "adjacencyState")));

        var local = Assert.IsType<PyTuple>(await Call("GetLocalOccupationState"));
        Assert.Equal(new PyInt(amarrFrontline), local.Items[0]);
        Assert.Equal("FL", T(FwGet(FwFields(local.Items[1]), "adjacencyState")));

        hostSession!.Set("solarsystemid2", new PyInt(30_000_146));
        var outsideWarzone = Assert.IsType<PyTuple>(await Call("GetLocalOccupationState"));
        Assert.Equal(new PyInt(30_000_146), outsideWarzone.Items[0]);
        Assert.Same(PyNone.Instance, outsideWarzone.Items[1]);
    }

    private static PyDict FwFields(PyDict states, long systemId) =>
        FwFields(FwGet(states, new PyInt(systemId)));

    private static PyDict FwFields(PyValue state)
    {
        var obj = Assert.IsType<PyObject>(state);
        Assert.Equal("util.KeyVal", JsSemantics.StringValue(obj.Name));
        return Assert.IsType<PyDict>(obj.Args);
    }

    private static PyValue FwGet(PyDict dict, string key) =>
        dict.Entries.Single(entry => T(entry.Key) == key).Value;

    private static PyValue FwGet(PyDict dict, PyValue key) =>
        dict.Entries.Single(entry => entry.Key.Equals(key)).Value;
}
