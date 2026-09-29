using Eve.Marshal;

namespace Eve.Services.Tests;

public class RegistryTests
{
    private sealed class Stub(string name, bool reuse = false) : IService, IBoundObjectReleaseHandler, IReusableBoundObject
    {
        public string Name { get; } = name;

        public List<string> Released { get; } = [];

        public bool ReuseBoundObjectForSession { get; } = reuse;

        public bool HasMethod(string method) => false;

        public ValueTask<PyValue> CallAsync(ServiceCall call) => new(PyNone.Instance);

        public void OnBoundObjectReleased(string oid, ClientSession? session) => Released.Add(oid);
    }

    [Fact]
    public void Services_are_found_by_name_and_alias()
    {
        var registry = new ServiceRegistry();
        var service = new Stub("a");
        registry.Register(service);
        registry.RegisterAlias(" alias ", "a");
        Assert.Same(service, registry.Lookup("a"));
        Assert.Same(service, registry.Lookup("alias"));
        Assert.Null(registry.Lookup("b"));
        Assert.Null(registry.Lookup(""));
        Assert.Null(registry.Lookup(null));
        Assert.Equal(2, registry.Count);
        Assert.Equal(["a", "alias"], registry.ServiceNames.Order());
    }

    [Fact]
    public void An_alias_needs_a_name_and_an_existing_target()
    {
        var registry = new ServiceRegistry();
        registry.Register(new Stub("a"));
        Assert.Throws<ArgumentException>(() => registry.RegisterAlias("  ", "a"));
        Assert.Throws<InvalidOperationException>(() => registry.RegisterAlias("x", "missing"));
        Assert.Throws<ArgumentException>(() => registry.Register(new Stub("")));
    }

    [Fact]
    public void Registering_a_name_twice_replaces_the_service_and_warns()
    {
        var registry = new ServiceRegistry();
        var warnings = new List<string>();
        registry.OnWarning = warnings.Add;
        var first = new Stub("a");
        var second = new Stub("a");
        registry.Register(first);
        registry.Register(second);
        Assert.Same(second, registry.Lookup("a"));
        Assert.Single(warnings);
    }

    [Fact]
    public void A_bound_object_is_found_by_its_oid_and_keeps_its_newest_refid()
    {
        var registry = new ServiceRegistry();
        var service = new Stub("a");
        registry.RegisterBoundObject("N=1:1", service, new PyLong(10));
        registry.RegisterBoundObject("N=1:1", service, new PyLong(5)); // older: ignored
        registry.RegisterBoundObject("N=1:1", service, new PyInt(99)); // a number is not a refID: ignored
        Assert.Same(service, registry.Lookup("N=1:1"));
        Assert.Equal(10, registry.BoundObjects["N=1:1"].RefId);

        registry.RegisterBoundObject("N=1:1", service, new PyLong(20));
        Assert.Equal(20, registry.BoundObjects["N=1:1"].RefId);
    }

    [Fact]
    public void A_session_remembers_the_bound_objects_it_was_given_once_each_until_they_are_released()
    {
        var registry = new ServiceRegistry();
        var service = new Stub("a");
        var session = new ClientSession();
        registry.RegisterBoundObject("N=1:1", service);
        registry.RegisterBoundObject("N=1:2", service);
        session.RememberBoundObject("N=1:1");
        session.RememberBoundObject("N=1:2");
        session.RememberBoundObject("N=1:1");
        Assert.Equal(["N=1:1", "N=1:2"], session.BoundOids);

        registry.UnregisterBoundObject("N=1:1", session);
        Assert.Equal(["N=1:2"], session.BoundOids);
        session.ForgetBoundObject("N=1:9"); // not there: nothing happens
        Assert.Equal(["N=1:2"], session.BoundOids);
    }

    [Fact]
    public void Releasing_removes_the_object_its_parameters_and_the_sessions_bookkeeping()
    {
        var registry = new ServiceRegistry();
        var service = new Stub("a");
        var session = new ClientSession
        {
            CurrentBoundObjectId = "N=1:1",
            LastBoundObjectId = "N=1:1",
        };
        session.BoundObjectIds["a"] = "N=1:1";
        session.BoundObjectIds["b"] = "N=2:2";
        session.BoundObjectStates["a"] = new BoundObjectState("N=1:1");
        registry.RegisterBoundObject("N=1:1", service, new PyLong(1));
        registry.RegisterBoundObjectParams("N=1:1", new PyInt(98000000));
        Assert.Equal(new PyInt(98000000), registry.GetBoundObjectParams("N=1:1"));

        Assert.True(registry.UnregisterBoundObject("N=1:1", session));
        Assert.Null(registry.Lookup("N=1:1"));
        Assert.Null(registry.GetBoundObjectParams("N=1:1"));
        Assert.Equal(["N=1:1"], service.Released);
        Assert.Equal(["b"], session.BoundObjectIds.Keys);
        Assert.Empty(session.BoundObjectStates);
        Assert.Null(session.CurrentBoundObjectId);
        Assert.Null(session.LastBoundObjectId);
        Assert.False(registry.UnregisterBoundObject("N=1:1", session)); // already gone
        Assert.False(registry.UnregisterBoundObject("", session));
    }

    [Fact]
    public void A_release_for_an_older_bind_keeps_the_object()
    {
        var registry = new ServiceRegistry();
        var service = new Stub("a");
        registry.RegisterBoundObject("N=1:1", service, new PyLong(100));
        Assert.False(registry.UnregisterBoundObject("N=1:1", releasedRefId: 99));
        Assert.NotNull(registry.Lookup("N=1:1"));
        Assert.Empty(service.Released);
        Assert.True(registry.UnregisterBoundObject("N=1:1", releasedRefId: 100));
        Assert.Null(registry.Lookup("N=1:1"));
    }

    [Fact]
    public void A_service_that_reuses_its_bound_object_keeps_the_sessions_identity_for_it()
    {
        var registry = new ServiceRegistry();
        var service = new Stub("a", reuse: true);
        var session = new ClientSession();
        session.BoundObjectIds["a"] = "N=1:1";
        registry.RegisterBoundObject("N=1:1", service);
        registry.UnregisterBoundObject("N=1:1", session);
        Assert.Equal("N=1:1", session.BoundObjectIds["a"]);
    }

    [Fact]
    public async Task Registration_and_lookup_are_safe_across_threads()
    {
        var registry = new ServiceRegistry();
        var service = new Stub("s");
        registry.Register(service);
        await Task.WhenAll(Enumerable.Range(0, 8).Select(t => Task.Run(() =>
        {
            for (var i = 0; i < 500; i++)
            {
                var oid = $"N={t}:{i}";
                registry.RegisterBoundObject(oid, service, new PyLong(i));
                Assert.Same(service, registry.Lookup(oid));
                Assert.True(registry.UnregisterBoundObject(oid));
            }
        })));
        Assert.Empty(registry.BoundObjects);
    }
}
