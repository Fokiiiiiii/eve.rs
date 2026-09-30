using Eve.Inventory;
using Eve.Marshal;
using Eve.Space;

namespace Eve.Services.Space.Tests;

/// <summary>A pilot whose ship is destroyed: the wreck and the items, the capsule, and what the client is told and in what order.</summary>
public class PlayerDeathTests
{
    private static readonly double[] Place = [311989835983.1308, -13069154020.916222, 156646308895.76187];
    private static readonly double[] Facing = [0.6159, 0.01306, 0.78769];
    private static readonly SimWreckKind FrigateWreck = new(26506, "Caldari Frigate Wreck", 3117);

    private static async Task<(Rig Rig, Eve.Characters.Created Character, ClientSession Session)> InSpaceAsync()
    {
        var rig = await Rig.CreateAsync();
        var character = await rig.NewCharacterAsync();
        var session = await rig.SessionAsync(character);
        var oid = await rig.BindShipAsync(session);
        await rig.ShipCallAsync(session, "Undock", oid, new PyLong(session.GetInt("shipid")!.Value), PyBool.False);
        await rig.BindBeyonceAsync(session);
        Sequence.Of(session);
        return (rig, character, session);
    }

    private static SimDestroyed Destroyed(ClientSession session, long shipId, SimWreckKind? kind) =>
        new((ulong)session.SessionId, shipId, 0, Place, Facing, 47, 0, kind);

    [PgFact]
    public async Task A_pilot_whose_ship_is_destroyed_is_in_a_capsule_told_in_the_order_of_the_reference_and_the_wreck_is_put_in_space()
    {
        var (rig, c, session) = await InSpaceAsync();
        await using var _ = rig;
        var system = session.GetInt("solarsystemid")!.Value;

        rig.Sim.Say(Destroyed(session, c.ShipId, FrigateWreck));
        await Rig.UntilAsync(() => rig.Sim.Heard.OfType<SimPlaceWreck>().Any());

        var told = Sequence.Of(session, clear: false);
        var bound = told.TakeWhile(name => name == "OnMachoObjectDisconnect").Count();
        Assert.True(bound >= 1, string.Join(",", told));
        // three modules (their item changes come one at a time, in the order the rules did them) and the ship
        Assert.Equal(
            ["OnPodOrCorvetteAssembly", "OnMultiEvent", "OnItemsChanged", "OnItemsChanged", "OnItemsChanged", "OnItemsChanged", "$sessionchange", "OnShipCosmeticsChanged", "OnShipCosmeticsChanged", "OnCurrentShipSkinChange", "OnItemChange"],
            told.Skip(bound));

        var capsuleId = session.GetInt("shipid")!.Value;
        Assert.NotEqual(c.ShipId, capsuleId);
        var capsule = (await rig.Items.GetAsync(capsuleId))!;
        Assert.Equal((670, c.CharacterId, (long)system, 0), (capsule.TypeId, capsule.OwnerId, capsule.LocationId, capsule.Flag));
        Assert.Null(await rig.Items.GetAsync(c.ShipId));

        // the simulation is given the capsule where the ship was, then the wreck
        var respawn = rig.Sim.Heard.OfType<SimRespawn>().Single();
        Assert.Equal(((ulong)session.SessionId, capsuleId, 670), (respawn.SessionId, respawn.Ship.ShipId, respawn.Ship.TypeId));
        Assert.Equal(Place, respawn.Position);
        Assert.Equal(Facing, respawn.Direction);
        var wreck = rig.Sim.Heard.OfType<SimPlaceWreck>().Single();
        var wreckItem = (await rig.Items.GetAsync(wreck.WreckId))!;
        var character = (await rig.Characters.GetAsync(c.CharacterId))!;
        Assert.Equal((system, 26506, c.ShipId, 47.0), (wreck.SolarSystemId, wreckItem.TypeId, wreck.LauncherId, wreck.Radius));
        Assert.Equal(Place, wreck.Position);
        Assert.Equal(new SimCredit(c.CharacterId, character.CorporationId, character.AllianceId ?? 0, character.FactionId ?? 0), wreck.Credit);
        Assert.Equal(rig.Sim.Heard.IndexOf(respawn) + 1, rig.Sim.Heard.IndexOf(wreck));
        Assert.True(rig.Space.IsInSpace(session));
    }

