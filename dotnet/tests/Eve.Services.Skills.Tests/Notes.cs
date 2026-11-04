using Eve.Macho;
using Eve.Marshal;

namespace Eve.Services.Skills.Tests;

/// <summary>The events the server sent a session, read back: the name, how they are addressed, and what they carry.</summary>
internal static class Notes
{
    public sealed record Note(string Name, string IdType, PyValue[] Args);

    public static IReadOnlyList<Note> Of(Capture sink)
    {
        var notes = new List<Note>();
        foreach (var packet in sink.Sent.OfType<PyObject>())
        {
            var state = ((PyTuple)packet.Args).Items;
            if (state[0] is not PyInt { Value: MachoMessage.Notification })
            {
                continue;
            }
            var address = ((PyTuple)((PyObject)state[2]).Args).Items;
            var body = (PyBuffer)((PyTuple)((PyTuple)state[4]).Items[0]).Items[1];
            var inner = (PyTuple)MarshalCodec.Decode(body.Value.ToArray());
            var args = ((PyTuple)((PyTuple)inner.Items[1]).Items[1]).Items;
            notes.Add(new Note(JsSemantics.StringValue(address[1])!, JsSemantics.StringValue(address[3])!, [.. args]));
        }
        sink.Sent.Clear();
        return notes;
    }

    public static long Number(PyValue value) => value switch { PyInt i => i.Value, PyLong l => l.Value, _ => throw new InvalidOperationException(value.ToString()) };

    /// <summary>The fields of a <c>KeyVal</c> object (the queue lines, the skill info).</summary>
    public static Dictionary<string, PyValue> Fields(PyValue keyVal) =>
        ((PyDict)((PyObject)keyVal).Args).Entries.ToDictionary(e => JsSemantics.StringValue(e.Key)!, e => e.Value);
}
