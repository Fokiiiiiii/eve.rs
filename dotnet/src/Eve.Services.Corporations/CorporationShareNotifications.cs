using Eve.Characters;
using Eve.Corporations;
using Eve.Events;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;

namespace Eve.Services.Corporations;

public static class CorporationShareNotifications
{
    public const string Topic = "corporation.share.changed";

    public static void Register(CorporationStore store, PgCharacterStore characters, CharacterSessions sessions, EventBus events)
    {
        store.ShareChangeWriter = async (connection, transaction, change, token) =>
        {
            await events.PublishAsync(connection, transaction, Topic, change, cancellationToken: token);
        };
        events.Subscribe("corporation.share.notifications", [Topic], async (context, token) =>
        {
            var change = context.Event.Payload<CorporationShareChange>()
                ?? throw new InvalidOperationException("Share event has no payload");
            var targets = new HashSet<ClientSession>();
            if (sessions.Of(change.ShareholderId) is { } individual) targets.Add(individual);
            var corporation = await store.GetAsync(change.ShareholderId, token);
            if (corporation is { IsNpc: false, Deleted: false })
            {
                foreach (var session in sessions.OfCorporation(corporation.Id))
                {
                    if (session.CharacterId is not { } actor ||
                        await characters.GetAsync(actor, token) is not { } character || character.CorporationId != corporation.Id ||
                        await store.MemberAsync(actor, token) is not { } member || member.CorporationId != corporation.Id) continue;
                    if (corporation.CeoId == actor && !corporation.CeoRolesSuspended ||
                        (CorporationRoles.Session(corporation, member, character.StationId,
                            await store.TitlesAsync(corporation.Id, token)).AtAll & CorporationRoles.Director) != 0)
                        targets.Add(session);
                }
            }
            static PyValue Value(long n) => n == 0 ? PyNone.Instance : CorporationWire.Id(n);
            var absentBefore = change.Before == 0;
            var absentAfter = change.After == 0;
            var fields = Py.Dict(
                ("shareholderID", new PyList([absentBefore ? PyNone.Instance : CorporationWire.Id(change.ShareholderId), absentAfter ? PyNone.Instance : CorporationWire.Id(change.ShareholderId)])),
                ("corporationID", new PyList([absentBefore ? PyNone.Instance : CorporationWire.Id(change.CorporationId), absentAfter ? PyNone.Instance : CorporationWire.Id(change.CorporationId)])),
                ("shares", new PyList([Value(change.Before), Value(change.After)])));
            context.AfterCommit(_ =>
            {
                foreach (var target in targets)
                    try { target.SendNotification("OnShareChange", "clientID", CorporationWire.Id(change.ShareholderId), CorporationWire.Id(change.CorporationId), fields); }
                    catch (Exception) { }
                return Task.CompletedTask;
            });
        });
    }
}
