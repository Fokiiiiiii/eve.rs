using Eve.Services;
using Eve.Services.Core;

namespace Eve.Services.Core.Tests;

/// <summary>
/// The fitting calls the client makes (fittingSvc.py: GetFittingMgr 86-93 picks charFittingMgr, corpFittingMgr or allianceFittingMgr; the save, get,
/// delete and rename calls at 300-304, 348, 430, 435, 448, 720). The call names are the client's; each must be answered by a registered service method,
/// or be listed here as open. An open call is answered with None by the generator's default (ServiceGenerator.cs), which is a silent answer: the list
/// must end empty before fitting persistence counts as done (AGENT_OVERRIDE.md).
/// </summary>
public class FittingCoverageTests
{
    private static readonly string[] FittingCalls =
        ["GetFittings", "SaveFitting", "UpdateFitting", "SaveManyFittings", "DeleteFitting", "DeleteManyFittings", "UpdateNameAndDescription"];

    private static readonly string[] Managers = ["charFittingMgr", "allianceFittingMgr", "corpFittingMgr"];

    /// <summary>The client calls no service answers yet (SF-2 removes them). Every manager misses every call; corpFittingMgr answers only GetCommunityFittings.</summary>
    private static readonly (string Service, string Method)[] Open =
        [.. Managers.SelectMany(manager => FittingCalls.Select(method => (manager, method)))];

    [Fact]
    public void Every_fitting_call_is_answered_or_listed_as_open()
    {
        var services = CoreServices.Create(new SubscriptionSettings(), new NoAccounts(), () => 0).ToDictionary(service => service.Name);
        var missing = Managers
            .SelectMany(manager => FittingCalls.Select(method => (manager, method)))
            .Where(call => !(services.TryGetValue(call.manager, out var service) && service.HasMethod(call.method)))
            .ToList();
        Assert.Equal(Open.Order(), missing.Order());
    }

    [Fact]
    public void The_community_fittings_are_still_answered_by_corpFittingMgr()
    {
        var corp = Assert.Single(CoreServices.Create(new SubscriptionSettings(), new NoAccounts(), () => 0), service => service.Name == "corpFittingMgr");
        Assert.True(corp.HasMethod("GetCommunityFittings"));
    }

    private sealed class NoAccounts : IOmegaAccountStore
    {
        public string? GetOmegaExpiry(long accountId) => null;
    }
}
