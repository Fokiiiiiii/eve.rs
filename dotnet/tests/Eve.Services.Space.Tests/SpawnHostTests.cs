using Eve.Space;

namespace Eve.Services.Space.Tests;

/// <summary>
/// <c>/spawn</c>, <c>/spawnn</c>, <c>/unspawn</c> and <c>/nuke</c> on the host: the hulls come into space around the pilot's ship as the bare hulls of
/// <c>/deathtest</c> do, and stay until they are taken out; a taken-out ship leaves the scene (a simulation leave, with no destroy and no wreck).
/// Database-backed (<see cref="PgFactAttribute"/>).
/// </summary>
public class SpawnHostTests
{
    private const int Ibis = 601;

    private static async Task<(Rig Rig, Eve.Characters.Created Character, ClientSession Session)> InSpaceAsync()
    {
        var rig = await Rig.CreateAsync();
        var character = await rig.NewCharacterAsync(shipTypeId: Ibis);
        var session = await rig.SessionAsync(character);
        var oid = await rig.BindShipAsync(session);
        await rig.ShipCallAsync(session, "Undock", oid, new Eve.Marshal.PyLong(session.GetInt("shipid")!.Value), Eve.Marshal.PyBool.False);
        return (rig, character, session);
    }

    [PgFact]
    public async Task Spawned_hulls_stay_in_space_until_taken_out_and_then_leave_the_scene_without_a_destroy()
    {
        var (rig, character, session) = await InSpaceAsync();
        await using var _ = rig;
        ISpaceSpawns spawns = rig.Space;

        var made = await spawns.SpawnHullsAsync(session, Ibis, 2, 5_000, CancellationToken.None);

        Assert.Null(made.Error);
        Assert.Equal(2, made.ShipIds.Count);
        Assert.All(made.ShipIds, shipId => Assert.True(shipId >= 4_200_000_000_000_000));
        var spawns1 = rig.Sim.Heard.OfType<SimSpawn>().ToList();
        Assert.Equal(2, spawns1.Count);
        Assert.All(spawns1, spawn => Assert.Equal(character.ShipId, spawn.AnchorShipId));
        var view = await spawns.SpawnedAsync(session, CancellationToken.None);
        Assert.NotNull(view);
        Assert.Equal(made.ShipIds.Order().ToArray(), view!.Ships.Select(ship => ship.ShipId).Order().ToArray());
        Assert.All(view.Ships, ship => Assert.Equal(601, ship.TypeId));

        // nothing destroys them: they stay until they are taken out
        await Task.Delay(TimeSpan.FromMilliseconds(200));
        Assert.Empty(rig.Sim.Heard.OfType<SimDestroy>());

        Assert.Equal(2, await spawns.RemoveAsync(made.ShipIds, CancellationToken.None));
        var leaves = rig.Sim.Heard.OfType<SimLeave>().ToList();
        Assert.Equal(2, leaves.Count);
        Assert.All(leaves, leave => Assert.Equal(LeaveWhy.Docked, leave.Why));
        Assert.Empty(rig.Sim.Heard.OfType<SimDestroy>());
        Assert.Empty((await spawns.SpawnedAsync(session, CancellationToken.None))!.Ships);
        Assert.Equal(0, await spawns.RemoveAsync(made.ShipIds, CancellationToken.None));
    }

    [PgFact]
    public async Task A_docked_pilot_is_told_it_is_not_in_space_and_nothing_is_made()
    {
        var rig = await Rig.CreateAsync();
        await using var _ = rig;
        var character = await rig.NewCharacterAsync(shipTypeId: Ibis);
        var session = await rig.SessionAsync(character);
        ISpaceSpawns spawns = rig.Space;

        var made = await spawns.SpawnHullsAsync(session, Ibis, 1, 0, CancellationToken.None);

        Assert.Equal(SpawnResult.NotInSpace, made.Error);
        Assert.Empty(made.ShipIds);
        Assert.Empty(rig.Sim.Heard.OfType<SimSpawn>());
        Assert.Null(await spawns.SpawnedAsync(session, CancellationToken.None));
    }
}
