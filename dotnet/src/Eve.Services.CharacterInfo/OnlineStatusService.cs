using Eve.Marshal;

namespace Eve.Services.CharacterInfo;

/// <summary>
/// Live character presence, filtered through mutual personal buddy lists.
/// Contract: EveJS 828823a/server/src/services/online/{onlineStatusService,onlineStatusRuntime}.js,
/// serviceHelpers.buildDbRowset, and eve-logic 1dbb571/eve/client/script/ui/shared/comtool/onlineStatus.py.
/// CCP's March 2016 release requires mutual buddy-list membership for another character's status.
/// </summary>
[Service("onlineStatus")]
public sealed partial class OnlineStatusService(
    CharacterSessions sessions,
    Func<long, CancellationToken, ValueTask<IReadOnlyCollection<long>>>? watchlist = null)
{
    private static readonly Descriptor StatusDescriptor = Descriptor.FromHeader(new PyObjectEx(
        ObjectExKind.Ex1,
        Py.Tuple(new PyToken("blue.DBRowDescriptor"), Py.Tuple(Py.Tuple(
            Py.Tuple(new PyStr("contactID"), new PyInt(3)),
            Py.Tuple(new PyStr("online"), new PyInt(11))))),
        [], []));

    /// <summary>
    /// The provider reads current personal contacts marked inWatchlist. Without one, the character has
    /// no buddies, matching the current empty personal-contact store; presence still comes from sessions.
    /// </summary>
    private ValueTask<IReadOnlyCollection<long>> Watchlist(long characterId, CancellationToken cancellation) =>
        watchlist?.Invoke(characterId, cancellation) ?? ValueTask.FromResult<IReadOnlyCollection<long>>([]);

    private async ValueTask<bool> IsOnline(
        long targetId, long observerId, CancellationToken cancellation,
        IReadOnlyCollection<long>? observerWatchlist = null)
    {
        cancellation.ThrowIfCancellationRequested();
        if (targetId <= 0)
        {
            return false;
        }

        // Like the reference, a call without a selected observer reads raw presence.
        if (observerId > 0 && observerId != targetId)
        {
            var contacts = observerWatchlist ?? await Watchlist(observerId, cancellation);
            if (!contacts.Contains(targetId)
                || !(await Watchlist(targetId, cancellation)).Contains(observerId))
            {
                return false;
            }
        }

        // Read after the asynchronous contact lookup: a disconnect or character switch must be visible.
        return sessions.Of(targetId) is not null;
    }

    [Method("GetOnlineStatus")]
    public async ValueTask<PyValue> GetOnlineStatus(ServiceCall call)
    {
        return PyValue.Of(await IsOnline(Eve.Macho.JsSemantics.ToPositiveInteger(call.Arg(0)),
            call.Session.CharacterId ?? 0, call.Cancellation));
    }

    [Method("GetInitialState")]
    public async ValueTask<PyValue> GetInitialState(ServiceCall call)
    {
        call.Cancellation.ThrowIfCancellationRequested();
        var observerId = call.Session.CharacterId ?? 0;
        var rows = new List<PyValue>();
        if (observerId > 0)
        {
            var contacts = (await Watchlist(observerId, call.Cancellation)).Where(id => id > 0).ToHashSet();
            foreach (var contactId in contacts.Order())
            {
                rows.Add(new PyPackedRow(StatusDescriptor, [
                    new PyInt(contactId),
                    PyValue.Of(await IsOnline(contactId, observerId, call.Cancellation, contacts)),
                ]));
            }
        }

        // CRowset is a list subclass on the client: ObjectEx2 with a descriptor keyword and packed rows.
        return new PyObjectEx(ObjectExKind.Ex2,
            Py.Tuple(Py.Tuple(new PyToken("carbon.common.script.sys.crowset.CRowset")),
                Py.Dict(("header", StatusDescriptor.Header))),
            rows, []);
    }

    [Method("Prime")]
    public ValueTask<PyValue> Prime(ServiceCall call) => GetInitialState(call);
}
