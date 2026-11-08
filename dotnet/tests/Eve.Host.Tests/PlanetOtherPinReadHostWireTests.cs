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
    public async Task Planet_other_pin_and_full_network_reads_match_client_wire_shapes_and_include_other_owners()
    {
        const long planetId = 40_486_618;
        const long firstOwner = 90_071;
        const long secondOwner = 90_072;
        const long firstCommandId = 800_071;
        const long firstEcuId = 800_072;
        const long secondCommandId = 800_073;
        const long secondEcuId = 800_074;
        var url = Environment.GetEnvironmentVariable("EVE_PG_URL")!;
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(url));
        var schema = "plr" + Guid.NewGuid().ToString("N")[..10];
        await PlanetStore.MigrateAsync(source, schema);
        try
        {
            var catalog = await PlanetCatalogLoader.LoadAsync(source);
            var body = Assert.IsType<PlanetBody>(await PlanetCatalogLoader.PlanetAsync(source, planetId));
            var store = new PlanetStore(source, schema);
            Colony ColonyOf(long owner, long commandId, long ecuId)
            {
                var colony = new Colony(catalog, new DeterministicResourceField(), planetId, body.TypeId, body.Radius, owner)
                {
                    CurrentSimTime = Py.FileTimeFromUnixMs(FixedNowMs),
                };
                colony.Pins[commandId] = new Pin { Id = commandId, TypeId = 2_524, Kind = PinKind.CommandCenter, OwnerId = owner };
                colony.Pins[ecuId] = new Pin { Id = ecuId, TypeId = 2_848, Kind = PinKind.Ecu, OwnerId = owner };
                return colony;
            }

            var first = ColonyOf(firstOwner, firstCommandId, firstEcuId);
            first.Links[(firstCommandId, firstEcuId)] = new Link
            {
                End1 = firstCommandId,
                End2 = firstEcuId,
                TypeId = catalog.LinkTypeId,
            };
            var second = ColonyOf(secondOwner, secondCommandId, secondEcuId);
            await using (var connection = await source.OpenConnectionAsync())
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                await store.SaveAsync(connection, transaction, firstOwner, planetId, ColonyDocument.From(first), FixedNowMs);
                await store.SaveAsync(connection, transaction, secondOwner, planetId, ColonyDocument.From(second), FixedNowMs);
                await transaction.CommitAsync();
            }

            var service = new PlanetMgrService(source, store, catalog, new DeterministicResourceField(), null!, null!, null!,
                () => FixedNowMs, (_, _) => Task.CompletedTask, _ => Task.CompletedTask, "unused");
            Start(
                configure: registry => registry.Register(service),
                sessionOpened: session => session.Set("charid", new PyLong(firstOwner)));
            var front = await Handshaken(await ConnectAsync());
            var open = Open();
            await front.SendAsync(open);
            _ = await front.ReceiveAsync();
            var kwargs = new PyDict([
                new KeyValuePair<PyValue, PyValue>(new PyStr("machoVersion"), new PyInt(1)),
            ]);
            async Task<PyValue?> Call(uint callId, string method, PyTuple args)
            {
                await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(Packets.CallWithKwargs(
                    open, callId, "planetMgr", method, args, kwargs))));
                var response = Assert.IsType<Send>(await front.ReceiveAsync());
                Assert.Equal(MachoMessage.CallRsp, Assert.IsType<MachoPacket>(MachoPacket.Decode(MarshalCodec.Decode(response.Payload))).Type);
                return Result(MarshalCodec.Decode(response.Payload));
            }

            var failures = new List<string>();
            var commandsValue = await Call(1, "GetCommandPinsForPlanet", Py.Tuple(new PyLong(planetId)));
            if (commandsValue is not PyDict commands)
            {
                failures.Add($"GetCommandPinsForPlanet returned {commandsValue?.GetType().Name ?? "null"}, expected PyDict");
            }
            else
            {
                var owners = commands.Entries.Select(e => PlanetReadIntegerOrNull(e.Key)).Order().ToArray();
                if (!owners.SequenceEqual(new long?[] { firstOwner, secondOwner })) failures.Add("command center owner keys were incomplete");
                if (PlanetReadOwnerPinField(commands, firstOwner, "pinID") != firstCommandId) failures.Add("first command summary omitted pinID");
                if (PlanetReadOwnerPinField(commands, secondOwner, "pinID") != secondCommandId) failures.Add("second command summary omitted pinID");
            }

            var extractorsValue = await Call(2, "GetExtractorsForPlanet", Py.Tuple(new PyLong(planetId)));
            if (extractorsValue is not PyList extractors)
            {
                failures.Add($"GetExtractorsForPlanet returned {extractorsValue?.GetType().Name ?? "null"}, expected PyList");
            }
            else
            {
                if (extractors.Items.Count != 2) failures.Add($"GetExtractorsForPlanet returned {extractors.Items.Count} ECUs, expected 2 owners");
                var pinIds = extractors.Items.Select(p => PlanetReadFieldOrNull(p, "pinID")).Order().ToArray();
                if (!pinIds.SequenceEqual(new long?[] { firstEcuId, secondEcuId })) failures.Add("extractor summaries omitted pinID or another owner's ECU");
                var owners = extractors.Items.Select(p => PlanetReadFieldOrNull(p, "ownerID")).Order().ToArray();
                if (!owners.SequenceEqual(new long?[] { firstOwner, secondOwner })) failures.Add("extractor summaries omitted ownerID or another owner");
            }

            var networkValue = await Call(3, "GetFullNetworkForOwner", Py.Tuple(new PyLong(planetId), new PyLong(firstOwner)));
            if (networkValue is not PyTuple { Items.Count: 2 } network)
            {
                failures.Add($"GetFullNetworkForOwner returned {networkValue?.GetType().Name ?? "null"}, expected two-value PyTuple");
            }
            else if (network.Items[0] is not PyList pins || network.Items[1] is not PyList links)
            {
                failures.Add("GetFullNetworkForOwner did not return pin and link lists");
            }
            else
            {
                var pinIds = pins.Items.Select(p => PlanetReadFieldOrNull(p, "pinID")).Order().ToArray();
                if (!pinIds.SequenceEqual(new long?[] { firstCommandId, firstEcuId })) failures.Add("full network pin rows omitted pinID");
                var expectedLink = new PyList([new PyLong(firstCommandId), new PyLong(firstEcuId)]);
                if (!links.Items.SequenceEqual([expectedLink])) failures.Add("full network links were not endpoint-pair lists");
            }

            var emptyValue = await Call(4, "GetFullNetworkForOwner", Py.Tuple(new PyLong(planetId), new PyLong(90_073)));
            if (emptyValue is not PyTuple { Items.Count: 2 } empty
                || empty.Items[0] is not PyList { Items.Count: 0 }
                || empty.Items[1] is not PyList { Items.Count: 0 })
                failures.Add("an owner without a colony did not receive two empty lists");

            Assert.Empty(failures);
        }
        finally
        {
            await using var cleanup = source.CreateCommand($"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE");
            await cleanup.ExecuteNonQueryAsync();
        }

    }

    private static long? PlanetReadIntegerOrNull(PyValue? value) => value switch
    {
        PyInt integer => integer.Value,
        PyLong integer => integer.Value,
        _ => null,
    };

    private static long? PlanetReadOwnerPinField(PyDict values, long ownerId, string name)
    {
        var entries = values.Entries.Where(e => PlanetReadIntegerOrNull(e.Key) == ownerId).ToArray();
        return entries.Length == 1 ? PlanetReadFieldOrNull(entries[0].Value, name) : null;
    }

    private static long? PlanetReadFieldOrNull(PyValue? value, string name)
    {
        if (value is not PyObject { Args: PyDict fields }) return null;
        var entries = fields.Entries.Where(e => JsSemantics.StringValue(e.Key) == name).ToArray();
        return entries.Length == 1 ? PlanetReadIntegerOrNull(entries[0].Value) : null;
    }
}
