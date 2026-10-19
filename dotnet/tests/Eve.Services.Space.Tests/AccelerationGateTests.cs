using Eve.Dungeons;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Space.Gates;
using Eve.Services.Space.Waves;
using Eve.Space;

namespace Eve.Services.Space.Tests;

/// <summary>The acceleration gate walk of a dungeon site (keeper.ActivateAccelerationGate): the reference's checks in order, and the room it leads to.</summary>
public sealed class AccelerationGateTests
{
    private static readonly double[] Origin = [5.0e11, 0.0, 2.0e11];
    private static readonly Dictionary<long, double> Radii = new() { [17831] = 12706, [35776] = 14000 };
    private static readonly DungeonCatalog Catalog = DungeonCatalog.LoadEmbedded();
    private const long Instance = 4_242;
    private const long Dungeon283 = 283;

    private static DungeonLayout LayoutOf(long dungeonId) =>
        DungeonRoomLayout.Build(((DungeonFound)Catalog.GetDungeon(dungeonId)).Dungeon, Origin, Radii);

    private static (string Name, Dictionary<string, PyValue> Values) UserError(MachoWrappedException error)
    {
        var arguments = ((PyTuple)((PyTuple)((PyObjectEx)error.Error.Payload).Header).Items[1]).Items;
        return (((PyStr)arguments[0]).Value, ((PyDict)arguments[1]).Entries.ToDictionary(e => ((PyStr)e.Key).Value, e => e.Value));
    }

    /// <summary>The service over one running site (dungeon 283 in instance 4 242), the pilot in that site, a ship of the given class and a sim answer.</summary>
    private sealed class Rig(ShipClass? ship, CommandReply reply)
    {
        public List<(long GateId, double[] Point)> Transits { get; } = [];
        public List<(long Instance, string Room, long RoomId)> Rooms { get; } = [];
        /// <summary>The rooms whose waves are done (the first room of dungeon 283 is cleared by default).</summary>
        public HashSet<string> Cleared { get; set; } = new(StringComparer.Ordinal) { "room:13849", "room:entry" };
        public ClientSession Session { get; } = new(new Sink());
        public SpaceAccelerationGates Gates { get; private set; } = null!;

        public void Build(long currentInstance, long instance, DungeonLayout? layout = null, long? archetypeId = null)
        {
            var pockets = new List<DungeonPocket> { new(instance, 30_000_001, Origin, layout ?? LayoutOf(Dungeon283), archetypeId) };
            Gates = new SpaceAccelerationGates(
                _ => Task.FromResult<IReadOnlyList<DungeonPocket>>(pockets),
                (_, _) => Task.FromResult<long?>(currentInstance),
                (_, _) => Task.FromResult<IReadOnlySet<string>>(Cleared),
                (_, _) => Task.FromResult(ship),
                (_, gateId, point, _) =>
                {
                    Transits.Add((gateId, point));
                    return Task.FromResult(reply);
                },
                (instance, room, roomId, _) =>
                {
                    Rooms.Add((instance, room, roomId));
                    return Task.CompletedTask;
                });
        }
    }

    private static long GateIdOf(DungeonLayout layout) =>
        SpaceContainers.AccelerationGateDeployableId(Instance, layout.Gates[0].GateIndex);

    [Fact]
    public void Gate_ids_are_below_two_to_the_53_and_read_back_to_their_instance_and_gate()
    {
        var id = SpaceContainers.AccelerationGateDeployableId(9_999_999, 255);
        Assert.True(id < 9_007_199_254_740_992L, "the simulation takes an id only below 2^53");
        Assert.Equal((9_999_999L, 255), SpaceAccelerationGates.GateOf(id));
        Assert.Null(SpaceAccelerationGates.GateOf(7L));
    }

    [Fact]
    public async Task A_pilot_outside_the_site_is_refused_before_anything_is_sent()
    {
        var rig = new Rig(new ShipClass(GroupId: 25, RaceId: 1), new CommandReply(true, 0, null));
        rig.Build(currentInstance: Instance + 1, Instance);

        var error = await Assert.ThrowsAsync<MachoWrappedException>(() =>
            rig.Gates.ActivateAsync(rig.Session, GateIdOf(LayoutOf(Dungeon283)), CancellationToken.None));
        Assert.Equal("DeniedTargetAttemptFailed", UserError(error).Name);
        Assert.Empty(rig.Transits);
    }

