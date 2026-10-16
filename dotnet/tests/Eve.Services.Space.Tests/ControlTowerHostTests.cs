using Eve.Macho;
using Eve.Marshal;
using Eve.Ships;
using Eve.Space;
using Eve.Structures;

namespace Eve.Services.Space.Tests;

/// <summary>
/// The player control tower launched from a ship (ST-19, protocol 73): a pilot drops one from the hold 2,500 m ahead of the ship, in a system below 0.5 security.
/// It is anchored when its anchoring time is over (the SDE's: 30 minutes for the Amarr Control Tower), never ends by time, and only its launching corporation
/// removes it. A tower within 100 km refuses a Mobile Observatory (the wiki, Mobile Observatory), and a restart puts it back with the state its record gives.
/// </summary>
public class ControlTowerHostTests
{
    /// <summary>The Amarr Control Tower (SDE group 365, radius 3394 m, anchoring delay 1 800 000 ms).</summary>
    private const int ControlTower = 12235, Observatory = 58904, Anchoring = 11584, AnchoringLevel = 3;
    /// <summary>Asabona (Derelik, security 0.32): below the cutoff, so an observatory may be dropped there.</summary>
    private const long LowSecSystem = 30000012;
    private const long AnchoringMs = 1_800_000;
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

        var session = await rig.SessionAsync(character, 1);
        var oid = await rig.BindShipAsync(session);
        await rig.ShipCallAsync(session, "Undock", oid, new PyLong(session.GetInt("shipid")!.Value), PyBool.False);
        await rig.BindBeyonceAsync(session);
        Events.Of(session);
        session.Set("solarsystemid2", new PyInt(system));
        return (session, items);
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
    public async Task A_launched_control_tower_is_anchored_after_its_sde_anchoring_time_and_never_ends_by_time()
    {
        await using var rig = await Rig.CreateAsync();
        var (session, items) = await PilotWithItemsAsync(rig, [ControlTower], LowSecSystem);
        var tower = items[0];
        var launchedAt = rig.Now;

        Assert.Equal(tower, await rig.Containers.LaunchFromShipAsync(session, tower, CancellationToken.None));

        var ball = Assert.Single(rig.Sim.Heard.OfType<SimPlaceControlTower>());
        Assert.False(ball.Active, "a control tower is not anchored when it is dropped");
        Assert.Equal(new[] { 2_500.0, 0.0, 0.0 }, ball.Position.ToArray());
        Assert.Equal(3_394.0, ball.Radius);
        var record = (await rig.Deployed.GetAsync(tower))!;
        Assert.Equal("control_tower", record.Kind);
        Assert.Equal(launchedAt + AnchoringMs, record.ActivateAtMs);
        Assert.Equal(long.MaxValue, record.ExpiresAtMs);

        rig.Sim.Heard.Clear();
        rig.Now += AnchoringMs - 1_000;
        await rig.Scheduler.RunDueAsync();
        Assert.DoesNotContain(rig.Sim.Heard, m => m is SimSetDisruptorActive);
        rig.Now += 2_000;
        await rig.Scheduler.RunDueAsync();
        Assert.Contains(rig.Sim.Heard, m => m is SimSetDisruptorActive anchored && anchored.DeployableId == tower && anchored.Active);

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

        // the observatory is dropped at the tower's point: refused (the wiki: 100 km from a control tower)
        var refused = await Assert.ThrowsAsync<MachoWrappedException>(() => rig.Containers.LaunchFromShipAsync(session, observatory, CancellationToken.None));
        Assert.Contains("control tower", Texts(refused.Error.Payload));
        Assert.Equal(CharacterShips.CargoFlag, (await rig.Items.GetAsync(observatory))!.Flag);

        // the tower is anchored after its SDE time; with no fuel it is offline, and then its corporation may unanchor it. Another corporation may not
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
        rig.Sim.Heard.Clear();

        rig.Now += 1_000;
        await rig.Containers.RestoreDeployedAsync(system);
        Assert.False(Assert.Single(rig.Sim.Heard.OfType<SimPlaceControlTower>()).Active);

        rig.Sim.Heard.Clear();
        rig.Now += AnchoringMs;
        await rig.Containers.RestoreDeployedAsync(system);
        Assert.True(Assert.Single(rig.Sim.Heard.OfType<SimPlaceControlTower>()).Active);
    }

    [PgFact]
    public async Task A_control_tower_burns_one_period_of_its_race_fuel_and_goes_offline_when_the_bay_cannot_pay()
    {
        await using var rig = await Rig.CreateAsync();
        var (session, items) = await PilotWithItemsAsync(rig, [ControlTower], LowSecSystem);
        var tower = items[0];
        var owner = session.GetInt("charid") ?? 0;

        // the Amarr Control Tower is large: 40 Helium Fuel Blocks an hour (SDE attribute 1031 = 3). The bay holds 39 of its race's block and 40 of Nitrogen,
        // the Caldari block, which does not fuel an Amarr tower
        await FuelAsync(rig, tower, HeliumFuelBlock, 39, owner);
        await FuelAsync(rig, tower, NitrogenFuelBlock, 40, owner);
        await rig.Containers.LaunchFromShipAsync(session, tower, CancellationToken.None);

        // anchored, the first period is paid at once: 39 blocks cannot pay it, so the tower is offline and nothing is taken
        rig.Now += AnchoringMs;
        await rig.Scheduler.RunDueAsync();
        Assert.Equal("offline", await rig.Deployed.TowerStateAsync(tower));
        Assert.Equal(39, await BayAsync(rig, tower, HeliumFuelBlock));
        Assert.Equal(40, await BayAsync(rig, tower, NitrogenFuelBlock));

        // fuel one more block: the next period pays the 40 and the tower is online
        await FuelAsync(rig, tower, HeliumFuelBlock, 1, owner);
        rig.Now += HourMs;
        await rig.Scheduler.RunDueAsync();
        Assert.Equal("online", await rig.Deployed.TowerStateAsync(tower));
        Assert.Equal(0, await BayAsync(rig, tower, HeliumFuelBlock));
        Assert.Equal(40, await BayAsync(rig, tower, NitrogenFuelBlock));

        // an online tower cannot be unanchored
        var refused = await Assert.ThrowsAsync<MachoWrappedException>(() => rig.Containers.UnanchorControlTowerAsync(session, tower));
        Assert.Contains("offline", Texts(refused.Error.Payload));
        Assert.Equal("online", await rig.Deployed.TowerStateAsync(tower));

        // the next period finds the bay empty: offline again, and the unanchoring may be asked
        rig.Now += HourMs;
        await rig.Scheduler.RunDueAsync();
        Assert.Equal("offline", await rig.Deployed.TowerStateAsync(tower));
        await rig.Containers.UnanchorControlTowerAsync(session, tower);
        Assert.Equal("unanchoring", await rig.Deployed.TowerStateAsync(tower));

        // an unanchoring tower burns no more fuel: its next period job does nothing
        await FuelAsync(rig, tower, HeliumFuelBlock, 40, owner);
        rig.Now += HourMs;
        await rig.Scheduler.RunDueAsync();
        Assert.Equal(40, await BayAsync(rig, tower, HeliumFuelBlock));
    }
}
