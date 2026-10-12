using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.CharacterSelect;

namespace Eve.Host;

/// <summary>The packet the client waits for after the handshake: its session's initial state.</summary>
public static class SessionInitialState
{
    public const string ClassName = "carbon.common.script.net.machoNetPacket.SessionInitialStateNotification";

    /// <summary>The value of the session type the client expects for a game session.</summary>
    private const int GameSessionType = 5;

    private const int UserType = 30;

    public static PyValue Build(ulong sessionId, ulong clientId, uint userId, ulong role, string address, string language, string countryCode)
    {
        // The keys and their order are the reference's. maxSessionTime is not sent: the client reads it only behind a
        // guard, so an absent key and None mean the same. inDetention is sent because the client reads it unguarded.
        // The login session's role is the account's bits plus the base bits every session has (EveJS composeSessionRoleMask).
        var state = Py.Dict(
            ("languageID", new PyStr(language)),
            ("countryCode", new PyStr(countryCode)),
            ("userid", new PyInt(userId)),
            ("userType", new PyInt(UserType)),
            ("role", new PyLong(unchecked((long)(role | (ulong)CharUnboundMgrService.SessionBaseRole)))),
            ("address", new PyStr(address)),
            ("inDetention", PyNone.Instance));
        var source = new MachoAddress(AddressKind.Node, NodeId: new PyInt(MachoMessage.ProxyNodeId), CallId: new PyInt(0));
        var destination = new MachoAddress(AddressKind.Client, ClientId: new PyLong(unchecked((long)clientId)), CallId: new PyInt(0));
        PyValue[] packet =
        [
            new PyInt(MachoMessage.SessionInitialStateNotification),
            source.Encode(),
            destination.Encode(),
            userId != 0 ? new PyInt(userId) : PyNone.Instance,
            new PyTuple([new PyLong(unchecked((long)sessionId)), new PyInt(GameSessionType), state]),
            new PyDict([]),
            .. Enumerable.Repeat<PyValue>(PyNone.Instance, 8),
        ];
        return new PyObject(new PyStr(ClassName), new PyTuple(packet));
    }
}
