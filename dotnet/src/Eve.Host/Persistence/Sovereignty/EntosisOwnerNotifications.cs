using System.Text.Json.Nodes;
using Eve.Characters;
using Eve.Events;
using Eve.Host.Persistence.Mail;
using Eve.Services.Space;
using Eve.Services.Space.Entosis;

namespace Eve.Host.Persistence.Sovereignty;

/// <summary>
/// ST-9 e: the owner's notices of an entosis capture. The capture publishes its events in its own transaction, and this subscriber writes the notices through the
/// mailbox in the subscriber's transaction, so a redelivered event writes nothing twice. The types and payloads are the V24.01 client's (eve-logic
/// notificationconst: 147 SovereigntyCaptureStarted, 160 SovereigntyStructuresReinforced) and EVE.js sovCampaignNotifications (the same keys); the recipients are
/// every character of the defending alliance. V24.01 names no notice for a capture's progress, so none is written.
/// </summary>
internal sealed class EntosisOwnerNotifications(
    Func<long, CancellationToken, Task<IReadOnlyList<long>>> allianceCorporations,
    PgCharacterStore characters,
    PersistenceTransactionMailbox mailbox)
{
    private const int CaptureStartedTypeId = 147;
    private const int StructuresReinforcedTypeId = 160;
    private const int SovereigntyGroupId = 6;

    // EVE.js sovCampaignState CAMPAIGN_EVENT_TYPE
    private const int CampaignEventType = 2;

    public void Subscribe(EventBus events) => events.Subscribe(
        "sov.entosis-owner-notifications",
        [EntosisCaptureService.CaptureStartedTopic, EntosisCaptureService.ReinforcedTopic],
        async (context, cancellationToken) =>
        {
            if (context.Event.Topic == EntosisCaptureService.CaptureStartedTopic)
            {
                var started = context.Event.Payload<EntosisCaptureStarted>()!;
                await WriteAsync(started.DefenderAllianceId, CaptureStartedTypeId, context, new JsonObject
                {
                    ["structureTypeID"] = SpaceSovHubs.TypeSovereigntyHub,
                    ["solarSystemID"] = started.SolarSystemId,
                }, cancellationToken);
                return;
            }
            var reinforced = context.Event.Payload<EntosisHubReinforced>()!;
            await WriteAsync(reinforced.DefenderAllianceId, StructuresReinforcedTypeId, context, new JsonObject
            {
                ["decloakTime"] = DateTimeOffset.FromUnixTimeMilliseconds(reinforced.ReinforcementExitMs).UtcDateTime.ToFileTimeUtc(),
                ["campaignEventType"] = CampaignEventType,
                ["solarSystemID"] = reinforced.SolarSystemId,
            }, cancellationToken);
        });

    private async Task WriteAsync(long allianceId, int typeId, EventContext context, JsonObject data, CancellationToken cancellationToken)
    {
        var recipients = new SortedSet<long>();
        foreach (var corporationId in await allianceCorporations(allianceId, cancellationToken))
        {
            foreach (var character in await characters.ForCorporationAsync(corporationId, cancellationToken))
            {
                recipients.Add(character.Id);
            }
        }
        await mailbox.WriteNotificationsAsync(
            allianceId, typeId, SovereigntyGroupId, recipients, $"entosis:{typeId}:event:{context.Event.Id}", data, cancellationToken, context);
    }
}
