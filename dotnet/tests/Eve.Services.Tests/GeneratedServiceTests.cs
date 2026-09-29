using Eve.Marshal;

namespace Eve.Services.Tests;

public class GeneratedServiceTests
{
    private static ServiceCall Call(string method, ClientSession? session = null, params PyValue[] args) => new()
    {
        Method = method,
        Args = args,
        Session = session ?? new ClientSession(),
    };

    [Fact]
    public async Task A_service_class_gets_its_dispatch_from_the_generator()
    {
        IService service = new FixtureService();
        Assert.Equal("fixtureSvc", service.Name);
        Assert.True(service.HasMethod("Echo"));
        Assert.False(service.HasMethod("echo")); // names are exact
        Assert.False(service.HasMethod("Nope"));
        Assert.Equal(new PyInt(42), await service.CallAsync(Call("Int")));
    }

    [Fact]
    public async Task A_method_the_service_lacks_is_answered_with_none()
    {
        IService service = new FixtureService();
        Assert.Equal(PyNone.Instance, await service.CallAsync(Call("Nope")));
        Assert.Equal(PyNone.Instance, await service.CallAsync(Call("")));
    }

    [Fact]
    public async Task Sync_task_and_value_task_methods_all_work()
    {
        IService service = new FixtureService();
        Assert.Equal(new PyList([new PyInt(1), new PyInt(2), new PyInt(3)]), await service.CallAsync(Call("Async")));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await service.CallAsync(Call("AsyncBoom")));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await service.CallAsync(Call("Boom")));
    }

    [Fact]
    public void The_catalog_lists_and_registers_every_service_with_its_aliases()
    {
        Assert.Equal(["fixtureSvc"], Eve.Services.Generated.Eve_Services_TestsServiceCatalog.Names);
        var registry = new ServiceRegistry();
        Eve.Services.Generated.Eve_Services_TestsServiceCatalog.Register(registry, new NoServices());
        var service = registry.Lookup("fixtureSvc");
        Assert.NotNull(service);
        Assert.Same(service, registry.Lookup("fixtureAlias"));
    }

    [Fact]
    public void The_catalog_prefers_the_instance_the_provider_supplies()
    {
        var supplied = new FixtureService();
        var registry = new ServiceRegistry();
        Eve.Services.Generated.Eve_Services_TestsServiceCatalog.Register(registry, new Supplying(supplied));
        Assert.Same(supplied, registry.Lookup("fixtureSvc"));
    }

    [Fact]
    public async Task A_service_sees_the_call_it_is_given()
    {
        var session = new ClientSession { CurrentBoundObjectId = "N=1:2" };
        IService service = new FixtureService();
        var result = await service.CallAsync(Call("BoundCall", session, new PyBytes(new byte[] { 65 })));
        var tuple = Assert.IsType<PyTuple>(result);
        Assert.Equal(new PyStr("N=1:2"), tuple.Items[0]);
        Assert.Equal(new PyTuple([new PyBuffer(new byte[] { 65 })]), tuple.Items[1]);
    }

    [Fact]
    public void Call_helpers_read_arguments_and_keywords()
    {
        var call = new ServiceCall
        {
            Method = "M",
            Args = [new PyInt(1)],
            Kwargs = new PyDict([new(new PyBytes("key"u8.ToArray()), new PyInt(9))]),
            Session = new ClientSession(),
        };
        Assert.Equal(new PyInt(1), call.Arg(0));
        Assert.Equal(PyNone.Instance, call.Arg(3));
        Assert.Equal(new PyInt(9), call.Kwarg("key"));
        Assert.Null(call.Kwarg("other"));
        Assert.Null(new ServiceCall { Method = "M", Args = [], Session = new ClientSession() }.Kwarg("key"));
    }

    private sealed class NoServices : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }

    private sealed class Supplying(FixtureService instance) : IServiceProvider
    {
        public object? GetService(Type serviceType) => serviceType == typeof(FixtureService) ? instance : null;
    }
}
