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
        if (!_binding.Owns(call) || Number(call.Arg(0)) is not { } itemId || call.Arg(1) is not PyStr component || call.Arg(2) is not PyStr method)
        {
            return None;
        }
        if (component.Value == "linkWithShip" && method.Value is "InitiateLink" or "InitiateLinkQuick" && InitiateBeaconLink is { } link)
        {
            await link(call.Session, (long)itemId);
        }
        return None;
    }
}
