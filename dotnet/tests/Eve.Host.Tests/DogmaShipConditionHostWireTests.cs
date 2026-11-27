using Eve.Characters;
using Eve.Data;
using Eve.Dogma;
using Eve.Fitting;
using Eve.Inventory;
using Eve.Macho;
using Eve.Marshal;
using Eve.Sde;
using Eve.Services.Dogma;
using Eve.Skills;
using Eve.Ships;
using Npgsql;
using Xunit;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [PgFact]
    public async Task Ship_condition_is_sent_in_absolute_dogma_units_after_loading_it_from_postgres()
    {
        const int shipTypeId = 601;
        const long stationId = 60_015_249;
        var connectionString = Environment.GetEnvironmentVariable("EVE_PG_URL");
        Assert.False(string.IsNullOrWhiteSpace(connectionString));
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(connectionString!));
        var sde = new SdeReader(source);
        Assert.Equal(SdeBuildPin.Standard, await sde.BuildAsync());
        var statics = await DogmaStatic.LoadAsync(sde);
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var charactersSchema = "dogma_ship_char_" + suffix;
        var inventorySchema = "dogma_ship_inv_" + suffix;
        var skillsSchema = "dogma_ship_skills_" + suffix;
        var fittingSchema = "dogma_ship_fit_" + suffix;
        try
        {
            await PgCharacterStore.MigrateAsync(source, charactersSchema);
            await PgItemStore.MigrateAsync(source, inventorySchema);
            await PgSkillStore.MigrateAsync(source, skillsSchema);
            await PgModuleStateStore.MigrateAsync(source, fittingSchema);

            var characters = new PgCharacterStore(source, charactersSchema);
            var items = new PgItemStore(source, inventorySchema);
            var skills = new PgSkillStore(source, skillsSchema);
            await statics.EnsureTypesAsync([shipTypeId, 1373]);
            var type = Assert.IsType<TypeDef>(statics.Type(shipTypeId));
            Assert.Equal(6, type.CategoryId);

            long characterId;
            long shipId;
            await using (var connection = await source.OpenConnectionAsync())
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                characterId = await characters.InsertAsync(connection, transaction, new NewCharacter(
                    1, "Dogma Ship Condition", 0, 1, 1, 1, 0, 1373, 1, null, stationId, 30_000_142,
                    20_000_020, 10_000_002, null, null));
                shipId = await items.CreateAsync(connection, transaction,
                    new NewItem(shipTypeId, characterId, stationId, ItemFlags.Hangar, 1, Singleton: true));
                await characters.SetShipAsync(connection, transaction, characterId, shipId);
                await transaction.CommitAsync();
            }

            var condition = new ShipCondition(0.25, 0.5, 0.75, 0.6, false);
            var persistedModules = new PgModuleStateStore(source, fittingSchema);
            await persistedModules.SetShipConditionAsync(shipId, condition);
            var restartedModules = new PgModuleStateStore(source, fittingSchema);
            Assert.Equal(condition, await restartedModules.ShipConditionAsync(shipId));

            var ships = new CharacterShips(source, statics, characters, items, skills, restartedModules);
            var service = new DogmaIMService(
                statics, ships, () => FixedNowMs,
                (_, _, _, _, _, _) => ValueTask.FromResult<bool?>(null));
            Start(
                configure: registry => registry.Register(service),
                sessionOpened: session =>
                {
                    session.Set("charid", new PyLong(characterId));
                    session.Set("stationid", new PyLong(stationId));
                });

            var front = await Handshaken(await ConnectAsync());
            var open = Open();
            await front.SendAsync(open);
            _ = await front.ReceiveAsync();
            await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(Packets.CallWithKwargs(
                open, 1, "dogmaIM", "MachoBindObject",
                new PyTuple([new PyTuple([new PyLong(stationId), new PyInt(15)]), PyNone.Instance]),
                MachoVersion()))));
            var bindReply = Assert.IsType<Send>(await front.ReceiveAsync());
            Assert.Equal(MachoMessage.CallRsp, Envelope(bindReply).Type);
            var bindResult = Assert.IsType<PyTuple>(Result(MarshalCodec.Decode(bindReply.Payload)));
            var bound = Assert.IsType<PySubStruct>(bindResult.Items[0]);
            var boundTuple = Assert.IsType<PyTuple>(Assert.IsType<PySubStream>(bound.Value).Decoded);
            var objectId = JsSemantics.StringValue(boundTuple.Items[0])!;

            var answer = Assert.IsType<PyObject>(await HostBoundRpcAsync(
                front, open, 2, objectId, "GetAllInfo", new PyTuple([PyBool.True, PyBool.True, PyBool.False])));
            var shipInfo = Assert.IsType<PyDict>(DogmaField(answer, "shipInfo"));
            var itemInfo = Assert.IsType<PyObject>(DogmaDictValue(shipInfo, shipId));
            var attributes = Assert.IsType<PyDict>(DogmaField(itemInfo, "attributes"));
            var expected = new Dictionary<int, double>
            {
                [3] = Math.Round(type.Attributes.GetValueOrDefault(9) * condition.Damage, 6),
                [18] = Math.Round(type.Attributes.GetValueOrDefault(482) * condition.Charge, 6),
                [266] = Math.Round(type.Attributes.GetValueOrDefault(265) * condition.ArmorDamage, 6),
                [264] = Math.Round(type.Attributes.GetValueOrDefault(263) * condition.ShieldCharge, 6),
            };
            foreach (var (attributeId, value) in expected)
            {
                Assert.Equal(value, DogmaNumber(DogmaDictValue(attributes, attributeId)));
            }

            var shipState = Assert.IsType<PyTuple>(DogmaField(answer, "shipState"));
            var instanceRows = Assert.IsType<PyDict>(shipState.Items[0]);
            var stateRow = Assert.IsType<PyPackedRow>(DogmaDictValue(instanceRows, shipId));
            foreach (var (column, attributeId) in new[]
            {
                ("damage", 3), ("charge", 18), ("armorDamage", 266), ("shieldCharge", 264),
            })
            {
                var index = stateRow.Columns.Select((entry, i) => (entry, i)).Single(entry => entry.entry.Name == column).i;
                Assert.Equal(expected[attributeId], DogmaNumber(stateRow.Values[index]));
            }
        }
        finally
        {
            foreach (var schema in new[] { charactersSchema, inventorySchema, skillsSchema, fittingSchema })
            {
                await using var drop = source.CreateCommand($"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE");
                await drop.ExecuteNonQueryAsync();
            }
        }
    }

    private static PyValue DogmaField(PyObject keyVal, string name) =>
        Assert.IsType<PyDict>(keyVal.Args).Entries.Single(entry => JsSemantics.StringValue(entry.Key) == name).Value;

    private static PyValue DogmaDictValue(PyDict dict, long key) =>
        dict.Entries.Single(entry => DogmaId(entry.Key) == key).Value;

    private static long? DogmaId(PyValue value) => value switch
    {
        PyInt number => number.Value,
        PyLong number => number.Value,
        _ => null,
    };

    private static double DogmaNumber(PyValue value) => value switch
    {
        PyInt number => number.Value,
        PyLong number => number.Value,
        PyReal number => number.Value,
        _ => throw new Xunit.Sdk.XunitException($"Expected numeric dogma value, received {value.GetType().Name}."),
    };
}
