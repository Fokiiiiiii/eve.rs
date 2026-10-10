using Eve.Macho;
using Eve.Marshal;
using Eve.Ships;
using Eve.Space;

namespace Eve.Services.Space.Tests;

/// <summary>
/// The mobile warp disruptors (WP-8; EVE.js <c>mobileWarpDisruptorRuntime</c>): a pilot drops one from the hold into space, where it is anchored when its anchoring
/// delay is over (the durable job); it leaves with its item when its time is up; a restart puts it back in space with the state its record gives; and a pilot without
/// the skill it needs cannot drop it.
/// </summary>
public class MobileWarpDisruptorHostTests
{
    private const int SmallMk1 = 12198, Anchoring2 = 11584, Anchoring2Level = 2, Skill2 = 3435, Skill2Level = 3;
    /// <summary>A null-security system (the SDE's security -1.0); a station's system is high-security, where the disruptor is not allowed.</summary>
    private const long NullSecSystem = 30000021;
    private const long AnchoringMs = 120_000;
    private const long TwoDaysMs = 2L * 24 * 60 * 60 * 1000;

    /// <summary>A pilot with a small disruptor in its hold, in space in a null-security system; it has the skills the disruptor needs unless told not to.</summary>
    private static async Task<(ClientSession Session, long Disruptor)> PilotWithDisruptorAsync(Rig rig, bool skilled = true)
    {
        var character = await rig.NewCharacterAsync();
        if (skilled)
        {
            await using var connection = await rig.Source.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            await rig.Skills.GrantAsync(connection, transaction, character.CharacterId,
                [new Eve.Skills.CharacterSkill(Anchoring2, Anchoring2Level, 0), new Eve.Skills.CharacterSkill(Skill2, Skill2Level, 0)]);
            await transaction.CommitAsync();
        }
        var granted = (await rig.Ships.GrantAsync(character.CharacterId, SmallMk1, 1, singleton: true))!.Single().After;
        var moved = await rig.Ships.MoveAsync(character.CharacterId, granted.Id, Rig.Station, character.ShipId, CharacterShips.CargoFlag, 1);
        Assert.True(moved is Eve.Ships.ShipChanged, moved.ToString());

        var session = await rig.SessionAsync(character, 1);
        var oid = await rig.BindShipAsync(session);
        await rig.ShipCallAsync(session, "Undock", oid, new PyLong(session.GetInt("shipid")!.Value), PyBool.False);
        await rig.BindBeyonceAsync(session);
        Events.Of(session);
        // the pilot flies in a null-security system: the disruptor is allowed there
        session.Set("solarsystemid2", new PyInt(NullSecSystem));
        return (session, granted.Id);
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
    public async Task A_dropped_disruptor_is_in_space_and_is_anchored_when_its_anchoring_delay_is_over()
    {
        await using var rig = await Rig.CreateAsync();
        var (session, disruptor) = await PilotWithDisruptorAsync(rig);
        var system = (int)session.GetInt("solarsystemid2")!.Value;
        var launchedAt = rig.Now;

        var placed = await rig.Containers.LaunchFromShipAsync(session, disruptor, CancellationToken.None);

        Assert.Equal(disruptor, placed);
        var item = (await rig.Items.GetAsync(disruptor))!;
        Assert.Equal(system, (int)item.LocationId);
        Assert.Equal(CharacterShips.InSpaceFlag, item.Flag);
        var ball = Assert.Single(rig.Sim.Heard.OfType<SimPlaceDisruptor>());
        Assert.False(ball.Active, "a disruptor is not anchored when it is dropped");
        Assert.Equal(5_000, ball.Range);
        // dropped at the simulation's point ahead of the ship, with the SDE radius of the type (70 m for the small Mk I)
        Assert.Equal(new[] { 2_500.0, 0.0, 0.0 }, ball.Position.ToArray());
        Assert.Equal(70.0, ball.Radius);
        Assert.Equal(system, ball.SolarSystemId);
        var record = (await rig.Deployed.GetAsync(disruptor))!;
        Assert.Equal("mobile_warp_disruptor", record.Kind);
        Assert.Equal(launchedAt + AnchoringMs, record.ActivateAtMs);

        rig.Sim.Heard.Clear();
        rig.Now += AnchoringMs - 1_000;
        await rig.Scheduler.RunDueAsync();
        Assert.DoesNotContain(rig.Sim.Heard, m => m is SimSetDisruptorActive);

        rig.Now += 2_000;
        await rig.Scheduler.RunDueAsync();
        Assert.Contains(rig.Sim.Heard, m => m is SimSetDisruptorActive anchored && anchored.DeployableId == disruptor && anchored.Active);
    }

    [PgFact]
    public async Task A_disruptor_leaves_at_the_sweep_when_its_time_is_up_and_its_item_is_gone()
    {
        await using var rig = await Rig.CreateAsync();
        var (session, disruptor) = await PilotWithDisruptorAsync(rig);
        await rig.Containers.LaunchFromShipAsync(session, disruptor, CancellationToken.None);
        rig.Sim.Heard.Clear();

        // a small Mk I lives two days: still there just before, gone at the sweep after
        rig.Now += TwoDaysMs - 1_000;
        await rig.Containers.SweepAsync();
        Assert.NotNull(await rig.Items.GetAsync(disruptor));
        Assert.DoesNotContain(rig.Sim.Heard, m => m is SimRemoveDeployed);

        rig.Now += 2_000;
        await rig.Containers.SweepAsync();
        Assert.Contains(rig.Sim.Heard, m => m is SimRemoveDeployed removed && removed.DeployableId == disruptor);
        Assert.Null(await rig.Items.GetAsync(disruptor));
        Assert.Null(await rig.Deployed.GetAsync(disruptor));
    }

    [PgFact]
    public async Task A_pilot_without_the_skill_its_disruptor_needs_cannot_drop_it_and_it_stays_in_the_hold()
    {
        await using var rig = await Rig.CreateAsync();
        var (session, disruptor) = await PilotWithDisruptorAsync(rig, skilled: false);

        var refused = await Assert.ThrowsAsync<MachoWrappedException>(() => rig.Containers.LaunchFromShipAsync(session, disruptor, CancellationToken.None));

        Assert.Contains("requires", Texts(refused.Error.Payload));
        Assert.Equal(CharacterShips.CargoFlag, (await rig.Items.GetAsync(disruptor))!.Flag);
        Assert.Empty(rig.Sim.Heard.OfType<SimPlaceDisruptor>());
    }

    [PgFact]
    public async Task A_disruptor_in_space_comes_back_after_a_restart_unanchored_or_anchored_as_its_record_says()
    {
        await using var rig = await Rig.CreateAsync();
        var (session, disruptor) = await PilotWithDisruptorAsync(rig);
        var system = (int)session.GetInt("solarsystemid2")!.Value;
        await rig.Containers.LaunchFromShipAsync(session, disruptor, CancellationToken.None);
        rig.Sim.Heard.Clear();

        // a restart before the anchoring delay is over: put back, not anchored yet
        rig.Now += 1_000;
        await rig.Containers.RestoreDeployedAsync(system);
        var waiting = Assert.Single(rig.Sim.Heard.OfType<SimPlaceDisruptor>());
        Assert.Equal(disruptor, waiting.DeployableId);
        Assert.False(waiting.Active);

        // a restart after it: put back anchored
        rig.Sim.Heard.Clear();
        rig.Now += AnchoringMs;
        await rig.Containers.RestoreDeployedAsync(system);
        var anchored = Assert.Single(rig.Sim.Heard.OfType<SimPlaceDisruptor>());
        Assert.Equal(disruptor, anchored.DeployableId);
        Assert.True(anchored.Active);
    }
}
