using Eve.Inventory;
using Eve.Macho;
using Eve.Marshal;
using Eve.Space;

namespace Eve.Services.Space.Tests;

/// <summary>
/// An NPC that is destroyed leaves a wreck the host makes: an item in the solar system that holds what its loot table gave, put in space where the ship was with the pilot who dealt the
/// last blow as the one who may take it.
/// </summary>
public class NpcLootHostTests
{
    private static readonly SimWreckKind FrigateWreck = new(26506, "Caldari Frigate Wreck", 3117);
    private static readonly double[] Place = [1000.5, -20.25, 300];
    private static readonly double[] Facing = [0, 1, 0];

    private static async Task<(Rig Rig, Eve.Characters.Created Character, ClientSession Session)> InSpaceAsync(bool npcLoot = true)
    {
        var rig = await Rig.CreateAsync(npcLoot: npcLoot);
        var character = await rig.NewCharacterAsync();
        var session = await rig.SessionAsync(character);
        var oid = await rig.BindShipAsync(session);
        await rig.ShipCallAsync(session, "Undock", oid, new PyLong(session.GetInt("shipid")!.Value), PyBool.False);
        await rig.BindBeyonceAsync(session);
        Events.Of(session);
        return (rig, character, session);
    }

    private static async Task<SimSpawn> SpawnAsync(Rig rig, ClientSession session)
    {
        var report = await rig.Space.SpawnNpcsAsync(session, "Blood Visionary", 1, CancellationToken.None);
        Assert.Null(report.Error);
        return rig.Sim.Heard.OfType<SimSpawn>().Last();
    }

    /// <summary>The scene made no wreck (its id is 0): what the host is told of an NPC that asked for its wreck to be the host\'s.</summary>
    private static SimDestroyed Destroyed(SimSpawn spawn, long killerShip, long wreck = 0) =>
        new(1UL << 62, spawn.Ship.ShipId, wreck, Place, Facing, 47, killerShip, FrigateWreck);

    [PgFact]
    public async Task An_npc_asks_for_its_wreck_to_be_the_hosts_only_when_the_host_has_loot_to_put_in_it()
    {
        var (rig, _, session) = await InSpaceAsync();
        await using var _ = rig;
        Assert.True((await SpawnAsync(rig, session)).Identity.HostWreck);

        var (plain, _, plainSession) = await InSpaceAsync(npcLoot: false);
        await using var __ = plain;
        Assert.False((await SpawnAsync(plain, plainSession)).Identity.HostWreck);
    }

    [PgFact]
    public async Task The_wreck_is_an_item_of_the_pilot_who_dealt_the_last_blow_with_what_the_table_gave_and_it_is_put_in_space_where_the_ship_was()
    {
        var (rig, c, session) = await InSpaceAsync();
        await using var _ = rig;
        var system = session.GetInt("solarsystemid")!.Value;
        var spawn = await SpawnAsync(rig, session);

        rig.Sim.Say(Destroyed(spawn, c.ShipId));
        await Rig.UntilAsync(() => rig.Sim.Heard.OfType<SimPlaceWreck>().Any());

        var placed = rig.Sim.Heard.OfType<SimPlaceWreck>().Single();
        var wreck = (await rig.Items.GetAsync(placed.WreckId))!;
        Assert.Equal((FrigateWreck.TypeId, c.CharacterId, (long)system, 0, true), (wreck.TypeId, wreck.OwnerId, wreck.LocationId, wreck.Flag, wreck.Singleton));
        var held = Assert.Single(await rig.Items.InContainerAsync(wreck.Id));
        // the dice come out 0: the first type of the pool, the least of the quantities
        Assert.Equal((222, c.CharacterId, ItemFlags.Hangar, 7L, false), (held.TypeId, held.OwnerId, held.Flag, held.Quantity, held.Singleton));

        var character = (await rig.Characters.GetAsync(c.CharacterId))!;
        Assert.Equal((system, 186, spawn.Ship.ShipId, false), (placed.SolarSystemId, placed.GroupId, placed.LauncherId, placed.IsEmpty));
        Assert.Equal(FrigateWreck, placed.Kind);
        Assert.Equal(new SimCredit(c.CharacterId, character.CorporationId, character.AllianceId ?? 0, character.FactionId ?? 0), placed.Credit);
        Assert.Equal(Place, placed.Position);
        Assert.Equal(Facing, placed.Direction);
        Assert.Equal(47.0, placed.Radius);
        // it is kept where it lies, to be put back with the scene and to go when its time is up: the host saves it in the background after
        // telling the scene, so the test waits for the row rather than racing the save
        await Rig.UntilAsync(() => rig.SpaceItems.GetAsync(placed.WreckId).Result is not null);
        Assert.NotNull(await rig.SpaceItems.GetAsync(placed.WreckId));
    }

