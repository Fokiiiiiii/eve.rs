using Eve.Data;
using Eve.Events;
using Eve.Inventory.Mutation;
using Npgsql;

namespace Eve.Inventory;

/// <summary>The client's inventory flags that S2 needs.</summary>
public static class ItemFlags
{
    /// <summary>The hangar of a station.</summary>
    public const int Hangar = 4;

    /// <summary>The cargo hold of a ship (what a character carries loose in its ship).</summary>
    public const int Cargo = 5;

    /// <summary>The seven corporation hangar divisions.</summary>
    public const int CorpSAG1 = 116;
    public const int CorpSAG2 = 117;
    public const int CorpSAG3 = 118;
    public const int CorpSAG4 = 119;
    public const int CorpSAG5 = 120;
    public const int CorpSAG6 = 121;
    public const int CorpSAG7 = 122;
    public const int HiddenModifiers = 156;
    public static bool IsCorporationHangar(int flag) => flag is >= CorpSAG1 and <= CorpSAG7;

    /// <summary>The first of the eight low slots of a ship (11 to 18).</summary>
    public const int LowSlot0 = 11;

    /// <summary>The first of the eight medium slots of a ship (19 to 26).</summary>
    public const int MedSlot0 = 19;

    /// <summary>The first of the eight high slots of a ship (27 to 34).</summary>
    public const int HiSlot0 = 27;
}

public sealed record Item(long Id, int TypeId, long OwnerId, long LocationId, int Flag, long Quantity,
    bool Singleton, string? CustomName, bool BlueprintCopy = false, bool BlueprintLocked = false,
    int? BlueprintRuns = null, int MaterialEfficiency = 0, int TimeEfficiency = 0, string? CustomInfo = null);


public sealed record ItemMergeResult(Item SourceBefore, Item DestinationBefore, Item DestinationAfter, Item? SourceAfter);

public sealed record NewItem(int TypeId, long OwnerId, long LocationId, int Flag, long Quantity = 1,
    bool Singleton = false, string? CustomName = null, bool BlueprintCopy = false, bool BlueprintLocked = false,
    int? BlueprintRuns = null, int MaterialEfficiency = 0, int TimeEfficiency = 0, string? CustomInfo = null);


public enum ShipContractInvalidationReason
{
    OwnerChanged,
    CorporationHangar,
    Repackaged,
}

public interface IShipInsuranceInvalidator
{
    ValueTask InvalidateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long itemId,
        ShipContractInvalidationReason reason,
        CancellationToken cancellationToken);
}

/// <summary>Items in PostgreSQL, in the module's own schema (<c>inventory</c> unless told otherwise).</summary>
public sealed class PgItemStore
{
    public const string DefaultSchema = "inventory";

    private const string Columns = "id, type_id, owner_id, location_id, flag, quantity, singleton, custom_name, blueprint_copy, blueprint_locked, blueprint_runs, material_efficiency, time_efficiency, custom_info";

    private readonly NpgsqlDataSource _source;
    private readonly string _schema;

    public IShipInsuranceInvalidator? ShipInsuranceInvalidator { get; set; }

    /// <summary>
    /// The bus that announces a move (<see cref="ItemMovedTopic"/>), written in the move's own transaction. Null: moves are not announced.
    /// A container's loot leaving it is what a site waits for (<c>site.container.cleared</c>).
    /// </summary>
    public EventBus? Events { get; set; }

    public const string ItemMovedTopic = "inventory.item_moved";

    /// <summary>The provenance of the mutated items (<c>item_mutations</c>, in this store's schema): the one way an item's mutation is read.</summary>
    public ItemMutationStore Mutations { get; }

