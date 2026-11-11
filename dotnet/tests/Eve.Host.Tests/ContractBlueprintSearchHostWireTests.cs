using Eve.Data;
using Eve.Host.Persistence;
using Eve.Inventory;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services.Core;
using Npgsql;
using Xunit;

namespace Eve.Host.Tests;

public sealed partial class HostServerTests
{
    [PgFact]
    public async Task Registered_contract_search_filters_blueprint_originals_by_the_client_search_hint()
    {
        var connectionString = Environment.GetEnvironmentVariable("EVE_PG_URL")
            ?? throw new InvalidOperationException("EVE_PG_URL must name the assigned PostgreSQL lane");
        var contractsSchema = $"contract_bpo_search_{Guid.NewGuid():N}";
        var sdeSchema = $"sde_bpo_search_{Guid.NewGuid():N}";
        var inventorySchema = $"items_bpo_search_{Guid.NewGuid():N}";
        var nowFileTime = Eve.Services.Py.FileTimeFromUnixMs(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        await using var source = NpgsqlDataSource.Create(PgConnection.ToConnectionString(connectionString));
        await PersistenceContractReadModel.MigrateAsync(source, contractsSchema);
        await PgItemStore.MigrateAsync(source, inventorySchema);
        await using (var createSde = source.CreateCommand($"""
            CREATE SCHEMA "{sdeSchema}";
            CREATE TABLE "{sdeSchema}".npcStations (ord integer NOT NULL, key bigint PRIMARY KEY, doc jsonb NOT NULL);
            CREATE TABLE "{sdeSchema}".mapSolarSystems (ord integer NOT NULL, key bigint PRIMARY KEY, doc jsonb NOT NULL);
            CREATE TABLE "{sdeSchema}".types (ord integer NOT NULL, key bigint PRIMARY KEY, doc jsonb NOT NULL);
            CREATE TABLE "{sdeSchema}".groups (ord integer NOT NULL, key bigint PRIMARY KEY, doc jsonb NOT NULL);
            """))
            await createSde.ExecuteNonQueryAsync();

        try
        {
            await using (var seedContracts = source.CreateCommand($"""
                INSERT INTO "{contractsSchema}".contract_records
                    (contract_id, issuer_id, issuer_corporation_id, contract_type, status, availability,
                     start_station_id, date_expired_filetime, created_at_filetime, title)
                VALUES (9057, 100, 84, 1, 0, 0, 3001, $1, $2, 'Blueprint original'),
                       (9058, 101, 84, 1, 0, 0, 3001, $1, $2, 'Blueprint copy')
                """))
            {
                seedContracts.Parameters.AddWithValue(nowFileTime + 86_400_000_000L);
                seedContracts.Parameters.AddWithValue(nowFileTime);
                await seedContracts.ExecuteNonQueryAsync();
            }
            var inventory = new PgItemStore(source, inventorySchema);
            long originalItemId;
            long copyItemId;
            await using (var connection = await source.OpenConnectionAsync())
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                originalItemId = await inventory.CreateAsync(connection, transaction,
                    new NewItem(34, 1, 9_300_009_057, 0, Singleton: true), CancellationToken.None);
                copyItemId = await inventory.CreateAsync(connection, transaction,
                    new NewItem(34, 1, 9_300_009_058, 0, Singleton: true, BlueprintCopy: true,
                        BlueprintRuns: 5, MaterialEfficiency: 10, TimeEfficiency: 20), CancellationToken.None);
                await using var seedItems = new NpgsqlCommand($"""
                    INSERT INTO "{contractsSchema}".contract_items
                        (contract_id, item_id, type_id, quantity, in_crate)
                    VALUES (9057, $1, 34, 1, true), (9058, $2, 34, 1, true)
                    """, connection, transaction);
                seedItems.Parameters.AddWithValue(originalItemId);
                seedItems.Parameters.AddWithValue(copyItemId);
                await seedItems.ExecuteNonQueryAsync();
                await transaction.CommitAsync();
            }
            await using (var seedSde = source.CreateCommand($$$"""
                INSERT INTO "{{{sdeSchema}}}".npcStations (ord, key, doc)
                VALUES (0, 3001, '{"solarSystemID":30000001}'::jsonb);
                INSERT INTO "{{{sdeSchema}}}".mapSolarSystems (ord, key, doc)
                VALUES (0, 30000001, '{"constellationID":20000001,"regionID":10000001,"securityStatus":0.8,"name":{"en":"System"}}'::jsonb);
                INSERT INTO "{{{sdeSchema}}}".types (ord, key, doc)
                VALUES (0, 34, '{"groupID":300,"volume":10,"name":{"en":"Blueprint"}}'::jsonb);
                INSERT INTO "{{{sdeSchema}}}".groups (ord, key, doc)
                VALUES (0, 300, '{"categoryID":9}'::jsonb);
                """))
                await seedSde.ExecuteNonQueryAsync();

            var readModel = new PersistenceContractReadModel(source, contractsSchema, sdeSchema, inventorySchema);
            StartContractHost(new RecordingContractWriter(), readModel);
            var front = await Handshaken(await ConnectAsync());
            var open = Open();
            await front.SendAsync(open);
            _ = await front.ReceiveAsync();
            async Task<PyObject> Search(uint callId, int searchHint)
            {
                await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
                    Packets.CallWithKwargs(open, callId, "contractProxy", "SearchContracts", new PyTuple([]),
                        Eve.Services.Py.Dict(("machoVersion", new PyInt(1)), ("searchHint", new PyInt(searchHint)))))));
                while (true)
                {
                    var reply = Assert.IsType<Send>(await front.ReceiveAsync());
                    var wire = MarshalCodec.Decode(reply.Payload);
                    if (wire is not PyObject { Args: PyTuple args } || args.Items.Count < 3 ||
                        MachoAddress.Decode(args.Items[2]).CallId != new PyInt(callId))
                        continue;
                    return Assert.IsType<PyObject>(Result(wire));
                }
            }

            static long ContractId(PyObject entry)
            {
                var contract = Assert.IsType<PyObject>(Field(entry, "contract"));
                return Assert.IsType<PyInt>(Field(contract, "contractID")).Value;
            }

            var originals = Assert.IsType<PyList>(Field(await Search(1, 1), "contracts"));
            Assert.Equal([9057L], originals.Items.Cast<PyObject>().Select(ContractId));
            var copies = Assert.IsType<PyList>(Field(await Search(2, 2), "contracts"));
            Assert.Equal([9058L], copies.Items.Cast<PyObject>().Select(ContractId));
            var searchEntry = Assert.IsType<PyObject>(Assert.Single(copies.Items));
            var searchItem = Assert.IsType<PyObject>(Assert.Single(Assert.IsType<PyList>(Field(searchEntry, "items")).Items));
            Assert.Equal(new PyInt(1), Field(searchItem, "copy"));
            Assert.Equal(new PyInt(5), Field(searchItem, "licensedProductionRunsRemaining"));
            Assert.Equal(new PyInt(10), Field(searchItem, "materialLevel"));
            Assert.Equal(new PyInt(20), Field(searchItem, "productivityLevel"));

            await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
                Packets.CallWithKwargs(open, 3, "contractProxy", "GetContract", new PyTuple([new PyInt(9058)]),
                    Eve.Services.Py.Dict(("machoVersion", new PyInt(1)))))));
            while (true)
            {
                var reply = Assert.IsType<Send>(await front.ReceiveAsync());
                var wire = MarshalCodec.Decode(reply.Payload);
                if (wire is not PyObject { Args: PyTuple args } || args.Items.Count < 3 ||
                    MachoAddress.Decode(args.Items[2]).CallId != new PyInt(3))
                    continue;
                var detail = Assert.IsType<PyObject>(Result(wire));
                var detailItem = Assert.IsType<PyPackedRow>(Assert.Single(Assert.IsType<PyList>(Field(detail, "items")).Items));
                var detailItemFields = detailItem.Fields().ToDictionary(field => field.Key, field => field.Value);
                Assert.Same(PyBool.True, detailItemFields["copy"]);
                Assert.Equal(new PyInt(5), detailItemFields["licensedProductionRunsRemaining"]);
                break;
            }
        }
        finally
        {
            await using var cleanup = source.CreateCommand(
                $"DROP SCHEMA IF EXISTS \"{contractsSchema}\" CASCADE; DROP SCHEMA IF EXISTS \"{sdeSchema}\" CASCADE; DROP SCHEMA IF EXISTS \"{inventorySchema}\" CASCADE");
            await cleanup.ExecuteNonQueryAsync();
        }
    }
}
