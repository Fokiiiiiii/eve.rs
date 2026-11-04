using Eve.Marshal;
using Eve.Services;
using Eve.Services.Inventory;

namespace Eve.Host.Tests;

public sealed class CorporationDeliveriesHostWireTests
{
    private const long CharacterId = 90000001;
    private const long CorporationId = 98000001;
    private const long StationId = 60000001;

    private static ClientSession Session()
    {
        var session = new ClientSession() { SessionId = 1 };
        session.Change(
            ("charid", new PyLong(CharacterId)),
            ("corpid", new PyLong(CorporationId)),
            ("stationid", new PyLong(StationId)));
        return session;
    }

    private static ServiceCall DeliveryCall(ClientSession session) => new()
    {
        Method = "DeliverToCorpHangar",
        Args =
        [
            new PyLong(StationId),
            new PyLong(StationId),
            new PyList([new PyLong(70000001)]),
            PyNone.Instance,
            new PyLong(CharacterId),
            new PyInt(Eve.Inventory.ItemFlags.CorpSAG1),
        ],
        Session = session,
    };

    [Fact]
    public async Task Registered_invbroker_opens_the_corporation_deliveries_container()
    {
        var service = new InvBrokerService(null!, null!, null!, null!, null!, () => 0);
        var registry = new ServiceRegistry();
        registry.Register(service);

        var result = await registry.Lookup("invbroker")!.CallAsync(new ServiceCall
        {
            Method = "GetInventory",
            Args = [new PyInt(InvBrokerService.ContainerCorpMarket)],
            Session = Session(),
        });

        Assert.IsType<PySubStruct>(result);
    }

    [Fact]
    public async Task DeliverToCorpHangar_checks_for_an_active_office_before_reading_items()
    {
        var officeChecks = 0;
        var service = new InvBrokerService(
            null!, null!, null!, null!, null!, () => 0,
            corporationHangarAccess: (_, _, _, _, _, _) => ValueTask.FromResult(true),
            hasCorporationOffice: (_, _, _) =>
            {
                officeChecks++;
                return ValueTask.FromResult(false);
            });
        var registry = new ServiceRegistry();
        registry.Register(service);

        var result = await registry.Lookup("invbroker")!.CallAsync(DeliveryCall(Session()));

        Assert.Same(PyNone.Instance, result);
        Assert.Equal(1, officeChecks);
    }

    [Fact]
    public async Task DeliverToCorpHangar_checks_corporation_access_before_reading_items()
    {
        var accessChecks = 0;
        var service = new InvBrokerService(
            null!, null!, null!, null!, null!, () => 0,
            corporationHangarAccess: (_, _, _, _, _, _) =>
            {
                accessChecks++;
                return ValueTask.FromResult(false);
            },
            hasCorporationOffice: (_, _, _) => ValueTask.FromResult(true));
        var registry = new ServiceRegistry();
        registry.Register(service);

        var result = await registry.Lookup("invbroker")!.CallAsync(DeliveryCall(Session()));

        Assert.Same(PyNone.Instance, result);
        Assert.Equal(1, accessChecks);
    }
}
