using Eve.Data;
using Eve.Inventory;
using Eve.Macho;
using Eve.Marshal;
using Eve.Planets;
using Eve.Services;
using Eve.Services.Planets;
using Eve.Skills;
using Npgsql;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [PgFact]
    public async Task Concurrent_command_center_placements_for_one_owner_observe_the_planet_limit()
    {
        const long owner = 90_081;
        const int barrenType = 2_016;
        const int barrenCommandCenterType = 2_524;
        var url = Environment.GetEnvironmentVariable("EVE_PG_URL")!;
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(url));
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var planetSchema = "plc_p" + suffix;
        var inventorySchema = "plc_i" + suffix;
        var skillSchema = "plc_s" + suffix;
        await PlanetStore.MigrateAsync(source, planetSchema);
        await PgItemStore.MigrateAsync(source, inventorySchema);
        await PgSkillStore.MigrateAsync(source, skillSchema);
        try
        {
            var planets = new PlanetStore(source, planetSchema);
            var items = new PgItemStore(source, inventorySchema);
            var skills = new PgSkillStore(source, skillSchema);
            var catalog = await PlanetCatalogLoader.LoadAsync(source);
            var bodies = new List<PlanetBody>();
            await using (var connection = await source.OpenConnectionAsync())
            await using (var command = new NpgsqlCommand(
                "SELECT key FROM sde.\"mapPlanets\" WHERE doc->>'typeID' = $1 ORDER BY key LIMIT 2", connection))
            {
                command.Parameters.AddWithValue(barrenType.ToString(System.Globalization.CultureInfo.InvariantCulture));
                await using var reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                    bodies.Add(Assert.IsType<PlanetBody>(await PlanetCatalogLoader.PlanetAsync(source, long.Parse(reader.GetString(0), System.Globalization.CultureInfo.InvariantCulture))));
            }
            Assert.Equal(2, bodies.Count);
            var firstCommandCenterItem = await CreateCommandCenterAsync(bodies[0]);
            var secondCommandCenterItem = await CreateCommandCenterAsync(bodies[1]);

            await using (var connection = await source.OpenConnectionAsync())
            await using (var command = new NpgsqlCommand($"""
                CREATE FUNCTION {planetSchema}.delay_colony_insert() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    PERFORM pg_sleep(0.6);
                    RETURN NEW;
                END $$;
                CREATE TRIGGER delay_colony_insert BEFORE INSERT ON {planetSchema}.colonies
                    FOR EACH ROW EXECUTE FUNCTION {planetSchema}.delay_colony_insert();
                """, connection))
                await command.ExecuteNonQueryAsync();

            var service = new PlanetMgrService(source, planets, catalog, new DeterministicResourceField(), items, null!, skills,
                () => FixedNowMs, (_, _) => Task.CompletedTask, _ => Task.CompletedTask, inventorySchema);
            Start(
                configure: registry => registry.Register(service),
                sessionOpened: session => session.Set("charid", new PyLong(owner)));
            var kwargs = new PyDict([
                new KeyValuePair<PyValue, PyValue>(new PyStr("machoVersion"), new PyInt(1)),
            ]);
            var first = await ConnectBoundAsync(Open(50_081, 81), bodies[0].PlanetId);
            var second = await ConnectBoundAsync(Open(50_082, 82), bodies[1].PlanetId);

            async Task<int> PlaceAsync(TestFront front, SessionOpen open, string objectId, long commandCenterItemId)
            {
                var createCommand = Py.Tuple(new PyInt(1), Py.Tuple(new PyLong(commandCenterItemId), new PyInt(barrenCommandCenterType), new PyReal(0.5), new PyReal(1.0)));
                await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(Packets.BoundCall(
                    open, 2, objectId, "UserUpdateNetwork", new PyTuple([new PyList([createCommand])]), kwargs))));
                var response = Assert.IsType<Send>(await front.ReceiveAsync());
                return Assert.IsType<MachoPacket>(MachoPacket.Decode(MarshalCodec.Decode(response.Payload))).Type;
            }

            var outcomes = await Task.WhenAll(
                PlaceAsync(first.Front, first.Open, first.ObjectId, firstCommandCenterItem),
                PlaceAsync(second.Front, second.Open, second.ObjectId, secondCommandCenterItem));

            Assert.Equal(1, outcomes.Count(type => type == MachoMessage.CallRsp));
            Assert.Equal(1, outcomes.Count(type => type == MachoMessage.ErrorResponse));
            Assert.Single(await planets.ColoniesOfAsync(owner));

            async Task<long> CreateCommandCenterAsync(PlanetBody body)
            {
                await using var connection = await source.OpenConnectionAsync();
                await using var transaction = await connection.BeginTransactionAsync();
                var id = await items.CreateAsync(connection, transaction,
                    new NewItem(barrenCommandCenterType, owner, body.SolarSystemId, ItemFlags.Hangar, Singleton: true));
                await transaction.CommitAsync();
                return id;
            }

            async Task<(TestFront Front, SessionOpen Open, string ObjectId)> ConnectBoundAsync(SessionOpen open, long planetId)
            {
                var front = await Handshaken(await ConnectAsync());
                await front.SendAsync(open);
                _ = await front.ReceiveAsync();
                await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(Packets.CallWithKwargs(
                    open, 1, "planetMgr", "MachoBindObject", new PyTuple([new PyLong(planetId), PyNone.Instance]), kwargs))));
                var reply = Assert.IsType<Send>(await front.ReceiveAsync());
                var result = Assert.IsType<PyTuple>(Result(MarshalCodec.Decode(reply.Payload)));
                var bound = Assert.IsType<PySubStruct>(result.Items[0]);
                var reference = Assert.IsType<PyTuple>(Assert.IsType<PySubStream>(bound.Value).Decoded);
                return (front, open, Assert.IsType<string>(JsSemantics.StringValue(reference.Items[0])));
            }
        }
        finally
        {
            foreach (var schema in new[] { planetSchema, inventorySchema, skillSchema })
            {
                await using var cleanup = source.CreateCommand($"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE");
                await cleanup.ExecuteNonQueryAsync();
            }
        }
    }
}
