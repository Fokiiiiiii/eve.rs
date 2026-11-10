using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Ships;
using Eve.Space;
using Eve.Structures;
using Npgsql;

namespace Eve.Services.Space.Tests;

/// <summary>
/// It is anchored when its anchoring time is over (the SDE's: 30 minutes for the Amarr Control Tower), never ends by time, and only its launching corporation
/// </summary>
public class ControlTowerHostTests
{
    /// <summary>The Amarr Control Tower (SDE group 365, radius 3394 m, anchoring delay 1 800 000 ms).</summary>
    private const int ControlTower = 12235, Observatory = 58904, Anchoring = 11584, AnchoringLevel = 3;
    /// <summary>Asabona (Derelik, security 0.32): below the cutoff, so an observatory may be dropped there.</summary>
    private const long LowSecSystem = 30000012;
    private const long TestCorporation = 98000001;
    private const long AnchoringMs = 1_800_000;
    private const long OnliningMs = 1_800_000;
    private const long HourMs = 3_600_000;

    /// <summary>The fuel blocks of the Amarr tower's race (Helium) and of the Caldari (Nitrogen), SDE 4247 and 4051.</summary>
    private const int HeliumFuelBlock = 4247, NitrogenFuelBlock = 4051;

    /// <summary>Puts fuel blocks in a tower's fuel bay: its container, the items at its id.</summary>
    private static async Task FuelAsync(Rig rig, long tower, int fuelType, long quantity, long owner)
    {
        await using var connection = await rig.Source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await rig.Items.CreateAsync(connection, transaction, new Eve.Inventory.NewItem(fuelType, owner, tower, 0, quantity));
        await transaction.CommitAsync();
    }

    /// <summary>The quantity of one fuel block type in a tower's fuel bay.</summary>
    private static async Task<long> BayAsync(Rig rig, long tower, int fuelType) =>
        (await rig.Items.InContainerAsync(tower)).Where(stack => stack.TypeId == fuelType).Sum(stack => stack.Quantity);

    /// <summary>A pilot with the given types in its hold, in space in <paramref name="system"/>, with Anchoring III.</summary>
    private static async Task<(ClientSession Session, long[] Items)> PilotWithItemsAsync(Rig rig, int[] typeIds, long system)
    {
        var character = await rig.NewCharacterAsync();
        await using (var connection = await rig.Source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await rig.Skills.GrantAsync(connection, transaction, character.CharacterId, [new Eve.Skills.CharacterSkill(Anchoring, AnchoringLevel, 0)]);
            await transaction.CommitAsync();
        }
        var items = new long[typeIds.Length];
        for (var index = 0; index < typeIds.Length; index++)
        {
            var granted = (await rig.Ships.GrantAsync(character.CharacterId, typeIds[index], 1, singleton: true))!.Single().After;
            var moved = await rig.Ships.MoveAsync(character.CharacterId, granted.Id, Rig.Station, character.ShipId, CharacterShips.CargoFlag, 1);
            Assert.True(moved is Eve.Ships.ShipChanged, moved.ToString());
            items[index] = granted.Id;
        }
        await PlaceCharacterAtSystemStationAsync(rig, character, (int)system);

        var session = await rig.SessionAsync(character, 1);
        var oid = await rig.BindShipAsync(session);
        await rig.ShipCallAsync(session, "Undock", oid, new PyLong(session.GetInt("shipid")!.Value), PyBool.False);
        await rig.BindBeyonceAsync(session);
        Events.Of(session);
        session.Set("solarsystemid2", new PyInt(system));
        session.Set("corpid", new PyLong(TestCorporation));
        session.Set("isCEO", PyBool.True);
        session.Set("corprole", new PyLong(1));
        return (session, items);
    }