    public PgItemStore(NpgsqlDataSource source, string schema = DefaultSchema, IShipInsuranceInvalidator? shipInsuranceInvalidator = null)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(schema, "^[a-z_][a-z0-9_]{0,62}$"))
        {
            throw new ArgumentException($"not a usable schema name: {schema}", nameof(schema));
        }
        _source = source;
        _schema = schema;
        ShipInsuranceInvalidator = shipInsuranceInvalidator;
        Mutations = new ItemMutationStore(source, schema);
    }

    public static IReadOnlyList<Migration> MigrationSet() =>
    [
        .. Migrations.Embedded(typeof(PgItemStore).Assembly, "Migrations"),
        new Migration("0002_blueprint_lock_state", """
            ALTER TABLE items
                ADD COLUMN blueprint_copy boolean NOT NULL DEFAULT false,
                ADD COLUMN blueprint_locked boolean NOT NULL DEFAULT false;
            """),
        new Migration("0003_industry_blueprint_quality", """
            ALTER TABLE items
                ADD COLUMN blueprint_runs integer,
                ADD COLUMN material_efficiency integer NOT NULL DEFAULT 0,
                ADD COLUMN time_efficiency integer NOT NULL DEFAULT 0;
            """),
    ];

    public static Task<IReadOnlyList<string>> MigrateAsync(NpgsqlDataSource source, string schema = DefaultSchema, CancellationToken cancellationToken = default) =>
        Migrations.ApplyAsync(source, schema, MigrationSet(), cancellationToken);

    private static Item Read(NpgsqlDataReader r) => new(
        r.GetInt64(0), r.GetInt32(1), r.GetInt64(2), r.GetInt64(3), r.GetInt32(4), r.GetInt64(5),
        r.GetBoolean(6), r.IsDBNull(7) ? null : r.GetString(7), r.GetBoolean(8), r.GetBoolean(9),
        r.IsDBNull(10) ? null : r.GetInt32(10), r.GetInt32(11), r.GetInt32(12),
        r.IsDBNull(13) ? null : r.GetString(13));

    /// <summary>Creates an item inside the caller's transaction and returns its id.</summary>
    public async Task<long> CreateAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, NewItem item, CancellationToken cancellationToken = default)
    {
        await using var command = new NpgsqlCommand(
            $"INSERT INTO {_schema}.items (type_id, owner_id, location_id, flag, quantity, singleton, custom_name, blueprint_copy, blueprint_locked, blueprint_runs, material_efficiency, time_efficiency, custom_info) VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13) RETURNING id",
            connection, transaction)
        {
            Parameters =
            {
                new() { Value = item.TypeId },
                new() { Value = item.OwnerId },
                new() { Value = item.LocationId },
                new() { Value = item.Flag },
                new() { Value = item.Quantity },
                new() { Value = item.Singleton },
                new() { Value = (object?)item.CustomName ?? DBNull.Value },
                new() { Value = item.BlueprintCopy },
                new() { Value = item.BlueprintLocked },
                new() { Value = (object?)item.BlueprintRuns ?? DBNull.Value },
                new() { Value = item.MaterialEfficiency },
                new() { Value = item.TimeEfficiency },
                new() { Value = (object?)item.CustomInfo ?? DBNull.Value },
            },
        };
        return (long)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    /// <summary>Puts an item somewhere else (another location, another flag), inside the caller's transaction.</summary>
    public async Task MoveAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long id, long locationId, int flag, CancellationToken cancellationToken = default)
    {
        var item = await LockAndGetAsync(connection, transaction, id, cancellationToken);
        if (item is null) return;
        EnsureUnlocked(item);
        await using var command = new NpgsqlCommand(
            $"UPDATE {_schema}.items SET location_id = $2, flag = $3 WHERE id = $1",
            connection, transaction)
        {
            Parameters = { new() { Value = id }, new() { Value = locationId }, new() { Value = flag } },
        };
        await command.ExecuteNonQueryAsync(cancellationToken);
        await AnnounceMoveAsync(connection, transaction, item, locationId, flag, cancellationToken);
        if (ItemFlags.IsCorporationHangar(flag) && ShipInsuranceInvalidator is { } invalidator)
            await invalidator.InvalidateAsync(connection, transaction, id, ShipContractInvalidationReason.CorporationHangar, cancellationToken);
    }

    /// <summary>Puts an item somewhere else and gives it another owner (what is taken out of a can or a wreck is the taker's), inside the caller's transaction.</summary>
    public async Task MoveToOwnerAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long id, long ownerId, long locationId, int flag, CancellationToken cancellationToken = default)
    {
        var item = await LockAndGetAsync(connection, transaction, id, cancellationToken);
        if (item is null) return;
        EnsureUnlocked(item);
        await using var command = new NpgsqlCommand(
            $"UPDATE {_schema}.items SET owner_id = $2, location_id = $3, flag = $4 WHERE id = $1",
            connection, transaction)
        {
            Parameters = { new() { Value = id }, new() { Value = ownerId }, new() { Value = locationId }, new() { Value = flag } },
        };
        await command.ExecuteNonQueryAsync(cancellationToken);
        await AnnounceMoveAsync(connection, transaction, item, locationId, flag, cancellationToken);
        if (item.OwnerId != ownerId && ShipInsuranceInvalidator is { } invalidator)
            await invalidator.InvalidateAsync(connection, transaction, id, ShipContractInvalidationReason.OwnerChanged, cancellationToken);
    }

    /// <summary>Sets an item's custom info (the text the host keeps on it, such as a crystal's damage), inside the caller's transaction.</summary>
    public async Task SetCustomInfoAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long id, string? customInfo, CancellationToken cancellationToken = default)
    {
        await using var command = new NpgsqlCommand(
            $"UPDATE {_schema}.items SET custom_info = $2 WHERE id = $1", connection, transaction)
        {
            Parameters = { new() { Value = id }, new() { Value = (object?)customInfo ?? DBNull.Value } },
        };
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>Gives a stack another quantity, inside the caller's transaction.</summary>
    public async Task SetQuantityAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long id, long quantity, CancellationToken cancellationToken = default)
    {
        var item = await LockAndGetAsync(connection, transaction, id, cancellationToken);
        if (item is null) return;
        EnsureUnlocked(item);
        await using var command = new NpgsqlCommand(
            $"UPDATE {_schema}.items SET quantity = $2 WHERE id = $1",
            connection, transaction)
        {
            Parameters = { new() { Value = id }, new() { Value = quantity } },
        };
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>Makes an item a singleton (an assembled one: a drone that was launched from a stack is one), inside the caller's transaction.</summary>
    public async Task MakeSingletonAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long id, CancellationToken cancellationToken = default)
    {
        var item = await LockAndGetAsync(connection, transaction, id, cancellationToken);
        if (item is null) return;
        EnsureUnlocked(item);
        await using var command = new NpgsqlCommand(
            $"UPDATE {_schema}.items SET singleton = true, quantity = 1 WHERE id = $1",
            connection, transaction)
        {
            Parameters = { new() { Value = id } },
        };
        await command.ExecuteNonQueryAsync(cancellationToken);
    }


    public async Task<bool> SetCustomNameAsync(long id, string? customName, CancellationToken cancellationToken = default)
    {
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var item = await LockAndGetAsync(connection, transaction, id, cancellationToken);
        if (item is null) return false;
        EnsureUnlocked(item);
        await using var command = new NpgsqlCommand(
            $"UPDATE {_schema}.items SET custom_name = $2 WHERE id = $1", connection, transaction)
        {
            Parameters = { new() { Value = id }, new() { Value = (object?)customName ?? DBNull.Value } },
        };
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    /// <summary>Who owns something with this flag, once each (what a sweep for items that are in space asks: a ship, a can or a wreck has the flag of no place).</summary>
    public async Task<IReadOnlyList<long>> OwnersWithFlagAsync(int flag, CancellationToken cancellationToken = default)
    {
        await using var command = _source.CreateCommand($"SELECT DISTINCT owner_id FROM {_schema}.items WHERE flag = $1 ORDER BY owner_id");
        command.Parameters.Add(new NpgsqlParameter { Value = flag });
        var owners = new List<long>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            owners.Add(reader.GetInt64(0));
        }
        return owners;
    }

    /// <summary>The number of items in a place (what a container still holds), inside the caller's transaction.</summary>
    public async Task<long> CountInLocationAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long locationId, CancellationToken cancellationToken = default)
    {
        await using var command = new NpgsqlCommand($"SELECT count(*) FROM {_schema}.items WHERE location_id = $1", connection, transaction)
        {
            Parameters = { new() { Value = locationId } },
        };
        return (long)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    /// <summary>Announces that an item left its place (in the move's transaction, so it commits with the move). Nothing when no bus is attached or the place did not change.</summary>
    private async Task AnnounceMoveAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Item before, long locationId, int flag, CancellationToken cancellationToken)
    {
        if (Events is null || before.LocationId == locationId) return;
        await Events.PublishInTransactionAsync(connection, transaction, ItemMovedTopic,
            new { itemId = before.Id, ownerId = before.OwnerId, previousLocationId = before.LocationId, locationId, flag }, key: $"item:{before.Id}", cancellationToken: cancellationToken);
    }

    /// <summary>Lock an inventory fact in the caller's transaction before deciding custody or quantity.</summary>
    public async Task<Item?> LockAndGetAsync(NpgsqlConnection connection,NpgsqlTransaction transaction,long id,CancellationToken cancellationToken = default)
    {
        await using var command = new NpgsqlCommand($"SELECT {Columns} FROM {_schema}.items WHERE id=$1 FOR UPDATE",connection,transaction)
        {
            Parameters = { new() { Value=id } },
        };
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
    }

