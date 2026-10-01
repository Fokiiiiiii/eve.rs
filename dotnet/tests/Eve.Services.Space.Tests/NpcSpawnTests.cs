using Eve.Space;

namespace Eve.Services.Space.Tests;

/// <summary><c>/npc</c>: NPCs of the authored profiles and pools come into space around the pilot, armed with what their hulls carry, running the simulation's belt rat tree.</summary>
public class NpcSpawnTests
{
    private static async Task<(Rig Rig, Eve.Characters.Created Character, ClientSession Session)> InSpaceAsync()
    {
        var rig = await Rig.CreateAsync();
        var character = await rig.NewCharacterAsync();
        var session = await rig.SessionAsync(character);
        var oid = await rig.BindShipAsync(session);
        await rig.ShipCallAsync(session, "Undock", oid, new Eve.Marshal.PyLong(session.GetInt("shipid")!.Value), Eve.Marshal.PyBool.False);
        return (rig, character, session);
    }

    [PgFact]
    public async Task A_named_profile_comes_armed_with_the_weapon_of_its_hull_and_runs_the_belt_rat_tree()
    {
        var (rig, character, session) = await InSpaceAsync();
        await using var _ = rig;

        var report = await rig.Space.SpawnNpcsAsync(session, "Blood Visionary", 2, CancellationToken.None);

        Assert.Null(report.Error);
        Assert.Equal("Blood Visionary", report.SelectionName);
        Assert.Equal([("Blood Visionary", 2)], report.Composition);
        Assert.Equal(2, report.Spawned);
        var spawns = rig.Sim.Heard.OfType<SimSpawn>().ToList();
        Assert.Equal(2, spawns.Count);
        Assert.All(spawns, spawn =>
        {
            Assert.Equal(character.ShipId, spawn.AnchorShipId);
            Assert.Equal("belt rat", spawn.Behavior);
            Assert.False(spawn.Identity.Hull);
            Assert.Null(spawn.Identity.Wreck);
            Assert.Equal((-11L, 11L), (spawn.Identity.HostileResponseThreshold, spawn.Identity.FriendlyResponseThreshold));
            Assert.Equal(0, spawn.Ship.CharacterId);
            Assert.Equal(23245, spawn.Ship.TypeId);
            // what the profile says of whose it is
            Assert.Equal((1000134L, 1000134, 0, 500012, -5.0), (spawn.Ship.OwnerId, spawn.Ship.CorporationId, spawn.Ship.AllianceId, spawn.Ship.WarFactionId, spawn.Ship.SecurityStatus));
            Assert.Empty(spawn.Ship.Modules);
            // the hull's own weapon, as the reference's npcEquipment makes it of type 23245 (oracle/fixtures/npc/hull_weapons.json)
            var weapon = Assert.Single(spawn.Identity.Weapons);
            Assert.Equal(spawn.Ship.ShipId, weapon.ModuleId);
            Assert.Equal(10, weapon.EffectId);
            Assert.Equal(23245, weapon.WeaponTypeId);
            Assert.Equal("effects.Laser", weapon.Fx);
            Assert.Equal(2_500, weapon.CycleMs);
            Assert.Equal(7_500, weapon.OptimalRange);
            Assert.Equal(2_000, weapon.Falloff);
            Assert.Equal(0.197, weapon.TrackingSpeed);
            Assert.Equal(40, weapon.SignatureResolution);
            Assert.Equal([23.541667, 18.833333, 0, 0], weapon.RawDamage);
            Assert.True(spawn.Identity.AggressionRange > 0);
            // it locks as far as it attacks, and holds at least one lock
            Assert.True(spawn.Ship.MaxTargetRange >= spawn.Identity.AggressionRange);
            Assert.True(spawn.Ship.MaxLockedTargets >= 1);
            Assert.Equal(0, spawn.Velocity.Sum(Math.Abs));
            Assert.Equal(1.0, Math.Sqrt(spawn.Direction.Sum(x => x * x)), 6);
        });
        Assert.All(spawns, spawn => Assert.True(spawn.Ship.ShipId > 4_100_000_000_000_000 && spawn.Ship.ShipId < 4_200_000_000_000_000));
        Assert.Equal(2, spawns.Select(s => s.Ship.ShipId).Distinct().Count());
        Assert.Equal(2, spawns.Select(s => s.RequestId).Distinct().Count());
    }

