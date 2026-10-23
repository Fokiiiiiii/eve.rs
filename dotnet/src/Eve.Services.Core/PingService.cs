using Eve.Marshal;
using Eve.Macho;
using Eve.Services;

namespace Eve.Services.Core;

/// <summary>The client's <c>pingService</c> (not the low-level PingReq): answers None.</summary>
[Service("pingService")]
public sealed partial class PingService
{
    [Method("Ping")]
    public PyValue Ping(ServiceCall call)
    {
        if (call.Args.Count != 0 || call.Kwargs is not PyNone)
            throw MachoErrors.ThrowUserError("PING_ARGUMENTS_INVALID");
        return PyNone.Instance;
    }
}
