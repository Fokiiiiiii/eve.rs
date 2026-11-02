using Eve.Services;
using Eve.Services.Core;
using Eve.Marshal;
using Xunit;

namespace Eve.Services.Core.Tests;

public sealed class ProjectDiscoveryServiceTests
{
    [Fact]
    public void Core_catalog_registers_the_reference_project_discovery_rpcs()
    {
        var service = CoreServices.Create(new NoAccounts(), () => 0)
            .SingleOrDefault(candidate => candidate.Name == "ProjectDiscovery");

        Assert.NotNull(service);
        foreach (var method in new[]
                 {
                     "fix_task_id",
                     "get_player_analysis_kredits",
                     "get_player_state",
                     "get_player_statistics",
                     "get_tutorial_completion_status",
                     "get_project_id",
                     "get_total_needed_xp",
                     "get_tutorial_level",
                     "get_tutorial_xp_reward",
                     "get_is_player_entitled_to_tutorial_reward",
                     "give_tutorial_rewards",
                     "increase_tutorial_level",
                     "reset_tutorial",
                     "skip_tutorial",
                     "is_enabled",
                 })
            Assert.True(service!.HasMethod(method), method);
    }

    [Fact]
    public async Task Project_discovery_static_progression_methods_match_the_client_contract()
    {
        var service = CoreServices.Create(new NoAccounts(), () => 0)
            .Single(candidate => candidate.Name == "ProjectDiscovery");
        var session = new ClientSession();

        Assert.Equal(new PyInt(1), await service.CallAsync(Request("get_project_id", session)));
        Assert.Equal(new PyInt(300), await service.CallAsync(Request("get_total_needed_xp", session, new PyInt(3))));
        Assert.Equal(new PyInt(100), await service.CallAsync(Request("get_tutorial_xp_reward", session)));
        Assert.Equal(PyBool.False, await service.CallAsync(Request("get_is_player_entitled_to_tutorial_reward", session)));
        Assert.Equal(PyBool.False, await service.CallAsync(Request("give_tutorial_rewards", session)));
    }

    [Fact]
    public async Task Unavailable_backend_uses_reference_read_shapes_and_refuses_unwired_task_fixes()
    {
        var service = CoreServices.Create(new NoAccounts(), () => 0)
            .Single(candidate => candidate.Name == "ProjectDiscovery");
        var session = new ClientSession();

        Assert.Equal(
            Py.KeyVal(
                ("rank", new PyInt(1)),
                ("experience", new PyInt(0)),
                ("analysisKredits", new PyInt(0)),
                ("finishedTutorial", PyBool.False),
                ("tutorialLevel", new PyInt(0))),
            await service.CallAsync(Request("get_player_state", session)));
        Assert.Equal(new PyInt(0), await service.CallAsync(Request("get_player_analysis_kredits", session)));
        Assert.Equal(PyBool.False, await service.CallAsync(Request("get_tutorial_completion_status", session)));
        Assert.Equal(PyBool.True, await service.CallAsync(Request("is_enabled", session)));
        var unavailable = await Assert.ThrowsAsync<MachoWrappedException>(
            () => service.CallAsync(Request("fix_task_id", session, new PyLong(42))).AsTask());
        Assert.Equal("PROJECT_DISCOVERY_TASK_FIX_UNAVAILABLE", unavailable.Error.UserErrorMessage);
        Assert.Equal(
            new PyDict(
            [
                Pair("message", new PyStr("Project Discovery API is not backed by live MMOS data in this emulator")),
                Pair("code", new PyInt(103010)),
                Pair("projectID", new PyStr("mcgill-polyseg")),
            ]),
            await service.CallAsync(Request("get_player_statistics", session)));
    }

    [Fact]
    public async Task Player_state_and_task_fix_use_the_future_backend_callback()
    {
        long readCharacter = 0;
        long fixedCharacter = 0;
        long? fixedTaskId = null;
        var service = new ProjectDiscoveryService(new ProjectDiscoveryRpcCallbacks(
            ReadPlayerStateAsync: (characterId, _) =>
            {
                readCharacter = characterId;
                return ValueTask.FromResult<ProjectDiscoveryPlayerState?>(
                    new ProjectDiscoveryPlayerState(0.9, -3, 4.7, true, 2.9));
            },
            FixTaskIdAsync: (characterId, taskId, _) =>
            {
                fixedCharacter = characterId;
                fixedTaskId = taskId;
                return ValueTask.CompletedTask;
            }));
        var session = new ClientSession { CharacterId = 98000001 };
        session.Set("accountRole", new PyLong(Eve.Services.GameMasterRoles.Qa));

        Assert.Equal(
            Py.KeyVal(
                ("rank", new PyInt(1)),
                ("experience", new PyInt(0)),
                ("analysisKredits", new PyInt(4)),
                ("finishedTutorial", PyBool.True),
                ("tutorialLevel", new PyInt(2))),
            await service.CallAsync(Request("get_player_state", session)));
        Assert.Equal(98000001, readCharacter);
        await service.CallAsync(Request("fix_task_id", session, new PyLong(42)));
        Assert.Equal(98000001, fixedCharacter);
        Assert.Equal(42L, fixedTaskId);
        await service.CallAsync(Request("fix_task_id", session, new PyLong(-42)));
        Assert.Null(fixedTaskId);
        await service.CallAsync(Request("fix_task_id", session, new PyInt(0)));
        Assert.Null(fixedTaskId);
    }

