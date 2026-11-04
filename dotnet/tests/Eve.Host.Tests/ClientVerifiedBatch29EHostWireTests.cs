using Eve.Marshal;
using Eve.Services.Core;
using Xunit;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [Fact]
    public async Task Eveguard_heartbeat_calls_record_events_through_host_wire()
    {
        const long characterId = 90000001;
        const long accountId = 90000002;
        var service = new EveguardReportService();
        Start(configure: registry => registry.Register(service),
            sessionOpened: session =>
            {
                session.Set("charid", new PyInt(characterId));
                session.Set("userid", new PyInt(accountId));
            });
        var (front, open) = await OpenFrontAsync();

        Assert.Same(PyNone.Instance, await HostRpcAsync(
            front, open, 1, "eveguard_report", "heartbeat_started"));
        Assert.Same(PyNone.Instance, await HostRpcAsync(
            front, open, 2, "eveguard_report", "heartbeat_stopped"));
        Assert.Same(PyNone.Instance, await HostRpcAsync(
            front, open, 3, "eveguard_report", "heartbeat_stopped"));

        var events = service.Events;
        Assert.Equal(["started", "stopped", "stopped"], events.Select(entry => entry.EventType));
        Assert.Equal([1, 2, 3], events.Select(entry => entry.EventId));
        Assert.All(events, entry =>
        {
            Assert.Equal(characterId, entry.CharacterId);
            Assert.Equal(accountId, entry.AccountId);
            Assert.True(entry.RecordedAt > DateTimeOffset.MinValue);
        });
    }
}
