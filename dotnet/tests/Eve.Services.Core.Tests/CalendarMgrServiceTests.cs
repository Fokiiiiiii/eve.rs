using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;

namespace Eve.Services.Core.Tests;

public class CalendarMgrServiceTests
{
    [Theory]
    [InlineData("calendarMgr", "GetResponsesForCharacter")]
    [InlineData("calendarProxy", "GetEventList")]
    public async Task An_unconfigured_calendar_returns_the_reference_empty_wire_shape(
        string serviceName, string method)
    {
        var service = CalendarServices().Lookup(serviceName)!;
        Assert.Equal(serviceName, service.Name);
        Assert.True(service.HasMethod(method));
        var args = method == "GetEventList"
            ? new PyValue[] { new PyInt(10), new PyInt(2026) }
            : [];
        var result = await service.CallAsync(Request(method, args));

        PyValue expected = method == "GetEventList"
            ? new PyTuple([new PyList([]), PyNone.Instance, PyNone.Instance])
            : new PyList([]);
        Assert.Equal(MarshalCodec.Encode(expected), MarshalCodec.Encode(result));

        // eveCalendarsvc.GetEventsByMonthYear flattens every non-None collection.
        // GetEventResponses iterates the response list directly.
        var decoded = MarshalCodec.Decode(MarshalCodec.Encode(result));
        if (method == "GetEventList")
        {
            var groups = Assert.IsType<PyTuple>(decoded);
            Assert.Equal(3, groups.Items.Count);
            Assert.Empty(Assert.IsType<PyList>(groups.Items[0]).Items);
            Assert.IsType<PyNone>(groups.Items[1]);
            Assert.IsType<PyNone>(groups.Items[2]);
        }
        else
        {
            Assert.Empty(Assert.IsType<PyList>(decoded).Items);
        }
    }

    [Theory]
    [InlineData("calendarMgr", "GetResponsesToEvent", true)]
    [InlineData("calendarMgr", "GetResponsesToEvent", false)]
    [InlineData("calendarProxy", "GetEventDetails", true)]
    [InlineData("calendarProxy", "GetEventDetails", false)]
    public async Task Event_specific_reads_report_not_found_instead_of_fabricating_an_event(
        string serviceName, string method, bool includeEventId)
    {
        var service = CalendarServices().Lookup(serviceName)!;
        Assert.True(service.HasMethod(method));
        PyValue[] args = includeEventId
            ? [new PyLong(980000000000L), new PyInt(90000001)]
            : [];
        var error = await Assert.ThrowsAsync<MachoWrappedException>(
            () => service.CallAsync(Request(method, args)).AsTask());

        Assert.Equal(Eve.Macho.MachoErrorType.WrappedException, error.Error.ErrorCode);
        Assert.True(error.Error.IsUserError);
        Assert.Equal("CustomNotify", error.Error.UserErrorMessage);
        var expected = MachoErrors.UserError(
            "CustomNotify", ("notify", new PyStr("Calendar event not found.")));
        Assert.Equal(MarshalCodec.Encode(expected.Payload), MarshalCodec.Encode(error.Error.Payload));
    }

    [Fact]
    public void Manager_and_proxy_keep_the_client_service_and_method_routing()
    {
        var services = CalendarServices();
        var manager = services.Lookup("calendarMgr")!;
        var proxy = services.Lookup("calendarProxy")!;

        Assert.True(manager.HasMethod("GetResponsesForCharacter"));
        Assert.True(manager.HasMethod("GetResponsesToEvent"));
        Assert.False(manager.HasMethod("GetEventList"));
        Assert.False(manager.HasMethod("GetEventDetails"));
        Assert.True(proxy.HasMethod("GetEventList"));
        Assert.True(proxy.HasMethod("GetEventDetails"));
        Assert.False(proxy.HasMethod("GetResponsesForCharacter"));
        Assert.False(proxy.HasMethod("GetResponsesToEvent"));

        string[] writers =
        [
            "CreatePersonalEvent", "CreateCorporationEvent", "CreateAllianceEvent",
            "EditPersonalEvent", "EditCorporationEvent", "EditAllianceEvent",
            "DeleteEvent", "UpdateEventParticipants", "SendEventResponse",
        ];
        foreach (var writer in writers)
        {
            Assert.False(manager.HasMethod(writer));
            Assert.False(proxy.HasMethod(writer));
        }
    }

    private static ServiceRegistry CalendarServices()
    {
        var services = new ServiceRegistry();
        services.Register((IService)new CalendarMgrService());
        services.Register((IService)new CalendarProxyService());
        return services;
    }

    private static ServiceCall Request(string method, params PyValue[] args) => new()
    {
        Method = method,
        Session = new ClientSession(new Sink()) { CharacterId = 90000001 },
        Args = args,
    };

    private sealed class Sink : IPacketSink
    {
        public void Send(PyValue packet) { }
    }
}
