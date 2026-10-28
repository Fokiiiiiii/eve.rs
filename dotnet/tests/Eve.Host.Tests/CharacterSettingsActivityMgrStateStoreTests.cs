using Eve.Marshal;
using Eve.Services;
using Eve.Services.CharacterInfo;
using Eve.Services.Core;

namespace Eve.Host.Tests;

public sealed class CharacterSettingsActivityMgrStateStoreTests
{
    [Fact]
    public async Task Activity_state_survives_service_recreation_in_character_settings()
    {
        CharMgrPersonalData? personalData = CharMgrPersonalData.Empty;

        ValueTask<CharMgrPersonalData?> ReadAsync(long characterId, CancellationToken cancellationToken) =>
            ValueTask.FromResult<CharMgrPersonalData?>(personalData);

        ValueTask<CharMgrPersonalData?> UpdateAsync(
            long characterId,
            Func<CharMgrPersonalData, CharMgrPersonalData> update,
            CancellationToken cancellationToken)
        {
            personalData = update(personalData ?? CharMgrPersonalData.Empty);
            return ValueTask.FromResult<CharMgrPersonalData?>(personalData);
        }

        var first = new ActivityMgrService(new CharacterSettingsActivityMgrStateStore(ReadAsync, UpdateAsync));
        var session = new ClientSession { CharacterId = 1001 };
        Assert.Equal(PyBool.True, await Call(first, session, "ActivitySeen", new PyLong(4_294_967_297)));
        Assert.Equal(PyBool.True, await Call(first, session, "SetActivitiesPopState", PyBool.False));

        var afterRestart = new ActivityMgrService(new CharacterSettingsActivityMgrStateStore(ReadAsync, UpdateAsync));
        Assert.Equal(new PyList([new PyLong(4_294_967_297)]),
            await Call(afterRestart, session, "GetActivityBadgeStatuses"));
        Assert.Equal(new PyInt(0), await Call(afterRestart, session, "GetActivitiesPopState"));
        Assert.Contains("eve.rs.activityMgr.seenActivityIDs", personalData!.CharacterSettings.Keys);
        Assert.Contains("eve.rs.activityMgr.popState", personalData.CharacterSettings.Keys);
    }

    [Fact]
    public async Task Missing_character_state_does_not_return_fake_success()
    {
        var service = new ActivityMgrService(new MissingActivityMgrStateStore());
        var session = new ClientSession { CharacterId = 0 };

        Assert.Equal(PyBool.False, await Call(service, session, "ActivitySeen", new PyInt(42)));
        Assert.Equal(PyBool.False, await Call(service, session, "SetActivitiesPopState", PyBool.False));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            _ = await Call(service, session, "GetActivityBadgeStatuses");
        });
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            _ = await Call(service, session, "GetActivitiesPopState");
        });
    }

    private static ValueTask<PyValue> Call(IService service, ClientSession session, string method, params PyValue[] args) =>
        service.CallAsync(new ServiceCall { Method = method, Args = args, Session = session });

    private sealed class MissingActivityMgrStateStore : IActivityMgrStateStore
    {
        public ValueTask<ActivityMgrState?> ReadAsync(long characterId, CancellationToken cancellationToken) =>
            ValueTask.FromResult<ActivityMgrState?>(null);

        public ValueTask<ActivityMgrState?> UpdateAsync(
            long characterId,
            Func<ActivityMgrState, ActivityMgrState> update,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<ActivityMgrState?>(null);
    }
}