    [Fact]
    public async Task A_gate_out_of_a_room_whose_waves_are_not_done_is_locked_and_nothing_is_sent()
    {
        var rig = new Rig(new ShipClass(GroupId: 25, RaceId: 1), new CommandReply(true, 0, null));
        rig.Build(Instance, Instance);
        rig.Cleared = new HashSet<string>(StringComparer.Ordinal);

        var error = await Assert.ThrowsAsync<MachoWrappedException>(() =>
            rig.Gates.ActivateAsync(rig.Session, GateIdOf(LayoutOf(Dungeon283)), CancellationToken.None));
        var (name, values) = UserError(error);
        Assert.Equal("CustomInfo", name);
        Assert.Equal("The acceleration gate is locked.", ((PyStr)values["info"]).Value);
        Assert.Empty(rig.Transits);
        Assert.Empty(rig.Rooms);
    }

    [Fact]
    public async Task A_gate_into_the_entry_room_is_open_before_any_wave_is_cleared()
    {
        // the complex's gate (one room, the entry): the pilot walks in from the site's ring, no room has been cleared yet (the waves are inside)
        var room = new RoomPlacement(0, "room:entry", null, Origin, DungeonRoomLayout.EntryPointOf(Origin, Origin));
        var single = new DungeonLayout([room], [new GatePlacement(0, "gate:1", DungeonRoomLayout.AccelerationGateTypeId, "room:entry", "room:entry", [], 12_706, Origin)]);
        var gateId = SpaceContainers.AccelerationGateDeployableId(Instance, 0);
        var rig = new Rig(new ShipClass(GroupId: 25, RaceId: 1), new CommandReply(true, 0, null));
        rig.Build(Instance, Instance, single);
        rig.Cleared = new HashSet<string>(StringComparer.Ordinal);

        var answer = await rig.Gates.ActivateAsync(rig.Session, gateId, CancellationToken.None);

        Assert.IsType<PyNone>(answer);
        Assert.Equal(room.EntryPoint, Assert.Single(rig.Transits).Point);
        Assert.Equal((Instance, "room:entry", 0L), Assert.Single(rig.Rooms));
    }

    [Fact]
    public async Task A_capital_hull_is_barred_by_the_blacklist_when_the_gate_names_no_race_and_a_race_list_decides_otherwise()
    {
        var layout = LayoutOf(Dungeon283);
        var gateId = GateIdOf(layout);
        var capsule = new Rig(new ShipClass(GroupId: 29, RaceId: 1), new CommandReply(true, 0, null));
        capsule.Build(Instance, Instance);
        var refused = await Assert.ThrowsAsync<MachoWrappedException>(() => capsule.Gates.ActivateAsync(capsule.Session, gateId, CancellationToken.None));
        Assert.Equal("DunBlacklistCannotWarp", UserError(refused).Name);
        Assert.Empty(capsule.Transits);

    }

    [Fact]
    public async Task Battlefield_gate_rejects_ships_outside_the_t1_and_navy_allowlist()
    {
        var layout = LayoutOf(Dungeon283);
        var gateId = GateIdOf(layout);
        var ship = new ShipClass(GroupId: 25, RaceId: 1) { BattlefieldAllowed = false };
        var rig = new Rig(ship, new CommandReply(true, 0, null));
        rig.Build(Instance, Instance, archetypeId: 68);

        var error = await Assert.ThrowsAsync<MachoWrappedException>(() =>
            rig.Gates.ActivateAsync(rig.Session, gateId, CancellationToken.None));
        Assert.Equal("DunShipCannotWarp", UserError(error).Name);
        Assert.Empty(rig.Transits);
    }

    [Fact]
    public async Task Battlefield_gate_allows_a_ship_in_the_t1_and_navy_allowlist()
    {
        var layout = LayoutOf(Dungeon283);
        var gateId = GateIdOf(layout);
        var ship = new ShipClass(GroupId: 25, RaceId: 1) { BattlefieldAllowed = true };
        var rig = new Rig(ship, new CommandReply(true, 0, null));
        rig.Build(Instance, Instance, archetypeId: 68);

        await rig.Gates.ActivateAsync(rig.Session, gateId, CancellationToken.None);

        Assert.Single(rig.Transits);
    }


