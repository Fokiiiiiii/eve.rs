using System.Text.Json;
using Eve.Corporations;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Corporations;

namespace Eve.Corporations.Tests;

/// <summary>The corporation item locks a blueprint vote makes: the lock on the item, the list the client reads, and the push.</summary>
public sealed class ItemLockTests
{
    private const int BlueprintTypeId = 34;
    private const long LocationId = 60000001;

    [PgFact]
    public async Task A_lockdown_vote_locks_the_blueprint_and_lists_it_until_an_unlock_vote_releases_it()
    {
        await using var rig = await Rig.CreateAsync();
        var (actor, corporation) = await CreateCorporationAsync(rig);
        var itemId = await CreateBlueprintAsync(rig, corporation.Id);
        var changes = new List<ItemLockChange>();
        var store = StoreWithLocks(rig);
        store.ItemLockChanged += changes.Add;

        await store.InsertVoteCaseAsync(corporation.Id, actor, "Lock the blueprint", "test", 5, [0x01], 2,
            itemId, BlueprintTypeId, LocationId, 1);

        Assert.Equal([itemId], (await store.LockedItemsAsync(corporation.Id, LocationId)).Select(item => item.ItemId));
        Assert.Equal([LocationId], await store.LockedItemLocationsAsync(corporation.Id));
        Assert.Equal([new ItemLockChange(itemId, corporation.Id, LocationId, true)], changes);
        await Assert.ThrowsAsync<InvalidOperationException>(() => MoveAsync(rig, itemId));

        // a vote that is not passed when it ends releases the lock
        rig.Now += 3 * 24L * 60 * 60 * 1000;
        await store.ResolveAllExpiredVotesAsync();

        Assert.Empty(await store.LockedItemsAsync(corporation.Id, LocationId));
        Assert.Empty(await store.LockedItemLocationsAsync(corporation.Id));
        Assert.Equal(new ItemLockChange(itemId, corporation.Id, LocationId, false), changes[^1]);
        await MoveAsync(rig, itemId);
    }

    [PgFact]
    public async Task The_push_is_sent_after_the_lock_is_committed()
    {
        await using var rig = await Rig.CreateAsync();
        var (actor, corporation) = await CreateCorporationAsync(rig);
        var itemId = await CreateBlueprintAsync(rig, corporation.Id);
        var store = StoreWithLocks(rig);
        var committedWhenPushed = new List<long>();
        // a synchronous read on a new connection: the push runs on the caller's thread, after the commit
        store.ItemLockChanged += change =>
        {
            using var connection = rig.Source.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT count(*) FROM {rig.CorporationSchema}.item_locks WHERE item_id=$1 AND location_id=$2";
            command.Parameters.AddWithValue(change.ItemId);
            command.Parameters.AddWithValue(change.LocationId);
            committedWhenPushed.Add(Convert.ToInt64(command.ExecuteScalar()));
        };

        await store.InsertVoteCaseAsync(corporation.Id, actor, "Lock the blueprint", "test", 5, [0x01], 2,
            itemId, BlueprintTypeId, LocationId, 1);

        Assert.Equal([1L], committedWhenPushed);
    }

    [PgFact]
    public async Task The_client_reads_its_corporations_locks_and_no_one_elses()
    {
        await using var rig = await Rig.CreateAsync();
        var (actor, corporation) = await CreateCorporationAsync(rig);
        var itemId = await CreateBlueprintAsync(rig, corporation.Id);
        var store = StoreWithLocks(rig);
        await store.InsertVoteCaseAsync(corporation.Id, actor, "Lock the blueprint", "test", 5, [0x01], 2,
            itemId, BlueprintTypeId, LocationId, 1);
        IService service = new ItemLockingService(store);
        Assert.True(service.HasMethod("GetItemsByLocation"));
        Assert.True(service.HasMethod("GetLockedItemLocations"));

        var member = new ClientSession { CharacterId = actor };
        member.Set("corpid", new PyInt(corporation.Id));
        var items = Assert.IsType<PyDict>(await service.CallAsync(new ServiceCall
        {
            Method = "GetItemsByLocation", Args = [new PyLong(LocationId)], Session = member,
        }));
        var entry = Assert.Single(items.Entries);
        Assert.Equal(InventoryWire.Id(itemId), entry.Key);
        Assert.IsType<PyObject>(entry.Value);

        var outsider = new ClientSession { CharacterId = actor };
        outsider.Set("corpid", new PyInt(corporation.Id + 1));
        var none = Assert.IsType<PyDict>(await service.CallAsync(new ServiceCall
        {
            Method = "GetItemsByLocation", Args = [new PyLong(LocationId)], Session = outsider,
        }));
        Assert.Empty(none.Entries);
    }

    private static CorporationStore StoreWithLocks(Rig rig) => new(rig.Source, rig.Characters, rig.Wallets, rig.Transactions,
        rig.Skills, () => rig.Now, rig.CorporationSchema,
        setBlueprintLock: (connection, transaction, corporationId, itemId, typeId, locationId, locked, token) =>
            rig.Items.SetBlueprintLockedAsync(connection, transaction, corporationId, itemId, locked, token));

    private static async Task MoveAsync(Rig rig, long itemId)
    {
        await using var connection = await rig.Source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await rig.Items.MoveAsync(connection, transaction, itemId, LocationId, Eve.Inventory.ItemFlags.CorpSAG2);
        await transaction.CommitAsync();
    }

    private static async Task<(long Actor, Corporation Corporation)> CreateCorporationAsync(Rig rig)
    {
        var actor = await rig.CharacterAsync("Lock CEO");
        var corporation = await rig.Store.CreateAsync(actor,
            new NewCorporation("Lock Works", "LOCKW", "Lock test.", "https://example.invalid", 0.05m,
                Settings: JsonSerializer.SerializeToElement(new { shape1 = 1, color1 = 2 })),
            "item-lock-founding");
        return (actor, corporation);
    }

    private static async Task<long> CreateBlueprintAsync(Rig rig, long corporationId)
    {
        await using var connection = await rig.Source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var id = await rig.Items.CreateAsync(connection, transaction,
            new Eve.Inventory.NewItem(BlueprintTypeId, corporationId, LocationId, Eve.Inventory.ItemFlags.CorpSAG1, Singleton: true));
        await transaction.CommitAsync();
        return id;
    }
}
