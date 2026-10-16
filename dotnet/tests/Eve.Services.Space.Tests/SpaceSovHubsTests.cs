using Eve.Services.Core;
using Eve.Space;
using Eve.Structures;

namespace Eve.Services.Space.Tests;

/// <summary>
/// ST-9 (a): a system's Sovereignty Hub is a structure ball in the simulation, read from the sovereignty records. The records are the real read model's
/// snapshot shape; the simulation is the fake one.
/// </summary>
public sealed class SpaceSovHubsTests
{
    private const int SovereigntyHub = 32_458;
    private const int InfrastructureHubRow = 32_226; // a TCU row: not a Sovereignty Hub ball
    private const int System = 30_100_032;
    private const long HubId = 1_040_000_000_555;
    private const long Corporation = 98_000_001;
    private const long Alliance = 99_000_001;

    private static SovFwReadSnapshot Snapshot(int systemId, long hubId, int hubRowType, long? corporation, long? alliance) => new(
        new Dictionary<long, SovClaimRecord>(),
        new Dictionary<long, SovHubRecord> { [systemId] = new(hubId, corporation, alliance, 116_444_736_000_000_000L) },
        new Dictionary<long, IReadOnlyList<SovStructureRecord>>
        {
            [systemId] = [new SovStructureRecord(hubId, hubRowType, corporation, corporation, alliance, systemId)],
        },
        [],
        new Dictionary<long, long?>());

    [Fact]
    public void A_hub_row_of_the_sovereignty_hub_type_is_the_systems_hub()
    {
        var hub = SpaceSovHubs.HubOf(Snapshot(System, HubId, SovereigntyHub, Corporation, Alliance), System);

        Assert.Equal(new SovHubBall(HubId, System, Corporation, Alliance), hub);
    }

    [Fact]
    public void A_neutral_hub_has_no_owner_and_a_system_without_a_hub_has_none()
    {
        Assert.Equal(new SovHubBall(HubId, System, null, null), SpaceSovHubs.HubOf(Snapshot(System, HubId, SovereigntyHub, null, null), System));
        Assert.Null(SpaceSovHubs.HubOf(Snapshot(System, HubId, SovereigntyHub, Corporation, Alliance), System + 1));
        Assert.Null(SpaceSovHubs.HubOf(SovFwReadSnapshot.Empty, System));
    }

    [Fact]
    public void A_hub_whose_row_is_another_type_is_not_a_hub_ball()
    {
        Assert.Null(SpaceSovHubs.HubOf(Snapshot(System, HubId, InfrastructureHubRow, Corporation, Alliance), System));
    }

    [PgFact]
    public async Task A_hub_of_a_system_is_put_in_space_once_as_a_structure_ball_with_the_static_data_and_the_owner()
    {
        await using var rig = await Rig.CreateAsync();
        var snapshot = Snapshot(System, HubId, SovereigntyHub, Corporation, Alliance);
        var hubs = new SpaceSovHubs(rig.Sim, rig.Space, rig.Statics,
            (systemId, _) => Task.FromResult(SpaceSovHubs.HubOf(snapshot, systemId)));

        await hubs.RestoreAsync(System);

        var placed = Assert.Single(rig.Sim.Heard.OfType<SimPlaceStructure>());
        var type = rig.Statics.Type(SovereigntyHub)!;
        Assert.Equal(HubId, placed.StructureId);
        Assert.Equal(System, placed.SolarSystemId);
        Assert.Equal(SovereigntyHub, placed.TypeId);
        Assert.Equal(type.GroupId, placed.GroupId);
        Assert.Equal(1012, placed.GroupId);
        Assert.Equal(Corporation, placed.OwnerId);
        Assert.Equal((int)Alliance, placed.AllianceId);
        Assert.Equal(new double[] { 0, 0, 0 }, placed.Position);
        Assert.Equal(45_000d, placed.Radius);
        Assert.Equal(StructureState.ShieldVulnerable, placed.Status.State);

        // a system with no hub of its own places nothing new (the restore reads the system's hub only)
        await hubs.RestoreAsync(System + 1);
        Assert.Single(rig.Sim.Heard.OfType<SimPlaceStructure>());
    }

    [PgFact]
    public async Task A_system_without_a_sovereignty_hub_places_nothing()
    {
        await using var rig = await Rig.CreateAsync();
        var hubs = new SpaceSovHubs(rig.Sim, rig.Space, rig.Statics,
            (systemId, _) => Task.FromResult(SpaceSovHubs.HubOf(SovFwReadSnapshot.Empty, systemId)));

        await hubs.RestoreAsync(System);

        Assert.Empty(rig.Sim.Heard.OfType<SimPlaceStructure>());
    }

    [PgFact]
    public async Task A_replaced_hub_is_taken_out_and_put_back_with_its_new_owner()
    {
        await using var rig = await Rig.CreateAsync();
        var current = Snapshot(System, HubId, SovereigntyHub, null, null);
        var hubs = new SpaceSovHubs(rig.Sim, rig.Space, rig.Statics, (systemId, _) => Task.FromResult(SpaceSovHubs.HubOf(current, systemId)));
        await hubs.RestoreAsync(System);

        // the claim changed the records (ST-9 d): the ball is replaced with the capturer as its owner
        current = Snapshot(System, HubId, SovereigntyHub, Corporation, Alliance);
        await hubs.ReplaceAsync(HubId, System);

        var removed = Assert.Single(rig.Sim.Heard.OfType<SimRemoveStructure>());
        Assert.Equal(HubId, removed.StructureId);
        var placed = rig.Sim.Heard.OfType<SimPlaceStructure>().Last();
        Assert.Equal((HubId, Corporation, (int)Alliance), (placed.StructureId, placed.OwnerId, placed.AllianceId));
    }
}
