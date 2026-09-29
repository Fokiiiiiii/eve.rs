using Eve.Marshal;

namespace Eve.Services.Core;

/// <summary>The client's <c>pingService</c> (not the low-level PingReq): answers None.</summary>
[Service("pingService")]
public sealed partial class PingService
{
    [Method("Ping")]
    public PyValue Ping(ServiceCall call) => PyNone.Instance;
}
