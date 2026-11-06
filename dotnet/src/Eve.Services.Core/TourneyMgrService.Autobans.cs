using Eve.Marshal;
using Eve.Services;

namespace Eve.Services.Core;

public sealed record TourneyBanSubmitRequest(long BanId, IReadOnlyList<PyValue> ShipTypes, ClientSession Session);

public sealed partial class TourneyMgrService
{
    [Method("BanShip")]
    public ValueTask<PyValue> BanShip(ServiceCall call)
    {
        if (call.Args.Count != 2 || Positive(call.Arg(0)) is not { } banId || call.Session.CharacterId is not > 0 ||
            call.Arg(1) is not PyList types || matches?.SubmitBanAsync is not { } submit)
            throw new ArgumentException("Invalid tournament ban submission.");
        return submit(new TourneyBanSubmitRequest(banId, types.Items, call.Session), call.Cancellation);
    }
}
