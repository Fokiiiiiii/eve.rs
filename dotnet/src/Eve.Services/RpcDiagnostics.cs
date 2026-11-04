using System.Security.Cryptography;
using Eve.Macho;
using Eve.Marshal;

namespace Eve.Services;

/// <summary>Compact text for RPC traces (EVE_TRACE_RPC=full) and strict-mode failures: a type tree, cut short.</summary>
internal static class RpcDiagnostics
{
    private const int MaxLength = 400;
    private const int MaxItems = 8;

    /// <summary>A short id that ties a strict-mode error response to its stderr entry.</summary>
    public static string CorrelationId() => Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();

    /// <summary>The arguments of a call as one bracketed list.</summary>
    public static string Args(IReadOnlyList<PyValue> args) => Cut("[" + string.Join(", ", args.Select(a => Shape(a, 2))) + "]");

    /// <summary>Type and shape of a value, e.g. <c>tuple(str'machoNet', long, dict{str'k': int(1)})</c>.</summary>
    public static string Describe(PyValue? value) => Cut(Shape(value, 3));

    private static string Cut(string text) => text.Length <= MaxLength ? text : string.Concat(text.AsSpan(0, MaxLength), "...");

    private static string Shape(PyValue? value, int depth) => value switch
    {
        null => "missing",
        PyNone => "None",
        PyBool b => b.Value ? "True" : "False",
        PyInt i => $"int({i.Value})",
        PyLong l => $"long({l.Value})",
        PyReal r => $"float({r.Value})",
        PyStr s => $"str'{Clip(s.Value)}'",
        PyWString w => $"wstr'{Clip(w.Value)}'",
        PyToken t => $"token:{t.Value}",
        PyBytes b => $"bytes[{b.Value.Length}]",
        PyBuffer b => $"buffer[{b.Value.Length}]",
        PySubStream sub => sub.Decoded is { } inner ? $"substream({Shape(inner, depth)})" : $"substream[{sub.Raw.Length}]",
        PyTuple t => Sequence("tuple", t.Items, depth),
        PyList l => Sequence("list", l.Items, depth),
        PyDict d when depth <= 0 => $"dict[{d.Entries.Count}]",
        PyDict d => $"dict{{{string.Join(", ", d.Entries.Take(MaxItems).Select(e => $"{Shape(e.Key, 0)}: {Shape(e.Value, depth - 1)}"))}{More(d.Entries.Count)}}}",
        PyObject o => $"object({Shape(o.Name, 0)}, {Shape(o.Args, depth - 1)})",
        _ => value.GetType().Name,
    };

    private static string Sequence(string kind, IReadOnlyList<PyValue> items, int depth) => depth <= 0
        ? $"{kind}[{items.Count}]"
        : $"{kind}({string.Join(", ", items.Take(MaxItems).Select(i => Shape(i, depth - 1)))}{More(items.Count)})";

    private static string More(int count) => count > MaxItems ? $", +{count - MaxItems}" : "";

    private static string Clip(string text) => text.Length > 40 ? string.Concat(text.AsSpan(0, 40), "...") : text;
}
