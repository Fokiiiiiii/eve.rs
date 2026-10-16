using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;

namespace Eve.Services.Core.Tests;

public sealed class AchievementTrackerWriteTests
{
    [Fact]
    public async Task An_update_hands_both_maps_to_the_store_and_a_character_without_a_record_reads_empty_maps()
    {
        long? savedCharacter = null;
        PyDict? savedCompleted = null, savedEvents = null;
        var achievement = new AchievementTrackerMgrService(writeState: (characterId, completed, events, _) =>
        {
            savedCharacter = characterId;
            savedCompleted = completed;
            savedEvents = events;
            return ValueTask.CompletedTask;
        });
        var session = new ClientSession();
        session.Set("charid", new PyLong(42));
        var completed = new PyDict([new(new PyInt(9), new PyLong(134273341290000000))]);
        var events = new PyDict([new(new PyStr("client_openMap"), new PyInt(3))]);

        Assert.Same(PyNone.Instance, await achievement.CallAsync(new ServiceCall
        {
            Method = "UpdateClientAchievmentsAndCounters", Args = [completed, events], Session = session,
        }));
        Assert.Equal(42, savedCharacter);
        Assert.Same(completed, savedCompleted);
        Assert.Same(events, savedEvents);

        var read = Assert.IsType<PyDict>(await achievement.CallAsync(new ServiceCall
        {
            Method = "GetCompletedAchievementsAndClientEventCount", Args = [], Session = session,
        }));
        var entries = read.Entries.ToDictionary(entry => ((PyStr)entry.Key).Value, entry => (PyDict)entry.Value);
        Assert.Empty(entries["eventDict"].Entries);
        Assert.Empty(entries["completedDict"].Entries);
    }
}