private static void EnsureUnlocked(Item? item)
    {
        if (item is { BlueprintLocked: true })
            throw new InvalidOperationException("ITEM_LOCKED");
    }

    public async Task SetBlueprintLockedAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long corporationId,
        long itemId,
        bool locked,
        CancellationToken cancellationToken = default)
    {
        var item = await LockAndGetAsync(connection, transaction, itemId, cancellationToken);
        if (item is null || item.OwnerId != corporationId || item.LocationId <= 0 ||
            !ItemFlags.IsCorporationHangar(item.Flag) || !item.Singleton || item.BlueprintCopy)
            throw new InvalidOperationException("ITEM_NOT_CORPORATION_BLUEPRINT_ORIGINAL");
        if (item.BlueprintLocked == locked)
            throw new InvalidOperationException(locked ? "ITEM_ALREADY_LOCKED" : "ITEM_NOT_LOCKED");

        await using var command = new NpgsqlCommand(
            $"UPDATE {_schema}.items SET blueprint_locked=$3 WHERE id=$1 AND owner_id=$2",
            connection, transaction)
        {
            Parameters =
            {
                new() { Value = itemId },
                new() { Value = corporationId },
                new() { Value = locked },
            },
        };
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw new InvalidOperationException("ITEM_CHANGED");
    }