    [PgFact]
    public async Task What_the_client_is_told_of_the_items_is_the_moves_into_the_wreck_and_the_destruction_of_the_rest_and_the_ship()
    {
        var (rig, c, session) = await InSpaceAsync();
        await using var _ = rig;
        var fitted = (await rig.Items.InLocationAsync(c.ShipId, c.CharacterId)).OrderBy(i => i.Id).ToList();

        rig.Sim.Say(Destroyed(session, c.ShipId, FrigateWreck));
        await Rig.UntilAsync(() => rig.Sim.Heard.OfType<SimPlaceWreck>().Any());
        var wreckId = rig.Sim.Heard.OfType<SimPlaceWreck>().Single().WreckId;

        var changes = Events.Of(session).Where(e => e.Name == "OnItemsChanged").ToList();
        Assert.Equal(4, changes.Count);
        var rows = changes.Select(e => (Row: (PyPackedRow)((PyList)e.Args[0]).Items.Single(), From: ((PyDict)e.Args[1]).Entries.ToDictionary(x => Events.Number(x.Key), x => Events.Number(x.Value)))).ToList();
        // each of the ship's modules once, then the ship itself, in the junkyard
        Assert.Equal(fitted.Select(i => i.Id).Order(), rows.Take(3).Select(r => Events.Number(r.Row.Values[0])).Order());
        foreach (var (row, from) in rows.Take(3))
        {
            var location = Events.Number(row.Values[3]);
            var flag = Events.Number(row.Values[4]);
            Assert.Equal(c.ShipId, from[3]);
            if (location == wreckId)
            {
                Assert.Equal((long)ItemFlags.Hangar, flag);
                Assert.True(from.ContainsKey(4));
            }
            else
            {
                Assert.Equal(6L, location);
                Assert.False(from.ContainsKey(4));
            }
        }
        var ship = rows[3];
        Assert.Equal((c.ShipId, 6L), (Events.Number(ship.Row.Values[0]), Events.Number(ship.Row.Values[3])));
        Assert.Equal((long)session.GetInt("solarsystemid")!.Value, ship.From[3]);
    }

    [PgFact]
    public async Task The_client_is_told_which_capsule_it_is_in_and_the_capsule_has_no_undocking_protection_to_end()
    {
        var (rig, c, session) = await InSpaceAsync();
        await using var _ = rig;
        rig.Sim.Say(Destroyed(session, c.ShipId, FrigateWreck));
        await Rig.UntilAsync(() => rig.Sim.Heard.OfType<SimPlaceWreck>().Any());
        var assembly = Events.Of(session).Single(e => e.Name == "OnPodOrCorvetteAssembly");
        Assert.Equal(("charid", session.GetInt("shipid"), 670L), (assembly.IdType, Events.Number(assembly.Args[0]), Events.Number(assembly.Args[1])));

        // the first thing the capsule does does not end a protection it never had
        rig.Sim.Say(new SimAccepted((ulong)session.SessionId, SimCommands.Stop, 0));
        await Task.Delay(200);
        Assert.DoesNotContain("OnInvulnCancelled", Sequence.Of(session));
    }

    /// <summary>What the wreck is told to be: empty when nothing of the ship was moved into it.</summary>
    [PgFact]
    public async Task A_wreck_is_empty_when_nothing_dropped_into_it_and_not_when_something_did()
    {
        // a ship with nothing in it
        var (rig, c, session) = await InSpaceAsync();
        await using var _ = rig;
        await using (var connection = await rig.Source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            foreach (var module in await rig.Items.InLocationAsync(c.ShipId, c.CharacterId))
            {
                await rig.Items.DeleteAsync(connection, transaction, module.Id);
            }
            await transaction.CommitAsync();
        }
        rig.Sim.Say(Destroyed(session, c.ShipId, FrigateWreck));
        await Rig.UntilAsync(() => rig.Sim.Heard.OfType<SimPlaceWreck>().Any());
        Assert.True(rig.Sim.Heard.OfType<SimPlaceWreck>().Single().IsEmpty);

        // a ship with something in its hold that the seed of its death drops
        var (rig2, c2, session2) = await InSpaceAsync();
        await using var _2 = rig2;
        var seed = $"ship:{c2.ShipId}:{Rig.ClockMs}";
        long item = 0;
        for (var tries = 0; tries < 200 && (item == 0 || !DeathOutcome.HashToDrop(seed, item, 0)); tries++)
        {
            item = await rig2.GiveCargoAsync(c2.CharacterId, c2.ShipId);
        }
        Assert.True(DeathOutcome.HashToDrop(seed, item, 0));
        rig2.Sim.Say(Destroyed(session2, c2.ShipId, FrigateWreck));
        await Rig.UntilAsync(() => rig2.Sim.Heard.OfType<SimPlaceWreck>().Any());
        Assert.False(rig2.Sim.Heard.OfType<SimPlaceWreck>().Single().IsEmpty);
    }

