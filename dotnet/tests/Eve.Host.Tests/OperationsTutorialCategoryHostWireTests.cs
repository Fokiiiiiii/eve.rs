using Eve.Marshal;
using Eve.Services.Core;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [Fact]
    public async Task Operations_tutorial_and_category_replies_keep_v24_host_wire_shapes()
    {
        const long characterId = 90_000_001;
        AirNpeCharacterState? state = new("tutorial", 1);
        var tutorialReads = new List<long>();
        var service = new OperationsManagerService(
            newCharacterExperienceMode: () => "tutorial",
            readTutorialState: (id, _) =>
            {
                tutorialReads.Add(id);
                return Task.FromResult<AirNpeCharacterState?>(state);
            });
        Start(
            configure: registry => registry.Register(service),
            sessionOpened: session => session.Set("charid", new PyLong(characterId)));
        var (front, open) = await OpenFrontAsync();

        // Client TutorialOperationsController.get_tutorial_state() takes no arguments and falls back to session charid.
        Assert.Equal(new PyInt(21),
            await HostRpcAsync(front, open, 1, "operationsManager", "get_tutorial_state"));
        Assert.Equal(characterId, Assert.Single(tutorialReads));
        // Client is_main_tutorial_finished() passes session.charid explicitly.
        Assert.Same(PyBool.False,
            await HostRpcAsync(front, open, 2, "operationsManager", "is_main_tutorial_finished",
                new PyLong(characterId)));
        Assert.Same(PyBool.False,
            await HostRpcAsync(front, open, 3, "operationsManager", "has_skipped_tutorial"));

        state = new AirNpeCharacterState("tutorial", 3);
        Assert.Equal(new PyInt(23),
            await HostRpcAsync(front, open, 4, "operationsManager", "get_tutorial_state"));
        Assert.Same(PyBool.True,
            await HostRpcAsync(front, open, 5, "operationsManager", "is_main_tutorial_finished",
                new PyLong(characterId)));
        Assert.Same(PyBool.True,
            await HostRpcAsync(front, open, 6, "operationsManager", "has_skipped_tutorial"));

        // Client OperationsController.get_category_state / is_category_complete return the manager value directly.
        Assert.Equal(new PyInt(20),
            await HostRpcAsync(front, open, 7, "operationsManager", "get_category_state", new PyInt(2122)));
        Assert.Same(PyBool.False,
            await HostRpcAsync(front, open, 8, "operationsManager", "is_category_complete", new PyInt(2122)));
        // Client complete_category(category_id) calls complete_category_for_character(character_id, category_id).
        Assert.Same(PyBool.False,
            await HostRpcAsync(front, open, 9, "operationsManager", "complete_category_for_character",
                new PyLong(characterId), new PyInt(2122)));
        // Client is_mission_avoidance_system() tests membership in the returned collection, so an empty list is valid.
        Assert.Empty(Assert.IsType<PyList>(
            await HostRpcAsync(front, open, 10, "operationsManager", "get_mission_avoidance_systems")).Items);
    }
}
