using Eve.Space;

namespace Eve.Services.Space.Tests;

/// <summary><c>/deathtest</c>: hulls nobody flies come into space around the pilot and are destroyed a moment later.</summary>
public class HullTests
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
    public async Task Hulls_of_the_pilots_ship_type_are_spawned_from_the_pilots_ship_at_places_3_to_20_km_apart_and_destroyed_after_two_seconds()
    {
        var (rig, character, session) = await InSpaceAsync();
        await using var _ = rig;

        var field = await rig.Space.DeathTestAsync(session, null, 4, CancellationToken.None);

        Assert.NotNull(field);
        Assert.Equal(("Ibis", 4, 20_000.0, 2.0), (field.ShipName, field.Spawned, field.RadiusMeters, field.DelaySeconds));
        var spawns = rig.Sim.Heard.OfType<SimSpawn>().ToList();
        Assert.Equal(4, spawns.Count);
        Assert.All(spawns, spawn =>
        {
            Assert.Equal(character.ShipId, spawn.AnchorShipId);
            Assert.Equal("", spawn.Behavior);
            Assert.True(spawn.Identity.Hull);
            var distance = Math.Sqrt(spawn.Position.Sum(x => x * x));
            Assert.InRange(distance, 3_000 - 1, 20_000 + 1);
            Assert.Equal(0, spawn.Ship.CharacterId);
            Assert.Equal("Ibis", spawn.Ship.Name);
            Assert.Equal(601, spawn.Ship.TypeId);
            Assert.Equal(47, spawn.Ship.Radius);
            Assert.Equal(1_163_000, spawn.Ship.Mass);
            Assert.Empty(spawn.Ship.Modules);
        });
        Assert.Equal(4, spawns.Select(s => s.Ship.ShipId).Distinct().Count());
        Assert.All(spawns, spawn => Assert.True(spawn.Ship.ShipId >= 4_200_000_000_000_000));
        for (var a = 0; a < spawns.Count; a++)
        {
            for (var b = a + 1; b < spawns.Count; b++)
            {
                var apart = Math.Sqrt(Enumerable.Range(0, 3).Sum(i => Math.Pow(spawns[a].Position[i] - spawns[b].Position[i], 2)));
                Assert.True(apart >= 1_500, $"{apart}");
            }
        }
        Assert.Empty(rig.Sim.Heard.OfType<SimDestroy>());
        await Rig.UntilAsync(() => rig.Sim.Heard.OfType<SimDestroy>().Count() == 4);
        Assert.Equal(spawns.Select(s => s.Ship.ShipId).Order(), rig.Sim.Heard.OfType<SimDestroy>().Select(d => d.ShipId).Order());
        // the wrecks are the caller's to loot
        var caller = (await rig.Ships.LoadAsync(character.CharacterId, CancellationToken.None))!.Character;
        Assert.All(rig.Sim.Heard.OfType<SimDestroy>(), destroy =>
            Assert.Equal(new SimCredit(character.CharacterId, caller.CorporationId, caller.AllianceId ?? 0, caller.FactionId ?? 0), destroy.Credit));
        Assert.NotEqual(0, caller.CorporationId);
    }

    [PgFact]
    public async Task A_named_ship_type_is_spawned_instead_and_a_hull_the_simulation_refuses_is_not_counted()
    {
        var (rig, _, session) = await InSpaceAsync();
        await using var _ = rig;

        var field = await rig.Space.DeathTestAsync(session, 596, 1, CancellationToken.None);
        Assert.Equal("Impairor", field!.ShipName);
        Assert.Equal(596, rig.Sim.Heard.OfType<SimSpawn>().Single().Ship.TypeId);

        rig.Sim.SpawnAnswer = new SimSpawnReply(0, null, "NO_SCENE 30100032");
        Assert.Null(await rig.Space.DeathTestAsync(session, null, 2, CancellationToken.None));
    }

    [PgFact]
    public async Task A_session_that_is_not_in_space_or_a_type_that_is_not_there_spawns_nothing()
    {
        var rig = await Rig.CreateAsync();
        await using var _ = rig;
        var character = await rig.NewCharacterAsync();
        var docked = await rig.SessionAsync(character);
        Assert.Null(await rig.Space.DeathTestAsync(docked, null, 3, CancellationToken.None));
        Assert.Empty(rig.Sim.Heard);

        var (other, _, session) = await InSpaceAsync();
        await using var _2 = other;
        Assert.Null(await other.Space.DeathTestAsync(session, 1, 3, CancellationToken.None));
        Assert.Empty(other.Sim.Heard.OfType<SimSpawn>());
    }
}