/// <summary>Locks any SDE-validated blueprint item for an industry job, including personal and copy blueprints.</summary>
    public async Task SetJobBlueprintLockedAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long ownerId,
        long itemId,
        int typeId,
        long locationId,
        int flag,
        bool blueprintCopy,
        bool locked,
        CancellationToken cancellationToken = default)
    {
        var item = await LockAndGetAsync(connection, transaction, itemId, cancellationToken);
        if (item is null || item.OwnerId != ownerId || item.TypeId != typeId ||
            item.LocationId != locationId || item.Flag != flag || !item.Singleton || item.BlueprintCopy != blueprintCopy)
            throw new InvalidOperationException("INDUSTRY_BLUEPRINT_CHANGED");
        if (item.BlueprintLocked == locked)
            throw new InvalidOperationException(locked ? "INDUSTRY_BLUEPRINT_ALREADY_LOCKED" : "INDUSTRY_BLUEPRINT_NOT_LOCKED");

        await using var command = new NpgsqlCommand(
            $"UPDATE {_schema}.items SET blueprint_locked=$2 WHERE id=$1 AND owner_id=$3",
            connection, transaction)
        {
            Parameters =
            {
                new() { Value = itemId },
                new() { Value = locked },
                new() { Value = ownerId },
            },
        };
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw new InvalidOperationException("INDUSTRY_BLUEPRINT_CHANGED");
    }

    /// <summary>Persists copy runs and research efficiency on the authoritative inventory item row.</summary>
    public async Task SetIndustryBlueprintStateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long ownerId,
        long itemId,
        int typeId,
        bool blueprintCopy,
        int? blueprintRuns,
        int materialEfficiency,
        int timeEfficiency,
        CancellationToken cancellationToken = default)
    {
        var item = await LockAndGetAsync(connection, transaction, itemId, cancellationToken);
        if (item is null || item.OwnerId != ownerId || item.TypeId != typeId ||
            !item.Singleton || item.BlueprintCopy != blueprintCopy || item.BlueprintLocked)
            throw new InvalidOperationException("INDUSTRY_BLUEPRINT_CHANGED");
        if (blueprintRuns < 0 || materialEfficiency < 0 || timeEfficiency < 0)
            throw new ArgumentOutOfRangeException(nameof(blueprintRuns));

        await using var command = new NpgsqlCommand(
            $"UPDATE {_schema}.items SET blueprint_runs=$2, material_efficiency=$3, time_efficiency=$4 WHERE id=$1 AND owner_id=$5",
            connection, transaction)
        {
            Parameters =
            {
                new() { Value = itemId },
                new() { Value = (object?)blueprintRuns ?? DBNull.Value },
                new() { Value = materialEfficiency },
                new() { Value = timeEfficiency },
                new() { Value = ownerId },
            },
        };
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw new InvalidOperationException("INDUSTRY_BLUEPRINT_CHANGED");
    }

    /// <summary>Move or split a packaged stack atomically. A stale expected fact cannot spend the same items twice.</summary>
    public async Task<Item> MoveStackAsync(NpgsqlConnection connection,NpgsqlTransaction transaction,Item expected,long quantity,
        long ownerId,long locationId,int flag,CancellationToken cancellationToken = default)
    {
        if (ownerId <= 0 || locationId <= 0 || flag < 0) throw new ArgumentOutOfRangeException(nameof(ownerId));
        var item = await LockAndGetAsync(connection,transaction,expected.Id,cancellationToken);
        if (item != expected) throw new InvalidOperationException("ITEM_CHANGED");
        EnsureUnlocked(item);
        if (quantity <= 0 || item.Singleton || item.Quantity < quantity) throw new ArgumentOutOfRangeException(nameof(quantity));
        var sql = item.Quantity == quantity
            ? $"UPDATE {_schema}.items SET owner_id=$3,location_id=$4,flag=$5 WHERE id=$1 AND quantity=$2 RETURNING {Columns}"
            : $"""
              WITH reduced AS (
                  UPDATE {_schema}.items SET quantity=quantity-$2 WHERE id=$1 RETURNING type_id,custom_name,blueprint_copy,custom_info
              )
              INSERT INTO {_schema}.items(type_id,owner_id,location_id,flag,quantity,singleton,custom_name,blueprint_copy,custom_info)
              SELECT type_id,$3,$4,$5,$2,false,custom_name,blueprint_copy,custom_info FROM reduced RETURNING {Columns}
              """;
        Item moved;
        await using (var command = new NpgsqlCommand(sql,connection,transaction)
        {
            Parameters = { new() { Value=item.Id },new() { Value=quantity },new() { Value=ownerId },new() { Value=locationId },new() { Value=flag } },
        })
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken)) throw new InvalidOperationException("ITEM_CHANGED");
            moved = Read(reader);
        }
        await AnnounceMoveAsync(connection, transaction, item, locationId, flag, cancellationToken);
        var reason = item.OwnerId != ownerId
            ? ShipContractInvalidationReason.OwnerChanged
            : ItemFlags.IsCorporationHangar(flag) ? ShipContractInvalidationReason.CorporationHangar : (ShipContractInvalidationReason?)null;
        if (reason is { } invalidationReason && ShipInsuranceInvalidator is { } invalidator)
            await invalidator.InvalidateAsync(connection, transaction, moved.Id, invalidationReason, cancellationToken);
        return moved;
    }


    public async Task<ItemMergeResult?> MergeAsync(
        long sourceId, long destinationId, long quantity, long ownerId, long sourceLocationId, long destinationLocationId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var merged = await MergeAsync(connection, transaction, sourceId, destinationId, quantity, ownerId, sourceLocationId, destinationLocationId, cancellationToken);
        if (merged is null) return null;
        await transaction.CommitAsync(cancellationToken);
        return merged;
    }

    public async Task<ItemMergeResult?> MergeAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, long sourceId, long destinationId, long quantity,
        long ownerId, long sourceLocationId, long destinationLocationId, CancellationToken cancellationToken = default)
    {
        if (sourceId <= 0 || destinationId <= 0 || sourceId == destinationId || ownerId <= 0
            || sourceLocationId <= 0 || destinationLocationId <= 0)
            return null;

        var firstId = Math.Min(sourceId, destinationId);
        var secondId = Math.Max(sourceId, destinationId);
        var first = await LockAndGetAsync(connection, transaction, firstId, cancellationToken);
        var second = await LockAndGetAsync(connection, transaction, secondId, cancellationToken);
        var sourceItem = sourceId == firstId ? first : second;
        var destinationItem = destinationId == firstId ? first : second;
        if (sourceItem is null || destinationItem is null || sourceItem.OwnerId != ownerId || destinationItem.OwnerId != ownerId
            || sourceItem.LocationId != sourceLocationId || destinationItem.LocationId != destinationLocationId
            || sourceItem.TypeId != destinationItem.TypeId || sourceItem.Singleton || destinationItem.Singleton
            || sourceItem.Quantity <= 0 || destinationItem.Quantity <= 0)
            return null;
        EnsureUnlocked(sourceItem);
        EnsureUnlocked(destinationItem);
        var moved = quantity <= 0 ? sourceItem.Quantity : quantity;
        if (moved > sourceItem.Quantity || destinationItem.Quantity > long.MaxValue - moved) return null;

        await SetQuantityAsync(connection, transaction, destinationItem.Id, destinationItem.Quantity + moved, cancellationToken);
        Item? sourceAfter = null;
        if (moved == sourceItem.Quantity)
            await DeleteAsync(connection, transaction, sourceItem.Id, cancellationToken);
        else
        {
            await SetQuantityAsync(connection, transaction, sourceItem.Id, sourceItem.Quantity - moved, cancellationToken);
            sourceAfter = sourceItem with { Quantity = sourceItem.Quantity - moved };
        }
        return new ItemMergeResult(sourceItem, destinationItem, destinationItem with { Quantity = destinationItem.Quantity + moved }, sourceAfter);
    }


    public async Task<Item?> RepackageAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long id,
        bool ship,
        CancellationToken cancellationToken = default)
    {
        var item = await LockAndGetAsync(connection, transaction, id, cancellationToken);
        if (item is null || !item.Singleton) return null;
        EnsureUnlocked(item);

        var resetName = ship ? ", custom_name = NULL" : string.Empty;
        await using var command = new NpgsqlCommand(
            $"UPDATE {_schema}.items SET singleton = false, quantity = 1{resetName} WHERE id = $1 AND singleton = true RETURNING {Columns}",
            connection,
            transaction)
        {
            Parameters = { new() { Value = id } },
        };
        Item packaged;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken)) return null;
            packaged = Read(reader);
        }

        // Persist the void in the same transaction so it is visible only if the repackage commits.
        if (ship && ShipInsuranceInvalidator is { } invalidator)
            await invalidator.InvalidateAsync(connection, transaction, id, ShipContractInvalidationReason.Repackaged, cancellationToken);
        return packaged;
    }

    public async Task<Item?> GetAsync(long id, CancellationToken cancellationToken = default)
    {
        await using var command = _source.CreateCommand($"SELECT {Columns} FROM {_schema}.items WHERE id = $1");
        command.Parameters.Add(new NpgsqlParameter { Value = id });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
    }

    /// <summary>The items an owner has at a location (a station), oldest first.</summary>
    public async Task<IReadOnlyList<Item>> InLocationAsync(long locationId, long ownerId, CancellationToken cancellationToken = default)
    {
        await using var command = _source.CreateCommand($"SELECT {Columns} FROM {_schema}.items WHERE location_id = $1 AND owner_id = $2 ORDER BY id");
        command.Parameters.Add(new NpgsqlParameter { Value = locationId });
        command.Parameters.Add(new NpgsqlParameter { Value = ownerId });
        var list = new List<Item>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            list.Add(Read(reader));
        }
        return list;
    }

