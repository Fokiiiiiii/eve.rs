using Eve.Macho;
using Eve.Marshal;

namespace Eve.Services.Space.Tests;

/// <summary>The events the server sent a session, read back: the name, how they are addressed, and what they carry.</summary>
internal static class Events
{
    public sealed record Event(string Name, string IdType, PyValue[] Args);

    public static IReadOnlyList<Event> Of(ClientSession session, bool clear = true)
    {
        var sink = (Sink)session.Sink!;
        var events = new List<Event>();
        foreach (var packet in sink.Sent.ToArray().OfType<PyObject>())
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

/// <summary>What the session was sent, in order, as a test reads it: the events and the session changes among them.</summary>
internal static class Sequence
{
    /// <summary>The names of the events and <c>$sessionchange</c> for each change of the session's attributes, oldest first.</summary>
    public static IReadOnlyList<string> Of(ClientSession session, bool clear = true)
    {
        var sink = (Sink)session.Sink!;
        var names = new List<string>();
        foreach (var packet in sink.Sent.ToArray().OfType<PyObject>())
        {
            var state = ((PyTuple)packet.Args).Items;
            if (state[0] is PyInt { Value: MachoMessage.SessionChangeNotification })
            {
                names.Add("$sessionchange");
            }
            else if (state[0] is PyInt { Value: MachoMessage.Notification })
            {
                var address = ((PyTuple)((PyObject)state[2]).Args).Items;
                names.Add(JsSemantics.StringValue(address[1])!);
            }
        }
        if (clear)
        {
            sink.Sent.Clear();
        }
        return names;
    }

    /// <summary>The attributes the last session change announced, in order, as <c>(name, old, new)</c>.</summary>
    public static IReadOnlyList<(string Name, PyValue Old, PyValue New)> LastChange(ClientSession session)
    {
        var sink = (Sink)session.Sink!;
        var packet = sink.Sent.OfType<PyObject>().Last(p => ((PyTuple)p.Args).Items[0] is PyInt { Value: MachoMessage.SessionChangeNotification });
        var body = (PyTuple)((PyTuple)((PyTuple)packet.Args).Items[4]).Items[1];
        return [.. ((PyDict)body.Items[1]).Entries.Select(e => (JsSemantics.StringValue(e.Key)!, ((PyTuple)e.Value).Items[0], ((PyTuple)e.Value).Items[1]))];
    }
}