    [Theory]
    [InlineData(6, null, null, true)]
    [InlineData(6, 1, null, true)]
    [InlineData(6, 2, 500001L, false)]
    [InlineData(6, 3, 500001L, false)]
    [InlineData(6, 4, 500001L, true)]
    [InlineData(6, 4, 500010L, false)]
    [InlineData(4, 1, null, false)]
    public void Battlefield_allowlist_uses_ship_category_meta_group_and_faction(int categoryId, int? metaGroupId, long? factionId, bool expected)
    {
        Assert.Equal(expected, SpaceAccelerationGates.IsBattlefieldAllowed(categoryId, metaGroupId, factionId));
    }

    [Fact]
    public async Task A_gate_that_names_races_lets_only_those_races_in_and_the_blacklist_does_not_apply_to_it()
    {
        // the gate lists race 4 only: a capsule of race 4 may walk it, a frigate of race 1 may not
        var room = new RoomPlacement(0, "room:entry", null, Origin, DungeonRoomLayout.EntryPointOf(Origin, Origin));
        var raced = new DungeonLayout([room], [new GatePlacement(0, "gate:1", DungeonRoomLayout.AccelerationGateTypeId, "room:entry", "room:entry", [4], 12_706, Origin)]);
        var gateId = SpaceContainers.AccelerationGateDeployableId(Instance, 0);

        var outsider = new Rig(new ShipClass(GroupId: 25, RaceId: 1), new CommandReply(true, 0, null));
        outsider.Build(Instance, Instance, raced);
        var refused = await Assert.ThrowsAsync<MachoWrappedException>(() => outsider.Gates.ActivateAsync(outsider.Session, gateId, CancellationToken.None));
        Assert.Equal("DunShipCannotWarp", UserError(refused).Name);

        var listed = new Rig(new ShipClass(GroupId: 29, RaceId: 4), new CommandReply(true, 0, null));
        listed.Build(Instance, Instance, raced);
        await listed.Gates.ActivateAsync(listed.Session, gateId, CancellationToken.None);
        Assert.Single(listed.Transits);
    }

    [Fact]
    public async Task An_accepted_walk_sends_the_gate_and_the_entry_point_of_the_room_it_leads_to_and_answers_none()
    {
        var layout = LayoutOf(Dungeon283);
        var rig = new Rig(new ShipClass(GroupId: 25, RaceId: 1), new CommandReply(true, 0, null));
        rig.Build(Instance, Instance);

        var answer = await rig.Gates.ActivateAsync(rig.Session, GateIdOf(layout), CancellationToken.None);

        Assert.IsType<PyNone>(answer);
        var (gateId, point) = Assert.Single(rig.Transits);
        Assert.Equal(GateIdOf(layout), gateId);
        Assert.Equal(layout.Room("room:13850")!.EntryPoint, point);
        // the pilot is recorded in the room it leads to
        Assert.Equal((Instance, "room:13850", 13850L), Assert.Single(rig.Rooms));
    }

    [Fact]
    public async Task The_simulations_refusals_reach_the_client_under_the_reference_names()
    {
        var gateId = GateIdOf(LayoutOf(Dungeon283));
        var far = new Rig(new ShipClass(GroupId: 25, RaceId: 1), new CommandReply(false, 0, "TOO_FAR_FROM_GATE"));
        far.Build(Instance, Instance);
        var tooFar = await Assert.ThrowsAsync<MachoWrappedException>(() => far.Gates.ActivateAsync(far.Session, gateId, CancellationToken.None));
        Assert.Equal("TargetTooFar", UserError(tooFar).Name);

        var warping = new Rig(new ShipClass(GroupId: 25, RaceId: 1), new CommandReply(false, 0, "IN_WARP"));
        warping.Build(Instance, Instance);
        var inWarp = await Assert.ThrowsAsync<MachoWrappedException>(() => warping.Gates.ActivateAsync(warping.Session, gateId, CancellationToken.None));
        Assert.Equal("ShipInWarp", UserError(inWarp).Name);
    }
}
