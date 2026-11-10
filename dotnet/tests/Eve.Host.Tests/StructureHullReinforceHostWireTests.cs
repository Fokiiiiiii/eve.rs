using Eve.Data;
using Eve.Dogma;
using Eve.Events;
using Eve.Fitting;
using Eve.Host.Persistence;
using Eve.Inventory;
using Eve.Macho;
using Eve.Marshal;
using Eve.Scheduler;
using Eve.Sde;
using Eve.Services;
using Eve.Services.Ships;
using Eve.Ships;
using Eve.Structures;
using Npgsql;
using Xunit;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    private const long ReinforcePilot = 98_710_061;
    private const long ReinforceCorporation = 98_710_062;
    private const long ReinforceStructure = 9_871_006_201;
    private const int ReinforceSolarSystem = 30_000_142;
    private const int Fortizar = 35_833;
    private const int HullMarketHubModule = 35_892;

    private sealed class PersistedServiceModuleStates(PgModuleStateStore modules) : IStructureServiceModuleStates
    {
        public Task SetAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long moduleId, bool online,
            CancellationToken cancellationToken) =>
            modules.SetAsync(connection, transaction, moduleId, online ? ItemState.Online : ItemState.Offline, cancellationToken);
    }

    [StructureControlPgFact]
    public async Task Hull_reinforce_offlines_a_fitted_service_module_and_notifies_the_controller_after_commit()
    {
        var url = Environment.GetEnvironmentVariable("EVE_PG_URL") ?? throw new InvalidOperationException("EVE_PG_URL is required");
        await using var source = NpgsqlDataSource.Create(PgConnection.ToConnectionString(url));
        var prefix = "hull_offline_" + Guid.NewGuid().ToString("N")[..12];
        var itemSchema = prefix + "i";
        var fittingSchema = prefix + "f";
        var structureSchema = prefix + "r";
        var schedulerSchema = prefix + "q";
        var eventSchema = prefix + "e";
        var schemas = new[] { itemSchema, fittingSchema, structureSchema, schedulerSchema, eventSchema };
        try
        {
            await PgItemStore.MigrateAsync(source, itemSchema);
            await PgModuleStateStore.MigrateAsync(source, fittingSchema);
            await StructureStore.MigrateAsync(source, structureSchema);
            await DurableScheduler.MigrateAsync(source, schedulerSchema);
            await EventBus.MigrateAsync(source, eventSchema);

            var sde = new SdeReader(source);
            Assert.Equal(3_396_210L, await sde.BuildAsync());
            var rules = await StructureRulesSource.LoadAsync(sde);
            var items = new PgItemStore(source, itemSchema);
            var modules = new PgModuleStateStore(source, fittingSchema);
            var structures = new StructureStore(source, structureSchema);
            var nowMs = FixedNowMs;
            var scheduler = new DurableScheduler(source, () => nowMs, new SchedulerOptions { Concurrency = 1 }, schedulerSchema, _ => { });
            var events = new EventBus(source, scheduler, new EventBusOptions { MaxAttempts = 3 }, eventSchema, _ => { });
            var moduleStates = new PersistedServiceModuleStates(modules);
            var timers = new StructureTimers(structures, scheduler, events, rules, moduleStates: moduleStates);
            timers.Register();
            var sessions = new CharacterSessions();
            StructureNotifications.Subscribe(events, structures, sessions,
                (_, _) => ValueTask.FromResult<PyValue?>(null));
            await events.PumpAsync();

            long moduleId;
            await using (var connection = await source.OpenConnectionAsync())
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                Assert.True(await structures.CreateAsync(connection, transaction,
                    new NewStructure(ReinforceStructure, Fortizar, ReinforceSolarSystem, 1, 2, 3, 0,
                        ReinforceCorporation, 0, "Hull Reinforce Test Fortizar", "")));
                await structures.SaveAsync(connection, transaction, new StructureSnapshot
                {
                    StructureId = ReinforceStructure,
                    State = StructureState.ArmorVulnerable,
                    StateStartedMs = nowMs,
                    Upkeep = UpkeepState.FullPower,
                });
                moduleId = await items.CreateAsync(connection, transaction,
                    new NewItem(MarketHubModule, ReinforceCorporation, ReinforceStructure,
                        StructureServiceIds.ServiceSlotFirst, 1, true));
                await modules.SetAsync(connection, transaction, moduleId, ItemState.Online);
                await structures.SetServiceModuleFuelCycleAsync(connection, transaction, ReinforceStructure, moduleId,
                        HullMarketHubModule, [StructureServiceIds.Market], nowMs, nowMs + StructureFuel.HourMs, 0);
                await transaction.CommitAsync();
            }

            Start(sessionOpened: session =>
            {
                session.CharacterId = ReinforcePilot;
                session.Set("corpid", new PyLong(ReinforceCorporation));
                session.Set("shipid", new PyLong(ReinforceStructure));
                session.Set("structureid", new PyLong(ReinforceStructure));
                sessions.Enter(ReinforcePilot, session);
            });
            var front = await Handshaken(await ConnectAsync());
            var open = Open();
            await front.SendAsync(open);
            _ = await front.ReceiveAsync();

            await using (var connection = await source.OpenConnectionAsync())
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                var transition = await timers.ApplyAsync(connection, transaction, ReinforceStructure,
                    new Damaged(StructureLayer.Armor, Depleted: true, PausesRepair: false));
                Assert.NotNull(transition);
                Assert.Equal(StructureState.HullReinforce, transition.Next.State);
                await transaction.CommitAsync();
            }
            await events.PumpAsync();

            await using (var connection = await source.OpenConnectionAsync())
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                var persistedStructure = Assert.IsType<StructureRecord>(
                    await new StructureStore(source, structureSchema).GetAsync(connection, transaction, ReinforceStructure, false));
                Assert.Equal(StructureState.HullReinforce, persistedStructure.Snapshot.State);
                Assert.Equal(ItemState.Offline, await new PgModuleStateStore(source, fittingSchema).StateAsync(connection, transaction, moduleId));
                Assert.Null(await new StructureStore(source, structureSchema)
                    .ServiceModuleFuelCycleAsync(connection, transaction, ReinforceStructure, moduleId));
                await transaction.CommitAsync();
            }

            var notices = new List<(string Name, string IdType, PyTuple Args)>();
            for (var i = 0; i < 4; i++)
            {
                var packet = Assert.IsType<Send>(await front.ReceiveAsync());
                Assert.Equal(MachoMessage.Notification, Envelope(packet).Type);
                notices.Add(DecodeClientNotification(packet));
            }
            Assert.Equal(
                ["OnStructureStateChanged", "OnCurrentStructureStateChanged", "OnCorporationStructuresUpdated", "OnMultiEvent"],
                notices.Select(notice => notice.Name));
            Assert.Equal("clientID", notices[0].IdType);
            Assert.Equal("shipid", notices[1].IdType);
            Assert.Equal("clientID", notices[3].IdType);
            var dogmaEvents = Assert.IsType<PyList>(notices[3].Args.Items[0]).Items;
            Assert.Equal(new[] { "OnModuleAttributeChange", "OnGodmaShipEffect" }, dogmaEvents.Select(value =>
            {
                var eventAndTime = Assert.IsType<PyTuple>(value);
                var dogmaEvent = Assert.IsType<PyTuple>(eventAndTime.Items[0]);
                return JsSemantics.StringValue(dogmaEvent.Items[0]);
            }));
            var attributeEvent = Assert.IsType<PyTuple>(Assert.IsType<PyTuple>(dogmaEvents[0]).Items[0]);
            Assert.Equal(new PyInt(checked((int)moduleId)), attributeEvent.Items[2]);
            Assert.Equal(new PyInt(BuiltInEffects.IsOnline), attributeEvent.Items[3]);
            Assert.Equal(new PyInt(0), attributeEvent.Items[5]);
            Assert.Equal(new PyInt(1), attributeEvent.Items[6]);
            var effectEvent = Assert.IsType<PyTuple>(Assert.IsType<PyTuple>(dogmaEvents[1]).Items[0]);
            Assert.Equal(new PyInt(checked((int)moduleId)), effectEvent.Items[1]);
            Assert.Equal(new PyInt(BuiltInEffects.Online), effectEvent.Items[2]);
            Assert.Equal(new PyInt(0), effectEvent.Items[4]);
        }
        finally
        {
            await using var connection = await source.OpenConnectionAsync();
            foreach (var schema in schemas)
                await using (var command = new NpgsqlCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE", connection))
                    await command.ExecuteNonQueryAsync();
        }
    }
}