    [PgFact]
    public async Task NPCs_come_at_the_distance_of_their_profile_spread_by_the_size_of_the_batch_and_stepped_one_after_another()
    {
        var (rig, _, session) = await InSpaceAsync();
        await using var _ = rig;

        await rig.Space.SpawnNpcsAsync(session, "Blood Visionary", 12, CancellationToken.None);

        var spawns = rig.Sim.Heard.OfType<SimSpawn>().ToList();
        Assert.Equal(12, spawns.Count);
        for (var index = 0; index < spawns.Count; index++)
        {
            var distance = Math.Sqrt(spawns[index].Position.Sum(x => x * x));
            // 8 km from the profile, plus or minus 6 km (800 m and 600 m for each of 12, at most 6 km), plus 1.25 km for each one before it, at most 12 km
            var step = Math.Min(12_000, index * 1_250);
            Assert.InRange(distance, Math.Max(1_000, 8_000 - 6_000 + step) - 1, 8_000 + 6_000 + step + 1);
        }
        Assert.Equal(12, spawns.Select(s => Math.Sqrt(s.Position.Sum(x => x * x))).Distinct().Count());
    }

    [Fact]
    public void The_distance_is_what_the_reference_makes_of_a_profile_a_random_number_a_batch_and_a_place_in_it()
    {
        var profile = new Eve.Npc.NpcProfile("p", "P", [], "npc", 1, 0, 0, 0, "", "", "", 0, 0, 8_000);
        Assert.Equal(8_000, SpaceSessions.NpcSpawnDistance(profile, 0, 1, 0.5));
        // one in a batch of one: 800 + 600 = 1400 either way
        Assert.Equal(8_000 - 1_400, SpaceSessions.NpcSpawnDistance(profile, 0, 1, 0), 6);
        Assert.Equal(8_000 + 1_400, SpaceSessions.NpcSpawnDistance(profile, 0, 1, 1), 6);
        // the window stops growing at 6 km (a batch of 9 or more), the step at 12 km
        Assert.Equal(8_000 + 6_000, SpaceSessions.NpcSpawnDistance(profile, 0, 9, 1), 6);
        Assert.Equal(8_000 + 6_000, SpaceSessions.NpcSpawnDistance(profile, 0, 50, 1), 6);
        Assert.Equal(8_000 + 3_750, SpaceSessions.NpcSpawnDistance(profile, 3, 1, 0.5), 6);
        Assert.Equal(8_000 + 12_000, SpaceSessions.NpcSpawnDistance(profile, 30, 1, 0.5), 6);
        // never nearer than a kilometre, and a profile that says less than that is a kilometre
        Assert.Equal(1_000, SpaceSessions.NpcSpawnDistance(profile with { SpawnDistanceMeters = 500 }, 0, 1, 0.5));
        Assert.Equal(1_000, SpaceSessions.NpcSpawnDistance(profile with { SpawnDistanceMeters = 1_200 }, 0, 50, 0));
    }

    [PgFact]
    public async Task Nothing_named_rolls_the_default_pool_and_every_one_is_armed()
    {
        var (rig, _, session) = await InSpaceAsync();
        await using var _ = rig;

        var report = await rig.Space.SpawnNpcsAsync(session, "", 30, CancellationToken.None);

        Assert.Null(report.Error);
        Assert.Equal("Standard Pirate Hostiles", report.SelectionName);
        Assert.Equal(30, report.Spawned);
        Assert.Equal(30, report.Requested);
        var spawns = rig.Sim.Heard.OfType<SimSpawn>().ToList();
        Assert.Equal(30, spawns.Count);
        // the pool has hundreds of profiles: thirty rolls are not all the same
        Assert.True(spawns.Select(s => s.Ship.TypeId).Distinct().Count() > 3);
        Assert.All(spawns, spawn =>
        {
            Assert.True(spawn.Ship.Vitals!.ShieldCapacity + spawn.Ship.Vitals.ArmorHp + spawn.Ship.Vitals.StructureHp > 0);
            Assert.All(spawn.Identity.Weapons, weapon =>
            {
                Assert.Equal(spawn.Ship.ShipId, weapon.ModuleId);
                Assert.True(weapon.CycleMs >= 1 && weapon.RawDamage.Sum() > 0, $"type {spawn.Ship.TypeId}");
            });
        });
        // the hulls that fire missiles have no weapon here yet (8 % of the pool), and the report says how many
        var unarmed = spawns.Count(spawn => spawn.Identity.Weapons.Count == 0);
        Assert.Equal(unarmed, report.Unarmed);
        Assert.InRange(unarmed, 0, 15);
        Assert.Equal(30, report.Composition.Sum(part => part.Count));
        Assert.Equal(report.Composition.Count, report.Composition.Select(part => part.Name).Distinct().Count());
    }