    [PgFact]
    public async Task A_ship_the_simulation_destroys_twice_or_that_is_not_the_pilots_any_more_is_not_destroyed_again()
    {
        var (rig, c, session) = await InSpaceAsync();
        await using var _ = rig;
        rig.Sim.Say(Destroyed(session, c.ShipId, FrigateWreck));
        await Rig.UntilAsync(() => rig.Sim.Heard.OfType<SimPlaceWreck>().Any());
        var capsule = session.GetInt("shipid")!.Value;

        // the old ship's death again: the pilot is in the capsule and nothing more happens
        rig.Sim.Say(Destroyed(session, c.ShipId, FrigateWreck));
        await Task.Delay(300);
        Assert.Equal(capsule, session.GetInt("shipid"));
        Assert.Single(rig.Sim.Heard.OfType<SimPlaceWreck>());
        Assert.Single(rig.Sim.Heard.OfType<SimRespawn>());
    }

    [PgFact]
    public async Task With_no_kind_of_wreck_nothing_is_left_and_everything_the_ship_held_is_destroyed()
    {
        var (rig, c, session) = await InSpaceAsync();
        await using var _ = rig;
        rig.Sim.Say(Destroyed(session, c.ShipId, null));
        await Rig.UntilAsync(() => rig.Sim.Heard.OfType<SimRespawn>().Any());
        await Task.Delay(200);
        Assert.Empty(rig.Sim.Heard.OfType<SimPlaceWreck>());
        Assert.DoesNotContain("OnItemChange", Sequence.Of(session));
    }

    [PgFact]
    public async Task A_pilot_the_simulation_will_not_give_a_capsule_is_not_in_space_and_its_death_is_still_done()
    {
        var (rig, c, session) = await InSpaceAsync();
        await using var _ = rig;
        rig.Sim.RespawnAnswer = "NOT_DESTROYED 1";
        rig.Sim.Say(Destroyed(session, c.ShipId, FrigateWreck));
        await Rig.UntilAsync(() => session.GetInt("shipid") != c.ShipId);
        await Task.Delay(300);
        Assert.False(rig.Space.IsInSpace(session));
        Assert.Contains(rig.Log, line => line.Contains("did not take the capsule"));
        Assert.Contains(rig.Sim.Heard.OfType<SimLeave>(), leave => leave.SessionId == (ulong)session.SessionId);
    }

    [PgFact]
    public async Task A_session_that_closes_between_the_death_and_the_capsule_is_taken_out_of_the_ballpark()
    {
        var (rig, c, session) = await InSpaceAsync();
        await using var _ = rig;
        // the simulation is slow to answer: the death is being told when the client goes away
        var gate = new TaskCompletionSource();
        rig.Sim.RespawnGate = gate;
        rig.Sim.Say(Destroyed(session, c.ShipId, FrigateWreck));
        await Rig.UntilAsync(() => rig.Sim.Heard.OfType<SimRespawn>().Any());
        session.Close();
        gate.SetResult();
        await Task.Delay(300);
        Assert.Contains(rig.Sim.Heard.OfType<SimLeave>(), leave => leave.SessionId == (ulong)session.SessionId);
    }

    [PgFact]
    public async Task Self_destruction_asks_the_simulation_and_answers_with_the_wreck_and_the_capsule_when_the_death_is_done()
    {
        var (rig, c, session) = await InSpaceAsync();
        await using var _ = rig;

        var asked = rig.Space.SelfDestructAsync(session, CancellationToken.None);
        await Rig.UntilAsync(() => rig.Sim.Heard.OfType<SimDestroy>().Any());
        Assert.Equal(c.ShipId, rig.Sim.Heard.OfType<SimDestroy>().Single().ShipId);
        Assert.False(asked.IsCompleted);
        rig.Sim.Say(Destroyed(session, c.ShipId, FrigateWreck));

        var done = (await asked)!;
        Assert.Equal(c.ShipId, done.ShipId);
        Assert.Equal(("Capsule", session.GetInt("shipid")), (done.CapsuleName, done.CapsuleId));
        Assert.Equal(("Caldari Frigate Wreck", rig.Sim.Heard.OfType<SimPlaceWreck>().Single().WreckId), (done.WreckName, done.WreckId));
    }

    [PgFact]
    public async Task A_pilot_in_a_capsule_or_not_in_space_cannot_self_destruct()
    {
        var (rig, c, session) = await InSpaceAsync();
        await using var _ = rig;
        rig.Sim.Say(Destroyed(session, c.ShipId, FrigateWreck));
        await Rig.UntilAsync(() => rig.Sim.Heard.OfType<SimPlaceWreck>().Any());
        rig.Sim.Heard.Clear();
        Assert.Null(await rig.Space.SelfDestructAsync(session, CancellationToken.None));
        Assert.Empty(rig.Sim.Heard.OfType<SimDestroy>());

        var docked = await rig.SessionAsync(await rig.NewCharacterAsync("Docked One", 2), 2);
        Assert.Null(await rig.Space.SelfDestructAsync(docked, CancellationToken.None));
    }
}
