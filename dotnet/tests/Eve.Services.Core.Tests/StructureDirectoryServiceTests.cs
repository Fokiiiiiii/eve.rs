using Eve.Marshal;

namespace Eve.Services.Core.Tests;

public sealed class StructureDirectoryServiceTests
{
    [Fact]
    public void Exposes_only_the_read_side_structure_directory_methods()
    {
        var service = new StructureDirectoryService(new TestReadCallbacks());

        Assert.Equal("structureDirectory", service.Name);
        Assert.True(service.HasMethod("GetJumpBridgesWithMyAccess"));
        Assert.True(service.HasMethod("GetNearbyJumpBridges"));
        Assert.True(service.HasMethod("GetStructureDescription"));
        Assert.False(service.HasMethod("SetStructureDescription"));
    }

    [Fact]
    public async Task Jump_bridge_rows_are_returned_unchanged_in_callback_order()
    {
        var callbacks = new TestReadCallbacks
        {
            JumpBridgesResult = Py.List(
                Py.KeyVal(("rowOrder", new PyInt(2)), ("label", new PyStr("second"))),
                Py.KeyVal(("rowOrder", new PyInt(1)), ("label", new PyStr("first")))),
        };
        var service = new StructureDirectoryService(callbacks);
        var call = Request("GetJumpBridgesWithMyAccess", new PyStr("jump-bridge-request"));

        var result = await service.CallAsync(call);

        Assert.Equal(callbacks.JumpBridgesResult, result);
        Assert.Same(call, callbacks.LastCall);
        Assert.Equal(new PyStr("jump-bridge-request"), callbacks.LastCall!.Arg(0));
        var rows = Assert.IsType<PyList>(result);
        Assert.Equal(new PyInt(2), Field(rows.Items[0], "rowOrder"));
        Assert.Equal(new PyStr("second"), Field(rows.Items[0], "label"));
        Assert.Equal(new PyInt(1), Field(rows.Items[1], "rowOrder"));
        Assert.Equal(new PyStr("first"), Field(rows.Items[1], "label"));
    }

    [Fact]
    public async Task Nearby_jump_bridge_empty_result_is_preserved_as_an_empty_list()
    {
        var callbacks = new TestReadCallbacks { NearbyJumpBridgesResult = Py.List() };
        var service = new StructureDirectoryService(callbacks);
        var call = Request("GetNearbyJumpBridges", new PyStr("nearby-request"));

        var result = await service.CallAsync(call);

        Assert.Same(call, callbacks.LastCall);
        Assert.Equal(new PyStr("nearby-request"), callbacks.LastCall!.Arg(0));
        Assert.Empty(Assert.IsType<PyList>(result).Items);
    }

    [Fact]
    public async Task Structure_description_result_is_returned_without_coercion()
    {
        var callbacks = new TestReadCallbacks { StructureDescriptionResult = new PyStr("description payload") };
        var service = new StructureDirectoryService(callbacks);
        var call = Request("GetStructureDescription", new PyStr("description-request"));

        var result = await service.CallAsync(call);

        Assert.Same(call, callbacks.LastCall);
        Assert.Equal(new PyStr("description-request"), callbacks.LastCall!.Arg(0));
        Assert.IsType<PyStr>(result);
        Assert.Equal(new PyStr("description payload"), result);
    }

    [Fact]
    public async Task Unknown_and_write_methods_return_none_without_calling_read_callbacks()
    {
        var callbacks = new TestReadCallbacks();
        var service = new StructureDirectoryService(callbacks);

        var result = await service.CallAsync(Request("SetStructureDescription", new PyInt(90000002), new PyStr("ignored")));

        Assert.Same(PyNone.Instance, result);
        Assert.Null(callbacks.LastCall);
    }

    private static ServiceCall Request(string method, params PyValue[] args) => new()
    {
        Method = method,
        Args = args,
        Session = null!,
    };

    private static PyValue Field(PyValue row, string name) =>
        Assert.IsType<PyDict>(Assert.IsType<PyObject>(row).Args).Entries
            .Single(entry => Assert.IsType<PyStr>(entry.Key).Value == name).Value;

    private sealed class TestReadCallbacks : IStructureDirectoryReadCallbacks
    {
        public PyValue JumpBridgesResult { get; init; } = Py.List();

        public PyValue NearbyJumpBridgesResult { get; init; } = Py.List();

        public PyValue StructureDescriptionResult { get; init; } = PyNone.Instance;

        public ServiceCall? LastCall { get; private set; }

        public ValueTask<PyValue> GetJumpBridgesWithMyAccessAsync(ServiceCall call) =>
            Complete(call, JumpBridgesResult);

        public ValueTask<PyValue> GetNearbyJumpBridgesAsync(ServiceCall call) =>
            Complete(call, NearbyJumpBridgesResult);

        public ValueTask<PyValue> GetStructureDescriptionAsync(ServiceCall call) =>
            Complete(call, StructureDescriptionResult);

        private ValueTask<PyValue> Complete(ServiceCall call, PyValue result)
        {
            LastCall = call;
            return ValueTask.FromResult(result);
        }
    }
}
