using System.Text.Json.Nodes;
using Eve.Data;
using Eve.GameStore;
using Eve.Host;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;
using Eve.Wallet;
using Npgsql;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [PgFact]
    public async Task Sov_mgr_reads_and_skyhook_and_fuel_access_writes_round_trip_through_host_and_postgres()
    {
        var url = Environment.GetEnvironmentVariable("EVE_PG_URL")
            ?? throw new InvalidOperationException("EVE_PG_URL is required.");
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var storeSchema = $"sov_rpc_{suffix}";
        var walletSchema = $"sov_wallet_{suffix}";
        const long systemId = 30_000_142;
        const long characterId = 90_000_001;
        const long corporationId = 98_000_001;
        const long allianceId = 99_000_001;
        const long hubId = 10_200_002;
        const long initialGroupId = 99_000_077;
        const long nextGroupId = 99_000_078;
        const long destroyedSkyhookId = 81_000_001;
        const long acquiredSkyhookId = 81_000_002;

        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(url));
        var store = new PersistenceStore(source, storeSchema);
        OwnerSession? owner = null;
        GameStoreCache? cache = null;
        try
        {
            await store.EnsureSchemaAsync();
            owner = await OwnerSession.StartAsync(
                store, PersistenceRoles.World, $"sov-rpc-{suffix}",
                new AcquireOptions(LeaseMs: 600_000), renewEvery: TimeSpan.FromHours(1));
            cache = new GameStoreCache(store, owner,
                new GameStoreCacheOptions { ProcessRole = PersistenceRoles.World, FlushDelay = TimeSpan.FromHours(1) });
            await cache.RecoverAsync();
            await cache.LoadAsync("sovereignty");
            await cache.LoadAsync("accessGroups");

            var claimTime = DateTime.UtcNow.Subtract(TimeSpan.FromMinutes(5)).ToFileTimeUtc();
            var sovereignty = new JsonObject
            {
                ["systems"] = new JsonObject
                {
                    [systemId.ToString(System.Globalization.CultureInfo.InvariantCulture)] = new JsonObject
                    {
                        ["solarSystemID"] = systemId,
                        ["claimStructureID"] = 10_200_001,
                        ["infrastructureHubID"] = hubId,
                        ["corporationID"] = corporationId,
                        ["allianceID"] = allianceId,
                        ["claimTime"] = claimTime,
                        ["fuelAccessGroupID"] = initialGroupId,
                        ["structures"] = new JsonArray
                        {
                            new JsonObject
                            {
                                ["itemID"] = hubId,
                                ["typeID"] = 32226,
                                ["ownerID"] = corporationId,
                                ["corporationID"] = corporationId,
                                ["allianceID"] = allianceId,
                                ["solarSystemID"] = systemId,
                                ["defenseMultiplier"] = 1.25,
                                ["isCapital"] = true,
                            },
                        },
                    },
                },
                ["skyhooks"] = new JsonObject
                {
                    [destroyedSkyhookId.ToString(System.Globalization.CultureInfo.InvariantCulture)] = new JsonObject
                    {
                        ["skyhookID"] = destroyedSkyhookId,
                        ["typeID"] = 81080,
                        ["solarSystemID"] = systemId,
                        ["planetID"] = 40_000_001,
                        ["corporationID"] = corporationId,
                        ["allianceID"] = allianceId,
                        ["productionActive"] = true,
                        ["resourceVersion"] = "destroy-test-v1",
                    },
                    [acquiredSkyhookId.ToString(System.Globalization.CultureInfo.InvariantCulture)] = new JsonObject
                    {
                        ["skyhookID"] = acquiredSkyhookId,
                        ["typeID"] = 81080,
                        ["solarSystemID"] = systemId,
                        ["planetID"] = 40_000_002,
                        ["corporationID"] = corporationId + 1,
                        ["allianceID"] = allianceId + 1,
                        ["productionActive"] = true,
                        ["resourceVersion"] = "acquire-test-v1",
                        ["acquisitionPriceISK"] = 1_250m,
                        ["acquisitionWalletRefTypeID"] = 184,
                    },
                },
            };
            var accessGroups = new JsonObject
            {
                [initialGroupId.ToString(System.Globalization.CultureInfo.InvariantCulture)] = AccessGroup(characterId),
                [nextGroupId.ToString(System.Globalization.CultureInfo.InvariantCulture)] = new JsonObject
                {
                    ["creatorID"] = characterId,
                    ["members"] = new JsonArray(),
                },
            };
            Assert.True(cache.Write("sovereignty", "/", sovereignty).Success);
            Assert.True(cache.Write("accessGroups", "/", accessGroups).Success);
            await cache.FlushTableAsync("sovereignty");
            await cache.FlushTableAsync("accessGroups");

            await PgWalletStore.MigrateAsync(source, walletSchema);
            var wallets = new PgWalletStore(source, walletSchema);
            var walletTransactions = new WalletTransactions(source, walletSchema);
            await using (var connection = await source.OpenConnectionAsync())
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                await wallets.OpenAsync(connection, transaction, corporationId, 10_000_000m, 0);
                await transaction.CommitAsync();
            }

            var model = new PersistenceSovFwReadModel(
                async (table, cancellationToken) =>
                    await store.LoadTableAsync(table, createIfMissing: false, cancellationToken),
                (id, _) => Task.FromResult(id == characterId
                    ? new Eve.Relations.Affiliation(corporationId, allianceId, null)
                    : new Eve.Relations.Affiliation(0, null, null)));

            async ValueTask<bool> ChargeSkyhooksAsync(
                IReadOnlyList<SovSkyhookAcquisitionCharge> charges,
                CancellationToken cancellationToken)
            {
                if (charges.Count == 0)
                    return true;
                await using var connection = await source.OpenConnectionAsync(cancellationToken);
                await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
                foreach (var charge in charges)
                    await walletTransactions.AdjustAsync(
                        connection, transaction, charge.CorporationId, -charge.AmountIsk, 0,
                        charge.ReferenceTypeId, "Skyhook acquisition", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                        charge.OperationKey, cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return true;
            }

            async ValueTask<bool> HasWalletOperationAsync(
                SovSkyhookAcquisitionCharge charge,
                CancellationToken cancellationToken)
            {
                await using var connection = await source.OpenConnectionAsync(cancellationToken);
                await using var command = new NpgsqlCommand(
                    $"SELECT EXISTS (SELECT 1 FROM \"{walletSchema}\".journal WHERE owner_id = $1 AND account_key = 1000 AND operation_key = $2)",
                    connection);
                command.Parameters.AddWithValue(charge.CorporationId);
                command.Parameters.AddWithValue(charge.OperationKey);
                return await command.ExecuteScalarAsync(cancellationToken) is true;
            }

            var writer = new PersistenceSovFwWriteOperations(
                table => cache.Read(table).Data as JsonObject ?? new JsonObject(),
                (table, path, data) => cache.Write(table, path, data),
                cache.FlushTableAsync,
                async (table, cancellationToken) =>
                    await store.LoadTableAsync(table, createIfMissing: false, cancellationToken),
                (call, requestedSystem, hub) => ValueTask.FromResult(
                    call.Session.CharacterId == characterId &&
                    (call.Session.GetInt("corpid") ?? call.Session.GetInt("corporationID")) == corporationId &&
                    requestedSystem == systemId && hub.CorporationId == corporationId && hub.AllianceId == allianceId),
                new SemaphoreSlim(1, 1),
                ChargeSkyhooksAsync,
                HasWalletOperationAsync);
            var hubWrites = new SovHubWriteOperations(
                writer.CanManageHubAsync,
                writer.SetFuelAccessGroupAsync,
                writer.SaveUpgradeConfigurationAsync,
                writer.SelfDestructUpgradeAsync,
                writer.SaveWorkforceConfigurationAsync);
            var skyhookWrites = new SovSkyhookWriteOperations(
                writer.DestroySkyhooksAsync,
                writer.AcquireSkyhooksAsync);
            var sov = SovFwService.Create(model.ReadSnapshotAsync, model.IsOnLocalFuelAccessGroupAsync, hubWrites, skyhookWrites)
                .Single(service => service.Name == "sovMgr");

            Start(
                configure: registry => registry.Register(sov),
                sessionOpened: session =>
                {
                    session.CharacterId = characterId;
                    session.Set("charid", new PyLong(characterId));
                    session.Set("corpid", new PyLong(corporationId));
                    session.Set("allianceid", new PyLong(allianceId));
                    session.Set("solarsystemid2", new PyLong(systemId));
                });
            var (front, open) = await OpenFrontAsync();

            var localStructures = Assert.IsType<PyList>(
                await HostRpcAsync(front, open, 1, "sovMgr", "GetSovStructuresInfoForLocalSolarSystem"));
            var structure = Assert.IsType<PyObject>(Assert.Single(localStructures.Items));
            Assert.Equal(new PyInt(hubId), WireField(structure, "itemID"));
            Assert.Equal(new PyInt(32226), WireField(structure, "typeID"));
            Assert.Equal(new PyInt(systemId), WireField(structure, "solarSystemID"));

            var remoteStructures = Assert.IsType<PyList>(
                await HostRpcAsync(front, open, 2, "sovMgr", "GetSovStructuresInfoForSolarSystem", new PyLong(systemId)));
            Assert.Single(remoteStructures.Items);

            var claim = Assert.IsType<PyObjectEx>(
                await HostRpcAsync(front, open, 3, "sovMgr", "GetSystemSovereigntyInfo", new PyLong(systemId)));
            Assert.Equal(new PyToken("sovereignty.data_types.SovClaimInfo"),
                Assert.IsType<PyTuple>(claim.Header).Items[0]);

            var hubInfo = Assert.IsType<PyObjectEx>(
                await HostRpcAsync(front, open, 4, "sovMgr", "GetInfrastructureHubInfo", new PyLong(systemId)));
            Assert.Equal(new PyToken("sovereignty.data_types.SovHubInfo"),
                Assert.IsType<PyTuple>(hubInfo.Header).Items[0]);

            Assert.Equal(new PyInt(initialGroupId),
                await HostRpcAsync(front, open, 5, "sovMgr", "GetSovHubFuelAccessGroup", new PyLong(systemId)));
            Assert.Same(PyBool.True,
                await HostRpcAsync(front, open, 6, "sovMgr", "IsOnLocalSovHubFuelAccessGroup"));
            Assert.Same(PyNone.Instance,
                await HostRpcAsync(front, open, 7, "sovMgr", "SetSovHubFuelAccessGroup", new PyLong(systemId), new PyLong(nextGroupId)));
            Assert.Equal(new PyInt(nextGroupId),
                await HostRpcAsync(front, open, 8, "sovMgr", "GetSovHubFuelAccessGroup", new PyLong(systemId)));
            Assert.Same(PyNone.Instance,
                await HostRpcAsync(front, open, 9, "sovMgr", "SetSovHubFuelAccessGroup", new PyLong(systemId), PyNone.Instance));
            Assert.Same(PyNone.Instance,
                await HostRpcAsync(front, open, 10, "sovMgr", "GetSovHubFuelAccessGroup", new PyLong(systemId)));
            Assert.Same(PyNone.Instance,
                await HostRpcAsync(front, open, 11, "sovMgr", "SetSovHubFuelAccessGroup", new PyLong(systemId), new PyLong(initialGroupId)));

            Assert.Equal(new PyList([new PyInt(destroyedSkyhookId)]),
                await HostRpcAsync(front, open, 12, "sovMgr", "DestroySkyhooks", new PyList([new PyLong(destroyedSkyhookId)])));
            Assert.Equal(new PyList([new PyInt(acquiredSkyhookId)]),
                await HostRpcAsync(front, open, 13, "sovMgr", "AcquireSkyhooks",
                    new PyList([new PyLong(acquiredSkyhookId)]), new PyLong(initialGroupId)));
            Assert.Equal(new PyList([new PyInt(acquiredSkyhookId)]),
                await HostRpcAsync(front, open, 14, "sovMgr", "AcquireSkyhooks",
                    new PyList([new PyLong(acquiredSkyhookId)]), new PyLong(initialGroupId)));

            var persisted = await store.LoadTableAsync("sovereignty", createIfMissing: false);
            Assert.True(persisted["systems"]![systemId.ToString(System.Globalization.CultureInfo.InvariantCulture)]!["fuelAccessGroupID"]!.GetValue<long>() == initialGroupId);
            Assert.True(persisted["skyhooks"]![destroyedSkyhookId.ToString(System.Globalization.CultureInfo.InvariantCulture)]!["destroyed"]!.GetValue<bool>());
            Assert.False(persisted["skyhooks"]![destroyedSkyhookId.ToString(System.Globalization.CultureInfo.InvariantCulture)]!["productionActive"]!.GetValue<bool>());
            Assert.Equal(corporationId, persisted["skyhooks"]![acquiredSkyhookId.ToString(System.Globalization.CultureInfo.InvariantCulture)]!["corporationID"]!.GetValue<long>());
            Assert.Equal(initialGroupId, persisted["skyhooks"]![acquiredSkyhookId.ToString(System.Globalization.CultureInfo.InvariantCulture)]!["accessListID"]!.GetValue<long>());
            Assert.Equal("completed", Assert.Single(persisted["skyhookAcquisitionSettlements"]!.AsObject().Select(entry => entry.Value!.AsObject()))["state"]!.GetValue<string>());
            Assert.Equal(9_998_750m, (await wallets.GetAsync(corporationId))!.Isk);
            Assert.Equal(-1_250m, await wallets.LastIskChangeAsync(corporationId));
            Assert.Same(PyNone.Instance,
                await HostRpcAsync(front, open, 15, "sovMgr", "GetSystemSovereigntyInfo", new PyLong(systemId + 1)));
            Assert.Same(PyNone.Instance,
                await HostRpcAsync(front, open, 16, "sovMgr", "GetInfrastructureHubInfo", new PyLong(systemId + 1)));
            Assert.Equal(new PyList([]),
                await HostRpcAsync(front, open, 17, "sovMgr", "GetSovStructuresInfoForSolarSystem", new PyLong(systemId + 1)));
            Assert.Same(PyNone.Instance,
                await HostRpcAsync(front, open, 18, "sovMgr", "GetSovHubFuelAccessGroup", new PyLong(systemId + 1)));
        }
        finally
        {
            if (cache is not null) await cache.DisposeAsync();
            if (owner is not null) await owner.DisposeAsync();
            await store.DisposeAsync();
            await using var connection = await source.OpenConnectionAsync();
            await using var command = new NpgsqlCommand(
                $"DROP SCHEMA IF EXISTS \"{storeSchema}\" CASCADE; DROP SCHEMA IF EXISTS \"{walletSchema}\" CASCADE",
                connection);
            await command.ExecuteNonQueryAsync();
        }
    }

    private static JsonObject AccessGroup(long memberId) => new()
    {
        ["creatorID"] = memberId,
        ["members"] = new JsonArray
        {
            new JsonObject { ["memberID"] = memberId, ["membershipType"] = 0 },
        },
    };
}
