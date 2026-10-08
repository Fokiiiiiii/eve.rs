using Eve.Characters;
using Eve.Data;
using Eve.GameStore;
using Eve.Inventory;
using Eve.Macho;
using Eve.Host;
using Eve.Marshal;
using Eve.Services;
using Eve.Sde;
using Npgsql;
using System.Text.Json.Nodes;
using System.Text;
using Xunit;

namespace Eve.Host.Tests;

public sealed class CompatibilityRpcServiceTests
{
    private static ClientSession Session(long? characterId = null)
    {
        var session = new ClientSession();
        if (characterId is { } id) session.Set("charid", new PyInt(id));
        return session;
    }

    private static PyValue Field(PyValue value, string name)
    {
        var dict = Assert.IsType<PyDict>(Assert.IsType<PyObject>(value).Args);
        return dict.Entries.Single(pair => JsSemantics.StringValue(pair.Key) == name).Value;
    }

    private static ServiceCall Call(string method, ClientSession? session = null, params PyValue[] args) => new()
    {
        Method = method,
        Args = args,
        Session = session ?? Session(),
    };

    [Fact]
    public void Every_assigned_rpc_is_advertised_by_its_service()
    {
        var services = new IService[]
        {
            new ShipCosmeticsMgrRpcService(),
            new CharacterEnergyMgrRpcService(), new PaperDollServerRpcService(),
            new PlanetMgrRpcService(), new IndustryMonitorRpcService(),
        };
        var expected = new Dictionary<string, string[]>
        {
            ["shipCosmeticsMgr"] = ["ActivateSkinLicense", "ApplySkinToShip", "GMExpireSkinLicense", "GetEnabledCosmetics", "GetFirstPartySkinData", "GiveSkin", "RemoveSkin"],
            ["characterEnergyMgr"] = ["GetMyEnergyState"],
            ["paperDollServer"] = ["GetMyPaperDollData", "GetPaperDollData", "GetPaperDollDataBatch", "GetPaperDollPortraitDataFor", "UpdateExistingCharacterFull", "UpdateExistingCharacterLimited"],
            ["planetMgr"] = ["DeleteLaunch", "GetMyLaunchesDetails", "GetPlanetsForChar"],
            ["industryMonitor"] = ["ConnectJob", "DisconnectJob"],
        };
        Assert.Equal(expected.Keys.Order(), services.Select(service => service.Name).Order());
        foreach (var service in services)
            foreach (var method in expected[service.Name])
                Assert.True(service.HasMethod(method), $"{service.Name}.{method}");
    }