    [PgFact]
    public async Task A_hull_that_fires_missiles_comes_without_a_weapon_and_the_report_says_so()
    {
        var (rig, _, session) = await InSpaceAsync();
        await using var _ = rig;

        // Dire Guristas Invader (type 13041) has no turret effect: the reference arms it with missile launchers, which the simulation cannot fire yet
        var report = await rig.Space.SpawnNpcsAsync(session, "parity_dire_guristas_invader", 2, CancellationToken.None);

        Assert.Null(report.Error);
        Assert.Equal(2, report.Spawned);
        Assert.Equal(2, report.Unarmed);
        Assert.All(rig.Sim.Heard.OfType<SimSpawn>(), spawn =>
        {
            Assert.Empty(spawn.Identity.Weapons);
            Assert.Equal(5_000, spawn.Identity.AggressionRange);
            // the type has no lock range (attribute 76) and the sensor range of the NPC is what it attacks at
            Assert.Equal(5_000, spawn.Ship.MaxTargetRange);
        });
    }

    [PgFact]
    public async Task What_no_profile_or_pool_has_is_not_found_and_what_several_have_is_ambiguous_with_what_was_close()
    {
        var (rig, _, session) = await InSpaceAsync();
        await using var _ = rig;

        var missing = await rig.Space.SpawnNpcsAsync(session, "no such rat at all", 1, CancellationToken.None);
        var ambiguous = await rig.Space.SpawnNpcsAsync(session, "swarm", 1, CancellationToken.None);

        Assert.Equal("PROFILE_NOT_FOUND", missing.Error);
        Assert.Empty(missing.Suggestions);
        Assert.Equal("PROFILE_AMBIGUOUS", ambiguous.Error);
        Assert.Equal(8, ambiguous.Suggestions.Count);
        Assert.Matches(@"^.+ \(.+\)$", ambiguous.Suggestions[0]);
        Assert.Empty(rig.Sim.Heard.OfType<SimSpawn>());
    }

    [PgFact]
    public async Task A_profile_whose_hull_has_no_hit_points_is_not_made()
    {
        var (rig, _, session) = await InSpaceAsync();
        await using var _ = rig;

        // entity type 33352 has no shield, armor or structure in the static data
        var report = await rig.Space.SpawnNpcsAsync(session, "parity_serpentis_cruiser", 1, CancellationToken.None);

        Assert.Equal("PROFILE_HULL_NOT_DAMAGEABLE", report.Error);
        Assert.Empty(rig.Sim.Heard.OfType<SimSpawn>());
    }

    [PgFact]
    public async Task A_session_that_is_not_in_space_gets_nothing()
    {
        var rig = await Rig.CreateAsync();
        await using var _ = rig;
        var docked = await rig.SessionAsync(await rig.NewCharacterAsync());

        var report = await rig.Space.SpawnNpcsAsync(docked, "", 3, CancellationToken.None);

        Assert.Equal("NOT_IN_SPACE", report.Error);
        Assert.Empty(rig.Sim.Heard);
    }

    [PgFact]
    public async Task When_the_simulation_refuses_the_first_NPC_none_is_made()
    {
        var (rig, _, session) = await InSpaceAsync();
        await using var _ = rig;
        rig.Sim.SpawnAnswer = new SimSpawnReply(0, null, "NO_SCENE 30100032");

        var report = await rig.Space.SpawnNpcsAsync(session, "Blood Visionary", 3, CancellationToken.None);

        Assert.Equal("NPC_NATIVE_SPAWN_FAILED", report.Error);
        // the rest are not tried once one is refused
        Assert.Single(rig.Sim.Heard.OfType<SimSpawn>());
    }

    [PgFact]
    public async Task A_simulation_that_cannot_be_reached_is_a_message_for_the_client()
    {
        var (rig, _, session) = await InSpaceAsync();
        await using var _ = rig;
        rig.Sim.IsConnected = false;
        rig.Sim.SpawnUnavailable = true;

        await Assert.ThrowsAnyAsync<Exception>(() => rig.Space.SpawnNpcsAsync(session, "Blood Visionary", 1, CancellationToken.None));
    }
}