    [PgFact]
    public async Task A_table_that_gives_nothing_leaves_an_empty_wreck_and_the_pilot_is_still_paid_the_bounty()
    {
        var (rig, c, session) = await InSpaceAsync();
        await using var _ = rig;
        rig.LootTable = null;
        var spawn = await SpawnAsync(rig, session);

        rig.Sim.Say(Destroyed(spawn, c.ShipId));
        await Rig.UntilAsync(() => rig.Sim.Heard.OfType<SimPlaceWreck>().Any() && rig.Bounties.PendingAsync().Result.Count == 1);

        var placed = rig.Sim.Heard.OfType<SimPlaceWreck>().Single();
        Assert.True(placed.IsEmpty);
        Assert.Empty(await rig.Items.InContainerAsync(placed.WreckId));
        Assert.Contains(Events.Of(session), e => e.Name == "OnBountyAddedToPayout");
    }

    [PgFact]
    public async Task A_wreck_nobody_may_claim_belongs_to_the_npcs_corporation_and_has_no_credit()
    {
        var (rig, _, session) = await InSpaceAsync();
        await using var _ = rig;
        var spawn = await SpawnAsync(rig, session);

        rig.Sim.Say(Destroyed(spawn, 0));
        await Rig.UntilAsync(() => rig.Sim.Heard.OfType<SimPlaceWreck>().Any());

        var placed = rig.Sim.Heard.OfType<SimPlaceWreck>().Single();
        Assert.Equal(new SimCredit(0, 0, 0, 0), placed.Credit);
        Assert.Equal(spawn.Ship.CorporationId, (int)(await rig.Items.GetAsync(placed.WreckId))!.OwnerId);
    }

    [PgFact]
    public async Task The_types_the_loot_is_drawn_from_are_read_once_and_a_read_that_failed_is_made_again_by_the_next_kill()
    {
        await using var rig = await Rig.CreateAsync();
        var reads = 0;
        var wrecks = new NpcWrecks(
            _ => rig.LootTable,
            async () =>
            {
                await Task.Yield();
                return ++reads == 1 ? throw new IOException("the database was away") : new Eve.Npc.NpcLoot(Rig.LootPool);
            },
            rig.Ships, () => 0);

        await Assert.ThrowsAsync<IOException>(() => wrecks.LeaveAsync(FrigateWreck.TypeId, "t", 1_000_125, 30100032));
        var made = await wrecks.LeaveAsync(FrigateWreck.TypeId, "t", 1_000_125, 30100032);
        await wrecks.LeaveAsync(FrigateWreck.TypeId, "t", 1_000_125, 30100032);

        Assert.Single(made.Contents);
        Assert.Equal(2, reads);
    }

    [PgFact]
    public async Task A_wreck_the_scene_made_is_left_as_it_is()
    {
        var (rig, c, session) = await InSpaceAsync();
        await using var _ = rig;
        var spawn = await SpawnAsync(rig, session);

        rig.Sim.Say(Destroyed(spawn, c.ShipId, wreck: 4_300_000_000_000_001));
        await Rig.UntilAsync(() => rig.Bounties.PendingAsync().Result.Count == 1);

        Assert.Empty(rig.Sim.Heard.OfType<SimPlaceWreck>());
    }
}
