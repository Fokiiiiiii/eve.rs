using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;

namespace Eve.Services.Core.Tests;

public sealed class PosMgrServiceTests
{
    [Fact]
    public async Task GetControlTowers_returns_an_empty_list_without_a_provider()
    {
        var service = (IService)new PosMgrService();

        Assert.Equal("posMgr", service.Name);
        Assert.True(service.HasMethod("GetControlTowers"));
        Assert.Empty(Assert.IsType<PyList>(await service.CallAsync(Call("GetControlTowers"))).Items);
    }

    [Fact]
    public async Task GetControlTowers_emits_reference_rows_from_the_typed_provider()
    {
        var session = new ClientSession();
        ClientSession? receivedSession = null;
        CancellationToken receivedCancellation = default;
        var service = (IService)new PosMgrService((actualSession, cancellationToken) =>
        {
            receivedSession = actualSession;
            receivedCancellation = cancellationToken;
            return ValueTask.FromResult<IReadOnlyList<ControlTowerRow>>(
            [
                new(16213, 9002, 30000142),
                new(16214, 9001, 30000143),
                new(0, 9003, 30000144),
            ]);
        });
        using var cancellation = new CancellationTokenSource();
        var result = Assert.IsType<PyList>(await service.CallAsync(new ServiceCall
        {
            Method = "GetControlTowers",
            Args = [],
            Session = session,
            Cancellation = cancellation.Token,
        }));

        Assert.Same(session, receivedSession);
        Assert.Equal(cancellation.Token, receivedCancellation);
        Assert.Equal(2, result.Items.Count);
        Assert.Equal(new PyList([new PyInt(16213), new PyInt(9002), new PyInt(30000142)]), result.Items[0]);
        Assert.Equal(new PyList([new PyInt(16214), new PyInt(9001), new PyInt(30000143)]), result.Items[1]);
    }

    [Fact]
    public async Task Core_factory_wires_the_control_tower_provider()
    {
        var session = new ClientSession();
        var services = CoreServices.Create(
            new PolicyOmegaAccounts(),
            () => 0,
            readControlTowerRows: (actualSession, _) =>
            {
                Assert.Same(session, actualSession);
                return ValueTask.FromResult<IReadOnlyList<ControlTowerRow>>([new(16213, 9002, 30000142)]);
            });
        var service = Assert.Single(services, candidate => candidate.Name == "posMgr");

        var result = Assert.IsType<PyList>(await service.CallAsync(new ServiceCall
        {
            Method = "GetControlTowers",
            Args = [],
            Session = session,
        }));

        Assert.Equal(new PyList([new PyInt(16213), new PyInt(9002), new PyInt(30000142)]), Assert.Single(result.Items));
    }

    private static ServiceCall Call(string method) => new()
    {
        Method = method,
        Args = [],
        Session = new ClientSession(),
    };

    private sealed class PolicyOmegaAccounts : IOmegaAccountStore
    {
        public string? GetOmegaExpiry(long accountId) => OmegaLicenses.DefaultOmegaExpiryText;
    }

    [Fact]
    public async Task UnanchorStructure_hands_the_tower_to_the_provider_with_the_session_and_answers_none()
    {
        var session = new ClientSession();
        ClientSession? receivedSession = null;
        long receivedItem = 0;
        var service = (IService)new PosMgrService(unanchorControlTower: (actualSession, itemId, _) =>
        {
            receivedSession = actualSession;
            receivedItem = itemId;
            return ValueTask.CompletedTask;
        });
        using var cancellation = new CancellationTokenSource();

        Assert.True(service.HasMethod("UnanchorStructure"));
        Assert.IsType<PyNone>(await service.CallAsync(new ServiceCall
        {
            Method = "UnanchorStructure",
            Args = [new PyLong(1_000_000_123)],
            Session = session,
            Cancellation = cancellation.Token,
        }));
        Assert.Same(session, receivedSession);
        Assert.Equal(1_000_000_123, receivedItem);
    }

    [Fact]
    public async Task UnanchorStructure_without_a_provider_or_an_item_does_nothing()
    {
        var service = (IService)new PosMgrService();
        using var cancellation = new CancellationTokenSource();

        Assert.IsType<PyNone>(await service.CallAsync(new ServiceCall
        {
            Method = "UnanchorStructure",
            Args = [new PyInt(5)],
            Session = new ClientSession(),
            Cancellation = cancellation.Token,
        }));
    }
}
