using Eve.Events;
using Eve.Ships;

namespace Eve.Host;

/// <summary>
/// The mining ledger writer: the subscriber <c>mining.ledger</c> on <c>mining.ore_mined</c>. One entry per granted cycle goes to the mining ledger table
/// (<see cref="MiningLedgerStore"/>, in the space schema) in the subscriber's own transaction, so the entry and the cursor commit together. The entry id is the
/// event id: a replayed event finds its entry and writes nothing. The shape is EVE.js's (<c>miningLedgerState.js</c> <c>recordMiningLedgerEvent</c>); observers are
/// not written, because a ship's laser has no observer structure.
/// </summary>
public sealed class MiningLedgerRecorder(string spaceSchema)
{
    public async Task RecordAsync(EventContext context, CancellationToken cancellationToken)
    {
        if (context.Event.Topic == MoonMiningObserverEvent.Topic)
        {
            var observer = context.Event.Payload<MoonMiningObserverEvent>()
                ?? throw new InvalidDataException($"mining observer event {context.Event.Id} has no payload");
            await MiningLedgerStore.RecordObserverAsync(context.Connection, context.Transaction, spaceSchema, context.Event.Id,
                observer.Ore, MiningLedgerStore.FileTimeOf(observer.MinedAtMs), cancellationToken);
            return;
        }

        var ore = context.Event.Payload<OreMinedEvent>()
            ?? throw new InvalidDataException($"mining.ore_mined event {context.Event.Id} has no payload");
        await MiningLedgerStore.RecordAsync(context.Connection, context.Transaction, spaceSchema, context.Event.Id, ore,
            MiningLedgerStore.FileTimeOf(context.Event.OccurredMs), cancellationToken);
        var hasMoonMetadata = ore.MoonExtractionId > 0 || ore.MoonStructureId > 0 || ore.MoonId > 0 || ore.MoonOwnerCorporationId > 0;
        if (!hasMoonMetadata)
        {
            return;
        }
        if (ore.MoonExtractionId <= 0 || ore.MoonStructureId <= 0 || ore.MoonId <= 0 || ore.MoonOwnerCorporationId <= 0)
        {
            throw new InvalidDataException($"mining.ore_mined event {context.Event.Id} has incomplete moon observer metadata");
        }
        await context.PublishAsync(MoonMiningObserverEvent.Topic, new MoonMiningObserverEvent(context.Event.OccurredMs, ore),
            $"observer:{ore.MoonStructureId}", cancellationToken);
    }
}