    private static async Task PlaceCharacterAtSystemStationAsync(Rig rig, Eve.Characters.Created character, int systemId)
    {
        long stationId;
        int constellationId;
        int regionId;
        await using (var connection = await rig.Source.OpenConnectionAsync())
        {
            await using var stationCommand = new NpgsqlCommand(
                "SELECT key FROM sde.\"npcStations\" WHERE (doc->>'solarSystemID')::int = $1 ORDER BY key LIMIT 1", connection);
            stationCommand.Parameters.Add(new NpgsqlParameter { Value = systemId });
            var stationKey = await stationCommand.ExecuteScalarAsync() as string
                ?? throw new InvalidOperationException($"SDE has no NPC station in system {systemId}");
            stationId = long.Parse(stationKey, System.Globalization.CultureInfo.InvariantCulture);

            await using var systemCommand = new NpgsqlCommand(
                "SELECT (doc->>'constellationID')::int, (doc->>'regionID')::int FROM sde.\"mapSolarSystems\" WHERE key = $1", connection);
            systemCommand.Parameters.Add(new NpgsqlParameter { Value = systemId.ToString(System.Globalization.CultureInfo.InvariantCulture) });
            await using var reader = await systemCommand.ExecuteReaderAsync();
            if (!await reader.ReadAsync())
                throw new InvalidOperationException($"SDE has no solar system {systemId}");
            constellationId = reader.GetInt32(0);
            regionId = reader.GetInt32(1);
        }

        await using (var connection = await rig.Source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await rig.Characters.SetStationAsync(connection, transaction, character.CharacterId, stationId);
            await rig.Characters.SetLocationAsync(connection, transaction, character.CharacterId, systemId, constellationId, regionId);
            await rig.Items.MoveToOwnerAsync(connection, transaction, character.ShipId, character.CharacterId, stationId, Eve.Inventory.ItemFlags.Hangar);
            await transaction.CommitAsync();
        }
    }

