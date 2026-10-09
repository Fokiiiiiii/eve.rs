using System.Globalization;
using System.Text.Json.Nodes;
using Eve.Events;
using Eve.GameStore;
using Eve.Ships;

namespace Eve.Services.Space;

/// <summary>
/// The mining ledger writer: the subscriber <c>mining.ledger</c> on <c>mining.ore_mined</c>. One entry per granted cycle goes to the character's row of the
/// game-store <c>miningLedger</c> table, the table characterMiningLedger reads, in the subscriber's own transaction, so the entry and the cursor commit together.
/// The entry id is the event id: a replayed event finds its entry and writes nothing. The shape is EVE.js's (<c>miningLedgerState.js</c>
/// <c>recordMiningLedgerEvent</c>); observers are not written, because a ship's laser has no observer structure.
/// </summary>
public sealed class MiningLedgerRecorder(PersistenceStore store, OwnerFence fence)
{
    public const string Table = "miningLedger";

    /// <summary>miningCharacterLedgerDelayMs (the EVE.js default, 10 minutes): a personal entry is shown this long after its cycle.</summary>
    public const long CharacterDelayMs = 600_000;

    /// <summary>EVE.js keeps 90 days of entries; older ones are pruned when a new one is written.</summary>
    public const long RetentionMs = 90L * 24 * 60 * 60 * 1000;

    private const long TicksPerMs = 10_000;
    private const long UnixEpochFileTime = 116_444_736_000_000_000L;

    public async Task RecordAsync(EventContext context, CancellationToken cancellationToken)
    {
        var ore = context.Event.Payload<OreMinedEvent>()
            ?? throw new InvalidDataException($"mining.ore_mined event {context.Event.Id} has no payload");
        var eventDate = UnixEpochFileTime + context.Event.OccurredMs * TicksPerMs;
        var key = $"characters{GameStoreTables.RowKeySeparator}{ore.CharacterId}";

        var bucket = await store.ReadRowAsync(context.Connection, context.Transaction, Table, key, cancellationToken) is { } json
            ? JsonNode.Parse(json)!.AsObject()
            : new JsonObject();
        bucket["characterID"] = JsonValue.Create(ore.CharacterId);
        bucket["corporationID"] = JsonValue.Create(ore.CorporationId);
        if (bucket["entries"] is not JsonArray entries)
        {
            entries = new JsonArray();
            bucket["entries"] = entries;
        }

        var entryId = context.Event.Id;
        if (!entries.OfType<JsonObject>().Any(row => Number(row["entryID"]) == entryId))
        {
            entries.Add(Entry(ore, entryId, eventDate));
        }
        var oldest = eventDate - RetentionMs * TicksPerMs;
        for (var index = entries.Count - 1; index >= 0; index--)
        {
            if (entries[index] is JsonObject row && Number(row["eventDate"]) < oldest)
            {
                entries.RemoveAt(index);
            }
        }

        await store.ApplyChangesAsync(
            context.Connection, context.Transaction, Table, [new RowUpsert(key, bucket.ToJsonString())], [], fence, cancellationToken);
    }

    private static JsonObject Entry(OreMinedEvent ore, long entryId, long eventDate) => new()
    {
        ["entryID"] = JsonValue.Create(entryId),
        ["eventDate"] = JsonValue.Create(eventDate.ToString(CultureInfo.InvariantCulture)),
        ["characterID"] = JsonValue.Create(ore.CharacterId),
        ["corporationID"] = JsonValue.Create(ore.CorporationId),
        ["solarSystemID"] = JsonValue.Create(ore.SolarSystemId),
        ["typeID"] = JsonValue.Create(ore.OreTypeId),
        ["quantity"] = JsonValue.Create(ore.Quantity),
        ["quantityWasted"] = JsonValue.Create(ore.QuantityWasted),
        ["quantityCritical"] = JsonValue.Create(ore.QuantityCritical),
        ["shipTypeID"] = JsonValue.Create(ore.ShipTypeId),
        ["moduleTypeID"] = JsonValue.Create(ore.MinerTypeId),
        ["observerItemID"] = JsonValue.Create(0L),
        ["yieldKind"] = JsonValue.Create(ore.YieldKind),
        ["characterVisibleAfter"] = JsonValue.Create((eventDate + CharacterDelayMs * TicksPerMs).ToString(CultureInfo.InvariantCulture)),
    };

    /// <summary>A stored number: a file time is kept as text, as EVE.js keeps it, and a number reads the same.</summary>
    private static long Number(JsonNode? node) =>
        long.TryParse(node?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 0;
}
