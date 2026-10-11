using System.Text.Json;
using Eve.Hacking;

namespace Eve.Hacking.Tests;

public sealed class HackingGameTests
{
    private const int Flipped = HackingWire.EventTileFlipped;
    private const int Attack = HackingWire.EventAttack;
    private const int Killed = HackingWire.EventObjectKilled;
    private const int GameWon = HackingWire.EventGameWon;
    private const int GameLost = HackingWire.EventGameLost;
    private const int Ack = HackingWire.EventAck;
    private const int Distance = HackingWire.EventDistanceIndicator;
    private const int UeInUse = HackingWire.EventUeInUse;
    private const int UeRemoved = HackingWire.EventUeRemoved;
    private const int PickedUp = HackingWire.EventUePickedUp;
    private const int CacheOpen = HackingWire.EventDataCacheOpen;
    private const int KernelRot = HackingWire.EventKernelRot;
    private const int SelfRepair = HackingWire.EventSelfRepair;
    private const int Vector = HackingWire.EventSecondaryVector;
    private const int ShieldHit = HackingWire.EventPolymorphicShieldHit;
    private const int Suppressor = HackingWire.EventVirusSuppressor;
    private const int Healed = HackingWire.EventRestorationHeal;

    // The start is (0,4). Its explorable neighbours are (0,3), (0,5) and (1,4); (2,4) is two steps away.
    private static readonly Coord Left = new(0, 3);
    private static readonly Coord Down = new(0, 5);
    private static readonly Coord East = new(1, 4);
    private static readonly Coord Far = new(2, 4);

    private static HackingAttempt Attempt(int coherence = 40, int strength = 20, int slots = 3, int seed = 1, int difficulty = 1) =>
        new(9001, 22329, HackingWire.GameTypeHacking, difficulty, coherence, strength, slots, seed);

    private static HackingGame Blank(int coherence = 40, int strength = 20, int slots = 3) =>
        new(Attempt(coherence, strength, slots), HackingBoard.Blank());

    private static Tile Put(HackingGame game, Coord c, int type, int subtype = HackingWire.SubtypeNone, int coherence = 0, int strength = 0)
    {
        var tile = game.Board.Tiles[c];
        tile.Type = type;
        tile.Subtype = subtype;
        tile.Coherence = coherence;
        tile.Strength = strength;
        tile.Hidden = true;
        tile.Blocked = false;
        return tile;
    }

    private static int[] Ids(IEnumerable<HackingEvent> events) => events.Select(e => e.EventId).ToArray();

    private static HackingEvent Only(IEnumerable<HackingEvent> events, int id) => events.Single(e => e.EventId == id);

    private static string Dump(IEnumerable<HackingEvent> events) => JsonSerializer.Serialize(events);

    [Fact]
    public void A_click_that_no_explored_node_reaches_is_acknowledged_and_changes_nothing()
    {
        var game = Blank();
        var before = game.Board.Tiles[Far].Hidden;

        var events = game.ClickTile(Far);

        Assert.Equal(new[] { Ack }, Ids(events));
        Assert.Equal(before, game.Board.Tiles[Far].Hidden);
        Assert.Equal(40, game.VirusCoherence);
    }

    [Fact]
    public void An_empty_node_reports_the_steps_to_the_nearest_resource()
    {
        var game = Blank();
        Put(game, new Coord(3, 4), HackingWire.TypeCore, HackingWire.SubtypeCoreLow, 10, 10);

        var events = game.ClickTile(East);

        Assert.Equal(new[] { Flipped, Distance }, Ids(events));
        Assert.Equal(2, Only(events, Distance).Data["distanceIndicator"]);
    }

    [Fact]
    public void A_firewall_takes_the_virus_strength_and_strikes_back_once()
    {
        var game = Blank(coherence: 40, strength: 20);
        var wall = Put(game, East, HackingWire.TypeDefenseSoftware, HackingWire.SubtypeDsFirewall, 60, 10);

        var events = game.ClickTile(East);

        Assert.Equal(new[] { Flipped, Attack, Attack }, Ids(events));
        Assert.Equal(40, wall.Coherence);
        Assert.Equal(30, game.VirusCoherence);
        Assert.True(wall.Blocked);
    }

    [Fact]
    public void A_blocked_defence_does_not_open_its_neighbours_for_exploration()
    {
        var game = Blank(coherence: 400, strength: 20);
        Put(game, East, HackingWire.TypeDefenseSoftware, HackingWire.SubtypeDsFirewall, 60, 10);
        game.ClickTile(East);

        Assert.Equal(new[] { Ack }, Ids(game.ClickTile(Far)));
    }

    [Fact]
    public void Destroying_a_defence_leaves_an_empty_node_that_opens_its_neighbours()
    {
        var game = Blank(coherence: 40, strength: 20);
        var wall = Put(game, East, HackingWire.TypeDefenseSoftware, HackingWire.SubtypeDsFirewall, 10, 10);

        var events = game.ClickTile(East);

        Assert.Equal(new[] { Flipped, Attack, Killed }, Ids(events));
        Assert.Equal(HackingWire.TypeSegment, wall.Type);
        Assert.False(wall.Blocked);
        Assert.Equal(new[] { Flipped, Distance }, Ids(game.ClickTile(Far)));
    }

