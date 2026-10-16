using Eve.Macho;
using Eve.Marshal;
using Eve.Ships;
using Eve.Space;

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

        // another corporation cannot take the tower out; the launching corporation can
        var launching = session.GetInt("corpid") ?? 0;
        session.Set("corpid", new PyInt(launching + 1));
        await Assert.ThrowsAsync<MachoWrappedException>(() => rig.Containers.RemoveControlTowerAsync(session, tower));
        Assert.NotNull(await rig.Deployed.GetAsync(tower));
        session.Set("corpid", new PyInt(launching));
        rig.Sim.Heard.Clear();
        await rig.Containers.RemoveControlTowerAsync(session, tower);
        Assert.Contains(rig.Sim.Heard, m => m is SimRemoveDeployed removed && removed.DeployableId == tower);
        Assert.Null(await rig.Items.GetAsync(tower));
        Assert.Null(await rig.Deployed.GetAsync(tower));

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
}
