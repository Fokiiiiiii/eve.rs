using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;

namespace Eve.Services.Core.Tests;

public sealed class AccessGroupBookmarkMgrWireContractTests
{
    private sealed class Sink : IPacketSink
    {
        public void Send(PyValue packet) { }
    }

    [Fact]
    public void Delete_subfolder_updates_follow_the_client_order_with_and_without_bookmarks()
    {
        var updates = AccessGroupBookmarkMgrService.DeleteSubfolderUpdates(50, 70, [80, 90]);

        AssertUpdate(Assert.IsType<PyList>(updates[0]), 50, "subfolderRemoved", 70);
        AssertUpdate(Assert.IsType<PyList>(updates[1]), 50, "bookmarksRemoved", [80, 90]);

        var empty = AccessGroupBookmarkMgrService.DeleteSubfolderUpdates(50, 70, []);
        var onlyUpdate = Assert.IsType<PyList>(Assert.Single(empty));
        AssertUpdate(onlyUpdate, 50, "subfolderRemoved", 70);
    }

    [Fact]
    public void Static_location_subfolder_accepts_keyword_positional_integer_and_none_shapes()
    {
        var keyword = Call(
            [new PyLong(1), new PyLong(2), new PyStr("name"), new PyStr("note"), new PyInt(0)],
            Kwargs(("machoVersion", new PyInt(1)), ("subfolderID", new PyInt(73))));
        Assert.Equal(73, AccessGroupBookmarkMgrService.StaticLocationSubfolderId(keyword));

        var positional = Call(
            [new PyLong(1), new PyLong(2), new PyStr("name"), new PyStr("note"), new PyInt(0), new PyLong(74)],
            PyNone.Instance);
        Assert.Equal(74, AccessGroupBookmarkMgrService.StaticLocationSubfolderId(positional));

        var noSubfolder = Call(
            [new PyLong(1), new PyLong(2), new PyStr("name"), new PyStr("note"), new PyInt(0)],
            Kwargs(("subfolderID", PyNone.Instance)));
        Assert.Null(AccessGroupBookmarkMgrService.StaticLocationSubfolderId(noSubfolder));
    }

    private static ServiceCall Call(PyValue[] args, PyValue kwargs) => new()
    {
        Method = "BookmarkStaticLocation",
        Args = args,
        Kwargs = kwargs,
        Session = new ClientSession(new Sink()),
    };

    private static PyDict Kwargs(params (string Name, PyValue Value)[] entries) =>
        new([.. entries.Select(entry => new KeyValuePair<PyValue, PyValue>(new PyStr(entry.Name), entry.Value))]);

    private static void AssertUpdate(PyList update, long folderId, string name, long argument)
    {
        var parts = update.Items;
        Assert.Equal(3, parts.Count);
        var folderIds = Assert.IsType<PyList>(parts[0]);
        Assert.Equal(folderId, Number(Assert.Single(folderIds.Items)));
        Assert.Equal(name, Assert.IsType<PyStr>(parts[1]).Value);
        Assert.Equal(argument, Number(parts[2]));
    }

    private static void AssertUpdate(PyList update, long folderId, string name, long[] arguments)
    {
        var parts = update.Items;
        Assert.Equal(3, parts.Count);
        var folderIds = Assert.IsType<PyList>(parts[0]);
        Assert.Equal(folderId, Number(Assert.Single(folderIds.Items)));
        Assert.Equal(name, Assert.IsType<PyStr>(parts[1]).Value);
        var ids = Assert.IsType<PyList>(parts[2]);
        Assert.Equal(arguments, ids.Items.Select(Number));
    }

    private static long Number(PyValue value) => value switch
    {
        PyInt number => number.Value,
        PyLong number => number.Value,
        _ => throw new InvalidOperationException($"expected a wire id, got {value.GetType().Name}"),
    };
}
