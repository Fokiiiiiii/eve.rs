using Eve.Marshal;

namespace Eve.Services;

/// <summary>One item as the client's inventory rows show it (shared by the adapters that hand items out: the inventory broker and dogma).</summary>
public sealed record ItemRow(long ItemId, int TypeId, long OwnerId, long LocationId, int Flag, long Quantity, int GroupId, int CategoryId);

/// <summary>How inventory rows look on the wire: the descriptor the client is told about, and the three forms a row comes in.</summary>
public static class InventoryWire
{
    /// <summary>The names of the columns, in the order of every row form.</summary>
    public static readonly string[] Columns =
        ["itemID", "typeID", "ownerID", "locationID", "flagID", "quantity", "groupID", "categoryID", "customInfo", "stacksize", "singleton"];

    // The DBTYPE codes of the stored columns: 20 is a 64-bit integer, 3 a 32-bit one, 2 a 16-bit one, 129 a string.
    private static readonly (string Name, int Type)[] Stored =
        [("itemID", 20), ("typeID", 3), ("ownerID", 3), ("locationID", 20), ("flagID", 2), ("quantity", 3), ("groupID", 3), ("categoryID", 3), ("customInfo", 129)];

    /// <summary>The header of the row descriptor: the stored columns, and the two the client works out (stack size and singleton).</summary>
    public static readonly PyValue DescriptorHeader = new PyObjectEx(
        ObjectExKind.Ex1,
        Py.Tuple(
            new PyToken("blue.DBRowDescriptor"),
            Py.Tuple(Py.Tuple([.. Stored.Select(c => (PyValue)Py.Tuple(new PyStr(c.Name), new PyInt(c.Type)))])),
            new PyList([
                Py.Tuple(new PyStr("stacksize"), new PyToken("eve.common.script.sys.eveCfg.StackSize")),
                Py.Tuple(new PyStr("singleton"), new PyToken("eve.common.script.sys.eveCfg.Singleton")),
            ])),
        [],
        []);

    private static readonly Descriptor RowDescriptor = Descriptor.FromHeader(DescriptorHeader);

    /// <summary>A whole number as JavaScript's marshal writes it: an integer that fits 32 bits is an int, a larger one a long.</summary>
    public static PyValue Id(long value) => value is >= int.MinValue and <= int.MaxValue ? new PyInt(value) : new PyLong(value);

    /// <summary>
    /// A packed row (<c>blue.DBRow</c>): the stored columns, the client fills in the rest. The custom info is an empty string
    /// unless the caller says otherwise (dogma's item info leaves it out, <c>None</c>, for everything but the character).
    /// </summary>
    public static PyValue PackedRow(ItemRow item, PyValue? customInfo = null) => new PyPackedRow(RowDescriptor, [
        new PyLong(item.ItemId), new PyInt(item.TypeId), new PyInt(item.OwnerId), new PyLong(item.LocationId), new PyInt(item.Flag),
        new PyInt(item.Quantity), new PyInt(item.GroupId), new PyInt(item.CategoryId), customInfo ?? new PyStr("")]);

    private static long StackSize(ItemRow item) => item.Quantity < 0 ? 1 : item.Quantity;

    private static IReadOnlyList<PyValue> Line(ItemRow item, int singleton) =>
    [
        Id(item.ItemId), new PyInt(item.TypeId), Id(item.OwnerId), Id(item.LocationId), new PyInt(item.Flag), Id(item.Quantity),
        new PyInt(item.GroupId), new PyInt(item.CategoryId), new PyStr(""), Id(StackSize(item)), new PyInt(singleton),
    ];

    /// <summary>One item as a tuple of its columns in order, as the loot window's <c>OnWreckLootAll</c> carries the items that were taken.</summary>
    public static PyValue Tuple(ItemRow item) => Py.Tuple([.. Line(item, item.Quantity < 0 ? 1 : 0)]);

    /// <summary>A <c>util.Row</c>: the header and one line. The reference always says the item is a singleton here.</summary>
    public static PyValue UtilRow(ItemRow item) => new PyObject(
        new PyStr("util.Row"),
        new PyDict([
            new(new PyStr("header"), Py.Tuple([.. Columns.Select(c => (PyValue)new PyStr(c))])),
            new(new PyStr("line"), Py.Tuple([.. Line(item, 1)])),
        ]));

    /// <summary>A <c>Rowset</c> of the items, each line a tuple in column order.</summary>
    public static PyValue Rowset(IEnumerable<ItemRow> items) => new PyObject(
        new PyStr("eve.common.script.sys.rowset.Rowset"),
        new PyDict([
            new(new PyStr("header"), new PyList([.. Columns.Select(c => (PyValue)new PyStr(c))])),
            new(new PyStr("RowClass"), new PyToken("util.Row")),
            new(new PyStr("lines"), new PyList([.. items.Select(item => (PyValue)Py.Tuple([.. Line(item, item.Quantity < 0 ? 1 : 0)]))])),
        ]));

    /// <summary>What <c>List</c> answers when a flag was asked for and nothing is in it: an empty Python <c>set</c>.</summary>
    public static PyValue EmptySet() => Set([]);

    /// <summary>A Python <c>set</c> of values (how the reference answers what is in a docked ship when no flag is asked for).</summary>
    public static PyValue Set(IEnumerable<PyValue> values) => new PyObjectEx(ObjectExKind.Ex1, Py.Tuple(new PyToken("__builtin__.set"), Py.Tuple(new PyList([.. values]))), [], []);
}