    [Fact]
    public async Task Handlers_return_reference_shapes()
    {
        var cosmetics = new ShipCosmeticsMgrRpcService();
        var energy = new CharacterEnergyMgrRpcService(nowUnixMs: () => 1_700_000_000_000L);
        var paperDoll = new PaperDollServerRpcService();
        var planets = new PlanetMgrRpcService();
        var industry = new IndustryMonitorRpcService(availableMaterials: (_, _) => ValueTask.FromResult<IReadOnlyDictionary<int, long>>(new Dictionary<int, long> { [34] = 5 }));

        Assert.IsType<PyDict>(await cosmetics.CallAsync(Call("GetEnabledCosmetics", args: [new PyInt(10)])));
        Assert.IsType<PyNone>(await cosmetics.CallAsync(Call("GetFirstPartySkinData", args: [new PyInt(20), new PyInt(30)])));
        Assert.IsType<PyNone>(await cosmetics.CallAsync(Call("GiveSkin", Session(20), new PyInt(30))));

        var state = Assert.IsType<PyObjectEx>(await energy.CallAsync(Call("GetMyEnergyState", Session(20))));
        Assert.Equal(ObjectExKind.Ex1, state.Kind);
        Assert.IsType<PyNone>(await paperDoll.CallAsync(Call("GetPaperDollData", Session(20))));
        Assert.IsType<PyNone>(await paperDoll.CallAsync(Call("GetMyPaperDollData", Session(20), new PyInt(20))));
        Assert.IsType<PyDict>(await paperDoll.CallAsync(Call("GetPaperDollDataBatch", args: [new PyList([new PyInt(20)])])));
        Assert.IsType<PyTuple>(await paperDoll.CallAsync(Call("GetPaperDollPortraitDataFor", Session(20), new PyInt(20))));
        Assert.IsType<PyNone>(await paperDoll.CallAsync(Call("UpdateExistingCharacterFull", Session(20), new PyInt(20))));
        Assert.IsType<PyNone>(await paperDoll.CallAsync(Call("UpdateExistingCharacterLimited", Session(20), new PyInt(20))));
        Assert.IsType<PyObjectEx>(await planets.CallAsync(Call("GetMyLaunchesDetails", Session(20))));
        Assert.IsType<PyObjectEx>(await planets.CallAsync(Call("GetPlanetsForChar", Session(20))));
        Assert.Equal(PyBool.True, await planets.CallAsync(Call("DeleteLaunch", Session(20), new PyInt(100))));

        var connected = Assert.IsType<PyTuple>(await industry.CallAsync(Call("ConnectJob", Session(20),
            Py.KeyVal(("blueprintID", new PyInt(10)), ("activityID", new PyInt(1)), ("facilityID", new PyInt(20))))));
        Assert.Equal(new PyInt(1), connected.Items[0]);
        Assert.Contains(Assert.IsType<PyDict>(connected.Items[1]).Entries, entry => entry.Key == new PyInt(34) && entry.Value == new PyInt(5));
        Assert.IsType<PyNone>(await industry.CallAsync(Call("DisconnectJob", Session(20), connected.Items[0])));
        var secondMonitor = Assert.IsType<PyTuple>(await industry.CallAsync(Call("ConnectJob", Session(20), Py.KeyVal(("activityID", new PyInt(1))))));
        Assert.Equal(new PyInt(2), secondMonitor.Items[0]);
    }

