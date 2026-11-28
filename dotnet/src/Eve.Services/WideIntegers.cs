using System.Numerics;
using System.Runtime.CompilerServices;
using Eve.Marshal;

namespace Eve.Services;

/// <summary>Convert wire integers that fit in a signed 64-bit value for services.</summary>
internal static class WideIntegers
{
    public static PyValue Narrow(PyValue value) => NarrowGraph([value])[0];

    public static IReadOnlyList<PyValue> Narrow(IReadOnlyList<PyValue> values) => NarrowGraph(values);

    // The decoder uses this same completion operation for saved PyObject instances.
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "Complete")]
    private static extern void CompleteObject(PyObject instance, PyValue args);

    private static IReadOnlyList<PyValue> NarrowGraph(IReadOnlyList<PyValue> roots)
    {
        var parents = new Dictionary<PyValue, List<PyValue>>(ReferenceEqualityComparer.Instance);
        var replacements = new Dictionary<PyValue, PyValue>(ReferenceEqualityComparer.Instance);
        var pending = new Stack<PyValue>(roots);
        var changed = new Queue<PyValue>();

        while (pending.TryPop(out var current))
        {
            if (parents.ContainsKey(current))
                continue;
            parents.Add(current, []);
            PyValue? narrowed = current switch
            {
                PyBigUnsigned wide when wide.Bytes.Length <= sizeof(long)
                    && new BigInteger(wide.Bytes, isUnsigned: true) is var number && number <= long.MaxValue
                    => new PyLong((long)number),
                PyBigInteger signed when signed.Value >= long.MinValue && signed.Value <= long.MaxValue
                    => new PyLong((long)signed.Value),
                _ => null,
            };
            if (narrowed is not null)
            {
                replacements.Add(current, narrowed);
                changed.Enqueue(current);
            }
            foreach (var child in Children(current))
                pending.Push(child);
        }

        // Record reverse edges separately so cycles and shared children are handled uniformly.
        foreach (var value in parents.Keys)
            foreach (var child in Children(value))
                parents[child].Add(value);

        var needsCopy = new HashSet<PyValue>(ReferenceEqualityComparer.Instance);
        while (changed.TryDequeue(out var modified))
            foreach (var parent in parents[modified])
                if (needsCopy.Add(parent))
                    changed.Enqueue(parent);

        // Allocate every shell before connecting edges, including back edges.
        foreach (var value in needsCopy)
        {
            replacements[value] = value switch
            {
                PyList list => new PyList(new PyValue[list.Items.Count]),
                PyTuple tuple => new PyTuple(new PyValue[tuple.Items.Count]),
                PyDict dict => new PyDict(new KeyValuePair<PyValue, PyValue>[dict.Entries.Count]),
                PyObject obj => new PyObject(obj.Name, PyNone.Instance),
                _ => throw new InvalidOperationException("Unexpected parent type"),
            };
        }

        PyValue Result(PyValue value) => replacements.TryGetValue(value, out var result) ? result : value;
        foreach (var value in needsCopy)
        {
            switch (value)
            {
                case PyList list:
                    var listItems = (PyValue[])((PyList)replacements[value]).Items;
                    for (var i = 0; i < listItems.Length; i++) listItems[i] = Result(list.Items[i]);
                    break;
                case PyTuple tuple:
                    var tupleItems = (PyValue[])((PyTuple)replacements[value]).Items;
                    for (var i = 0; i < tupleItems.Length; i++) tupleItems[i] = Result(tuple.Items[i]);
                    break;
                case PyDict dict:
                    var entries = (KeyValuePair<PyValue, PyValue>[])((PyDict)replacements[value]).Entries;
                    for (var i = 0; i < entries.Length; i++)
                        entries[i] = new(Result(dict.Entries[i].Key), Result(dict.Entries[i].Value));
                    break;
                case PyObject obj:
                    CompleteObject((PyObject)replacements[value], Result(obj.Args));
                    break;
            }
        }

        PyValue[]? resultRoots = null;
        for (var i = 0; i < roots.Count; i++)
        {
            var result = Result(roots[i]);
            if (!ReferenceEquals(result, roots[i]))
                (resultRoots ??= roots.ToArray())[i] = result;
        }
        return resultRoots ?? roots;
    }

    private static IEnumerable<PyValue> Children(PyValue value)
    {
        switch (value)
        {
            case PyList list:
                foreach (var item in list.Items) yield return item;
                break;
            case PyTuple tuple:
                foreach (var item in tuple.Items) yield return item;
                break;
            case PyDict dict:
                foreach (var (key, item) in dict.Entries) { yield return key; yield return item; }
                break;
            case PyObject obj:
                yield return obj.Args;
                break;
        }
    }
}
