using Eve.Host;
using Eve.Services;
using Eve.Services.Space;
using Eve.Services.Core;
using Eve.Space;
using Eve.Structures;
using Xunit;

namespace Eve.Host.Tests;

public sealed class StructureAnchorDelayTests
{
    private const int SystemId = 30000142;
    private const int Astrahus = 35832;
    private const long DeployingAlliance = 99_100_001;
    private const long SovAlliance = 99_100_002;

    [Fact]
    public async Task Foreign_IHub_adds_one_day_for_each_Sovereignty_Strategic_Index_level()
    {
        var snapshot = SovFwReadSnapshot.Empty with
        {
            HubsBySolarSystemId = new Dictionary<long, SovHubRecord>
            {
                [SystemId] = new SovHubRecord(8_800_001, 98_100_001, SovAlliance, null),
            },
        };
        IReadOnlyList<DevelopmentIndexRecord> indices = [new(SystemId, 0, 0, 21)];
        var authority = ForSovereignty(snapshot, indices);

        var duration = await authority.ResolveSovereigntyAnchoringMsAsync(
            StructureRules.Of(StructureSize.Medium, SecurityBand.NullSec), Astrahus, SystemId,
            DeployingAlliance, CancellationToken.None);

        Assert.Equal(StructureTimes.AnchoringMs + 2 * StructureTimes.Day, duration);
    }

    [Fact]
    public async Task No_foreign_IHub_keeps_the_standard_rule_duration()
    {
        var authority = ForSovereignty(SovFwReadSnapshot.Empty, []);

        var duration = await authority.ResolveSovereigntyAnchoringMsAsync(
            StructureRules.Of(StructureSize.Medium, SecurityBand.NullSec), Astrahus, SystemId,
            DeployingAlliance, CancellationToken.None);

        Assert.Equal(StructureTimes.AnchoringMs, duration);
    }

    [Fact]
    public async Task Missing_development_index_for_a_foreign_IHub_fails_closed()
    {
        var snapshot = SovFwReadSnapshot.Empty with
        {
            HubsBySolarSystemId = new Dictionary<long, SovHubRecord>
            {
                [SystemId] = new SovHubRecord(8_800_001, 98_100_001, SovAlliance, null),
            },
        };
        var authority = ForSovereignty(snapshot, []);

        await Assert.ThrowsAsync<MachoWrappedException>(() => authority.ResolveSovereigntyAnchoringMsAsync(
            StructureRules.Of(StructureSize.Medium, SecurityBand.NullSec), Astrahus, SystemId,
            DeployingAlliance, CancellationToken.None).AsTask());
    }

    private static StructureAnchorAuthority ForSovereignty(
        SovFwReadSnapshot snapshot,
        IReadOnlyList<DevelopmentIndexRecord> indices) =>
        new(
            null!, null!, null!, null!, null!, null!, null!, null!,
            (_, _, _, _) => Task.FromResult<long?>(null),
            (_, _, _) => Task.FromResult<WarpCoordinates?>(null),
            _ => false,
            _ => Task.CompletedTask,
            sov: _ => ValueTask.FromResult((snapshot, indices)));
}
