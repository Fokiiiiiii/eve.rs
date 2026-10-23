using Eve.Events;
using Eve.Marshal;
using Eve.Services;

namespace Eve.Host.Persistence;

internal sealed class ContractNotifications(CharacterSessions sessions)
{
    internal const string Topic = "contracts.client_notification";

    internal sealed record Payload(string Name, long ContractId, long CharacterId, long CorporationId, long AllianceId);

    public void Subscribe(EventBus events) => events.Subscribe(
        "contracts.client_notifications",
        [Topic],
        (context, _) =>
        {
            var payload = context.Event.Payload<Payload>()
                ?? throw new InvalidOperationException("Contract notification event has no payload.");
            if (string.IsNullOrWhiteSpace(payload.Name) || payload.ContractId <= 0 ||
                payload.CharacterId < 0 || payload.CorporationId < 0 || payload.AllianceId < 0 ||
                (payload.CharacterId > 0 ? 1 : 0) + (payload.CorporationId > 0 ? 1 : 0) + (payload.AllianceId > 0 ? 1 : 0) != 1)
                throw new InvalidOperationException("Contract notification event has invalid recipients or content.");

            context.AfterCommit(_ =>
            {
                var recipients = Recipients(payload, sessions);
                PyValue[] args = payload.Name == "OnContractCreated"
                    ? []
                    : [new PyLong(payload.ContractId)];
                foreach (var recipient in recipients)
                    recipient.SendNotification(payload.Name, "clientID", args);
                return Task.CompletedTask;
            });
            return Task.CompletedTask;
        });

    internal static IReadOnlyList<ClientSession> Recipients(Payload payload, CharacterSessions sessions) =>
        payload.CorporationId > 0
            ? sessions.OfCorporation(payload.CorporationId)
            : payload.AllianceId > 0
                ? sessions.All().Where(session => session.GetInt("allianceid") == payload.AllianceId).ToArray()
                : sessions.Of(payload.CharacterId) is { } session ? [session] : [];
}
