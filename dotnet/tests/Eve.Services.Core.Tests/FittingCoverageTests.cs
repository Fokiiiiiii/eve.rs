using Eve.Fitting;
using Eve.Services;
using Eve.Services.Core;

namespace Eve.Services.Core.Tests;

/// <summary>
/// The fitting calls the client makes (fittingSvc.py: GetFittingMgr 86-93 picks charFittingMgr, corpFittingMgr or allianceFittingMgr; the save, get,
/// delete and rename calls at 300-304, 348, 430, 435, 448, 720). Each must be answered by a registered service method. The list of open calls is empty:
/// a missing call is answered with None by the generator's default, which is a silent answer (AGENT_OVERRIDE.md).
/// </summary>
public class FittingCoverageTests
{
    private static readonly string[] FittingCalls =
        ["GetFittings", "SaveFitting", "UpdateFitting", "SaveManyFittings", "DeleteFitting", "DeleteManyFittings", "UpdateNameAndDescription"];

    /// <summary>The services as the host builds them (the store and the host are not used by <c>HasMethod</c>, so they are left out here).</summary>
    private static IService[] Managers()
    {
        var calls = new FittingMgrCalls(null!, null!, () => 0);
        return [new CharFittingMgrService(calls), new CorpFittingMgrService(calls), new AllianceFittingMgrService(calls)];
    }

    [Fact]
    public void Every_fitting_call_is_answered_by_its_manager()
    {
        var managers = Managers().ToDictionary(service => service.Name);
        var missing = new[] { "charFittingMgr", "allianceFittingMgr", "corpFittingMgr" }
            .SelectMany(manager => FittingCalls.Select(method => (manager, method)))
            .Where(call => !managers[call.manager].HasMethod(call.method))
            .ToList();
        Assert.Empty(missing);
    }

    [Fact]
    public void The_community_fittings_are_still_answered_by_corpFittingMgr()
    {
        var corp = Managers().Single(service => service.Name == "corpFittingMgr");
        Assert.True(corp.HasMethod("GetCommunityFittings"));
    }
}
