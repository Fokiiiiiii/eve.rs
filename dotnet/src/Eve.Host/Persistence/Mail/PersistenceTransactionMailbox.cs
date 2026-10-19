using System.Globalization;
using System.Text.Json.Nodes;
using Eve.Events;
using Eve.GameStore;
using Eve.Marshal;
using Npgsql;

namespace Eve.Host.Persistence.Mail;

internal sealed class PersistenceTransactionMailbox
{
    private const string NotificationsTable = "notifications";

    private readonly NpgsqlDataSource _source;
    private readonly PersistenceStore _store;
    private readonly GameStoreCache _cache;
    private readonly OwnerSession _owner;
    private readonly SemaphoreSlim _mutationGate;
    private readonly Func<long> _nowUnixMs;
    private readonly Action<long, PyValue[]> _sendNotification;

    public PersistenceTransactionMailbox(
        NpgsqlDataSource source,
        PersistenceStore store,
        GameStoreCache cache,
        OwnerSession owner,
        SemaphoreSlim mutationGate,
        Func<long> nowUnixMs,
        Action<long, PyValue[]> sendNotification)
    {
        _source = source;
        _store = store;
        _cache = cache;
        _owner = owner;
        _mutationGate = mutationGate;
        _nowUnixMs = nowUnixMs;
        _sendNotification = sendNotification;
    }

    public async Task<int> WriteNotificationsAsync(
        long senderId,
        int typeId,
        int groupId,
        IEnumerable<long> recipientCharacterIds,
        string idempotencyKey,
        JsonObject data,
        CancellationToken cancellationToken = default,
        EventContext? context = null)
    {

        if (senderId < 0) throw new ArgumentOutOfRangeException(nameof(senderId));
        if (typeId <= 0) throw new ArgumentOutOfRangeException(nameof(typeId));
        if (groupId < 0) throw new ArgumentOutOfRangeException(nameof(groupId));
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        ArgumentNullException.ThrowIfNull(recipientCharacterIds);
        ArgumentNullException.ThrowIfNull(data);

        var recipients = recipientCharacterIds.Distinct().OrderBy(id => id).ToArray();
        if (recipients.Any(id => id <= 0))
            throw new ArgumentOutOfRangeException(nameof(recipientCharacterIds));
        if (recipients.Length == 0)
            return 0;

        await _mutationGate.WaitAsync(cancellationToken);
        context?.AfterCompletion(() => _mutationGate.Release());
        try
        {
            _owner.AssertHealthy();
            await _cache.LoadAsync(NotificationsTable, cancellationToken);
            var cacheRead = _cache.Read(NotificationsTable);
            if (!cacheRead.Success || cacheRead.Data is not JsonObject cachedRoot)
                throw new InvalidOperationException("The notifications table is not available in the mailbox cache.");
            var cachedBoxes = cachedRoot["boxes"] as JsonObject;
            var nextNotificationId = MaximumNotificationId(cachedBoxes);
            var created = ToFileTime(_nowUnixMs());
            var upserts = new List<RowUpsert>();
            var cacheWrites = new List<(string RecipientKey, string NotificationKey, JsonObject Record)>();
            var liveNotifications = new List<(long RecipientId, long NotificationId)>();

            await using var ownedConnection = context is null ? await _source.OpenConnectionAsync(cancellationToken) : null;
            var connection = context?.Connection ?? ownedConnection!;
            await using var ownedTransaction = context is null ? await connection.BeginTransactionAsync(cancellationToken) : null;
            var transaction = context?.Transaction ?? ownedTransaction!;
            foreach (var recipientId in recipients)
            {
                var recipientKey = Key(recipientId);
                var cachedBox = cachedBoxes?[recipientKey] as JsonObject;
                var rowKey = BoxRowKey(recipientKey);
                var saved = await _store.ReadRowAsync(connection, transaction, NotificationsTable, rowKey, cancellationToken);
                var persistedBox = saved is null
                    ? null
                    : JsonNode.Parse(saved) as JsonObject ?? throw new InvalidOperationException("The persisted notification box is not an object.");
                var box = MergeBox(persistedBox, cachedBox);
                var records = box["byID"] as JsonObject;
                if (records is null)
                    box["byID"] = records = new JsonObject();

                var recipientIdempotencyKey = $"{idempotencyKey}:{recipientKey}";
                var existing = FindIdempotentRecord(records, recipientIdempotencyKey);
                if (existing is { } duplicate)
                {
                    if (cachedBox?["byID"] is not JsonObject cachedRows || cachedRows[duplicate.Key] is null)
                        cacheWrites.Add((recipientKey, duplicate.Key, (JsonObject)duplicate.Value.DeepClone()));
                    if (persistedBox is null || !JsonNode.DeepEquals(box, persistedBox))
                        upserts.Add(new RowUpsert(rowKey, box.ToJsonString()));
                    continue;
                }

                var notificationId = checked(++nextNotificationId);
                var notificationKey = Key(notificationId);
                var record = new JsonObject
                {
                    ["notificationID"] = notificationId,
                    ["typeID"] = typeId,
                    ["senderID"] = senderId,
                    ["receiverID"] = recipientId,
                    ["groupID"] = groupId,
                    ["processed"] = false,
                    ["created"] = created,
                    ["data"] = data.DeepClone(),
                    ["idempotencyKey"] = recipientIdempotencyKey,
                };
                records[notificationKey] = record;
                upserts.Add(new RowUpsert(rowKey, box.ToJsonString()));
                cacheWrites.Add((recipientKey, notificationKey, (JsonObject)record.DeepClone()));
                liveNotifications.Add((recipientId, notificationId));
            }

            if (upserts.Count > 0)
            {
                _owner.AssertHealthy();
                await _store.ApplyChangesAsync(connection, transaction, NotificationsTable, upserts, [], _owner.Fence, cancellationToken);
            }
            if (context is null)
                await transaction.CommitAsync(cancellationToken);

            Task PublishCommitted(CancellationToken _)
            {
                foreach (var (recipientKey, notificationKey, record) in cacheWrites)
                {
                    var updated = _cache.Write(NotificationsTable, $"/boxes/{recipientKey}/byID/{notificationKey}", record);
                    if (!updated.Success)
                        throw new InvalidOperationException("The committed notification could not update the mailbox cache.");
                }

                foreach (var (recipientId, notificationId) in liveNotifications)
                    _sendNotification(recipientId, WireArguments(notificationId, typeId, senderId, created, data));
                return Task.CompletedTask;
            }
            if (context is null)
                await PublishCommitted(cancellationToken);
            else
                context.AfterCommit(PublishCommitted);

            return liveNotifications.Count;
        }
        finally
        {
            if (context is null)
                _mutationGate.Release();
        }
    }