    [Fact]
    public void The_virus_dies_to_a_hard_retaliation_and_the_attempt_is_lost()
    {
        var game = Blank(coherence: 40, strength: 20);
        Put(game, East, HackingWire.TypeDefenseSoftware, HackingWire.SubtypeDsAntiVirus, 100, 40);

        var events = game.ClickTile(East);

        Assert.Equal(new[] { Flipped, Attack, Attack, Killed, GameLost }, Ids(events));
        Assert.Equal(HackingOutcome.Lost, game.Outcome);
        Assert.Equal(new[] { Ack }, Ids(game.ClickTile(Down)));
    }

    [Fact]
    public void Destroying_the_core_wins_without_retaliation()
    {
        var game = Blank(coherence: 40, strength: 20);
        var core = Put(game, East, HackingWire.TypeCore, HackingWire.SubtypeCoreLow, 10, 10);

        var events = game.ClickTile(East);

        Assert.Equal(new[] { Flipped, Attack, Killed, GameWon }, Ids(events));
        Assert.Equal(HackingOutcome.Won, game.Outcome);
        Assert.False(core.Hidden);
        Assert.Equal(40, game.VirusCoherence);
    }

    [Fact]
    public void A_restoration_node_heals_a_defence_at_the_end_of_the_turn()
    {
        var game = Blank(coherence: 40, strength: 20);
        var node = Put(game, East, HackingWire.TypeDefenseSoftware, HackingWire.SubtypeDsRestorationNode, 30, 10);

        var events = game.ClickTile(East);

        Assert.Equal(new[] { Flipped, Attack, Attack, Healed }, Ids(events));
        Assert.Equal(30, node.Coherence);
        Assert.Equal(20, Only(events, Healed).Data["healingAmount"]);
    }

    [Fact]
    public void Self_repair_heals_the_virus_for_three_turns_and_then_leaves_its_slot()
    {
        var game = Blank(coherence: 40, strength: 20);
        game.Slots[0] = new UtilityItem(1, HackingWire.SubtypeUeSelfRepair);

        Assert.Equal(new[] { UeInUse }, Ids(game.UseOnVirus(0)));
        var first = game.ClickTile(Left);
        Assert.Equal(new[] { Flipped, Distance, SelfRepair }, Ids(first));
        Assert.Equal(2, Only(first, SelfRepair).Data["durationRemaining"]);
        Assert.Equal(new[] { Flipped, Distance, SelfRepair }, Ids(game.ClickTile(Down)));
        Assert.Equal(new[] { Flipped, Distance, SelfRepair, UeRemoved }, Ids(game.ClickTile(East)));

        Assert.InRange(game.VirusCoherence, 40 + 15, 40 + 30);
        Assert.Null(game.Slots[0]);
    }

    [Fact]
    public void Polymorphic_shield_nullifies_two_retaliations_and_then_the_third_hits_the_virus()
    {
        var game = Blank(coherence: 40, strength: 20);
        Put(game, Left, HackingWire.TypeDefenseSoftware, HackingWire.SubtypeDsFirewall, 100, 10);
        Put(game, Down, HackingWire.TypeDefenseSoftware, HackingWire.SubtypeDsFirewall, 100, 10);
        Put(game, East, HackingWire.TypeDefenseSoftware, HackingWire.SubtypeDsFirewall, 100, 10);
        game.Slots[0] = new UtilityItem(1, HackingWire.SubtypeUePolymorphicShield);
        game.UseOnVirus(0);

        Assert.Equal(new[] { Flipped, Attack, ShieldHit }, Ids(game.ClickTile(Left)));
        Assert.Equal(new[] { Flipped, Attack, ShieldHit, UeRemoved }, Ids(game.ClickTile(Down)));
        Assert.Equal(40, game.VirusCoherence);
        Assert.Null(game.Slots[0]);
        Assert.Equal(new[] { Flipped, Attack, Attack }, Ids(game.ClickTile(East)));
        Assert.Equal(30, game.VirusCoherence);
    }

    [Fact]
    public void Kernel_rot_halves_the_core_once_and_leaves_the_slot_empty()
    {
        var game = Blank();
        var core = Put(game, Far, HackingWire.TypeCore, HackingWire.SubtypeCoreLow, 100, 10);
        game.Slots[0] = new UtilityItem(1, HackingWire.SubtypeUeKernelRot);

        var events = game.UseOnTarget(0, Far);

        Assert.Equal(new[] { KernelRot, UeRemoved }, Ids(events));
        Assert.Equal(50, core.Coherence);
        Assert.Null(game.Slots[0]);
    }

    [Fact]
    public void A_utility_element_is_refused_on_a_plain_node_and_stays_in_its_slot()
    {
        var game = Blank();
        game.Slots[0] = new UtilityItem(1, HackingWire.SubtypeUeKernelRot);

        Assert.Equal(new[] { Ack }, Ids(game.UseOnTarget(0, East)));
        Assert.NotNull(game.Slots[0]);
    }