    [Fact]
    public async Task Game_store_backed_calls_read_and_write_the_existing_runtime_tables()
    {
        var pgUrl = Environment.GetEnvironmentVariable("EVE_PG_URL");
        Assert.False(string.IsNullOrWhiteSpace(pgUrl));
        await using var source = NpgsqlDataSource.Create(PgConnection.ToConnectionString(pgUrl!));
        var sde = new SdeReader(source);
        Assert.NotNull(await sde.BuildAsync());
        var skinCatalog = await ShipCosmeticsCatalog.LoadAsync(sde);
        Assert.NotEmpty(skinCatalog.Skins);
        Assert.NotEmpty(skinCatalog.LicenseTypes);
        var schema = "rpc_test_" + Guid.NewGuid().ToString("N");
        var inventorySchema = "rpc_inv_" + Guid.NewGuid().ToString("N");
        var store = new PersistenceStore(source, schema);
        try
        {
            await store.EnsureSchemaAsync();
            await using var owner = await OwnerSession.StartAsync(store, PersistenceRoles.World, "rpc-test-" + Guid.NewGuid().ToString("N"));
            await using var cache = new GameStoreCache(store, owner, new GameStoreCacheOptions { ProcessRole = PersistenceRoles.World, FlushDelay = TimeSpan.FromHours(1) });
            await cache.RecoverAsync();
            foreach (var table in new[] { "characterEnergyState", "shipCosmetics", "shipLogoFittings", "planetRuntimeState", "industryRuntime" })
                await cache.LoadAsync(table);
            await PgItemStore.MigrateAsync(source, inventorySchema);
            var items = new PgItemStore(source, inventorySchema);

            const long now = 1_700_000_000_000L;
            var nowFileTime = Py.FileTimeFromUnixMs(now);
            cache.Write("characterEnergyState", "/characters/20", new JsonObject
            {
                ["energyLevel"] = 20, ["lastRechargeTickTimestamp"] = nowFileTime - 792L * 10_000_000,
                ["energyIncreasePerRechargeTick"] = 2, ["rechargeTickPeriod"] = 792,
                ["minEnergyLevel"] = 0, ["quiescentEnergyLevel"] = 100,
            });
            cache.Write("shipLogoFittings", "/ships/55", new JsonObject
            {
                ["enabledByBackendSlot"] = new JsonObject { ["3"] = new JsonObject { ["cosmeticType"] = 900 } },
            });
            cache.Write("planetRuntimeState", "/launchesByID/77", new JsonObject
            {
                ["launchID"] = 77, ["itemID"] = 77, ["ownerID"] = 20, ["solarSystemID"] = 30000142,
                ["planetID"] = 40000001, ["status"] = 1, ["launchTime"] = nowFileTime.ToString(), ["x"] = 1.25,
            });
            cache.Write("planetRuntimeState", "/coloniesByKey/40000001:20", new JsonObject
            {
                ["planetID"] = 40000001, ["ownerID"] = 20, ["solarSystemID"] = 30000142,
                ["typeID"] = 2016, ["numberOfPins"] = 4, ["celestialIndex"] = 2,
            });

            var session = Session(20);
            var energy = Assert.IsType<PyObjectEx>(await new CharacterEnergyMgrRpcService(cache, () => now).CallAsync(Call("GetMyEnergyState", session)));
            var energyFields = Assert.IsType<PyList>(Assert.IsType<PyTuple>(energy.Header).Items[1]);
            Assert.Equal(new PyInt(22), energyFields.Items[0]);
            var enabled = Assert.IsType<PyDict>(await new ShipCosmeticsMgrRpcService(cache, cache, nowUnixMs: () => now)
                .CallAsync(Call("GetEnabledCosmetics", session, new PyInt(55))));
            Assert.Equal(new PyInt(900), Assert.Single(enabled.Entries).Value);

            var cosmetics = new ShipCosmeticsMgrRpcService(cache, cache, nowUnixMs: () => now);
            await cosmetics.CallAsync(new ServiceCall
            {
                Method = "GiveSkin", Args = [new PyInt(900)], Session = session,
                Kwargs = Py.Dict(("duration", new PyInt(30)), ("isSingleUse", PyBool.True)),
            });
            Assert.IsType<PyObject>(await cosmetics.CallAsync(Call("GetFirstPartySkinData", session, new PyInt(20), new PyInt(900))));

            var license = skinCatalog.LicenseTypes.Values.First(entry => skinCatalog.Skins.ContainsKey(entry.SkinId));
            long licenseItemId;
            await using (var itemConnection = await source.OpenConnectionAsync())
            await using (var transaction = await itemConnection.BeginTransactionAsync())
            {
                licenseItemId = await items.CreateAsync(itemConnection, transaction, new NewItem(license.LicenseTypeId, 20, 600, 4, 1, true));
                await transaction.CommitAsync();
            }
            long? appliedSkinId = null;
            long? activatedSkinId = null;
            long consumedItemId = 0;
            long consumedQuantity = -1;
            var wiredCosmetics = new ShipCosmeticsMgrRpcService(cache, cache, items, source, skinCatalog, () => now,
                (_, _, skinId) => appliedSkinId = skinId,
                (_, skinId) => activatedSkinId = skinId,
                (_, item, quantity, _) => { consumedItemId = item.Id; consumedQuantity = quantity; return ValueTask.CompletedTask; });
            await wiredCosmetics.CallAsync(Call("ActivateSkinLicense", session, new PyList([new PyLong(licenseItemId)])));
            Assert.Equal((long?)license.SkinId, activatedSkinId);
            Assert.Equal(licenseItemId, consumedItemId);
            Assert.Equal(0L, consumedQuantity);
            Assert.Null(await items.GetAsync(licenseItemId));
            Assert.IsType<PyObject>(await wiredCosmetics.CallAsync(Call("GetFirstPartySkinData", session, new PyInt(20), new PyInt(license.SkinId))));

            var shipTypeId = skinCatalog.Skins[license.SkinId].ShipTypeIds.FirstOrDefault();
            if (shipTypeId <= 0) shipTypeId = 587;
            long shipItemId;
            await using (var shipConnection = await source.OpenConnectionAsync())
            await using (var shipTransaction = await shipConnection.BeginTransactionAsync())
            {
                shipItemId = await items.CreateAsync(shipConnection, shipTransaction, new NewItem(shipTypeId, 20, 600, 4, 1, true));
                await shipTransaction.CommitAsync();
            }
            await wiredCosmetics.CallAsync(Call("ApplySkinToShip", session, new PyInt(shipItemId), new PyInt(license.SkinId)));
            Assert.Equal((long?)license.SkinId, appliedSkinId);
            Assert.True(cache.Read("shipCosmetics", $"/ships/{shipItemId}").Success);
            await wiredCosmetics.CallAsync(Call("RemoveSkin", session, new PyInt(license.SkinId), new PyInt(20)));
            Assert.IsType<PyNone>(await wiredCosmetics.CallAsync(Call("GetFirstPartySkinData", session, new PyInt(20), new PyInt(license.SkinId))));
            await wiredCosmetics.CallAsync(Call("GiveSkin", session, new PyInt(license.SkinId)));
            await wiredCosmetics.CallAsync(Call("GMExpireSkinLicense", session, new PyInt(license.SkinId)));
            Assert.IsType<PyNone>(await wiredCosmetics.CallAsync(Call("GetFirstPartySkinData", session, new PyInt(20), new PyInt(license.SkinId))));

            var planetMgr = new PlanetMgrRpcService(cache, nowUnixMs: () => now);
            Assert.Single(Assert.IsType<PyObjectEx>(await planetMgr.CallAsync(Call("GetMyLaunchesDetails", session))).List);
            Assert.Single(Assert.IsType<PyObjectEx>(await planetMgr.CallAsync(Call("GetPlanetsForChar", session))).List);
            Assert.Equal(PyBool.True, await planetMgr.CallAsync(Call("DeleteLaunch", session, new PyInt(77))));
            Assert.Empty(Assert.IsType<PyObjectEx>(await planetMgr.CallAsync(Call("GetMyLaunchesDetails", session))).List);

            var itemTypes = new ItemTypeCatalog(sde);
            Assert.Equal(9, (await itemTypes.GetAsync(681))?.CategoryId);
            long blueprintItemId;
            await using (var materialsConnection = await source.OpenConnectionAsync())
            await using (var materialsTransaction = await materialsConnection.BeginTransactionAsync())
            {
                blueprintItemId = await items.CreateAsync(materialsConnection, materialsTransaction, new NewItem(681, 20, 900, 4, 1, true));
                await items.CreateAsync(materialsConnection, materialsTransaction, new NewItem(34, 20, 900, 4, 5));
                await items.CreateAsync(materialsConnection, materialsTransaction, new NewItem(34, 20, 900, 5, 7));
                await items.CreateAsync(materialsConnection, materialsTransaction, new NewItem(35, 20, 900, 4, 3));
                await materialsTransaction.CommitAsync();
            }
            var industry = new IndustryMonitorRpcService(cache, items, nowUnixMs: () => now, itemTypes: itemTypes);
            var request = Py.KeyVal(("blueprintID", new PyInt(blueprintItemId)), ("blueprintTypeID", new PyInt(681)), ("activityID", new PyInt(1)), ("facilityID", new PyInt(20)), ("inputLocation", Py.KeyVal(("itemID", new PyInt(900)), ("ownerID", new PyInt(20)), ("flagID", new PyInt(4)), ("canTake", PyBool.True))));
            var connected = Assert.IsType<PyTuple>(await industry.CallAsync(Call("ConnectJob", session, request)));
            Assert.Equal(new PyInt(1), connected.Items[0]);
            var availableMaterials = Assert.IsType<PyDict>(connected.Items[1]);
            Assert.Equal(2, availableMaterials.Entries.Count);
            Assert.Contains(availableMaterials.Entries, entry => entry.Key == new PyInt(34) && entry.Value == new PyInt(5));
            Assert.Contains(availableMaterials.Entries, entry => entry.Key == new PyInt(35) && entry.Value == new PyInt(3));
            Assert.True(cache.Read("industryRuntime", "/monitors/1").Success);
            await industry.CallAsync(Call("DisconnectJob", session, connected.Items[0]));
            Assert.False(cache.Read("industryRuntime", "/monitors/1").Success);
        }
        finally
        {
            await using var connection = await source.OpenConnectionAsync();
            await using var command = new NpgsqlCommand($"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE", connection);
            await command.ExecuteNonQueryAsync();
            await using var inventoryCommand = new NpgsqlCommand($"DROP SCHEMA IF EXISTS {inventorySchema} CASCADE", connection);
            await inventoryCommand.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task Paper_doll_Rpcs_round_trip_character_state_and_emit_portrait_dbrows()
    {
        var pgUrl = Environment.GetEnvironmentVariable("EVE_PG_URL");
        Assert.False(string.IsNullOrWhiteSpace(pgUrl));
        await using var source = NpgsqlDataSource.Create(PgConnection.ToConnectionString(pgUrl!));
        var schema = "rpc_chars_" + Guid.NewGuid().ToString("N");
        try
        {
            await PgCharacterStore.MigrateAsync(source, schema);
            var characters = new PgCharacterStore(source, schema);
            long characterId;
            await using (var connection = await source.OpenConnectionAsync())
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                characterId = await characters.InsertAsync(connection, transaction, new NewCharacter(
                    20, "RPC Paper Doll", 1, 1, 1, 10, 35, 1373, 1_000_006, 500_001,
                    60_000_610, 30_002_775, 20_000_406, 10_000_033, null, null));
                await transaction.CommitAsync();
            }
            var service = new PaperDollServerRpcService(characters);
            var session = Session(characterId);
            var doll = Py.KeyVal(("dna", new PyStr("keep-me")));
            var portrait = Py.KeyVal(("cameraX", new PyReal(2.5)), ("backgroundID", new PyInt(4)));
            await service.CallAsync(Call("UpdateExistingCharacterFull", session,
                new PyInt(characterId), doll, portrait, PyBool.True));

            var data = await service.CallAsync(Call("GetPaperDollData", session, new PyInt(characterId)));
            Assert.Equal("keep-me", Encoding.UTF8.GetString(Assert.IsType<PyBytes>(Field(data, "dna")).Value.Span));
            var ownData = await service.CallAsync(Call("GetMyPaperDollData", session, new PyInt(characterId + 1)));
            Assert.Equal("keep-me", Encoding.UTF8.GetString(Assert.IsType<PyBytes>(Field(ownData, "dna")).Value.Span));
            var batch = Assert.IsType<PyDict>(await service.CallAsync(Call("GetPaperDollDataBatch", session,
                new PyList([new PyInt(characterId)]))));
            Assert.Equal("keep-me", Encoding.UTF8.GetString(Assert.IsType<PyBytes>(Field(Assert.Single(batch.Entries).Value, "dna")).Value.Span));

            var portraitResult = Assert.IsType<PyTuple>(await service.CallAsync(Call("GetPaperDollPortraitDataFor", session, new PyInt(characterId))));
            var row = Assert.IsType<PyPackedRow>(Assert.Single(Assert.IsType<PyList>(portraitResult.Items[0]).Items));
            Assert.Equal(37, row.Values.Count);
            Assert.Equal(new PyReal(2.5), row.Values[0]);
            Assert.Equal(new PyInt(4), row.Values[7]);
            Assert.IsType<PyNone>(portraitResult.Items[1]);

            await service.CallAsync(Call("UpdateExistingCharacterLimited", session,
                new PyInt(characterId), PyNone.Instance, PyNone.Instance, PyBool.False));
            Assert.IsType<PyNone>(await service.CallAsync(Call("GetPaperDollData", session, new PyInt(characterId))));
            var clearedPortrait = Assert.IsType<PyTuple>(await service.CallAsync(Call("GetPaperDollPortraitDataFor", session, new PyInt(characterId))));
            Assert.Empty(Assert.IsType<PyList>(clearedPortrait.Items[0]).Items);
        }
        finally
        {
            await using var connection = await source.OpenConnectionAsync();
            await using var command = new NpgsqlCommand($"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE", connection);
            await command.ExecuteNonQueryAsync();
        }
    }}