/// <summary>Locks candidate material rows in deterministic item order on the caller's transaction.</summary>
    public async Task<IReadOnlyList<Item>> LockInLocationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long locationId,
        long ownerId,
        int flag,
        IReadOnlyCollection<int> typeIds,
        CancellationToken cancellationToken = default)
    {
        var types = typeIds.Distinct().Order().ToArray();
        if (types.Length == 0) return [];
        await using var command = new NpgsqlCommand(
            $"SELECT {Columns} FROM {_schema}.items WHERE location_id=$1 AND owner_id=$2 AND flag=$3 AND type_id=ANY($4) ORDER BY id FOR UPDATE",
            connection, transaction)
        {
            Parameters =
            {
                new() { Value = locationId },
                new() { Value = ownerId },
                new() { Value = flag },
                new() { Value = types },
            },
        };
        var items = new List<Item>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) items.Add(Read(reader));
        return items;
    }

    /// <summary>Locks the stacks an owner holds of a type, for a consumption in the caller's transaction (ordered by id, so a second taker waits and then sees them gone).</summary>
    public async Task<IReadOnlyList<Item>> LockOwnedOfTypeAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long ownerId, int typeId, CancellationToken cancellationToken = default)
    {
        await using var command = new NpgsqlCommand(
            $"SELECT {Columns} FROM {_schema}.items WHERE owner_id=$1 AND type_id=$2 ORDER BY id FOR UPDATE",
            connection, transaction)
        {
            Parameters =
            {
                new() { Value = ownerId },
                new() { Value = typeId },
            },
        };
        var items = new List<Item>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) items.Add(Read(reader));
        return items;
    }

    /// <summary>Locks every item inside a container for an atomic custody change.</summary>
    public async Task<IReadOnlyList<Item>> LockContainerAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long locationId,
        CancellationToken cancellationToken = default)
    {
        await using var command = new NpgsqlCommand(
            $"SELECT {Columns} FROM {_schema}.items WHERE location_id=$1 ORDER BY id FOR UPDATE",
            connection, transaction)
        {
            Parameters = { new() { Value = locationId } },
        };
        var contents = new List<Item>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) contents.Add(Read(reader));
        return contents;
    }

    /// <summary>Everything in a place (a ship, a container, a wreck), whoever owns it, oldest first.</summary>
    public async Task<IReadOnlyList<Item>> InContainerAsync(long locationId, CancellationToken cancellationToken = default)
    {
        await using var command = _source.CreateCommand($"SELECT {Columns} FROM {_schema}.items WHERE location_id = $1 ORDER BY id");
        command.Parameters.Add(new NpgsqlParameter { Value = locationId });
        var list = new List<Item>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            list.Add(Read(reader));
        }
        return list;
    }

    /// <summary>Everything in a place, read inside the caller's transaction (a structure's fuel bay is read and burned under the structure's row lock).</summary>
    public async Task<IReadOnlyList<Item>> InContainerAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, long locationId, CancellationToken cancellationToken = default)
    {
        await using var command = Sql.Command(connection, transaction, $"SELECT {Columns} FROM {_schema}.items WHERE location_id = $1 ORDER BY id", locationId);
        var list = new List<Item>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            list.Add(Read(reader));
        }
        return list;
    }

    /// <summary>Takes an item out of the world, inside the caller's transaction.</summary>
    public async Task DeleteAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long id, CancellationToken cancellationToken = default)
    {
        var item = await LockAndGetAsync(connection, transaction, id, cancellationToken);
        if (item is null) return;
        EnsureUnlocked(item);
        await using var command = new NpgsqlCommand(
            $"DELETE FROM {_schema}.items WHERE id = $1",
            connection, transaction)
        {
            Parameters = { new() { Value = id } },
        };
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Item>> OwnedByAsync(long ownerId, CancellationToken cancellationToken = default)
    {
        await using var command = _source.CreateCommand($"SELECT {Columns} FROM {_schema}.items WHERE owner_id = $1 ORDER BY id");
        command.Parameters.Add(new NpgsqlParameter { Value = ownerId });
        var list = new List<Item>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            list.Add(Read(reader));
        }
        return list;
    }
}
