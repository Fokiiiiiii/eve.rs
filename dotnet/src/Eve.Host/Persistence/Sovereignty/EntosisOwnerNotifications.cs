using System.Text.Json.Nodes;
using Eve.Characters;
using Eve.Events;
using Eve.Host.Persistence.Mail;
using Eve.Services.Space;
using Eve.Services.Space.Entosis;

namespace Eve.Host.Persistence.Sovereignty;

/// <summary>

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
    private const int CommandNodeEventStartedTypeId = 161;
    private const int StructureDestroyedTypeId = 162;
    private const int SovereigntyGroupId = 6;

    // EVE.js sovCampaignState CAMPAIGN_EVENT_TYPE
    private const int CampaignEventType = 2;

    public void Subscribe(EventBus events) => events.Subscribe(
        "sov.entosis-owner-notifications",
        [
            EntosisCaptureService.CaptureStartedTopic, EntosisCaptureService.ReinforcedTopic, EntosisCaptureService.StageStartedTopic,
            EntosisCaptureService.HubDestroyedTopic,
        ],
        async (context, cancellationToken) =>
        {
            if (context.Event.Topic == EntosisCaptureService.StageStartedTopic)
            {
                // EVE.js notifySovCommandNodeEventStarted: the constellation, the source system and the campaign event type
                var stage = context.Event.Payload<CommandStageStarted>()!;
                // the static data gives a constellation for the systems of the campaigns; without one EVE.js writes no notice
                if (stage.ConstellationId > 0)
                {
                    await WriteAsync(stage.DefenderAllianceId, CommandNodeEventStartedTypeId, context, new JsonObject
                    {
                        ["constellationID"] = stage.ConstellationId,
                        ["solarSystemID"] = stage.SolarSystemId,
                        ["campaignEventType"] = CampaignEventType,
                    }, cancellationToken);
                }
                return;
            }
            if (context.Event.Topic == EntosisCaptureService.HubDestroyedTopic)
            {
                // EVE.js notifySovStructureDestroyed: the hub's type and the system
                var destroyed = context.Event.Payload<HubDestroyed>()!;
                await WriteAsync(destroyed.DefenderAllianceId, StructureDestroyedTypeId, context, new JsonObject
                {
                    ["structureTypeID"] = SpaceSovHubs.TypeSovereigntyHub,
                    ["solarSystemID"] = destroyed.SolarSystemId,
                }, cancellationToken);
                return;
            }
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
