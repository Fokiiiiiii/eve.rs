using Eve.ReadSide;

namespace Eve.Services.Core;

/// <summary>
/// The rowset queries of the mining ledger (<c>mining_ledger_entries</c> and <c>mining_ledger_observers</c> of the space schema, migration 0003), registered on the read
/// side. The schema is named <c>space</c> in the read side's schema map. A query's columns are the EVE.js field names of the reference's KeyVals.
/// </summary>
public static class MiningLedgerQueries
{
    public const string Character = "characterMiningLedger";
    public const string Observer = "observerMiningLedger";
    public const string Observers = "observerMiningHeaders";

    /// <summary>The schema name the queries expect in the read side's map.</summary>
    public const string SpaceSchema = "space";

    private static readonly Column[] EntryColumns =
    [
        Column.Of("entryID", ColumnType.Long, "entry_id"),
        Column.Of("eventDate", ColumnType.FileTime, "event_date"),
        Column.Of("characterID", ColumnType.Long, "character_id"),
        Column.Of("corporationID", ColumnType.Long, "corporation_id"),
        Column.Of("solarSystemID", ColumnType.Long, "solar_system_id"),
        Column.Of("solarsystemID", ColumnType.Long, "solar_system_id"),
        Column.Of("typeID", ColumnType.Long, "type_id"),
        Column.Of("quantity", ColumnType.Long, "quantity"),
        Column.Of("quantityWasted", ColumnType.Long, "quantity_wasted"),
        Column.Of("quantityCritical", ColumnType.Long, "quantity_critical"),
        Column.Of("shipTypeID", ColumnType.Long, "ship_type_id"),
        Column.Of("moduleTypeID", ColumnType.Long, "module_type_id"),
        Column.Of("observerItemID", ColumnType.Long, "observer_item_id"),
        Column.Of("yieldKind", ColumnType.Text, "yield_kind"),
    ];

    private static readonly Column[] ObserverColumns =
    [
        Column.Of("itemID", ColumnType.Long, "item_id"),
        Column.Of("solarSystemID", ColumnType.Long, "solar_system_id"),
        Column.Of("solarsystemID", ColumnType.Long, "solar_system_id"),
        Column.Of("itemName", ColumnType.Text, "COALESCE(NULLIF(item_name, ''), 'Observer ' || item_id::text)"),
    ];

    /// <summary>Registers the three queries. Arguments: a pilot's entries are (pilot, now, oldest); an observer's are (observer, corporation, now, oldest).</summary>
    public static Eve.ReadSide.ReadSide Register(Eve.ReadSide.ReadSide reads) => reads
        .Register(new RowsetQuery(Character, "{space}.mining_ledger_entries", EntryColumns)
        {
            Where = "owner_kind = 'character' AND owner_id = $1 AND visible_after <= $2 AND event_date >= $3",
            OrderBy = "event_date DESC, entry_id DESC",
        })
        .Register(new RowsetQuery(Observer, "{space}.mining_ledger_entries", EntryColumns)
        {
            Where = "owner_kind = 'observer' AND owner_id = $1 AND corporation_id = $2 AND visible_after <= $3 AND event_date >= $4",
            OrderBy = "event_date DESC, entry_id DESC",
        })
        .Register(new RowsetQuery(Observers, "{space}.mining_ledger_observers", ObserverColumns)
        {
            Where = "corporation_id = $1 AND first_visible_after <= $2 AND last_event_date >= $3",
            OrderBy = "item_name, item_id",
        });
}
