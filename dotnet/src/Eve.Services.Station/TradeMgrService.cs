using Eve.Marshal;
using Eve.Services;

namespace Eve.Services.Station;

[Service("trademgr")]
public sealed partial class TradeMgrService(
    CharacterSessions sessions,
    Func<ServiceCall, ClientSession, ValueTask<PyValue>>? initiateTrade = null)
{
    [Method("InitiateTrade")]
    public async ValueTask<PyValue> InitiateTrade(ServiceCall call)
    {
        call.Cancellation.ThrowIfCancellationRequested();
        var initiatorCharacterId = call.Session.CharacterId ??
            call.Session.GetInt("charID") ??
            call.Session.GetInt("characterID") ??
            0;
        var targetCharacterId = PositiveCharacterId(call.Arg(0));
        if (initiatorCharacterId <= 0 || targetCharacterId <= 0)
            throw Notify("Trade target could not be resolved.");
        if (initiatorCharacterId == targetCharacterId)
            throw Notify("You cannot trade with yourself.");

        var initiatorLocationId = DockedLocationId(call.Session);
        if (initiatorLocationId <= 0)
            throw Notify("Both pilots must be docked in the same station or structure to trade.");

        var targetSession = sessions.Of(targetCharacterId);
        if (targetSession is null || DockedLocationId(targetSession) <= 0)
            throw Notify("The selected pilot must be docked and online to trade.");

        if (initiatorLocationId != DockedLocationId(targetSession))
            throw Notify("Both pilots must be docked in the same station or structure to trade.");

        if (initiateTrade is not null)
            return await initiateTrade(call, targetSession).ConfigureAwait(false);

        throw Notify("Direct trading is not available.");
    }

    private static long PositiveCharacterId(PyValue value) => value switch
    {
        PyInt number when number.Value > 0 => number.Value,
        PyLong number when number.Value > 0 => number.Value,
        _ => 0,
    };

    private static long DockedLocationId(ClientSession session)
    {
        var structureId = session.GetInt("structureid") ?? 0;
        if (structureId > 0)
            return structureId;

        var stationId = session.GetInt("stationid") ?? 0;
        return stationId > 0 ? stationId : 0;
    }

    private static MachoWrappedException Notify(string message) =>
        MachoErrors.ThrowUserError("CustomNotify", ("notify", new PyStr(message)));
}
