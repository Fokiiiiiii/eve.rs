using Eve.Macho;
using Eve.Marshal;

namespace Eve.Services.Dogma.Tests;

/// <summary>The events the server sent a session, read back: the name, how they are addressed, and what they carry.</summary>
internal static class Events
{
    public sealed record Event(string Name, string IdType, PyValue[] Args);

    public static IReadOnlyList<Event> Of(ClientSession session, bool clear = true)
    {
        var sink = (Sink)session.Sink!;
        var events = new List<Event>();
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
            events.Add(new Event(JsSemantics.StringValue(address[1])!, JsSemantics.StringValue(address[3])!, [.. args]));
        }
        if (clear)
        {
            sink.Sent.Clear();
        }
        return events;
    }

    public static PyValue[] Items(PyValue tuple) => [.. ((PyTuple)tuple).Items];

    public static long Number(PyValue value) => value switch { PyInt i => i.Value, PyLong l => l.Value, _ => throw new InvalidOperationException(value.ToString()) };
}