    [Fact]
    public async Task Project_discovery_task_fix_requires_a_staff_account_role()
    {
        var callbackCalls = 0;
        var service = new ProjectDiscoveryService(new ProjectDiscoveryRpcCallbacks(
            FixTaskIdAsync: (_, _, _) =>
            {
                callbackCalls++;
                return ValueTask.CompletedTask;
            }));
        var session = new ClientSession { CharacterId = 42 };
        var error = await Assert.ThrowsAsync<MachoWrappedException>(
            () => service.CallAsync(Request("fix_task_id", session, new PyLong(12))).AsTask());
        Assert.Equal("PROJECT_DISCOVERY_TASK_FIX_UNAUTHORIZED", error.Error.UserErrorMessage);
        Assert.Equal(0, callbackCalls);
    }

    [Fact]
    public async Task Project_discovery_refuses_malformed_arguments_before_player_callbacks()
    {
        var callbackCalls = 0;
        var service = new ProjectDiscoveryService(new ProjectDiscoveryRpcCallbacks(
            ReadPlayerStateAsync: (_, _) =>
            {
                callbackCalls++;
                return ValueTask.FromResult<ProjectDiscoveryPlayerState?>(new ProjectDiscoveryPlayerState());
            },
            FixTaskIdAsync: (_, _, _) =>
            {
                callbackCalls++;
                return ValueTask.CompletedTask;
            }));
        var session = new ClientSession { CharacterId = 42 };

        foreach (var call in new[]
        {
            Request("get_player_state", session, new PyInt(99)),
            Request("fix_task_id", session, new PyStr("42")),
            Request("fix_task_id", session, new PyReal(42.5)),
            new ServiceCall
            {
                Method = "get_player_state", Args = [], Kwargs = new PyDict([]), Session = session,
            },
        })
        {
            var error = await Assert.ThrowsAsync<MachoWrappedException>(() => service.CallAsync(call).AsTask());
            Assert.Contains(error.Error.UserErrorMessage, new[]
            {
                "PROJECT_DISCOVERY_ARGUMENTS_INVALID", "PROJECT_DISCOVERY_TASK_ID_INVALID",
            });
        }

        Assert.Equal(0, callbackCalls);
    }

    [Fact]
    public async Task Project_discovery_clears_a_non_positive_task_id_like_the_reference()
    {
        var callbackCalls = 0;
        long? fixedTaskId = long.MinValue;
        var service = new ProjectDiscoveryService(new ProjectDiscoveryRpcCallbacks(
            FixTaskIdAsync: (_, taskId, _) =>
            {
                callbackCalls++;
                fixedTaskId = taskId;
                return ValueTask.CompletedTask;
            }));
        var session = new ClientSession { CharacterId = 42 };
        session.Set("accountRole", new PyLong(Eve.Services.GameMasterRoles.Qa));

        Assert.Same(PyNone.Instance, await service.CallAsync(Request("fix_task_id", session, new PyInt(-1))));
        Assert.Equal(1, callbackCalls);
        Assert.Null(fixedTaskId);
    }

    [Fact]
    public async Task Unavailable_legacy_bonus_and_training_are_explicit_errors()
    {
        var service = new ProjectDiscoveryService();
        var session = new ClientSession { CharacterId = 42 };
        foreach (var method in new[] { "get_maximum_bonus_samples", "get_remaining_bonus_samples" })
        {
            Assert.True(service.HasMethod(method));
            var error = await Assert.ThrowsAsync<MachoWrappedException>(() => service.CallAsync(Request(method, session)).AsTask());
            Assert.Equal("PROJECT_DISCOVERY_BONUS_UNAVAILABLE", error.Error.UserErrorMessage);
        }
        Assert.True(service.HasMethod("new_training_task"));
        var unavailable = await Assert.ThrowsAsync<MachoWrappedException>(() => service.CallAsync(Request("new_training_task", session, new PyInt(2))).AsTask());
        Assert.Equal("PROJECT_DISCOVERY_TRAINING_UNAVAILABLE", unavailable.Error.UserErrorMessage);
        var invalid = await Assert.ThrowsAsync<MachoWrappedException>(() => service.CallAsync(Request("new_training_task", session, new PyStr("2"))).AsTask());
        Assert.Equal("PROJECT_DISCOVERY_ARGUMENTS_INVALID", invalid.Error.UserErrorMessage);
    }

    private static ServiceCall Request(string method, ClientSession session, params PyValue[] args) => new()
    {
        Method = method,
        Args = args,
        Session = session,
    };

    private static KeyValuePair<PyValue, PyValue> Pair(string key, PyValue value) =>
        new(new PyStr(key), value);

    private sealed class NoAccounts : IOmegaAccountStore
    {
        public string? GetOmegaExpiry(long accountId) => OmegaLicenses.DefaultOmegaExpiryText;
    }
}
