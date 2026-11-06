using Eve.Characters;
using Eve.Corporations;
using Eve.Events;
using Eve.Host;

namespace Eve.Host.Persistence.Mail;

internal sealed class CharacterBiomassMailNotifications(
    PgCharacterStore characters,
    CorporationStore corporations,
    PersistenceMailSendHostAdapter mailSendWriter)
{
    public void Subscribe(EventBus events) => events.Subscribe(
        "character.biomass.mail",
        [CharacterBiomassEvents.Topic],
        async (context, cancellationToken) =>
        {
            var biomass = context.Event.Payload<CharacterBiomassed>()
                ?? throw new InvalidOperationException("Character biomass event has no payload.");
            if (biomass.CharacterId <= 0 || biomass.CorporationId <= 0 ||
                biomass.RecipientIds.Any(id => id <= 0 || id == biomass.CharacterId) ||
                biomass.RecipientIds.Distinct().Count() != biomass.RecipientIds.Length)
                throw new InvalidOperationException("Character biomass event has invalid identities.");

            var character = await characters.GetAsync(biomass.CharacterId, cancellationToken)
                ?? throw new InvalidOperationException($"Biomassed character {biomass.CharacterId} no longer exists.");
            var corporation = await corporations.GetAsync(biomass.CorporationId, cancellationToken)
                ?? throw new InvalidOperationException($"Biomass corporation {biomass.CorporationId} no longer exists.");
            if (character.DeletionCorporationId != biomass.CorporationId ||
                corporation.IsNpc || corporation.Deleted || string.IsNullOrWhiteSpace(character.Name) ||
                string.IsNullOrWhiteSpace(corporation.Name))
                throw new InvalidOperationException("Character biomass event no longer matches its player corporation.");

            var messageId = await mailSendWriter.SendCharacterBiomassMailAsync(
                character.Id,
                character.Name,
                corporation.Id,
                corporation.Name,
                biomass.RecipientIds,
                cancellationToken);
            if (messageId <= 0)
                throw new InvalidOperationException("Character biomass mail was not durably stored.");
        });
}
