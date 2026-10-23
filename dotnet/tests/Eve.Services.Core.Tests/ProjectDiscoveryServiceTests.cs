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
                     "is_enabled",
                 })
            Assert.True(service!.HasMethod(method), method);
    }

    [Fact]
    public async Task Unavailable_backend_uses_the_reference_default_wire_shapes()
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
        Assert.Equal(PyNone.Instance, await service.CallAsync(Request("fix_task_id", session, new PyLong(42))));
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
        double? fixedTaskId = null;
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
        Assert.Equal(42, fixedTaskId);
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
            Request("fix_task_id", session, new PyInt(-1)),
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