    private static string BoxRowKey(string recipientKey) =>
        RowCodec.Explode(NotificationsTable, new JsonObject
        {
            ["boxes"] = new JsonObject { [recipientKey] = new JsonObject() },
        }).Keys.Single(key => key != "boxes");

    private static JsonObject MergeBox(JsonObject? persisted, JsonObject? cached)
    {
        var result = persisted is null ? new JsonObject() : (JsonObject)persisted.DeepClone();
        if (cached is null)
            return result;

        foreach (var (key, value) in cached)
        {
            if (key == "byID" && value is JsonObject cachedRows)
            {
                var rows = result["byID"] as JsonObject;
                if (rows is null)
                    result["byID"] = rows = new JsonObject();
                foreach (var (rowKey, row) in cachedRows)
                    rows[rowKey] = row?.DeepClone();
            }
            else
            {
                result[key] = value?.DeepClone();
            }
        }
        return result;
    }

    private static KeyValuePair<string, JsonObject>? FindIdempotentRecord(JsonObject records, string idempotencyKey)
    {
        foreach (var (key, value) in records)
            if (value is JsonObject record &&
                record["idempotencyKey"] is JsonValue stored &&
                stored.TryGetValue<string>(out var storedKey) &&
                string.Equals(storedKey, idempotencyKey, StringComparison.Ordinal))
                return new KeyValuePair<string, JsonObject>(key, record);
        return null;
    }

    private static long MaximumNotificationId(JsonObject? boxes)
    {
        var maximum = 0L;
        if (boxes is null)
            return maximum;

        foreach (var (_, boxNode) in boxes)
            if (boxNode is JsonObject box && box["byID"] is JsonObject records)
                foreach (var key in records.Select(entry => entry.Key))
                    if (long.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out var id))
                        maximum = Math.Max(maximum, id);
        return maximum;
    }

    private static string Key(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static long ToFileTime(long unixMs)
    {
        if (unixMs <= 0)
            throw new ArgumentOutOfRangeException(nameof(unixMs));
        return DateTimeOffset.FromUnixTimeMilliseconds(unixMs).UtcDateTime.ToFileTimeUtc();
    }

    private static PyValue[] WireArguments(long notificationId, int typeId, long senderId, long created, JsonObject data) =>
    [
        new PyLong(notificationId),
        new PyInt(typeId),
        new PyLong(senderId),
        Eve.Services.Py.FileTimeLong(created),
        NotificationWireValue(data),
    ];

    private static PyValue NotificationWireValue(JsonNode? value)
    {
        if (value is JsonObject obj)
            return new PyDict([.. obj.Select(pair => new KeyValuePair<PyValue, PyValue>(
                long.TryParse(pair.Key, NumberStyles.Integer, CultureInfo.InvariantCulture, out var key)
                    ? new PyLong(key)
                    : new PyStr(pair.Key),
                NotificationWireValue(pair.Value)))]);
        if (value is JsonArray array)
            return new PyList([.. array.Select(NotificationWireValue)]);
        if (value is not JsonValue scalar)
            return PyNone.Instance;
        if (scalar.TryGetValue<long>(out var integer))
            return new PyLong(integer);
        if (scalar.TryGetValue<decimal>(out var amount))
            return new PyReal((double)amount);
        if (scalar.TryGetValue<double>(out var number) && double.IsFinite(number))
            return new PyReal(number);
        if (scalar.TryGetValue<bool>(out var flag))
            return flag ? PyBool.True : PyBool.False;
        if (scalar.TryGetValue<string>(out var text))
            return new PyStr(text);
        return PyNone.Instance;
    }
}
