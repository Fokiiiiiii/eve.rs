using Eve.Services.Space.Gates;
using Eve.Universe;

namespace Eve.Services.Space.Tests;

/// <summary>The stargate rules (the client's allowed-ship lists and tolls, the SDE destinations): pure, no PostgreSQL.</summary>
public sealed class GateRulesTests
{
    private const int FreighterGroup = 513;
    private const int FrigateGroup = 25;
    private const long ZarzakhGate = 50016566;
    private const long TurnurGate = 50016562;
    private const int TollType = 77918;
    private const int Angel = 500011;

    private static readonly AlignmentToll Toll = new(0.015, 1.1, -100_000, 10_000, 0.0, [Angel, 500010]);

    private static GateRules Rules() => new(
        listOfGate: new Dictionary<long, int> { [ZarzakhGate] = 579, [TurnurGate] = 579 },
        groupsOfList: new Dictionary<int, IReadOnlySet<int>> { [579] = new HashSet<int> { FrigateGroup, 1022 } },
        destinationOf: new Dictionary<long, long> { [TurnurGate] = ZarzakhGate, [ZarzakhGate] = TurnurGate },
        typeOfGate: new Dictionary<long, int> { [TurnurGate] = TollType },
        tollOfType: new Dictionary<int, AlignmentToll> { [TollType] = Toll });

    [Fact]
    public void A_gate_with_a_list_admits_only_the_ship_groups_of_the_list()
    {
        var rules = Rules();

        Assert.True(rules.ShipAllowed(ZarzakhGate, FrigateGroup));
        Assert.False(rules.ShipAllowed(ZarzakhGate, FreighterGroup));
    }

    [Fact]
    public void A_gate_with_no_list_admits_every_ship()
    {
        Assert.True(Rules().ShipAllowed(42, FreighterGroup));
    }

    [Fact]
    public void Destinations_and_tolls_are_read_by_gate_and_type()
    {
        var rules = Rules();

        Assert.Equal(ZarzakhGate, rules.DestinationOf(TurnurGate));
        Assert.Null(rules.DestinationOf(42));
        Assert.Same(Toll, rules.TollOf(TurnurGate));
        Assert.Null(rules.TollOf(ZarzakhGate));
    }

    [Fact]
    public void The_toll_is_the_minimum_for_a_small_ship_and_the_client_formula_for_a_capital()
    {
        // a 1000 t ship: (1e6 kg * 0.015) ^ 1.1 - 100000 is below the minimum, so the wiki's 10k ISK
        Assert.Equal(10_000, GateRules.TollAmount(Toll, 1e6, aligned: false));

        // a 1e9 kg ship: (1.5e7) ^ 1.1 - 100000, about 77.8 million ISK (the wiki: "presumably goes as high as 100m ISK for capitals")
        var capital = GateRules.TollAmount(Toll, 1e9, aligned: false);
        Assert.InRange(capital, 77_000_000, 78_500_000);
    }

    [Fact]
    public void A_deploy_blocker_keeps_a_warp_bubble_within_its_range_of_the_gate_and_not_beyond()
    {
        var blocked = new DeployBlocker(BubbleRange: 100_000_000, StructureRange: 100_000_000);
        var rules = new GateRules(
            new Dictionary<long, int>(), new Dictionary<int, IReadOnlySet<int>>(), new Dictionary<long, long>(),
            new Dictionary<long, int>(), new Dictionary<int, AlignmentToll>(),
            new Dictionary<int, DeployBlocker> { [TollType] = blocked },
            [new GateSite(TurnurGate, 30_002_086, TollType, 0, 0, 0)]);

        Assert.NotNull(rules.GateBlocking(30_002_086, 60_000_000, 0, 0, AnchorKind.WarpBubble));
        Assert.Null(rules.GateBlocking(30_002_086, 150_000_000, 0, 0, AnchorKind.WarpBubble));
        Assert.NotNull(rules.GateBlocking(30_002_086, 1_000_000, 0, 0, AnchorKind.Structure));
        Assert.Null(rules.GateBlocking(30_002_087, 1_000_000, 0, 0, AnchorKind.WarpBubble));
    }

