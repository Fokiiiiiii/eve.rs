using Eve.Marshal;

namespace Eve.Services;

/// <summary>A game service: a named set of methods the client can call.</summary>
public interface IService
{
    string Name { get; }

    /// <summary>
    /// Calls <paramref name="call"/>.Method. A method the service does not have is answered with None,
    /// as the reference server does (and logs it).
    /// </summary>
    ValueTask<PyValue> CallAsync(ServiceCall call);

    /// <summary>True when the service has a method of this name.</summary>
    bool HasMethod(string method);
}

/// <summary>Implemented by services that clean up when the client releases one of their bound objects.</summary>
public interface IBoundObjectReleaseHandler
{
    void OnBoundObjectReleased(string oid, ClientSession? session);
}

/// <summary>Implemented by services that hand the same bound object to a session on every bind.</summary>
public interface IReusableBoundObject
{
    bool ReuseBoundObjectForSession { get; }
}

/// <summary>
/// A call result that answers later: the dispatcher hands it a callback and does not send a response
/// itself. It is never encoded.
/// </summary>
public sealed record PyDeferred(Func<DeferredContext, Task> Start) : PyValue;

/// <summary>What a deferred response needs to reply once its work is done.</summary>
public sealed class DeferredContext(Eve.Macho.MachoPacket packet, ClientSession session, string? responseServiceName, Action<PyValue> reply)
{
    public Eve.Macho.MachoPacket Packet { get; } = packet;

    public ClientSession Session { get; } = session;

    public string? ResponseServiceName { get; } = responseServiceName;

    /// <summary>Sends the call response.</summary>
    public void Reply(PyValue result) => reply(result);
}

/// <summary>
/// Something to do for a session when it has chosen a character and the session has been told (the client wants its
/// skill cache primed, its ...): each module that owns login state gives one.
/// </summary>
public interface ICharacterEntryHook
{
    /// <summary>Attributes to merge into character selection's single session change, before any login notification.</summary>
    ValueTask<IReadOnlyList<(string Name, PyValue Value)>> GetSessionAttributesAsync(ClientSession session, long characterId, CancellationToken cancellationToken) =>
        ValueTask.FromResult<IReadOnlyList<(string Name, PyValue Value)>>([]);

    ValueTask OnCharacterLeavingAsync(ClientSession session, long characterId, CancellationToken cancellationToken) =>
        ValueTask.CompletedTask;

    ValueTask OnCharacterEnteredAsync(ClientSession session, long characterId, CancellationToken cancellationToken);

    ValueTask OnInventoryRpcAsync(ClientSession session, string method, bool stationHangar, bool activeSpaceShipInventoryList, CancellationToken cancellationToken) =>
        ValueTask.CompletedTask;
}
