using System.Numerics;
using Eve.Marshal;

namespace Eve.Services.Tests;

public class WideIntegersTests
{
    [Fact]
    public void Unchanged_self_referencing_list_keeps_its_identity()
    {
        var items = new PyValue[1];
        var list = new PyList(items);
        items[0] = list;

        Assert.Same(list, WideIntegers.Narrow(list));
    }

    [Fact]
    public void Unchanged_self_referencing_dict_keeps_its_identity()
    {
        var entries = new KeyValuePair<PyValue, PyValue>[1];
        var dict = new PyDict(entries);
        entries[0] = new(new PyInt(1), dict);

        Assert.Same(dict, WideIntegers.Narrow(dict));
    }

    [Fact]
    public void Shared_child_remains_shared_after_narrowing()
    {
        var child = PyList.Of(new PyBigInteger(new BigInteger(42)));
        var root = PyTuple.Of(child, child);

        var result = Assert.IsType<PyTuple>(WideIntegers.Narrow(root));
        Assert.Same(result.Items[0], result.Items[1]);
        Assert.Equal(42L, Assert.IsType<PyLong>(Assert.IsType<PyList>(result.Items[0]).Items[0]).Value);
    }

    [Fact]
    public void Wide_integer_inside_cycle_is_narrowed_and_cycle_is_preserved()
    {
        var items = new PyValue[2];
        var list = new PyList(items);
        items[0] = list;
        items[1] = new PyBigInteger(new BigInteger(42));

        var result = Assert.IsType<PyList>(WideIntegers.Narrow(list));
        Assert.NotSame(list, result);
        Assert.Same(result, result.Items[0]);
        Assert.Equal(42L, Assert.IsType<PyLong>(result.Items[1]).Value);
        Assert.IsType<PyBigInteger>(list.Items[1]);
    }

    [Fact]
    public void Dict_cycle_is_rebuilt_when_its_integer_changes()
    {
        var entries = new KeyValuePair<PyValue, PyValue>[2];
        var dict = new PyDict(entries);
        entries[0] = new(new PyInt(1), dict);
        entries[1] = new(new PyInt(2), new PyBigInteger(new BigInteger(42)));

        var result = Assert.IsType<PyDict>(WideIntegers.Narrow(dict));
        Assert.Same(result, result.Entries[0].Value);
        Assert.Equal(42L, Assert.IsType<PyLong>(result.Entries[1].Value).Value);
    }

    [Fact]
    public void Object_in_a_cycle_is_rebuilt_with_its_new_arguments()
    {
        var items = new PyValue[2];
        var list = new PyList(items);
        var obj = new PyObject(new PyInt(1), list);
        items[0] = obj;
        items[1] = new PyBigInteger(new BigInteger(42));

        var result = Assert.IsType<PyObject>(WideIntegers.Narrow(obj));
        var args = Assert.IsType<PyList>(result.Args);
        Assert.Same(result, args.Items[0]);
        Assert.Equal(42L, Assert.IsType<PyLong>(args.Items[1]).Value);
    }

    [Fact]
    public void Deep_graph_is_traversed_without_recursion()
    {
        PyValue root = new PyBigInteger(new BigInteger(42));
        for (var i = 0; i < 10000; i++) root = PyList.Of(root);

        var result = WideIntegers.Narrow(root);
        for (var i = 0; i < 10000; i++) result = Assert.IsType<PyList>(result).Items[0];
        Assert.Equal(42L, Assert.IsType<PyLong>(result).Value);
    }
}
