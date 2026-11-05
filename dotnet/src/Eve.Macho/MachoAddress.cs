using Eve.Marshal;

namespace Eve.Macho;

public enum AddressKind
{
    Unknown,
    Node,
    Client,
    Broadcast,
    Any,
}

/// <summary>
/// A MachoNet address. Field order matches the client's <c>machoNetAddress.__getstate__</c>:
/// <c>ANY: [8, service, callID]</c>, <c>NODE: [1, nodeID, service, callID]</c>,
/// <c>CLIENT: [2, clientID, callID, service]</c>, <c>BROADCAST: [4, broadcastID, narrowcast, idtype]</c>.
/// Values that identify a peer are kept as the raw <see cref="PyValue"/>s so they re-encode as received.
/// </summary>
public sealed record MachoAddress(
    AddressKind Kind,
    string? Service = null,
    PyValue? CallId = null,
    PyValue? NodeId = null,
    PyValue? ClientId = null,
    string? BroadcastId = null,
    PyValue? NarrowTo = null,
    string? IdType = null)
{
    public const int TypeNode = 1;
    public const int TypeClient = 2;
    public const int TypeBroadcast = 4;
    public const int TypeAny = 8;

    public const string ClassName = "carbon.common.script.net.machoNetPacket.MachoAddress";

    public static MachoAddress Any(string? service, PyValue? callId = null) => new(AddressKind.Any, service, callId);

    /// <summary>Decodes an address (<c>decodeAddress</c>): a MachoAddress object or a bare tuple.</summary>
    public static MachoAddress Decode(PyValue? value)
    {
        if (value is PyObject { Args: PyTuple args })
        {
            value = args;
        }
        if (value is not PyTuple { Items.Count: >= 1 } tuple)
        {
            return new MachoAddress(AddressKind.Unknown);
        }

        var items = tuple.Items;
        PyValue At(int index, PyValue fallback) => items.Count > index ? items[index] : fallback;
        var zero = new PyInt(0);
        // Values that identify a peer go back out as the reference would send them.
        PyValue Raw(int index, PyValue fallback) => JsSemantics.AsReceived(At(index, fallback));
        var type = JsSemantics.IsNumber(items[0]) ? (int)JsSemantics.NumberValue(items[0]) : 0;

        return type switch
        {
            TypeNode => new MachoAddress(
                AddressKind.Node,
                Service: items.Count > 2 ? JsSemantics.StringValue(items[2]) : null,
                CallId: Raw(3, zero),
                NodeId: Raw(1, zero)),
            TypeClient => new MachoAddress(
                AddressKind.Client,
                Service: items.Count > 3 ? JsSemantics.StringValue(items[3]) : null,
                CallId: Raw(2, zero),
                ClientId: Raw(1, zero)),
            TypeBroadcast => new MachoAddress(
                AddressKind.Broadcast,
                BroadcastId: items.Count > 1 ? JsSemantics.StringValue(items[1]) : null,
                NarrowTo: items.Count > 2 ? JsSemantics.AsReceived(items[2]) : null,
                IdType: items.Count > 3 ? JsSemantics.StringValue(items[3]) : null),
            TypeAny => new MachoAddress(
                AddressKind.Any,
                Service: items.Count > 1 ? JsSemantics.StringValue(items[1]) : null,
                CallId: Raw(2, zero)),
            _ => new MachoAddress(AddressKind.Unknown),
        };
    }

    private static PyValue TextOrNull(string? text) => string.IsNullOrEmpty(text) ? PyNone.Instance : new PyStr(text);

    /// <summary>Encodes an address (<c>encodeAddress</c>): <c>PyObject("...MachoAddress", tuple)</c>.</summary>
    public PyValue Encode()
    {
        PyValue[] tuple;
        switch (Kind)
        {
            case AddressKind.Any:
                tuple = [new PyInt(TypeAny), TextOrNull(Service), JsSemantics.OrNull(CallId)];
                break;
            case AddressKind.Node:
                tuple = [new PyInt(TypeNode), NodeId is not null and not PyNone ? NodeId : new PyInt(1), TextOrNull(Service), JsSemantics.OrNull(CallId)];
                break;
            case AddressKind.Client:
                tuple = [
                    new PyInt(TypeClient),
                    ClientId is not null and not PyNone ? ClientId : new PyInt(0),
                    JsSemantics.OrNull(CallId),
                    TextOrNull(Service)];
                break;
            case AddressKind.Broadcast:
                tuple = [
                    new PyInt(TypeBroadcast),
                    TextOrNull(BroadcastId),
                    JsSemantics.Truthy(NarrowTo) ? NarrowTo! : new PyList([]),
                    TextOrNull(IdType)];
                break;
            default:
                tuple = [new PyInt(TypeAny), PyNone.Instance, PyNone.Instance];
                break;
        }
        return new PyObject(new PyStr(ClassName), new PyTuple(tuple));
    }
}