    [Fact]
    public void Secondary_vector_drains_its_target_for_three_turns()
    {
        var game = Blank(coherence: 400, strength: 20);
        var core = Put(game, Far, HackingWire.TypeCore, HackingWire.SubtypeCoreLow, 100, 10);
        game.Slots[0] = new UtilityItem(1, HackingWire.SubtypeUeSecondaryVector);

        Assert.Equal(new[] { UeInUse }, Ids(game.UseOnTarget(0, Far)));
        Assert.Equal(new[] { Flipped, Distance, Vector }, Ids(game.ClickTile(Left)));
        game.ClickTile(Down);
        Assert.Equal(new[] { Flipped, Distance, Vector, UeRemoved }, Ids(game.ClickTile(East)));

        Assert.Equal(100 - 3 * HackingGame.SecondaryVectorDamage, core.Coherence);
        Assert.Null(game.Slots[0]);
    }

    [Fact]
    public void A_utility_element_tile_is_picked_up_into_a_free_slot()
    {
        var game = Blank();
        Put(game, East, HackingWire.TypeUtilityElementTile, HackingWire.SubtypeUeSelfRepair);

        var events = game.ClickTile(East);

        Assert.Equal(new[] { Flipped, PickedUp }, Ids(events));
        Assert.Equal(HackingWire.SubtypeUeSelfRepair, game.Slots[0]!.Subtype);
    }

    [Fact]
    public void A_utility_element_tile_is_refused_when_no_slot_is_free()
    {
        var game = Blank(slots: 0);
        var tile = Put(game, East, HackingWire.TypeUtilityElementTile, HackingWire.SubtypeUeSelfRepair);

        Assert.Equal(new[] { Ack }, Ids(game.ClickTile(East)));
        Assert.True(tile.Hidden);
    }

    [Fact]
    public void A_data_cache_opens_into_the_defence_it_holds_and_is_blocked_while_alive()
    {
        var game = Blank();
        var cache = Put(game, East, HackingWire.TypeDataCache);
        cache.Contents = (HackingWire.TypeDefenseSoftware, HackingWire.SubtypeDsFirewall);

        var events = game.ClickTile(East);

        Assert.Equal(new[] { Flipped, CacheOpen }, Ids(events));
        Assert.Equal(HackingWire.TypeDefenseSoftware, cache.Type);
        Assert.Equal(60, cache.Coherence);
        Assert.True(cache.Blocked);
    }

    [Fact]
    public void A_revealed_suppressor_lowers_the_virus_strength_and_restores_it_when_destroyed()
    {
        var game = Blank(coherence: 40, strength: 20);
        Put(game, East, HackingWire.TypeDefenseSoftware, HackingWire.SubtypeDsVirusSuppressor, 30, 10);

        Assert.Equal(new[] { Flipped, Attack, Attack, Suppressor }, Ids(game.ClickTile(East)));
        Assert.Equal(10, game.VirusStrength);

        game.Slots[0] = new UtilityItem(2, HackingWire.SubtypeUeSecondaryVector);
        Assert.Equal(new[] { UeInUse }, Ids(game.UseOnTarget(0, East)));
        Assert.Equal(new[] { Flipped, Distance, Vector, Killed, UeRemoved, Suppressor }, Ids(game.ClickTile(Down)));
        Assert.Equal(20, game.VirusStrength);
    }

    [Fact]
    public void Quitting_loses_the_attempt()
    {
        var game = Blank();

        Assert.Equal(new[] { GameLost }, Ids(game.Quit()));
        Assert.Equal(HackingOutcome.Lost, game.Outcome);
        Assert.Equal(new[] { Ack }, Ids(game.ClickTile(East)));
    }

    [Fact]
    public void The_start_lists_the_tiles_without_leaking_hidden_types()
    {
        var game = HackingGame.Create(Attempt(seed: 3, difficulty: 2));

        var events = game.Start();

        Assert.Equal(HackingWire.EventGameStyle, events[0].EventId);
        Assert.Equal(90, events.Count(e => e.EventId == HackingWire.EventTileCreated));
        Assert.Equal(new[] { HackingWire.EventVirusCreated, HackingWire.EventGameStart }, events.TakeLast(2).Select(e => e.EventId));
        Assert.All(events.Where(e => e.EventId == HackingWire.EventTileCreated && (bool)e.Data["hidden"]!),
            e => Assert.Equal(HackingWire.TypeNone, e.Data["type"]));
    }

    [Fact]
    public void The_same_seed_and_the_same_clicks_give_the_same_events()
    {
        var first = HackingGame.Create(Attempt(seed: 7, difficulty: 2));
        var second = HackingGame.Create(Attempt(seed: 7, difficulty: 2));

        var a = new List<HackingEvent>(first.Start());
        var b = new List<HackingEvent>(second.Start());
        foreach (var c in new[] { Left, Down, East, Far, Left })
        {
            a.AddRange(first.ClickTile(c));
            b.AddRange(second.ClickTile(c));
        }

        Assert.Equal(Dump(a), Dump(b));
    }
}
