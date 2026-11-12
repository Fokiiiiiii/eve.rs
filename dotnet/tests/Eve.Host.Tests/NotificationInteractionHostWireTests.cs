using System.Text.Json.Nodes;
using Eve.Marshal;
using Eve.Services.Core;
using Xunit;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    // Client call site: notifications/client/notificationCenter.py OnEntryClick sends
    // sm.RemoteSvc('notificationMgr').LogNotificationInteraction(notification.data.get('reference_id', None)),
    // so the single positional argument may be an int or None.
    [Fact]
    public async Task Notification_log_interaction_accepts_the_client_reference_id_over_host_wire()
    {
        var logged = new List<(long ReferenceId, long CharacterId)>();
        var service = new NotificationMgrService(
            (_, _) => ValueTask.FromResult<JsonObject?>(null),
            () => 1_700_000_000_000L,
            logInteraction: (referenceId, session, _) =>
            {
                logged.Add((referenceId, session.CharacterId ?? 0));
                return ValueTask.CompletedTask;
            });
        Start(
            configure: registry => registry.Register(service),
            sessionOpened: session => session.Set("charid", new PyInt(20)));
        var (front, open) = await OpenFrontAsync();

        Assert.Same(PyNone.Instance, await HostRpcAsync(
            front, open, 1, "notificationMgr", "LogNotificationInteraction", new PyInt(55)));
        Assert.Same(PyNone.Instance, await HostRpcAsync(
            front, open, 2, "notificationMgr", "LogNotificationInteraction", PyNone.Instance));
        Assert.Equal([(55L, 20L), (0L, 20L)], logged);
    }
}
