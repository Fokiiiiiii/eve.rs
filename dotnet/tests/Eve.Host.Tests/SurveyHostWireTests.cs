using Eve.Marshal;
using Eve.Services.Core;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [Fact]
    public async Task Survey_calls_use_the_client_consumed_results_through_host_dispatch()
    {
        var events = new List<string>();
        Start(configure: registry => registry.Register(new SurveyService(
            _ => ValueTask.FromResult<int?>(90210),
            (name, _, _) =>
            {
                events.Add(name);
                return ValueTask.CompletedTask;
            })));
        var (front, open) = await OpenFrontAsync();

        Assert.Equal(new PyInt(90210), await HostRpcAsync(front, open, 1, "survey", "GetActiveSurveyID"));
        Assert.Same(PyNone.Instance, await HostRpcAsync(front, open, 2, "survey", "PerformSurveyChecks"));
        Assert.Same(PyNone.Instance, await HostRpcAsync(front, open, 3, "survey", "ClaimSurveyRewards"));

        Assert.Equal(["get_active_survey_id", "perform_checks", "claim_rewards"], events);
    }
}
