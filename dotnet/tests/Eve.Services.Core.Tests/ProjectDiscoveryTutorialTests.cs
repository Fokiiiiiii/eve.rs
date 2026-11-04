using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;
using Xunit;

namespace Eve.Services.Core.Tests;

public sealed class ProjectDiscoveryTutorialTests
{
    [Fact]
    public async Task Initialize_tutorial_status_answers_the_finished_flag_of_the_stored_tutorial_and_not_finished_without_one()
    {
        var finished = new ProjectDiscoveryService(new ProjectDiscoveryRpcCallbacks(
            ReadPlayerStateAsync: (_, _) => ValueTask.FromResult<ProjectDiscoveryPlayerState?>(new ProjectDiscoveryPlayerState(FinishedTutorial: true, TutorialLevel: 8))));
        var none = new ProjectDiscoveryService(new ProjectDiscoveryRpcCallbacks(
            ReadPlayerStateAsync: (_, _) => ValueTask.FromResult<ProjectDiscoveryPlayerState?>(null)));
        var session = new ClientSession { CharacterId = 98000001 };

        Assert.True(finished.HasMethod("initialize_tutorial_status"));
        Assert.Same(PyBool.True, await finished.CallAsync(new ServiceCall { Args = [], Method = "initialize_tutorial_status", Session = session }));
        Assert.Same(PyBool.False, await none.CallAsync(new ServiceCall { Args = [], Method = "initialize_tutorial_status", Session = session }));
    }

    [Fact]
    public async Task The_season_reads_answer_no_active_season_as_the_reference_does()
    {
        IService seasons = new SeasonManagerService();
        var session = new ClientSession { CharacterId = 98000001 };

        Assert.True(seasons.HasMethod("get_season_data_for_character"));
        Assert.True(seasons.HasMethod("get_season_end_time"));
        Assert.True(seasons.HasMethod("select_season"));
        Assert.Same(PyNone.Instance, await seasons.CallAsync(new ServiceCall { Args = [], Method = "get_season_data_for_character", Session = session }));
        Assert.Same(PyNone.Instance, await seasons.CallAsync(new ServiceCall { Method = "select_season", Args = [new PyInt(1)], Session = session }));
        var endTime = Assert.IsType<PyLong>(await seasons.CallAsync(new ServiceCall { Args = [], Method = "get_season_end_time", Session = session }));
        Assert.True(endTime.Value > DateTime.UtcNow.AddDays(-1).ToFileTimeUtc());
    }

    [Fact]
    public async Task The_challenge_reads_answer_no_challenge_as_the_reference_does()
    {
        IService seasons = new SeasonManagerService();
        var session = new ClientSession { CharacterId = 98000001 };

        Assert.Equal(new PyInt(0), await seasons.CallAsync(new ServiceCall { Args = [], Method = "get_max_active_challenges", Session = session }));
        Assert.Same(PyBool.True, await seasons.CallAsync(new ServiceCall { Args = [new PyInt(1)], Method = "challenge_is_dormant", Session = session }));
        Assert.Same(PyNone.Instance, await seasons.CallAsync(new ServiceCall { Args = [new PyInt(1)], Method = "get_challenge", Session = session }));
        Assert.Equal(new PyDict([]), await seasons.CallAsync(new ServiceCall { Args = [], Method = "fill_up_and_return_challenges_for_character", Session = session }));
        Assert.Same(PyBool.False, await seasons.CallAsync(new ServiceCall { Args = [new PyInt(1)], Method = "claim_challenge_rewards", Session = session }));
        Assert.Same(PyBool.False, await seasons.CallAsync(new ServiceCall { Args = [new PyInt(1)], Method = "claim_goal_reward", Session = session }));
        Assert.Equal(new PyList([]), await seasons.CallAsync(new ServiceCall { Args = [new PyInt(34)], Method = "get_loot_table_by_typeID", Session = session }));
        Assert.Equal(
            Py.KeyVal(
                ("character_id", new PyLong(98000001)),
                ("season_id", PyNone.Instance),
                ("seasonal_points", new PyInt(0)),
                ("max_seasonal_points", new PyInt(1)),
                ("seasonal_goals", new PyDict([])),
                ("next_seasonal_goal", PyNone.Instance),
                ("last_seasonal_goal", PyNone.Instance),
                ("challenges", new PyDict([]))),
            await seasons.CallAsync(new ServiceCall { Args = [], Method = "get_character_progress_by_character_id", Session = session }));
    }
}
