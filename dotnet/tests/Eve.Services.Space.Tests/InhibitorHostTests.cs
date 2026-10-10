using Eve.Macho;
using Eve.Marshal;
using Eve.Ships;
using Eve.Space;

namespace Eve.Services.Space.Tests;

/// <summary>
/// The Mobile Cynosural Inhibitor (E-03; the wiki's rules): a pilot drops one from the hold 2,500 m ahead of the ship; it is anchored two minutes later; it lives one hour
/// and leaves with its item at the sweep; a restart puts it back with the state its record gives; a pilot without Anchoring III cannot drop one, nor one within 200 km
/// of another.
/// </summary>
public class InhibitorHostTests
{
    private const int Inhibitor = 33476, Anchoring = 11584, AnchoringLevel = 3;
    /// <summary>A null-security system (the SDE's security -1.0): the inhibitor has no security rule of its own, so the system only matters for the drop.</summary>
    private const long NullSecSystem = 30000021;
    private const long AnchoringMs = 120_000;
    private const long OneHourMs = 60L * 60 * 1000;

    /// <summary>A pilot with <paramref name="count"/> inhibitors in its hold, in space in a null-security system; it has Anchoring III unless told not to.</summary>
    private static async Task<(ClientSession Session, long[] Items)> PilotWithInhibitorsAsync(Rig rig, int count, bool skilled = true)
    {
        var character = await rig.NewCharacterAsync();
        if (skilled)
        {
            await using var connection = await rig.Source.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            await rig.Skills.GrantAsync(connection, transaction, character.CharacterId, [new Eve.Skills.CharacterSkill(Anchoring, AnchoringLevel, 0)]);
            await transaction.CommitAsync();
        }
        var items = new long[count];
        for (var index = 0; index < count; index++)
        {
            var granted = (await rig.Ships.GrantAsync(character.CharacterId, Inhibitor, 1, singleton: true))!.Single().After;
            var moved = await rig.Ships.MoveAsync(character.CharacterId, granted.Id, Rig.Station, character.ShipId, CharacterShips.CargoFlag, 1);
            Assert.True(moved is Eve.Ships.ShipChanged, moved.ToString());
            items[index] = granted.Id;
        }

        var session = await rig.SessionAsync(character, 1);
        var oid = await rig.BindShipAsync(session);
        await rig.ShipCallAsync(session, "Undock", oid, new PyLong(session.GetInt("shipid")!.Value), PyBool.False);
        await rig.BindBeyonceAsync(session);
        Events.Of(session);
        session.Set("solarsystemid2", new PyInt(NullSecSystem));
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
    public async Task A_dropped_inhibitor_is_anchored_two_minutes_later_and_leaves_with_its_item_an_hour_after_the_drop()
    {
        await using var rig = await Rig.CreateAsync();
        var (session, items) = await PilotWithInhibitorsAsync(rig, 1);
        var inhibitor = items[0];
        var launchedAt = rig.Now;

        var placed = await rig.Containers.LaunchFromShipAsync(session, inhibitor, CancellationToken.None);

        Assert.Equal(inhibitor, placed);
        var ball = Assert.Single(rig.Sim.Heard.OfType<SimPlaceInhibitor>());
        Assert.False(ball.Active, "an inhibitor is not anchored when it is dropped");
        Assert.Equal(100_000, ball.Range);
        Assert.Equal(new[] { 2_500.0, 0.0, 0.0 }, ball.Position.ToArray());
        Assert.Equal(100.0, ball.Radius);
        var record = (await rig.Deployed.GetAsync(inhibitor))!;
        Assert.Equal("mobile_cyno_inhibitor", record.Kind);
        Assert.Equal(launchedAt + AnchoringMs, record.ActivateAtMs);

        rig.Sim.Heard.Clear();
        rig.Now += AnchoringMs - 1_000;
        await rig.Scheduler.RunDueAsync();
        Assert.DoesNotContain(rig.Sim.Heard, m => m is SimSetDisruptorActive);
        rig.Now += 2_000;
        await rig.Scheduler.RunDueAsync();
        Assert.Contains(rig.Sim.Heard, m => m is SimSetDisruptorActive anchored && anchored.DeployableId == inhibitor && anchored.Active);

        // an hour after the drop it leaves: still there just before, gone at the sweep after
        rig.Sim.Heard.Clear();
        rig.Now += OneHourMs - AnchoringMs - 2_000;
        await rig.Containers.SweepAsync();
        Assert.NotNull(await rig.Items.GetAsync(inhibitor));
        Assert.DoesNotContain(rig.Sim.Heard, m => m is SimRemoveDeployed);
        rig.Now += 2_000;
        await rig.Containers.SweepAsync();
        Assert.Contains(rig.Sim.Heard, m => m is SimRemoveDeployed removed && removed.DeployableId == inhibitor);
        Assert.Null(await rig.Items.GetAsync(inhibitor));
        Assert.Null(await rig.Deployed.GetAsync(inhibitor));
    }

    [PgFact]
    public async Task An_inhibitor_in_space_comes_back_after_a_restart_unanchored_or_anchored_as_its_record_says()
    {
        await using var rig = await Rig.CreateAsync();
        var (session, items) = await PilotWithInhibitorsAsync(rig, 1);
        var system = (int)session.GetInt("solarsystemid2")!.Value;
        await rig.Containers.LaunchFromShipAsync(session, items[0], CancellationToken.None);
        rig.Sim.Heard.Clear();

        rig.Now += 1_000;
        await rig.Containers.RestoreDeployedAsync(system);
        var waiting = Assert.Single(rig.Sim.Heard.OfType<SimPlaceInhibitor>());
        Assert.False(waiting.Active);

        rig.Sim.Heard.Clear();
        rig.Now += AnchoringMs;
        await rig.Containers.RestoreDeployedAsync(system);
        var anchored = Assert.Single(rig.Sim.Heard.OfType<SimPlaceInhibitor>());
        Assert.True(anchored.Active);
    }

    [PgFact]
    public async Task A_pilot_without_anchoring_three_cannot_drop_an_inhibitor_and_it_stays_in_the_hold()
    {
        await using var rig = await Rig.CreateAsync();
        var (session, items) = await PilotWithInhibitorsAsync(rig, 1, skilled: false);

        var refused = await Assert.ThrowsAsync<MachoWrappedException>(() => rig.Containers.LaunchFromShipAsync(session, items[0], CancellationToken.None));

        Assert.Contains("requires", Texts(refused.Error.Payload));
        Assert.Equal(CharacterShips.CargoFlag, (await rig.Items.GetAsync(items[0]))!.Flag);
        Assert.Empty(rig.Sim.Heard.OfType<SimPlaceInhibitor>());
    }

    [PgFact]
    public async Task A_second_inhibitor_within_200_km_of_the_first_is_refused_and_stays_in_the_hold()
    {
        await using var rig = await Rig.CreateAsync();
        var (session, items) = await PilotWithInhibitorsAsync(rig, 2);
        await rig.Containers.LaunchFromShipAsync(session, items[0], CancellationToken.None);

        // the simulation gives the same point ahead of the ship: the second would sit on the first
        var refused = await Assert.ThrowsAsync<MachoWrappedException>(() => rig.Containers.LaunchFromShipAsync(session, items[1], CancellationToken.None));

        Assert.Contains("too close", Texts(refused.Error.Payload));
        Assert.Equal(CharacterShips.CargoFlag, (await rig.Items.GetAsync(items[1]))!.Flag);
        Assert.Single(rig.Sim.Heard.OfType<SimPlaceInhibitor>());
    }
}