    [Fact]
    public void A_blocker_with_no_range_for_a_kind_does_not_block_that_kind()
    {
        var rules = new GateRules(
            new Dictionary<long, int>(), new Dictionary<int, IReadOnlySet<int>>(), new Dictionary<long, long>(),
            new Dictionary<long, int>(), new Dictionary<int, AlignmentToll>(),
            new Dictionary<int, DeployBlocker> { [TollType] = new DeployBlocker(0, 100_000_000) },
            [new GateSite(TurnurGate, 30_002_086, TollType, 0, 0, 0)]);

        Assert.Null(rules.GateBlocking(30_002_086, 1_000, 0, 0, AnchorKind.WarpBubble));
        Assert.NotNull(rules.GateBlocking(30_002_086, 1_000, 0, 0, AnchorKind.Structure));
    }

    [Fact]
    public void An_aligned_pilot_pays_the_aligned_multiplier_and_only_a_faction_of_the_list_is_aligned()
    {
        Assert.Equal(0, GateRules.TollAmount(Toll, 1e9, aligned: true));
        Assert.True(GateRules.IsAligned(Toll, Angel));
        Assert.False(GateRules.IsAligned(Toll, 500_001));
        Assert.False(GateRules.IsAligned(Toll, null));
    }
}

/// <summary>The emanation lock of Zarzakh: held per character, released by time or by leaving through the lock's gate.</summary>
public sealed class EmanationLocksTests
{
    private const int Zarzakh = 30_100_000;
    private const int Turnur = 30_002_086;
    private const long Arrival = 50016566;
    private const long Other = 50016567;
    private const long Character = 90000001;
    private const long Start = 1_700_000_000_000;

    [Fact]
    public void Entering_locks_the_pilot_to_the_arrival_gate_for_six_hours()
    {
        var locks = new EmanationLocks();

        locks.Entered(Character, Zarzakh, Arrival, Start);

        Assert.True(locks.MayLeave(Character, Zarzakh, Arrival, Start + 1));
        Assert.False(locks.MayLeave(Character, Zarzakh, Other, Start + 1));
        Assert.False(locks.MayLeave(Character, Zarzakh, Other, Start + EmanationLocks.DurationMs - 1));
        Assert.Equal(6L * 3_600_000, EmanationLocks.DurationMs);
    }

    [Fact]
    public void The_lock_ends_at_six_hours_and_the_other_gate_is_free_again()
    {
        var locks = new EmanationLocks();
        locks.Entered(Character, Zarzakh, Arrival, Start);

        Assert.True(locks.MayLeave(Character, Zarzakh, Other, Start + EmanationLocks.DurationMs));
    }

    [Fact]
    public void Leaving_the_restricted_system_removes_the_lock()
    {
        var locks = new EmanationLocks();
        locks.Entered(Character, Zarzakh, Arrival, Start);

        locks.Left(Character, Zarzakh);

        Assert.True(locks.MayLeave(Character, Zarzakh, Other, Start + 1));
        Assert.Null(locks.Active(Character, Zarzakh, Start + 1));
    }

    [Fact]
    public void Entering_again_through_another_gate_resets_the_lock_to_that_gate()
    {
        var locks = new EmanationLocks();
        locks.Entered(Character, Zarzakh, Arrival, Start);

        locks.Entered(Character, Zarzakh, Other, Start + 3_600_000);

        Assert.True(locks.MayLeave(Character, Zarzakh, Other, Start + 3_600_001));
        Assert.False(locks.MayLeave(Character, Zarzakh, Arrival, Start + 3_600_001));
        Assert.Equal(Start + 3_600_000 + EmanationLocks.DurationMs, locks.Active(Character, Zarzakh, Start + 3_600_001)!.ExpiresMs);
    }

    [Fact]
    public void Other_systems_have_no_lock_and_other_characters_are_not_held()
    {
        var locks = new EmanationLocks();
        locks.Entered(Character, Turnur, Arrival, Start);
        locks.Entered(Character, Zarzakh, Arrival, Start);

        Assert.True(locks.MayLeave(Character, Turnur, Other, Start + 1));
        Assert.True(locks.MayLeave(Character + 1, Zarzakh, Other, Start + 1));
    }
}
