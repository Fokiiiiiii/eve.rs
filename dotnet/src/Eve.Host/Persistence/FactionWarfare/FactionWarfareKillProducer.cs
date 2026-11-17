using Eve.Characters;
using Eve.Events;
using Eve.FactionWarfare;
using Eve.Services.Space;

namespace Eve.Host.Persistence.FactionWarfare;

/// <summary>Records final-blow player kills between combat-enemy FW factions in a warzone in the event's transaction.</summary>
internal sealed class FactionWarfareKillProducer(FwStore store, PgCharacterStore characters)
{
    public const string SubscriberName = "factionwarfare.player-kills";

    public void Subscribe(EventBus events) => events.Subscribe(SubscriberName, [ShipKillmailEvent.Topic], RecordAsync);

    private async Task RecordAsync(EventContext context, CancellationToken cancellationToken)
    {
        var kill = context.Event.Payload<ShipKillmailEvent>()
            ?? throw new InvalidOperationException("The ship killmail event has no payload.");
        if (kill.CharacterId is not > 0 || kill.SolarSystemId <= 0 || kill.OccurredAtMs <= 0 ||
            FwWarzones.WarzoneOf(kill.SolarSystemId) == 0 || kill.KillerShipId <= 0)
            return;

        var finalAttacker = kill.Attackers
            .Where(attacker => attacker.SourceShipId == kill.KillerShipId && attacker.CharacterId is > 0)
            .OrderByDescending(attacker => attacker.LastHitOrder)
            .FirstOrDefault();
        if (finalAttacker?.CharacterId is not > 0)
            return;

        var victim = await characters.GetAsync(kill.CharacterId.Value, cancellationToken);
        var aggressor = await characters.GetAsync(finalAttacker.CharacterId.Value, cancellationToken);
        if (victim is null || aggressor is null || aggressor.Id == victim.Id)
            return;

        var victimFaction = kill.VictimWarFactionId ?? await WarFactionAtAsync(context, victim, kill.OccurredAtMs, cancellationToken);
        var aggressorFaction = finalAttacker.WarFactionId ?? await WarFactionAtAsync(context, aggressor, kill.OccurredAtMs, cancellationToken);
        if (!FwFactions.IsCombatEnemy(aggressorFaction, victimFaction))
            return;

        var day = FwRules.DayOf(kill.OccurredAtMs);
        await store.AddPilotDailyAsync(context.Connection, context.Transaction, aggressor.Id, day, 1, 0, cancellationToken);
        var corporationId = finalAttacker.CorporationId > 0 ? finalAttacker.CorporationId : aggressor.CorporationId;
        if (corporationId > 0)
            await store.AddCorporationDailyAsync(context.Connection, context.Transaction, corporationId, day, 1, 0, cancellationToken);
    }

    private async Task<long> WarFactionAtAsync(EventContext context, Character character, long atMs, CancellationToken cancellationToken)
    {
        if (character.DirectFactionId is > 0)
            return character.DirectFactionId.Value;
        if (character.CorporationId <= 0)
            return 0;
        var corporationFaction = await store.FactionAtAsync(context.Connection, context.Transaction, FwStore.CorporationKind,
            character.CorporationId, atMs, cancellationToken);
        if (corporationFaction > 0)
            return corporationFaction;
        if (character.AllianceId is { } allianceId && allianceId > 0)
            return await store.FactionAtAsync(context.Connection, context.Transaction, FwStore.AllianceKind, allianceId, atMs, cancellationToken);
        return 0;
    }
}
