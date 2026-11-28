using System.Numerics;
using Eve.Marshal;

namespace Eve.Services;

/// <summary>
/// The decoder keeps an integer beyond 2^53 as <see cref="PyBigUnsigned"/> or <see cref="PyBigInteger"/>, as the reference's
/// JavaScript has to. The client sends FILETIMEs that way (a calendar date, an activity time), and services read integers: every
/// value of a call that fits a 64-bit integer is handed to the service as a <see cref="PyLong"/>.
/// </summary>
internal static class WideIntegers
{
    /// <summary>The same instance when nothing in <paramref name="value"/> had to change.</summary>
    public static PyValue Narrow(PyValue value)
    {
        switch (value)
        {
            case PyBigUnsigned wide when wide.Bytes.Length <= sizeof(long)
                    && new BigInteger(wide.Bytes, isUnsigned: true) is var number && number <= long.MaxValue:
                return new PyLong((long)number);
            case PyBigInteger signed when signed.Value >= long.MinValue && signed.Value <= long.MaxValue:
                return new PyLong((long)signed.Value);
            case PyTuple tuple:
                return Narrow(tuple.Items) is var tupleItems && ReferenceEquals(tupleItems, tuple.Items) ? tuple : new PyTuple(tupleItems);
            case PyList list:
                return Narrow(list.Items) is var listItems && ReferenceEquals(listItems, list.Items) ? list : new PyList(listItems);
            case PyDict dict:
                KeyValuePair<PyValue, PyValue>[]? entries = null;
                for (var index = 0; index < dict.Entries.Count; index++)
                {
                    var (key, item) = dict.Entries[index];
                    var narrowedKey = Narrow(key);
                    var narrowedItem = Narrow(item);
                    if (ReferenceEquals(narrowedKey, key) && ReferenceEquals(narrowedItem, item))
                        continue;
                    (entries ??= dict.Entries.ToArray())[index] = new(narrowedKey, narrowedItem);
                }
                return entries is null ? dict : new PyDict(entries);
            case PyObject instance:
                var args = Narrow(instance.Args);
                return ReferenceEquals(args, instance.Args) ? instance : new PyObject(instance.Name, args);
            default:
                return value;
        }
    }

    public static IReadOnlyList<PyValue> Narrow(IReadOnlyList<PyValue> values)
    {
        PyValue[]? changed = null;
        for (var index = 0; index < values.Count; index++)
        {
            var narrowed = Narrow(values[index]);
            if (!ReferenceEquals(narrowed, values[index]))
                (changed ??= values.ToArray())[index] = narrowed;
        }
        return changed ?? values;
    }
}