    /// <summary>Places the unanchored test tower at a real SDE moon warp-in point and sends the explicit posMgr anchor request.</summary>
    private static async Task AnchorAtMoonAsync(Rig rig, ClientSession session, long towerId)
    {
        var systemId = (int)(session.GetInt("solarsystemid2") ?? 0);
        await using var connection = await rig.Source.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            "SELECT key, (doc->>'radius')::float8, (doc->'position'->>'x')::float8, (doc->'position'->>'y')::float8, (doc->'position'->>'z')::float8 " +
            "FROM sde.\"mapMoons\" WHERE (doc->>'solarSystemID')::int = $1 ORDER BY key LIMIT 1", connection);
        command.Parameters.Add(new NpgsqlParameter { Value = systemId });
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync(), $"SDE has no moon in system {systemId}");
        var radius = reader.GetDouble(1);
        var x = reader.GetDouble(2);
        var y = reader.GetDouble(3);
        var z = reader.GetDouble(4);
        var warpRadius = radius + 5_000_000;
        var warpPoint = new[]
        {
            x + warpRadius * Math.Cos(radius),
            y + 1.3 * radius - 7_500,
            z - warpRadius * Math.Sin(radius),
        };
        await reader.DisposeAsync();

        var record = Assert.IsType<DeployedRecord>(await rig.Deployed.GetAsync(towerId));
        await rig.Deployed.SaveAsync(record with { Position = warpPoint });
        rig.Sim.PositionAnswers[towerId] = warpPoint;
        rig.Sim.PositionAnswers[session.GetInt("shipid")!.Value] = warpPoint;
        await rig.Containers.AnchorControlTowerAsync(session, towerId, [0, 0, 0], CancellationToken.None);
        var anchoredRecord = Assert.IsType<DeployedRecord>(await rig.Deployed.GetAsync(towerId));
        rig.Sim.PositionAnswers[session.GetInt("shipid")!.Value] = anchoredRecord.Position;
        rig.Sim.DropPointAnswer = (_, distance) =>
            [anchoredRecord.Position[0] + distance, anchoredRecord.Position[1], anchoredRecord.Position[2]];
        AssertControlTowerSlimState(rig, towerId, 1, rig.Now);
    }

    private static void AssertControlTowerSlimState(Rig rig, long towerId, int state, long? startedAtMs)
    {
        var fields = rig.Sim.Heard.OfType<SimSetSlimField>().Where(field => field.ItemId == towerId).ToArray();
        var stateField = fields.Last(field => field.FieldName == "posState");
        Assert.Equal(state, Assert.IsType<SimSlimInt>(stateField.Value).Value);
        var timestampField = fields.Last(field => field.FieldName == "posTimestamp");
        if (startedAtMs is { } started)
            Assert.Equal(Py.FileTimeFromUnixMs(started), Assert.IsType<SimSlimInt>(timestampField.Value).Value);
        else
            Assert.IsType<SimSlimNone>(timestampField.Value);
    }

    /// <summary>Every text inside a wrapped error's payload, joined (the refusal's words).</summary>
    private static string Texts(PyValue value) => value switch
    {
        PyStr text => text.Value,
        PyWString text => text.Value,
        PyBytes text => System.Text.Encoding.UTF8.GetString(text.Value.Span),
        PyTuple tuple => string.Join("|", tuple.Items.Select(Texts)),
        PyList list => string.Join("|", list.Items.Select(Texts)),
        PyDict dict => string.Join("|", dict.Entries.SelectMany(entry => new[] { Texts(entry.Key), Texts(entry.Value) })),
        PyObjectEx ex => string.Join("|", new[] { Texts(ex.Header) }.Concat(ex.List.Select(Texts)).Concat(ex.Dict.SelectMany(entry => new[] { Texts(entry.Key), Texts(entry.Value) }))),
        PyObject obj => Texts(obj.Args),
        _ => "",
    };

    [PgFact]
    public async Task A_dropped_control_tower_stays_unanchored_until_the_pilot_requests_anchor()
    {
        await using var rig = await Rig.CreateAsync();
        var (session, items) = await PilotWithItemsAsync(rig, [ControlTower], LowSecSystem);
        var tower = items[0];
        var launchedAt = rig.Now;

        Assert.Equal(tower, await rig.Containers.LaunchFromShipAsync(session, tower, CancellationToken.None));

        var ball = Assert.Single(rig.Sim.Heard.OfType<SimPlaceControlTower>());
        Assert.False(ball.Active, "a control tower is dropped unanchored");
        Assert.Equal(new[] { 2_500.0, 0.0, 0.0 }, ball.Position.ToArray());
        Assert.Equal(3_394.0, ball.Radius);
        var record = (await rig.Deployed.GetAsync(tower))!;
        Assert.Equal("control_tower", record.Kind);
        Assert.Equal(long.MaxValue, record.ActivateAtMs);
        Assert.Equal(long.MaxValue, record.ExpiresAtMs);
        Assert.Equal("unanchored", await rig.Deployed.TowerStateAsync(tower));
        AssertControlTowerSlimState(rig, tower, 0, null);

        rig.Sim.Heard.Clear();
        rig.Now += AnchoringMs + 100_000;
        await rig.Scheduler.RunDueAsync();
        Assert.DoesNotContain(rig.Sim.Heard, m => m is SimSetDisruptorActive anchored && anchored.DeployableId == tower && anchored.Active);

        // no time takes it out: a sweep a hundred days later leaves it in space
        rig.Sim.Heard.Clear();
        rig.Now = launchedAt + 100L * 24 * 60 * 60 * 1000;
        await rig.Containers.SweepAsync();
        Assert.NotNull(await rig.Items.GetAsync(tower));
        Assert.Empty(rig.Sim.Heard.OfType<SimRemoveDeployed>());
    }

    [PgFact]
    public async Task A_control_tower_refuses_a_mobile_observatory_within_100_km_until_its_corporation_removes_it()
    {
        await using var rig = await Rig.CreateAsync();
        var (session, items) = await PilotWithItemsAsync(rig, [ControlTower, Observatory], LowSecSystem);
        var (tower, observatory) = (items[0], items[1]);
        await rig.Containers.LaunchFromShipAsync(session, tower, CancellationToken.None);
        await AnchorAtMoonAsync(rig, session, tower);

        var refused = await Assert.ThrowsAsync<MachoWrappedException>(() => rig.Containers.LaunchFromShipAsync(session, observatory, CancellationToken.None));
        Assert.Contains("control tower", Texts(refused.Error.Payload));
        Assert.Equal(CharacterShips.CargoFlag, (await rig.Items.GetAsync(observatory))!.Flag);

        // the requested anchor completes after the SDE delay; it stays offline until explicitly onlined. Another corporation may not unanchor it.
        rig.Now += AnchoringMs;
        await rig.Scheduler.RunDueAsync();
        var launching = session.GetInt("corpid") ?? 0;
        session.Set("corpid", new PyInt(launching + 1));
        await Assert.ThrowsAsync<MachoWrappedException>(() => rig.Containers.UnanchorControlTowerAsync(session, tower));
        Assert.Equal("offline", await rig.Deployed.TowerStateAsync(tower));
        session.Set("corpid", new PyInt(launching));
        rig.Sim.Heard.Clear();
        await rig.Containers.UnanchorControlTowerAsync(session, tower);
        Assert.Equal("unanchoring", await rig.Deployed.TowerStateAsync(tower));

        // the unanchoring runs for the SDE's hour (attribute 676): the tower is still in space until then
        await rig.Scheduler.RunDueAsync();
        Assert.DoesNotContain(rig.Sim.Heard, m => m is SimRemoveDeployed);
        rig.Now += HourMs;
        await rig.Scheduler.RunDueAsync();
        Assert.Contains(rig.Sim.Heard, m => m is SimRemoveDeployed removed && removed.DeployableId == tower);
        Assert.Null(await rig.Deployed.GetAsync(tower));

        // the item is not deleted: it is dropped where the tower was, as an unanchor drop (the structure rule), marked for its corporation
        Assert.Equal(StructureUnanchorDrop.HullMarker(tower), (await rig.Items.GetAsync(tower))!.CustomInfo);
        Assert.Contains(rig.Sim.Heard, m => m is SimPlaceWreck wreck && wreck.WreckId == tower);

        // with the tower gone, the observatory drops
        Assert.Equal(observatory, await rig.Containers.LaunchFromShipAsync(session, observatory, CancellationToken.None));
    }

    [PgFact]
    public async Task A_control_tower_in_space_comes_back_after_a_restart_unanchored_or_anchored_as_its_record_says()
    {
        await using var rig = await Rig.CreateAsync();
        var (session, items) = await PilotWithItemsAsync(rig, [ControlTower], LowSecSystem);
        var system = (int)session.GetInt("solarsystemid2")!.Value;
        await rig.Containers.LaunchFromShipAsync(session, items[0], CancellationToken.None);
        await AnchorAtMoonAsync(rig, session, items[0]);
        rig.Sim.Heard.Clear();

        rig.Now += 1_000;
        await rig.Containers.RestoreDeployedAsync(system);
        Assert.False(Assert.Single(rig.Sim.Heard.OfType<SimPlaceControlTower>()).Active);
        AssertControlTowerSlimState(rig, items[0], 1, rig.Now - 1_000);

        rig.Sim.Heard.Clear();
        rig.Now += AnchoringMs;
        await rig.Scheduler.RunDueAsync();
        await rig.Containers.RestoreDeployedAsync(system);
        Assert.True(Assert.Single(rig.Sim.Heard.OfType<SimPlaceControlTower>()).Active);
        AssertControlTowerSlimState(rig, items[0], 1, null);

        var tower = items[0];
        var corporationId = session.GetInt("corpid")!.Value;
        await FuelAsync(rig, tower, HeliumFuelBlock, 40, corporationId);
        Assert.Equal(ModuleResult.Done, await rig.Space.ActivateAsync(session, tower, "onlineForStructures", 0, 1, CancellationToken.None));
        var onlineStartedAtMs = rig.Now;
        Assert.Equal(onlineStartedAtMs, await rig.Deployed.TowerStateStartedAtMsAsync(tower));

        await using var restartedScheduler = new Eve.Scheduler.DurableScheduler(rig.Source, () => rig.Now, null, rig.SchedulerSchema);
        using var restartedContainers = new SpaceContainers(rig.Sim, rig.Ships, rig.Items, rig.SpaceItems,
            new PgWorkCycleLedger(rig.Source, rig.SpaceSchema), new EventBusFacts(rig.Events), rig.Statics, rig.Space,
            () => rig.Now, deployed: rig.Deployed, scheduler: restartedScheduler, source: rig.Source);
        restartedScheduler.Register(SpaceContainers.ControlTowerOnlineJob, async (context, token) =>
        {
            var due = context.Payload<SpaceContainers.ControlTowerOnlineDue>()!;
            if (await restartedContainers.CompleteControlTowerOnlineAsync(context.Connection, context.Transaction, due.DeployableId, due.StartedAtMs, token))
                context.AfterCommit(_ => { rig.Events.Wake(); return Task.CompletedTask; });
        });
        restartedScheduler.Register(SpaceContainers.ControlTowerFuelJob, async (context, token) =>
        {
            if (await restartedContainers.ControlTowerFuelAsync(context.Connection, context.Transaction,
                context.Payload<SpaceContainers.ControlTowerFuelDue>()!.DeployableId, context.ScheduledForMs, token))
                context.AfterCommit(_ => { rig.Events.Wake(); return Task.CompletedTask; });
        });

        rig.Sim.Heard.Clear();
        rig.Now += 1_000;
        await restartedContainers.RestoreDeployedAsync(system);
        Assert.True(Assert.Single(rig.Sim.Heard.OfType<SimPlaceControlTower>()).Active);
        AssertControlTowerSlimState(rig, tower, 2, onlineStartedAtMs);
        Assert.Equal("onlining", await rig.Deployed.TowerStateAsync(tower));

        rig.Now += OnliningMs - 1_000;
        await restartedScheduler.RunDueAsync();
        Assert.Equal("online", await rig.Deployed.TowerStateAsync(tower));
        AssertControlTowerSlimState(rig, tower, 4, null);
        Assert.Equal(0, await BayAsync(rig, tower, HeliumFuelBlock));
    }

    [PgFact]
    public async Task Tower_eligibility_events_follow_deadline_and_transactional_removal()
    {
        await using var rig = await Rig.CreateAsync();
        var bus = new Eve.Events.EventBus(rig.Source, rig.Scheduler,
            new Eve.Events.EventBusOptions { MaxAttempts = 3 }, rig.EventsSchema, _ => { });
        var seen = new List<long>();
        bus.Subscribe("test.tower.eligibility", [SpaceContainers.ControlTowerEligibilityChangedTopic], (context, _) =>
        {
            var payload = context.Event.Payload<SpaceContainers.ControlTowerEligibilityChanged>()!;
            context.AfterCommit(_ => { seen.Add(payload.CorporationId); return Task.CompletedTask; });
            return Task.CompletedTask;
        });
        await bus.PumpAsync();
        var (session, items) = await PilotWithItemsAsync(rig, [ControlTower], LowSecSystem);
        var tower = items[0];
        const long corporation = 98_000_001;
        session.Set("corpid", new PyLong(corporation));
        await rig.Containers.LaunchFromShipAsync(session, tower, CancellationToken.None);
        await AnchorAtMoonAsync(rig, session, tower);
        await using (var connection = await rig.Source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await rig.Containers.PublishControlTowerAnchoredAsync(connection, transaction, bus, tower);
            await transaction.CommitAsync();
        }
        await bus.PumpAsync();
        Assert.Empty(seen);
        rig.Now += AnchoringMs;
        await rig.Scheduler.RunDueAsync();
        await using (var connection = await rig.Source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await rig.Containers.PublishControlTowerAnchoredAsync(connection, transaction, bus, tower);
            await transaction.CommitAsync();
        }
        await bus.PumpAsync();
        Assert.Equal(corporation, Assert.Single(seen));
        await rig.Containers.UnanchorControlTowerAsync(session, tower);
        await using (var connection = await rig.Source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await rig.Items.DeleteAsync(connection, transaction, tower);
            await transaction.CommitAsync();
        }
        await using (var connection = await rig.Source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await rig.Containers.CompleteControlTowerUnanchorAsync(tower, connection, transaction, bus);
            await transaction.RollbackAsync();
        }
        await bus.PumpAsync();
        Assert.NotNull(await rig.Deployed.GetAsync(tower));
        Assert.Single(seen);
        await using (var connection = await rig.Source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await rig.Containers.CompleteControlTowerUnanchorAsync(tower, connection, transaction, bus);
            await transaction.CommitAsync();
        }
        await bus.PumpAsync();
        Assert.Null(await rig.Deployed.GetAsync(tower));
        Assert.Equal(new[] { corporation, corporation }, seen);
        await using (var connection = await rig.Source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await rig.Containers.CompleteControlTowerUnanchorAsync(tower, connection, transaction, bus);
            await transaction.CommitAsync();
        }
        await bus.PumpAsync();
        Assert.Equal(2, seen.Count);
    }

    [PgFact]
    public async Task A_control_tower_burns_one_period_of_its_race_fuel_and_goes_offline_when_the_bay_cannot_pay()
    {
        await using var rig = await Rig.CreateAsync();
        _ = await rig.Events.PumpAsync(); // establish the subscriber's StartAt.Now cursor before publishing the fuel delta
        var (session, items) = await PilotWithItemsAsync(rig, [ControlTower], LowSecSystem);
        var tower = items[0];
        var owner = session.GetInt("corpid") ?? 0;

        // the Amarr Control Tower is large: 40 Helium Fuel Blocks an hour (SDE attribute 1031 = 3). The bay holds 39 of its race's block and 40 of Nitrogen,
        // the Caldari block, which does not fuel an Amarr tower
        await FuelAsync(rig, tower, HeliumFuelBlock, 39, owner);
        await FuelAsync(rig, tower, NitrogenFuelBlock, 40, owner);
        await rig.Containers.LaunchFromShipAsync(session, tower, CancellationToken.None);
        await AnchorAtMoonAsync(rig, session, tower);

        // Anchoring completes offline and does not debit fuel. An explicit online request refuses the 39-block bay.
        rig.Now += AnchoringMs;
        await rig.Scheduler.RunDueAsync();
        Assert.Equal("offline", await rig.Deployed.TowerStateAsync(tower));
        AssertControlTowerSlimState(rig, tower, 1, null);
        Assert.Equal(39, await BayAsync(rig, tower, HeliumFuelBlock));
        Assert.Equal(40, await BayAsync(rig, tower, NitrogenFuelBlock));
        await Assert.ThrowsAsync<MachoWrappedException>(() => rig.Space.ActivateAsync(session, tower, "onlineForStructures", 0, 1, CancellationToken.None));

        // One explicit Online request starts the SDE onlining delay. Startup fuel is paid only when the transition completes.
        await FuelAsync(rig, tower, HeliumFuelBlock, 2, owner);
        Assert.Equal(ModuleResult.Done, await rig.Space.ActivateAsync(session, tower, "onlineForStructures", 0, 1, CancellationToken.None));
        Assert.Equal("onlining", await rig.Deployed.TowerStateAsync(tower));
        AssertControlTowerSlimState(rig, tower, 2, rig.Now);
        Assert.Equal(41, await BayAsync(rig, tower, HeliumFuelBlock));
        Assert.Equal(ModuleResult.Done, await rig.Space.DeactivateAsync(session, tower, "onlineForStructures", CancellationToken.None));
        Assert.Equal("offline", await rig.Deployed.TowerStateAsync(tower));
        AssertControlTowerSlimState(rig, tower, 1, null);
        rig.Now += OnliningMs;
        await rig.Scheduler.RunDueAsync();
        Assert.Equal("offline", await rig.Deployed.TowerStateAsync(tower));
        Assert.Equal(41, await BayAsync(rig, tower, HeliumFuelBlock));
        Assert.Equal(ModuleResult.Done, await rig.Space.ActivateAsync(session, tower, "onlineForStructures", 0, 1, CancellationToken.None));
        Assert.Equal("onlining", await rig.Deployed.TowerStateAsync(tower));
        AssertControlTowerSlimState(rig, tower, 2, rig.Now);
        _ = Events.Of(session);
        rig.Now += OnliningMs;
        await rig.Scheduler.RunDueAsync();
        Assert.Equal("online", await rig.Deployed.TowerStateAsync(tower));
        AssertControlTowerSlimState(rig, tower, 4, null);
        Assert.Equal(1, await BayAsync(rig, tower, HeliumFuelBlock));
        Assert.Equal(40, await BayAsync(rig, tower, NitrogenFuelBlock));
        var pumped = await rig.Events.PumpAsync();
        Assert.Equal(1, pumped.Delivered);
        var fuelChanges = Events.Of(session);
        Assert.Equal(2, fuelChanges.Count(change => change.Name == "OnItemChange"));
        var changedQuantities = new List<long>();
        Assert.All(fuelChanges, change =>
        {
            Assert.Equal("OnItemChange", change.Name);
            var row = Assert.IsType<PyPackedRow>(change.Args[0]);
            changedQuantities.Add(Events.Number(row.Fields().Single(field => field.Key == "quantity").Value));
            var previous = Assert.IsType<PyDict>(change.Args[1]);
            Assert.Contains(previous.Entries, field => field.Key.Equals(new PyInt(9)) && Events.Number(field.Value) > 0);
        });
        Assert.Equal(new long[] { 0, 1 }, changedQuantities.OrderBy(quantity => quantity));

        // an online tower cannot be unanchored
        var refused = await Assert.ThrowsAsync<MachoWrappedException>(() => rig.Containers.UnanchorControlTowerAsync(session, tower));
        Assert.Contains("offline", Texts(refused.Error.Payload));
        Assert.Equal("online", await rig.Deployed.TowerStateAsync(tower));

        // the next hourly period finds the bay empty: offline again, and the unanchoring may be asked
        rig.Now += HourMs;
        await rig.Scheduler.RunDueAsync();
        Assert.Equal("offline", await rig.Deployed.TowerStateAsync(tower));
        AssertControlTowerSlimState(rig, tower, 1, null);
        await rig.Containers.UnanchorControlTowerAsync(session, tower);
        Assert.Equal("unanchoring", await rig.Deployed.TowerStateAsync(tower));
        AssertControlTowerSlimState(rig, tower, 0, rig.Now);

        // an unanchoring tower burns no more fuel: its next period job does nothing
        await FuelAsync(rig, tower, HeliumFuelBlock, 40, owner);
        rig.Now += HourMs;
        await rig.Scheduler.RunDueAsync();
        Assert.Equal(41, await BayAsync(rig, tower, HeliumFuelBlock));
    }
}
