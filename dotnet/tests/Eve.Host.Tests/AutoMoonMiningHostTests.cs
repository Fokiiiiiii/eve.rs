using System.Text.Json.Nodes;
using Eve.Host;
using Eve.Inventory;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Structures;
using Npgsql;
using Xunit;

namespace Eve.Host.Tests;

[Collection("host-composition")]
public sealed class AutoMoonMiningHostTests
{
    private const long CorporationId = 900_001_001;
    private const int MetenoxTypeId = 81_826;
    private const int ZeolitesTypeId = 45_490;
    private const int AtmosphericGasesTypeId = 16_634;
    private const int FuelBlockTypeId = 4_247;
    private const long MoonId = 40_000_001;
    private const long StructureId = 81_000_000_011;

    [PgFact]
    public async Task Auto_moon_mining_reads_authoritative_profile_and_inventory_over_registered_host_service()
    {
        await using var rig = await CharacterHostTests.CharacterHostRig.CreateAsync();
        var characterId = await rig.CreateCharacterAsync();
        await rig.AddCorporationMemberAsync(characterId);
        await using (var connection = await rig.Source.OpenConnectionAsync())
        await using (var role = new NpgsqlCommand(
            $"UPDATE {rig.CorporationsSchema}.members SET roles = $2 WHERE character_id = $1", connection))
        {
            role.Parameters.Add(new NpgsqlParameter { Value = characterId });
            role.Parameters.Add(new NpgsqlParameter { Value = Eve.Corporations.CorporationRoles.StationManager });
            Assert.Equal(1, await role.ExecuteNonQueryAsync());
        }

        var now = rig.NowUnixMs;
        var structures = new StructureStore(rig.Source, rig.StructuresSchema);
        var inventory = new PgItemStore(rig.Source, rig.InventorySchema);
        await using (var connection = await rig.Source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            Assert.True(await structures.CreateAsync(connection, transaction,
                new NewStructure(StructureId, MetenoxTypeId, 30_000_142, 0, 0, 0, 0,
                    CorporationId, 0, "Metenox test", "")));
            await structures.SetServiceAsync(connection, transaction, StructureId,
                StructureServiceIds.AutoMoonMining, ServiceState.Online, now);
            await inventory.CreateAsync(connection, transaction,
                new NewItem(FuelBlockTypeId, CorporationId, StructureId, 172, 4));
            await inventory.CreateAsync(connection, transaction,
                new NewItem(AtmosphericGasesTypeId, CorporationId, StructureId, 186, 8));
            var moonStore = new MoonExtractionStore(rig.Source, rig.StructuresSchema);
            var profile = new JsonObject
            {
                ["resourcesByStructureID"] = new JsonObject
                {
                    [StructureId.ToString()] = new JsonObject
                    {
                        ["moonID"] = MoonId,
                        ["solarSystemID"] = 30_000_142,
                        ["ownerID"] = CorporationId,
                        ["resources"] = new JsonObject { [ZeolitesTypeId.ToString()] = 0.4259 },
                    },
                },
            };
            Assert.Equal(1, await moonStore.ImportAsync(connection, transaction, profile));
            await transaction.CommitAsync();
        }

        var session = new ClientSession { CharacterId = characterId };
        session.Set("charid", new PyLong(characterId));
        var service = Assert.IsAssignableFrom<IService>(rig.Registry.Lookup("autoMoonMining"));
        Assert.True(service.HasMethod("RequestMiningDetails"));
        Assert.True(service.HasMethod("RequestMiningCycleOutput"));

        var details = Assert.IsType<PyDict>(await CallAsync(service,
            "RequestMiningDetails", session, new PyInt(30_000_142), new PyLong(StructureId)));
        Assert.Equal(new PyInt((int)MoonId), Field(details, "moonID"));
        Assert.Equal(new PyLong(Py.FileTimeFromUnixMs(now + 3_600_000)), Field(details, "nextHarvestTime"));
        Assert.Equal(new PyInt(4), Field(Assert.IsType<PyDict>(Field(details, "availableFuel")), "4247"));
        Assert.Equal(new PyInt(8), Field(Assert.IsType<PyDict>(Field(details, "outputMaterials")), "16634"));
        Assert.Equal(new PyReal(499_999.6), Field(details, "remainingOutputBayCapacity"));

        var cycle = Assert.IsType<PyDict>(await CallAsync(service,
            "RequestMiningCycleOutput", session, new PyInt(30_000_142), new PyLong(StructureId)));
        Assert.Equal(new PyInt(332), Field(cycle, "16634"));

        await rig.RestartAsync();
        service = Assert.IsAssignableFrom<IService>(rig.Registry.Lookup("autoMoonMining"));
        details = Assert.IsType<PyDict>(await CallAsync(service,
            "RequestMiningDetails", session, new PyInt(30_000_142), new PyLong(StructureId)));
        cycle = Assert.IsType<PyDict>(await CallAsync(service,
            "RequestMiningCycleOutput", session, new PyInt(30_000_142), new PyLong(StructureId)));
        Assert.Equal(new PyInt(8), Field(Assert.IsType<PyDict>(Field(details, "outputMaterials")), "16634"));
        Assert.Equal(new PyInt(332), Field(cycle, "16634"));
    }

    private static PyValue Field(PyValue value, string key) =>
        Assert.IsType<PyDict>(value).Entries.Single(entry =>
            JsSemantics.StringValue(entry.Key) == key ||
            long.TryParse(key, out var number) && entry.Key switch
            {
                PyInt integer => integer.Value == number,
                PyLong integer => integer.Value == number,
                _ => false,
            }).Value;

    private static ValueTask<PyValue> CallAsync(IService service, string method, ClientSession session, params PyValue[] args) =>
        service.CallAsync(new ServiceCall { Method = method, Args = args, Session = session });
}
