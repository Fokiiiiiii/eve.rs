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

    [Fact]
    public async Task Achievement_tracker_refuses_malformed_or_oversized_state_before_storage()
    {
        var writes = 0;
        var service = new AchievementTrackerMgrService(writeState: (_, _, _, _) =>
        {
            writes++;
            return ValueTask.CompletedTask;
        });
        var session = new ClientSession { CharacterId = 42 };
        var validCompleted = new PyDict([new(new PyInt(9), new PyLong(1234))]);
        var validEvents = new PyDict([new(new PyStr("client_openMap"), new PyInt(3))]);

        async Task Refused(ServiceCall call, string expected)
        {
            var error = await Assert.ThrowsAsync<MachoWrappedException>(() => service.CallAsync(call).AsTask());
            Assert.Equal(expected, error.Error.UserErrorMessage);
        }

        await Refused(Call("UpdateClientAchievmentsAndCounters", session), "ACHIEVEMENT_STATE_INVALID");
        await Refused(new ServiceCall
        {
            Method = "UpdateClientAchievmentsAndCounters",
            Args = [validCompleted, validEvents, new PyInt(1)],
            Session = session,
        }, "ACHIEVEMENT_STATE_INVALID");
        await Refused(new ServiceCall
        {
            Method = "UpdateClientAchievmentsAndCounters",
            Args = [validCompleted, validEvents],
            Kwargs = new PyDict([]),
            Session = session,
        }, "ACHIEVEMENT_ARGUMENTS_INVALID");
        await Refused(Call("UpdateClientAchievmentsAndCounters", session,
            new PyDict([new(new PyStr("9"), new PyLong(1234))]), validEvents), "ACHIEVEMENT_STATE_INVALID");
        await Refused(Call("UpdateClientAchievmentsAndCounters", session, validCompleted,
            new PyDict([new(new PyStr("client_openMap"), new PyReal(1.5))])), "ACHIEVEMENT_STATE_INVALID");
        var tooMany = new PyDict([.. Enumerable.Range(1, 5001).Select(id =>
            new KeyValuePair<PyValue, PyValue>(new PyInt(id), new PyLong(1234)))]);
        await Refused(Call("UpdateClientAchievmentsAndCounters", session, tooMany, validEvents), "ACHIEVEMENT_STATE_TOO_LARGE");
        await Refused(Call("HasEverWarped", session, new PyInt(42)), "ACHIEVEMENT_ARGUMENTS_INVALID");
        await Refused(Call("GetCompletedAchievementsAndClientEventCount", session, new PyInt(42)), "ACHIEVEMENT_ARGUMENTS_INVALID");

        Assert.Equal(0, writes);

        static ServiceCall Call(string method, ClientSession session, params PyValue[] args) => new()
        {
            Method = method,
            Args = args,
            Session = session,
        };
    }
}
