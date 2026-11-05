using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;

namespace Eve.Services.Core.Tests;

public sealed class ActivityMgrServiceTests
{
    [Fact]
    public async Task Activities_calls_track_character_state_and_refresh_badges()
    {
        var sink = new PacketSink();
        var session = new ClientSession(sink) { CharacterId = 1001 };
        var service = new ActivityMgrService();

        Assert.Equal(PyIntForTest(1), await Call(service, session, "GetActivitiesPopState"));
        Assert.Equal(PyBool.True, await Call(service, session, "ActivitySeen", new PyInt(42)));
        Assert.Equal(PyBool.True, await Call(service, session, "ActivitySeen", new PyInt(7)));
        Assert.Equal(PyBool.False, await Call(service, session, "ActivitySeen", new PyInt(0)));
        Assert.Equal(new PyList([new PyInt(7), new PyInt(42)]),
            await Call(service, session, "GetActivityBadgeStatuses"));

        Assert.Single(sink.Packets);
        Assert.Equal(ClientNotification.Build(session.UserId ?? 0, session.NodeId, "OnSystemActivityUpdated", "None", Array.Empty<PyValue>(), 0), sink.Packets[0]);

        var sameCharacter = new ClientSession { CharacterId = 1001 };
        Assert.Equal(new PyList([new PyInt(7), new PyInt(42)]),
            await Call(service, sameCharacter, "GetActivityBadgeStatuses"));
        var otherCharacter = new ClientSession { CharacterId = 1002 };
        Assert.Equal(new PyList([]), await Call(service, otherCharacter, "GetActivityBadgeStatuses"));

        Assert.Equal(PyBool.True, await Call(service, session, "SetActivitiesPopState", new PyInt(0)));
        Assert.Equal(PyIntForTest(0), await Call(service, session, "GetActivitiesPopState"));
        Assert.Single(sink.Packets);
    }

    [Fact]
    public void Core_factory_registers_the_activity_manager_methods()
    {
        var service = Assert.Single(CoreServices.Create(new NoAccounts(), () => 0),
            candidate => candidate.Name == "activityMgr");

        Assert.All(new[]
        {
            "ActivitySeen", "GetActivityBadgeStatuses", "GetActivitiesPopState", "SetActivitiesPopState",
        }, method => Assert.True(service.HasMethod(method)));
        Assert.Contains("activityMgr", CoreServices.Names);
    }

    private static ValueTask<PyValue> Call(IService service, ClientSession session, string method, params PyValue[] args) =>
        service.CallAsync(new ServiceCall { Method = method, Args = args, Session = session });

    private static PyInt PyIntForTest(int value) => new(value);

    private sealed class PacketSink : IPacketSink
    {
        public List<PyValue> Packets { get; } = [];
        public void Send(PyValue packet) => Packets.Add(packet);
    }

    private sealed class NoAccounts : IOmegaAccountStore
    {
        public string? GetOmegaExpiry(long accountId) => OmegaLicenses.DefaultOmegaExpiryText;
    }
}
