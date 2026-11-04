using System.Text.Json.Nodes;
using Eve.Characters;
using Eve.Dogma;
using Eve.Events;
using Eve.Inventory;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;
using Eve.Services.Dogma;
using Eve.Ships;
using Eve.Structures;
using Npgsql;

namespace Eve.Host;


public sealed class StructureDeliveryBackend(
    NpgsqlDataSource source,
    PgItemStore items,
    StructureStore structures,
    IStructureRulesSource rules,
    DogmaStatic dogma,
    PgCharacterStore characters,
    CharacterSessions sessions,
    EventBus events,
    Func<long, CancellationToken, ValueTask<PersistedStructureInfo?>> structureInfoAsync,
    Func<ServiceCall, long, long, CancellationToken, ValueTask<double?>> cargoDepositSurfaceDistanceAsync,
    StructureDeliveryBackend.PersistDeliveryInboxAsync? persistDeliveryInboxAsync = null,
    Func<long>? nowUnixMs = null)
{
    public const double CargoDepositRangeMeters = 10_000;
    public const string ItemMovedTopic = "inventory.item_moved";
    private const int DeliveriesFlag = 173;
    private const int StructureFuelBayFlag = 172;
    // The V24.01 delivery set excludes these five bays from the shared ship-hold set.
    private static readonly HashSet<int> ExcludedHolds = [87, 151, 158, 179, 186];

    public sealed record ItemMoved(
        long CharacterId, long ItemId, long PreviousOwnerId, long OwnerId,
        long PreviousLocationId, long LocationId, int PreviousFlag, int Flag);
    private sealed record Move(Item Before, Item After);

    /// <summary>
    /// Persists into the existing notifications mailbox using this connection/transaction and

    /// the uncommitted row in the live cache. Return the saved ID and the shared publisher's
    /// post-commit callback, which must refresh the mailbox cache before sending its arguments.
    /// </summary>
    public delegate ValueTask<DeliveryInboxCommit> PersistDeliveryInboxAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction,
        DeliveryInboxNotification notification, CancellationToken cancellationToken);

    public sealed record DeliveryInboxCommit(long NotificationID, Func<PyValue[], ValueTask> PublishAsync);


    // structures.py StructureItemsDelivered.MakeData consumes [quantity, typeID], not the reverse.
    public sealed record DeliveryInboxNotification(
        long RecipientCharacterID, long SenderCharacterID, long CreatedFileTime, JsonObject Data)
    {
        public const int TypeID = 199;
        public const int GroupID = 4;

        public JsonObject ToRecord(long notificationID) => new()
        {
            ["notificationID"] = notificationID,
            ["typeID"] = TypeID,
            ["senderID"] = SenderCharacterID,
            ["receiverID"] = RecipientCharacterID,
            ["groupID"] = GroupID,
            ["processed"] = false,
            ["created"] = CreatedFileTime,
            ["data"] = Data.DeepClone(),
        };

        // notificationSvc.OnNotificationReceived(notificationID, typeID, senderID, created, data).
        public PyValue[] WireArguments(long notificationID) =>
        [
            new PyLong(notificationID), new PyInt(TypeID), new PyLong(SenderCharacterID),
            Py.FileTimeLong(CreatedFileTime), WireValue(Data),
        ];

        private static PyValue WireValue(JsonNode? node) => node switch
        {
            JsonObject obj => new PyDict([.. obj.Select(pair =>
                new KeyValuePair<PyValue, PyValue>(new PyStr(pair.Key), WireValue(pair.Value)))]),
            JsonArray array => new PyList([.. array.Select(WireValue)]),
            JsonValue value when value.TryGetValue<string>(out var text) => new PyStr(text),
            JsonValue value when value.TryGetValue<long>(out var number) => new PyLong(number),
            JsonValue value when value.TryGetValue<int>(out var number) => new PyInt(number),
            _ => throw new InvalidOperationException("Invalid StructureItemsDelivered notification data."),
        };
    }

    public async ValueTask DeliverAsync(
        ServiceCall call, long structureID, long recipientCharacterID, IReadOnlyList<long> itemIDs)
    {
        var characterID = RequireCharacter(call);
        RequireDocked(call, characterID, structureID);
        if (recipientCharacterID <= 0 || await characters.GetAsync(recipientCharacterID, call.Cancellation) is null)
            throw Notify("Choose a capsuleer to receive the delivered items.");
        await RequireDeployedAsync(structureID, call.Cancellation);
        var inboxWriter = persistDeliveryInboxAsync ??
            throw Notify("Structure delivery inbox persistence is unavailable.");
        var ids = Normalize(itemIDs);
        var changes = new List<Move>();
        DeliveryInboxNotification? inbox = null;
        DeliveryInboxCommit? inboxCommit = null;
        await using (var connection = await source.OpenConnectionAsync(call.Cancellation))
        await using (var transaction = await connection.BeginTransactionAsync(call.Cancellation))
        {
            var structure = await structures.GetAsync(connection, transaction, structureID, true, call.Cancellation);
            if (structure is not null) await dogma.EnsureTypesAsync([structure.TypeId], call.Cancellation);
            if (SizeOf(structure) is not { } size || size == StructureSize.Flex)
                throw Notify("That structure cannot accept personal deliveries.");
            var locked = await LockItemsAsync(connection, transaction, ids, call.Cancellation);
            RequireDocked(call, characterID, structureID);
            foreach (var id in ids)
            {
                if (!locked.TryGetValue(id, out var item) || !ValidItem(item) ||
                    item.OwnerId != characterID || item.LocationId != structureID || item.Flag != ItemFlags.Hangar)
                    continue;
                changes.Add(await MoveAsync(connection, transaction, item, characterID,
                    recipientCharacterID, structureID, DeliveriesFlag, call.Cancellation));
            }
            if (changes.Count > 0)
            {
                var rows = new JsonArray();
                foreach (var group in changes.GroupBy(change => change.After.TypeId).OrderBy(group => group.Key))
                    rows.Add(new JsonArray(JsonValue.Create(group.Sum(change => change.After.Singleton ? 1L : change.After.Quantity)),
                        JsonValue.Create(group.Key)));
                var created = DateTimeOffset.FromUnixTimeMilliseconds(
                    nowUnixMs?.Invoke() ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()).UtcDateTime.ToFileTimeUtc();
                inbox = new DeliveryInboxNotification(recipientCharacterID, characterID, created, new JsonObject
                {
                    ["structureID"] = structureID,
                    ["structureShowInfoData"] = new JsonArray(JsonValue.Create("showinfo"),
                        JsonValue.Create(structure!.TypeId), JsonValue.Create(structureID)),
                    ["solarsystemID"] = structure.SolarSystemId,
                    ["structureTypeID"] = structure.TypeId,
                    ["charID"] = characterID,
                    ["listOfTypesAndQty"] = rows,
                });
                inboxCommit = await inboxWriter(connection, transaction, inbox, call.Cancellation);
                if (inboxCommit is null || inboxCommit.NotificationID <= 0 || inboxCommit.PublishAsync is null)
                    throw new InvalidOperationException("Structure delivery inbox persistence did not return a saved notification.");
            }
            await transaction.CommitAsync(call.Cancellation);
        }
        try { SendItemChanges(call, characterID, recipientCharacterID, changes); }
        finally
        {
            try
            {
                if (inboxCommit is not null)
                    await inboxCommit.PublishAsync(inbox!.WireArguments(inboxCommit.NotificationID));
            }
            finally { events.Wake(); }
        }
    }

    /// <summary>
    /// The distance callback must read the active simulation, return surface distance in metres,
    /// and return null for unreachable, cloaked, warping, weapons-timer or criminal-timer state.
    /// It must not test docking/tether permissions.
    /// </summary>
    public async ValueTask<IReadOnlyList<long>> DropOffItemsAsync(
        ServiceCall call, IReadOnlyList<long> itemIDs, long shipID, long structureID)
    {
        var characterID = RequireCharacter(call);
        var solarSystemID = RequireInSpace(call, characterID, shipID);
        var character = await characters.GetAsync(characterID, call.Cancellation);
        if (character is null || character.ShipId != shipID || character.SolarSystemId != solarSystemID)
            throw Notify("Cargo Deposit is only available for your active ship.");
        await RequireDeployedAsync(structureID, call.Cancellation);
        var preflight = await structures.GetAsync(structureID, call.Cancellation);
        if (preflight is not null) await dogma.EnsureTypesAsync([preflight.TypeId], call.Cancellation);
        if (preflight is null || SizeOf(preflight) is null || preflight.SolarSystemId != solarSystemID)
            throw Notify("You are not in the same solar system as that structure.");
        var distance = await cargoDepositSurfaceDistanceAsync(call, shipID, structureID, call.Cancellation);
        if (distance is not { } metres || !double.IsFinite(metres) || metres < 0 || metres > CargoDepositRangeMeters)
            throw Notify("That structure is not reachable within Cargo Deposit range.");

        var ids = Normalize(itemIDs);
        var changes = new List<Move>();
        await using (var connection = await source.OpenConnectionAsync(call.Cancellation))
        await using (var transaction = await connection.BeginTransactionAsync(call.Cancellation))
        {
            var structure = await structures.GetAsync(connection, transaction, structureID, true, call.Cancellation);
            var size = SizeOf(structure);
            if (structure is null || size is null || structure.SolarSystemId != solarSystemID || structure.TypeId != preflight.TypeId)
                throw Notify("That structure is no longer available.");
            var ship = await items.LockAndGetAsync(connection, transaction, shipID, call.Cancellation);
            if (ship is not null) await dogma.EnsureTypesAsync([ship.TypeId], call.Cancellation);
            if (ship is null || ship.OwnerId != characterID || ship.LocationId != solarSystemID ||
                dogma.Type(ship.TypeId) is not { } shipType || dogma.CategoryName(shipType.CategoryId) != "Ship")
                throw Notify("Cargo Deposit could not find your active ship in space.");
            var locked = await LockItemsAsync(connection, transaction, ids, call.Cancellation);
            if (RequireInSpace(call, characterID, shipID) != solarSystemID)
                throw Notify("Your active ship has changed solar systems.");
            var dockable = size != StructureSize.Flex;
            var ownerID = dockable ? characterID : structure.OwnerCorporationId;
            if (ownerID <= 0)
                throw Notify("That structure has no valid fuel-bay owner.");
            var flag = dockable ? DeliveriesFlag : StructureFuelBayFlag;
            foreach (var id in ids)
            {
                if (!locked.TryGetValue(id, out var item) || !ValidItem(item) ||
                    item.OwnerId != characterID || item.LocationId != shipID ||
                    !CharacterShips.IsShipHoldFlag(item.Flag) || ExcludedHolds.Contains(item.Flag) ||
                    (!dockable && !AllowedFlexFuel(item.TypeId, structure.TypeId)))
                    continue;
                changes.Add(await MoveAsync(connection, transaction, item, characterID,
                    ownerID, structureID, flag, call.Cancellation));
            }
            await transaction.CommitAsync(call.Cancellation);
        }
        events.Wake();
        // A corporate fuel bay is not a personal hangar; do not broadcast its contents to corporation members.
        SendItemChanges(call, characterID, null, changes);
        return changes.Select(change => change.After.Id).ToArray();
    }

    private async Task RequireDeployedAsync(long structureID, CancellationToken token)
    {
        if (structureID <= 0 || await structureInfoAsync(structureID, token) is not { PersistentlyDeployed: true })
            throw Notify("That structure is no longer available.");
    }

    private StructureSize? SizeOf(StructureRecord? structure)
    {
        if (structure is null || structure.Snapshot.DestroyedMs > 0 ||
            dogma.Type(structure.TypeId) is not { } type || dogma.CategoryName(type.CategoryId) != "Structure")
            return null;
        try { return rules.RulesOf(structure.TypeId, structure.SolarSystemId).Size; }
        catch (InvalidOperationException) { return null; }
    }

    private bool ValidItem(Item item) =>
        item.Quantity > 0 && !item.BlueprintLocked && dogma.Type(item.TypeId) is not null;

    private bool AllowedFlexFuel(int itemTypeID, int structureTypeID)
    {
        var type = dogma.Type(itemTypeID)!;
        if (dogma.GroupName(type.GroupId) == "Fuel Block")
            return true;

        return dogma.Type(structureTypeID)?.Name == "Metenox Moon Drill"
            ? dogma.GroupName(type.GroupId) == "Colony Reagents" && type.Name == "Magmatic Gas"
            : type.Name == "Liquid Ozone";
    }

    private async Task<Dictionary<long, Item>> LockItemsAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, long[] ids, CancellationToken token)
    {
        var locked = new Dictionary<long, Item>();
        // A stable lock order also covers overlapping requests sent in different client orders.
        foreach (var id in ids.OrderBy(id => id))
            if (await items.LockAndGetAsync(connection, transaction, id, token) is { } item)
                locked.Add(id, item);
        await dogma.EnsureTypesAsync(locked.Values.Select(item => item.TypeId), token);
        return locked;
    }

    private async Task<Move> MoveAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Item before, long actorID,
        long ownerID, long locationID, int flag, CancellationToken token)
    {
        await items.MoveToOwnerAsync(connection, transaction, before.Id, ownerID, locationID, flag, token);
        var after = before with { OwnerId = ownerID, LocationId = locationID, Flag = flag };
        await events.PublishInTransactionAsync(connection, transaction, ItemMovedTopic,
            new ItemMoved(actorID, before.Id, before.OwnerId, ownerID,
                before.LocationId, locationID, before.Flag, flag), cancellationToken: token);
        return new Move(before, after);
    }

    private void SendItemChanges(ServiceCall call, long actorID, long? recipientID, IReadOnlyList<Move> changes)
    {
        var affected = new HashSet<ClientSession>();
        if (call.Session.CharacterId == actorID) affected.Add(call.Session);
        if (sessions.Of(actorID) is { } actor) affected.Add(actor);
        if (recipientID is { } id && sessions.Of(id) is { } recipient) affected.Add(recipient);
        foreach (var change in changes)
        {
            var differences = new List<KeyValuePair<PyValue, PyValue>>();
            if (change.Before.OwnerId != change.After.OwnerId)
                differences.Add(new(new PyInt(2), InventoryWire.Id(change.Before.OwnerId)));
            if (change.Before.LocationId != change.After.LocationId)
                differences.Add(new(new PyInt(3), InventoryWire.Id(change.Before.LocationId)));
            if (change.Before.Flag != change.After.Flag)
                differences.Add(new(new PyInt(4), new PyInt(change.Before.Flag)));
            var row = InventoryWire.PackedRow(DogmaInfo.RowOf(change.After, dogma.Type(change.After.TypeId)));
            foreach (var session in affected)
                session.SendNotification("OnItemChange", "clientID", row, new PyDict(differences), PyNone.Instance);
        }
    }

    private static long[] Normalize(IReadOnlyList<long> ids) => ids.Where(id => id > 0).Distinct().ToArray();

    private static long RequireCharacter(ServiceCall call) =>
        call.Session.CharacterId is { } id && id > 0 ? id : throw Notify("You need an active character to deliver items.");

    private static void RequireDocked(ServiceCall call, long characterID, long structureID)
    {
        if (call.Session.CharacterId != characterID || structureID <= 0 || call.Session.GetInt("structureid") != structureID)
            throw Notify("You must be inside the structure to deliver items.");
    }

    private static int RequireInSpace(ServiceCall call, long characterID, long shipID)
    {
        var solarSystemID = call.Session.GetInt("solarsystemid");
        if (call.Session.CharacterId != characterID || call.Session.DockedLocationId is > 0 ||
            shipID <= 0 || call.Session.GetInt("shipid") != shipID ||
            solarSystemID is not { } currentSystem || currentSystem <= 0 || currentSystem > int.MaxValue)
            throw Notify("Cargo Deposit is only available for your active ship in space.");
        return (int)currentSystem;
    }

    private static Exception Notify(string text) =>
        MachoErrors.ThrowUserError("CustomNotify", ("notify", new PyStr(text)));
}
