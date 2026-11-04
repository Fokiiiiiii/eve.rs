using Eve.Data;
using Eve.Inventory;
using Eve.Planets;
using Eve.Ships;
using Eve.Structures;
using Npgsql;
using Xunit;

namespace Eve.Host.Tests;

public sealed class WarEligibilityOwnerSourcesTests
{
    [PgFact]
    public async Task Sovereignty_cache_flush_and_journal_recovery_publish_owner_changes_once()
    {
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(
            Environment.GetEnvironmentVariable("EVE_PG_URL") ?? throw new InvalidOperationException("EVE_PG_URL is required")));
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var schedulerSchema = "wc_sched_" + suffix;
        var eventSchema = "wc_event_" + suffix;
        var gameSchema = "wc_game_" + suffix;
        try
        {
            await Eve.Scheduler.DurableScheduler.MigrateAsync(source, schedulerSchema);
            await Eve.Events.EventBus.MigrateAsync(source, eventSchema);
            var scheduler = new Eve.Scheduler.DurableScheduler(source, () => 1000,
                new Eve.Scheduler.SchedulerOptions { Concurrency = 1 }, schedulerSchema, _ => { });
            var bus = new Eve.Events.EventBus(source, scheduler,
                new Eve.Events.EventBusOptions { MaxAttempts = 3 }, eventSchema, _ => { });
            var seen = new List<WarSovereigntyEvents.OwnersChanged>();
            bus.Subscribe("test.cache.owners", [WarSovereigntyEvents.Topic], (context, _) =>
            {
                var change = context.Event.Payload<WarSovereigntyEvents.OwnersChanged>()!;
                context.AfterCommit(_ => { seen.Add(change); return Task.CompletedTask; });
                return Task.CompletedTask;
            });
            await bus.PumpAsync();
            await using var store = new Eve.GameStore.PersistenceStore(source, gameSchema);
            store.BeforeApplyOperationAsync = (connection, transaction, operation, token) =>
                WarSovereigntyEvents.BeforeApplyAsync(bus, gameSchema, connection, transaction, operation, token);
            await store.EnsureSchemaAsync();
            await using var owner = await Eve.GameStore.OwnerSession.StartAsync(store, Eve.GameStore.PersistenceRoles.World, "war-cache-test");
            await using var cache = new Eve.GameStore.GameStoreCache(store, owner,
                new Eve.GameStore.GameStoreCacheOptions { ProcessRole = Eve.GameStore.PersistenceRoles.World, FlushDelay = TimeSpan.FromHours(1) });
            await cache.RecoverAsync();
            await cache.LoadAsync("sovereignty");
            var initial = System.Text.Json.Nodes.JsonNode.Parse("""
                {"systems":{"30000142":{"corporationID":100,"allianceID":500,"claimStructureID":20}},
                 "skyhooks":{"21":{"typeID":81080,"solarSystemID":30000142,"planetID":40000001,"corporationID":100}}}
                """)!.AsObject();
            Assert.True(cache.Write("sovereignty", "/", initial).Success);
            await cache.FlushTableAsync("sovereignty");
            await bus.PumpAsync();
            var first = Assert.Single(seen);
            Assert.Contains(100L, first.Corporations);
            Assert.Contains(500L, first.Alliances);
            Assert.True(cache.Write("sovereignty", "/skyhooks/21/corporationID", System.Text.Json.Nodes.JsonValue.Create(200)).Success);
            await cache.FlushTableAsync("sovereignty");
            await bus.PumpAsync();
            Assert.Equal(2, seen.Count);
            Assert.Equal(new long[] {100,200}, seen[1].Corporations.Order());
            var recoveryRoot = System.Text.Json.Nodes.JsonNode.Parse("""
                {"skyhooks":{"21":{"typeID":81080,"solarSystemID":30000142,"planetID":40000001,"corporationID":300}}}
                """)!.AsObject();
            var rows = Eve.GameStore.RowCodec.Explode("sovereignty", recoveryRoot);
            var entityKey = rows.Keys.Single(key => key.StartsWith("skyhooks" + Eve.GameStore.GameStoreTables.RowKeySeparator, StringComparison.Ordinal));
            await store.EnqueueAsync("sovereignty", [new Eve.GameStore.RowUpsert(entityKey, rows[entityKey]!.ToJsonString())], [], owner.Fence);
            Assert.Equal(1, await cache.RecoverAsync());
            Assert.Equal(0, await cache.RecoverAsync());
            await bus.PumpAsync();
            Assert.Equal(3, seen.Count);
            Assert.Equal(new long[] {200,300}, seen[2].Corporations.Order());
        }
        finally
        {
            await using var connection = await source.OpenConnectionAsync();
            foreach (var schema in new[] { schedulerSchema, eventSchema, gameSchema })
            {
                await using var command = new NpgsqlCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE", connection);
                await command.ExecuteNonQueryAsync();
            }
        }
    }

    [PgFact]
    public async Task Grouped_sovereignty_rows_announce_old_and_new_owners_on_transfer_and_delete()
    {
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(
            Environment.GetEnvironmentVariable("EVE_PG_URL") ?? throw new InvalidOperationException("EVE_PG_URL is required")));
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var schedulerSchema = "ws_sched_" + suffix;
        var eventSchema = "ws_event_" + suffix;
        var gameSchema = "ws_game_" + suffix;
        try
        {
            await Eve.Scheduler.DurableScheduler.MigrateAsync(source, schedulerSchema);
            await Eve.Events.EventBus.MigrateAsync(source, eventSchema);
            await using (var connection = await source.OpenConnectionAsync())
            await using (var command = new NpgsqlCommand($"CREATE SCHEMA {gameSchema}; CREATE TABLE {gameSchema}.sovereignty(key text PRIMARY KEY, doc jsonb NOT NULL)", connection))
                await command.ExecuteNonQueryAsync();
            var scheduler = new Eve.Scheduler.DurableScheduler(source, () => 1000,
                new Eve.Scheduler.SchedulerOptions { Concurrency = 1 }, schedulerSchema, _ => { });
            var bus = new Eve.Events.EventBus(source, scheduler,
                new Eve.Events.EventBusOptions { MaxAttempts = 3 }, eventSchema, _ => { });
            var seen = new List<WarSovereigntyEvents.OwnersChanged>();
            bus.Subscribe("test.sovereignty", [WarSovereigntyEvents.Topic], (context, _) =>
            {
                var change = context.Event.Payload<WarSovereigntyEvents.OwnersChanged>()!;
                context.AfterCommit(_ => { seen.Add(change); return Task.CompletedTask; });
                return Task.CompletedTask;
            });
            await bus.PumpAsync();
            var before = System.Text.Json.Nodes.JsonNode.Parse("""
                {"skyhooks":{"20":{"typeID":81080,"solarSystemID":30000142,"planetID":40000001,"corporationID":100}}}
                """)!.AsObject();
            var after = System.Text.Json.Nodes.JsonNode.Parse("""
                {"skyhooks":{"20":{"typeID":81080,"solarSystemID":30000142,"planetID":40000001,"corporationID":200}}}
                """)!.AsObject();
            var oldRows = Eve.GameStore.RowCodec.Explode("sovereignty", before);
            var newRows = Eve.GameStore.RowCodec.Explode("sovereignty", after);
            var entityKey = oldRows.Keys.Single(key => key.StartsWith("skyhooks" + Eve.GameStore.GameStoreTables.RowKeySeparator, StringComparison.Ordinal));
            await using (var connection = await source.OpenConnectionAsync())
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                await using var command = new NpgsqlCommand($"INSERT INTO {gameSchema}.sovereignty(key,doc) VALUES ($1,$2::jsonb)", connection, transaction);
                command.Parameters.AddWithValue(entityKey);
                command.Parameters.AddWithValue(oldRows[entityKey]!.ToJsonString());
                await command.ExecuteNonQueryAsync();
                await transaction.CommitAsync();
            }
            Eve.GameStore.PersistenceOperation Operation(long id, Eve.GameStore.RowUpsert[] upserts, string[] deletes) =>
                new(id,"sovereignty","world",1,"test",upserts,deletes,Eve.GameStore.OperationState.Pending,DateTimeOffset.UtcNow,null);
            await using (var connection = await source.OpenConnectionAsync())
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                await WarSovereigntyEvents.BeforeApplyAsync(bus, gameSchema, connection, transaction,
                    Operation(1,[new(entityKey,newRows[entityKey]!.ToJsonString())],[]), CancellationToken.None);
                await using var command = new NpgsqlCommand($"UPDATE {gameSchema}.sovereignty SET doc=$2::jsonb WHERE key=$1", connection, transaction);
                command.Parameters.AddWithValue(entityKey);
                command.Parameters.AddWithValue(newRows[entityKey]!.ToJsonString());
                await command.ExecuteNonQueryAsync();
                await transaction.CommitAsync();
            }
            await bus.PumpAsync();
            Assert.Equal(new long[] { 100,200 }, Assert.Single(seen).Corporations.Order());
            await using (var connection = await source.OpenConnectionAsync())
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                await WarSovereigntyEvents.BeforeApplyAsync(bus, gameSchema, connection, transaction,
                    Operation(2,[],[entityKey]), CancellationToken.None);
                await transaction.CommitAsync();
            }
            await bus.PumpAsync();
            Assert.Equal(2, seen.Count);
            Assert.Equal(new long[] { 200 }, seen[1].Corporations);
        }
        finally
        {
            await using var connection = await source.OpenConnectionAsync();
            foreach (var schema in new[] { schedulerSchema, eventSchema, gameSchema })
            {
                await using var command = new NpgsqlCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE", connection);
                await command.ExecuteNonQueryAsync();
            }
        }
    }

    [PgFact]
    public async Task Office_owner_events_commit_once_and_rollback_with_the_item_mutation()
    {
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(
            Environment.GetEnvironmentVariable("EVE_PG_URL") ?? throw new InvalidOperationException("EVE_PG_URL is required")));
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var schedulerSchema = "wo_sched_" + suffix;
        var eventSchema = "wo_event_" + suffix;
        var inventorySchema = "wo_inv_" + suffix;
        try
        {
            await Eve.Scheduler.DurableScheduler.MigrateAsync(source, schedulerSchema);
            await Eve.Events.EventBus.MigrateAsync(source, eventSchema);
            await PgItemStore.MigrateAsync(source, inventorySchema);
            var scheduler = new Eve.Scheduler.DurableScheduler(source, () => 1000,
                new Eve.Scheduler.SchedulerOptions { Concurrency = 1 }, schedulerSchema, _ => { });
            var bus = new Eve.Events.EventBus(source, scheduler,
                new Eve.Events.EventBusOptions { MaxAttempts = 3 }, eventSchema, _ => { });
            var seen = new List<PgItemStore.ItemOwnershipChanged>();
            bus.Subscribe("test.ownership", [PgItemStore.ItemOwnershipChangedTopic], (context, _) =>
            {
                var change = context.Event.Payload<PgItemStore.ItemOwnershipChanged>()!;
                context.AfterCommit(_ => { seen.Add(change); return Task.CompletedTask; });
                return Task.CompletedTask;
            });
            var items = new PgItemStore(source, inventorySchema) { Events = bus };
            await bus.PumpAsync();
            long id;
            await using (var connection = await source.OpenConnectionAsync())
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                id = await items.CreateAsync(connection, transaction, new NewItem(Customs.OfficeTypeId,100,30000142,0,1,true));
                await transaction.CommitAsync();
            }
            await using (var connection = await source.OpenConnectionAsync())
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                await items.MoveToOwnerAsync(connection, transaction, id, 200, 30000142, 0);
                await transaction.RollbackAsync();
            }
            await bus.PumpAsync();
            Assert.Empty(seen);
            await using (var connection = await source.OpenConnectionAsync())
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                await items.MoveToOwnerAsync(connection, transaction, id, 200, 30000142, 0);
                await items.MoveToOwnerAsync(connection, transaction, id, 200, 30000142, 0);
                await transaction.CommitAsync();
            }
            await bus.PumpAsync();
            Assert.Equal(new PgItemStore.ItemOwnershipChanged(id, Customs.OfficeTypeId,100,200), Assert.Single(seen));
            await using (var connection = await source.OpenConnectionAsync())
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                await items.DeleteAsync(connection, transaction, id);
                await items.DeleteAsync(connection, transaction, id);
                await transaction.CommitAsync();
            }
            await bus.PumpAsync();
            Assert.Equal(2, seen.Count);
            Assert.Equal(new PgItemStore.ItemOwnershipChanged(id, Customs.OfficeTypeId,200,0), seen[1]);
        }
        finally
        {
            await using var connection = await source.OpenConnectionAsync();
            foreach (var schema in new[] { schedulerSchema, eventSchema, inventorySchema })
            {
                await using var command = new NpgsqlCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE", connection);
                await command.ExecuteNonQueryAsync();
            }
        }
    }

    [PgFact]
    public async Task Committed_structure_and_alliance_events_trigger_refresh_but_rolled_back_events_do_not()
    {
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(
            Environment.GetEnvironmentVariable("EVE_PG_URL") ?? throw new InvalidOperationException("EVE_PG_URL is required")));
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var schedulerSchema = "we_sched_" + suffix;
        var eventSchema = "we_event_" + suffix;
        var structureSchema = "we_struct_" + suffix;
        try
        {
            await Eve.Scheduler.DurableScheduler.MigrateAsync(source, schedulerSchema);
            await Eve.Events.EventBus.MigrateAsync(source, eventSchema);
            await StructureStore.MigrateAsync(source, structureSchema);
            var clock = 1000L;
            var scheduler = new Eve.Scheduler.DurableScheduler(source, () => clock,
                new Eve.Scheduler.SchedulerOptions { Concurrency = 1 }, schedulerSchema, _ => { });
            var events = new Eve.Events.EventBus(source, scheduler,
                new Eve.Events.EventBusOptions { MaxAttempts = 3 }, eventSchema, _ => { });
            var store = new StructureStore(source, structureSchema);
            var seen = new List<(long Corporation, long Alliance)>();
            var failStructureOnce = true;
            CharacterServicesComposition.SubscribeWarEligibilityChanges(events, store, (corp, alliance, _) =>
            {
                if (corp == 100 && failStructureOnce)
                {
                    failStructureOnce = false;
                    throw new InvalidOperationException("temporary eligibility read failure");
                }
                return Task.FromResult<Action>(() => seen.Add((corp, alliance)));
            });
            await events.PumpAsync();
            await using (var connection = await source.OpenConnectionAsync())
            {
                await using var command = new NpgsqlCommand($"INSERT INTO {structureSchema}.structures(structure_id,type_id,solar_system_id,owner_corporation_id,state,destroyed_ms) VALUES (1,35832,30000142,100,110,0)", connection);
                await command.ExecuteNonQueryAsync();
            }
            var state = new StructureStateChanged(1,30000142,110,0,new StructureTimerInfo(0,0,0),false,null);
            await using (var connection = await source.OpenConnectionAsync())
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                await events.PublishInTransactionAsync(connection, transaction, StructureTimers.StateChangedTopic, state, "rollback");
                await transaction.RollbackAsync();
            }
            await events.PumpAsync();
            Assert.Empty(seen);
            await using (var connection = await source.OpenConnectionAsync())
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                await events.PublishInTransactionAsync(connection, transaction, StructureTimers.StateChangedTopic, state, "structure");
                await events.PublishInTransactionAsync(connection, transaction, "relation.affiliation.alliance.ended",
                    new Eve.Relations.RelationChange(1,"alliance",200,500,null,null,null,1000), "alliance");
                await events.PublishInTransactionAsync(connection, transaction, PgItemStore.ItemOwnershipChangedTopic,
                    new PgItemStore.ItemOwnershipChanged(2,Customs.OfficeTypeId,300,400), "office");
                await events.PublishInTransactionAsync(connection, transaction, PgItemStore.ItemOwnershipChangedTopic,
                    new PgItemStore.ItemOwnershipChanged(3,1,600,700), "other-item");
                await transaction.CommitAsync();
            }
            Assert.Empty(seen);
            await events.PumpAsync();
            Assert.DoesNotContain((100L,0L), seen);
            clock += 10000;
            await events.PumpAsync();
            Assert.Contains((100L,0L), seen);
            Assert.Contains((200L,500L), seen);
            Assert.Contains((300L,0L), seen);
            Assert.Contains((400L,0L), seen);
            Assert.Equal(4, seen.Count);
        }
        finally
        {
            await using var connection = await source.OpenConnectionAsync();
            foreach (var schema in new[] { schedulerSchema, eventSchema, structureSchema })
            {
                await using var command = new NpgsqlCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE", connection);
                await command.ExecuteNonQueryAsync();
            }
        }
    }

    [Fact]
    public void Live_sovereignty_owners_require_a_claim_hub_or_valid_undestroyed_skyhook()
    {
        var root = System.Text.Json.Nodes.JsonNode.Parse("""
            {"systems":{"1":{"corporationID":100,"allianceID":500,"claimStructureID":10},
                        "2":{"corporationID":101,"allianceID":501,"infrastructureHubID":11},
                        "3":{"corporationID":102,"allianceID":502}},
             "skyhooks":{"20":{"typeID":81080,"solarSystemID":30000142,"planetID":40000001,"corporationID":103},
                         "21":{"typeID":81080,"solarSystemID":30000142,"planetID":40000002,"corporationID":104,"destroyed":true},
                         "22":{"typeID":1,"solarSystemID":30000142,"planetID":40000003,"corporationID":105}}}
            """)!.AsObject();
        var (corporations, alliances) = CharacterServicesComposition.WarSovereigntyOwners(root);
        Assert.Equal(new long[] { 100,101,103 },corporations.Order());
        Assert.Equal(new long[] { 500,501 },alliances.Order());
    }

    [PgFact]
    public async Task Indexed_owner_reads_exclude_other_owners_inactive_and_removed_objects()
    {
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(
            Environment.GetEnvironmentVariable("EVE_PG_URL") ?? throw new InvalidOperationException("EVE_PG_URL is required")));
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var structures = "we_struct_" + suffix;
        var space = "we_space_" + suffix;
        var planets = "we_planet_" + suffix;
        var inventory = "we_inv_" + suffix;
        try
        {
            await StructureStore.MigrateAsync(source, structures);
            await PgSpaceItemStore.MigrateAsync(source, space);
            await PlanetStore.MigrateAsync(source, planets);
            await PgItemStore.MigrateAsync(source, inventory);
            var structureStore = new StructureStore(source, structures);
            var deployedStore = new PgDeployedStore(source, space);
            var planetStore = new PlanetStore(source, planets);
            var itemStore = new PgItemStore(source, inventory);
            await using var c = await source.OpenConnectionAsync();
            await using var tx = await c.BeginTransactionAsync();
            async Task Execute(string sql)
            {
                await using var command = new NpgsqlCommand(sql, c, tx);
                await command.ExecuteNonQueryAsync();
            }
            await Execute($"INSERT INTO {structures}.structures(structure_id,type_id,solar_system_id,owner_corporation_id,state,destroyed_ms) VALUES (1,35832,30000142,100,110,0), (2,35832,30000142,200,110,0), (3,35832,30000142,300,110,1), (4,35832,30000142,400,1,0)");
            Assert.True(await structureStore.HasEligibleStructureAsync(c,tx,[100],[35832]));
            Assert.False(await structureStore.HasEligibleStructureAsync(c,tx,[200],[35833]));
            Assert.False(await structureStore.HasEligibleStructureAsync(c,tx,[300,400],[35832]));
            Assert.False(await structureStore.HasEligibleStructureAsync(c,tx,[999],[35832]));

            await Execute($"INSERT INTO {space}.deployed(deployable_id,kind,solar_system_id,name,type_id,group_id,x,y,z,radius,corporation_id,expires_at_ms,activate_at_ms) VALUES (10,'control_tower',30000142,'Tower',16213,365,0,0,0,1,100,9223372036854775807,1000), (11,'control_tower',30000142,'Tower',16213,365,0,0,0,1,200,9223372036854775807,2000)");
            Assert.True(await deployedStore.HasAnchoredControlTowerAsync(c,tx,[100],1000));
            Assert.False(await deployedStore.HasAnchoredControlTowerAsync(c,tx,[200],1000));
            Assert.False(await deployedStore.HasAnchoredControlTowerAsync(c,tx,[999],3000));
            await Execute($"DELETE FROM {space}.deployed WHERE deployable_id=10");
            Assert.False(await deployedStore.HasAnchoredControlTowerAsync(c,tx,[100],1000));

            var officeId = await itemStore.CreateAsync(c,tx,new NewItem(Customs.OfficeTypeId,100,30000142,0,1,true));
            await Execute($"INSERT INTO {planets}.customs_offices(office_id,owner_corporation_id) VALUES ({officeId},100)");
            Assert.True(await planetStore.HasOwnedCustomsOfficeAsync(c,tx,inventory,[100]));
            Assert.False(await planetStore.HasOwnedCustomsOfficeAsync(c,tx,inventory,[200]));
            await Execute($"UPDATE {inventory}.items SET owner_id=200 WHERE id={officeId}");
            Assert.False(await planetStore.HasOwnedCustomsOfficeAsync(c,tx,inventory,[100]));
            Assert.True(await planetStore.HasOwnedCustomsOfficeAsync(c,tx,inventory,[200]));
            await itemStore.DeleteAsync(c,tx,officeId);
            Assert.False(await planetStore.HasOwnedCustomsOfficeAsync(c,tx,inventory,[200]));
            await tx.RollbackAsync();
        }
        finally
        {
            await using var c = await source.OpenConnectionAsync();
            foreach (var schema in new[] { structures, space, planets, inventory })
            {
                await using var command = new NpgsqlCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE", c);
                await command.ExecuteNonQueryAsync();
            }
        }
    }
}
