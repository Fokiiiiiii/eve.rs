using Eve.Macho;
using Eve.Marshal;
using Eve.Space;

namespace Eve.Services.Space;

public sealed partial class BeyonceService
{
    /// <summary>The link of a CRAB, from a client's component call (<c>InitiateLink</c> and <c>InitiateLinkQuick</c> are one link in EVE.js); set by the composition.</summary>
    public Func<ClientSession, long, Task>? InitiateBeaconLink { get; set; }

    /// <summary>
    /// <c>CallComponentFromClient(itemID, componentName, methodName)</c> (eve-logic <c>remoteBallpark.CallComponentFromClient</c>, used by the link of a CRAB): the link is
    /// served. Any other component is not, as EVE.js does, and the answer is None, which the client expects.
    /// </summary>
    [Method("CallComponentFromClient")]
    public async ValueTask<PyValue> CallComponentFromClient(ServiceCall call)
    {
        // the names are the client's Python strings (PyBytes on the wire) or string-table items (PyStr)
        if (!_binding.Owns(call) || Number(call.Arg(0)) is not { } itemId || TextOf(call.Arg(1)) is not { } component || TextOf(call.Arg(2)) is not { } method)
        {
            return None;
        }
        if (component == "linkWithShip" && method is "InitiateLink" or "InitiateLinkQuick" && InitiateBeaconLink is { } link)
        {
            await link(call.Session, (long)itemId);
        }
        return None;
    }

    private static string? TextOf(PyValue value) => value switch
    {
        PyStr text => text.Value,
        PyBytes bytes => System.Text.Encoding.UTF8.GetString(bytes.Value.Span),
        PyWString wide => wide.Value,
        _ => null,
    };
}
