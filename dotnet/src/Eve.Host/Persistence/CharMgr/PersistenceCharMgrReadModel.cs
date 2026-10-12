using System.Net;
using System.Net.Sockets;
using Eve.Characters;
using Eve.Data;
using Eve.Host;
using Eve.Identity;
using Eve.Dogma;
using Eve.Fitting;
using Eve.GameStore;
using System.Text.Json;
using System.Text.Json.Nodes;
using Eve.Inventory;
using Eve.Ships;
using Eve.Marshal;
using Eve.Npc;
using Eve.Sde;
using Eve.Skills;
using Eve.Universe;
using Eve.Wallet;
using Npgsql;
using Sentry;
using Eve.Services;
using Eve.Services.CharacterSelect;
using Eve.Services.Core;
using Eve.Services.Dogma;
using Eve.Services.Inventory;
using Eve.Services.CharacterInfo;
using Eve.Services.Skills;
using Eve.Services.Slash;
using Eve.Services.Space;
using Eve.Space;
using Eve.Services.Station;
using Eve.Services.Wallet;
using static Eve.Host.HostConfiguration;

namespace Eve.Host;

public sealed class PersistenceCharMgrReadModel(
    Func<string, CancellationToken, Task<JsonObject>> readTableAsync,
    Func<long, int, int, CancellationToken, ValueTask<IReadOnlyList<PyValue>>> readRecentShipKillmailPageAsync,
    Func<long, CancellationToken, ValueTask<string?>> readCharacterDescriptionAsync,
    Func<long, string, CancellationToken, ValueTask> writeCharacterDescriptionAsync,
    Func<long, CancellationToken, ValueTask<long?>> readMedal1GraphicIdAsync,
    Func<string, string, JsonNode?, WriteResult>? writePath = null,
    Func<string, string, WriteResult>? removePath = null)
{
    // ponytail: serialize personal-data read-modify-write, including the shared nextNoteID; split by owner plus a note-ID allocator if throughput matters.
    private readonly SemaphoreSlim _updateGate = new(1, 1);
    private const string DefaultFoldersLabel = "S:Folders";
    private const string DefaultFoldersNote = "1::F::0::Main|";

    public ValueTask<IReadOnlyList<PyValue>> ReadRecentShipKillmailPageAsync(
        long characterId, int num, int startIndex, CancellationToken cancellationToken) =>
        readRecentShipKillmailPageAsync(characterId, num, startIndex, cancellationToken);


    public static IReadOnlyList<PyValue> PackKillmailRows(
        IReadOnlyList<JsonObject> rows,
        Func<JsonNode?, PyValue> toPy)
    {
        if (rows.Count == 0)
        {
            return [];
        }

        var columns = rows
            .SelectMany(row => row.Select(property => property.Key))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var header = new PyObjectEx(
            ObjectExKind.Ex1,
            Py.Tuple(
                new PyToken("blue.DBRowDescriptor"),
                Py.Tuple(Py.Tuple([
                    .. columns.Select(name => (PyValue)Py.Tuple(
                        new PyStr(name),
                        new PyInt(Eve.Marshal.DbType.Empty)))
                ]))),
            [],
            []);
        var descriptor = Eve.Marshal.Descriptor.FromHeader(header);

        return rows.Select(row => (PyValue)new PyPackedRow(
            descriptor,
            [.. columns.Select(name => row.TryGetPropertyValue(name, out var value)
                ? toPy(value)
                : PyNone.Instance)])).ToArray();
    }

    public ValueTask<string?> ReadCharacterDescriptionAsync(
        long characterId, CancellationToken cancellationToken) =>
        readCharacterDescriptionAsync(characterId, cancellationToken);

    public ValueTask WriteCharacterDescriptionAsync(
        long characterId, string description, CancellationToken cancellationToken) =>
        writeCharacterDescriptionAsync(characterId, description, cancellationToken);

    public ValueTask<long?> ReadMedal1GraphicIdAsync(
        long characterId, CancellationToken cancellationToken) =>
        readMedal1GraphicIdAsync(characterId, cancellationToken);

    public async ValueTask<CharMgrPersonalData?> ReadAsync(long characterId, CancellationToken cancellationToken)
    {
        if (characterId <= 0)
        {
            return null;
        }

        var key = characterId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var characterTable = await ReadOptionalTableAsync("characters", cancellationToken);
        var notesTable = await ReadOptionalTableAsync("characterNotes", cancellationToken);
        var character = characterTable[key] as JsonObject;
        var notes = (notesTable["characters"] as JsonObject)?[key] as JsonObject;

        var nextNoteId = Number((notesTable["_meta"] as JsonObject)?["nextNoteID"]);
        return new CharMgrPersonalData(
            ReadContacts(character?["personalContacts"]),
            ReadBlockedSenders(character?["blockedOwners"]),
            ReadLabels(character?["personalContactLabels"]),
            ReadOwnerNotes(notes?["notes"]),
            ReadItemNotes(notes?["itemNotes"]))
        {
            NextOwnerNoteId = nextNoteId > 0 ? nextNoteId : null,
            CharacterSettings = ReadCharacterSettings(character?["characterSettings"]),
        };
    }

    public async ValueTask<CharMgrPersonalData?> UpdateAsync(
        long characterId,
        Func<CharMgrPersonalData, CharMgrPersonalData> update,
        CancellationToken cancellationToken)
    {
        if (characterId <= 0)
        {
            return null;
        }

        await _updateGate.WaitAsync(cancellationToken);
        try
        {
            var before = await ReadAsync(characterId, cancellationToken) ?? CharMgrPersonalData.Empty;
            var after = update(before);
            WriteChanges(characterId, before, after);
            return after;
        }
        finally
        {
            _updateGate.Release();
        }
    }

    public ValueTask WriteActivityStatusAsync(
        long characterId, long status, string? extraInfo, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var key = characterId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        WritePath("characters", $"/{key}/activityStatus", JsonValue.Create(status));
        WritePath("characters", $"/{key}/activityStatusExtraInfo",
            extraInfo is null ? null : JsonValue.Create(extraInfo));
        return ValueTask.CompletedTask;
    }

    public ValueTask WriteLogSettingsAsync(long characterId, PyValue settings, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var value = JsonFromPy(settings);
        if (value is not (JsonObject or JsonArray))
        {
            value = new JsonObject();
        }
        var key = characterId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        WritePath("characters", $"/{key}/lastSettingsTelemetry", value);
        return ValueTask.CompletedTask;
    }

    private void WriteChanges(long characterId, CharMgrPersonalData before, CharMgrPersonalData after)
{
    var owner = characterId.ToString(System.Globalization.CultureInfo.InvariantCulture);
    var fileTime = Py.FileTimeFromUnixMs(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
        .ToString(System.Globalization.CultureInfo.InvariantCulture);

    var oldContacts = before.Contacts.ToDictionary(contact => contact.ContactId);
    var newContacts = after.Contacts.ToDictionary(contact => contact.ContactId);
    foreach (var contactId in oldContacts.Keys.Union(newContacts.Keys))
    {
        if (!newContacts.TryGetValue(contactId, out var contact))
        {
            RemovePath("characters", $"/{owner}/personalContacts/{contactId}");
        }
        else if (!oldContacts.TryGetValue(contactId, out var old) || old != contact)
        {
            WritePath("characters", $"/{owner}/personalContacts/{contactId}", new JsonObject
            {
                ["contactID"] = contact.ContactId,
                ["inWatchlist"] = contact.InWatchlist,
                ["relationshipID"] = contact.RelationshipId,
                ["labelMask"] = contact.LabelMask,
            });
        }
    }

    var oldBlocked = before.BlockedSenders.ToHashSet();
    var newBlocked = after.BlockedSenders.ToHashSet();
    foreach (var ownerId in oldBlocked.Union(newBlocked))
    {
        if (!newBlocked.Contains(ownerId))
        {
            RemovePath("characters", $"/{owner}/blockedOwners/{ownerId}");
        }
        else if (!oldBlocked.Contains(ownerId))
        {
            WritePath("characters", $"/{owner}/blockedOwners/{ownerId}", new JsonObject
            {
                ["senderID"] = ownerId,
            });
        }
    }

    var oldOwnerNotes = before.OwnerNotes.ToDictionary(note => note.NoteId);
    var newOwnerNotes = after.OwnerNotes.ToDictionary(note => note.NoteId);
    foreach (var noteId in oldOwnerNotes.Keys.Union(newOwnerNotes.Keys))
    {
        var path = $"/characters/{owner}/notes/{noteId}";
        if (!newOwnerNotes.TryGetValue(noteId, out var note))
        {
            RemovePath("characterNotes", path);
        }
        else if (!oldOwnerNotes.TryGetValue(noteId, out var old))
        {
            WritePath("characterNotes", path, new JsonObject
            {
                ["noteID"] = note.NoteId,
                ["label"] = note.Label,
                ["note"] = note.Note,
                ["created"] = fileTime,
                ["updated"] = fileTime,
            });
        }
        else if (old != note)
        {
            if (old.Label != note.Label)
            {
                WritePath("characterNotes", $"{path}/label", JsonValue.Create(note.Label));
            }
            if (old.Note != note.Note)
            {
                WritePath("characterNotes", $"{path}/note", JsonValue.Create(note.Note));
            }
            WritePath("characterNotes", $"{path}/updated", JsonValue.Create(fileTime));
        }
    }

    var oldItemNotes = before.ItemNotes;
    var newItemNotes = after.ItemNotes;
    foreach (var itemId in oldItemNotes.Keys.Union(newItemNotes.Keys))
    {
        var path = $"/characters/{owner}/itemNotes/{itemId}";
        if (!newItemNotes.TryGetValue(itemId, out var note))
        {
            RemovePath("characterNotes", path);
        }
        else if (!oldItemNotes.TryGetValue(itemId, out var old))
        {
            WritePath("characterNotes", path, new JsonObject
            {
                ["itemID"] = itemId,
                ["note"] = note,
                ["updated"] = fileTime,
            });
        }
        else if (old != note)
        {
            WritePath("characterNotes", $"{path}/note", JsonValue.Create(note));
            WritePath("characterNotes", $"{path}/updated", JsonValue.Create(fileTime));
        }
    }

    if (!CharacterSettingsEqual(before.CharacterSettings, after.CharacterSettings))
    {
        var path = $"/{owner}/characterSettings";
        if (after.CharacterSettings.Count == 0)
        {
            RemovePath("characters", path);
        }
        else
        {
            var settings = new PyDict([.. after.CharacterSettings.Select(entry =>
                new KeyValuePair<PyValue, PyValue>(new PyStr(entry.Key), entry.Value))]);
            WritePath("characters", path, JsonFromPy(settings));
        }
    }

    if (before.NextOwnerNoteId != after.NextOwnerNoteId)
    {
        if (after.NextOwnerNoteId is > 0)
        {
            WritePath("characterNotes", "/_meta/nextNoteID", JsonValue.Create(after.NextOwnerNoteId.Value));
        }
        else
        {
            RemovePath("characterNotes", "/_meta/nextNoteID");
        }
    }
}

    private void WritePath(string table, string path, JsonNode? data)
    {
        var result = writePath?.Invoke(table, path, data)
            ?? throw new InvalidOperationException("CharMgr GameStore writer is not configured.");
        if (!result.Success)
        {
            throw new InvalidOperationException($"Could not write CharMgr data at {table}{path}: {result.ErrorMsg}");
        }
    }

    private void RemovePath(string table, string path)
    {
        var result = removePath?.Invoke(table, path)
            ?? throw new InvalidOperationException("CharMgr GameStore remover is not configured.");
        if (!result.Success && result.ErrorMsg != "ENTRY_NOT_FOUND")
        {
            throw new InvalidOperationException($"Could not remove CharMgr data at {table}{path}: {result.ErrorMsg}");
        }
    }

    private static JsonNode? JsonFromPy(PyValue value) => value switch
    {
        PyNone => null,
        PyBool boolean => JsonValue.Create(boolean.Value),
        PyInt number => JsonValue.Create(number.Value),
        PyLong number => JsonValue.Create(number.Value),
        PyReal number when double.IsFinite(number.Value) => JsonValue.Create(number.Value),
        PyReal => null,
        var text when Eve.Macho.JsSemantics.StringValue(text) is not null => JsonValue.Create(Eve.Macho.JsSemantics.StringValue(text)!),
        PyToken text => JsonValue.Create(text.Value),
        PyBytes bytes => JsonValue.Create(System.Text.Encoding.UTF8.GetString(bytes.Value.Span)),
        PyBuffer buffer => JsonValue.Create(System.Text.Encoding.UTF8.GetString(buffer.Value.Span)),
        PyList list => new JsonArray(list.Items.Select(JsonFromPy).ToArray()),
        PyTuple tuple => new JsonArray(tuple.Items.Select(JsonFromPy).ToArray()),
        PyDict dict => JsonFromPy(dict.Entries),
        PyObject obj => JsonFromPy(obj.Args),
        PyObjectEx objEx when objEx.Dict.Count > 0 => JsonFromPy(objEx.Dict),
        PyObjectEx objEx when objEx.List.Count > 0 => new JsonArray(objEx.List.Select(JsonFromPy).ToArray()),
        PyObjectEx => new JsonObject(),
        _ => null,
    };

    private static JsonObject JsonFromPy(IReadOnlyList<KeyValuePair<PyValue, PyValue>> entries)
    {
        var result = new JsonObject();
        foreach (var (key, value) in entries)
        {
            var name = key switch
            {
                var text when Eve.Macho.JsSemantics.StringValue(text) is not null => Eve.Macho.JsSemantics.StringValue(text)!,
                PyToken text => text.Value,
                PyInt number => number.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
                PyLong number => number.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
                PyBool boolean => boolean.Value ? "true" : "false",
                PyNone => "null",
                _ => Eve.Macho.JsSemantics.StringValue(key),
            };
            if (name is not null)
            {
                result[name] = JsonFromPy(value);
            }
        }
        return result;
    }

    private Task<JsonObject> ReadOptionalTableAsync(string table, CancellationToken cancellationToken) =>
        PersistenceReadHelpers.ReadOptionalTableAsync(readTableAsync, table, cancellationToken);

    private static IReadOnlyList<CharMgrContact> ReadContacts(JsonNode? source) =>
        Rows(source)
            .Select(entry => new CharMgrContact(
                Number(entry.Row["contactID"], NumberFromKey(entry.Key)),
                Boolean(entry.Row["inWatchlist"]),
                Number(entry.Row["relationshipID"]),
                Number(entry.Row["labelMask"])))
            .Where(contact => contact.ContactId > 0)
            .OrderBy(contact => contact.ContactId)
            .ToArray();

    private static IReadOnlyList<long> ReadBlockedSenders(JsonNode? source) =>
        Rows(source)
            .Select(entry => Number(
                entry.Row["senderID"],
                Number(entry.Row["contactID"], NumberFromKey(entry.Key))))
            .Where(senderId => senderId > 0)
            .Distinct()
            .Order()
            .ToArray();

    private static IReadOnlyDictionary<long, CharMgrContactLabel> ReadLabels(JsonNode? source)
    {
        var labels = new SortedDictionary<long, CharMgrContactLabel>();
        foreach (var entry in Rows(source))
        {
            var labelId = NumberFromKey(entry.Key);
            if (labelId <= 0)
            {
                continue;
            }

            labels[labelId] = new CharMgrContactLabel(
                Text(entry.Row["name"]),
                Number(entry.Row["color"]));
        }

        return labels;
    }

    private static IReadOnlyList<CharMgrOwnerNote> ReadOwnerNotes(JsonNode? source) =>
        Rows(source)
            .Select(entry => new CharMgrOwnerNote(
                Number(entry.Row["noteID"], NumberFromKey(entry.Key)),
                Text(entry.Row["label"]),
                Text(entry.Row["note"])))
            .Where(note => note.NoteId > 0)
            .OrderBy(note => note.NoteId)
            .ToArray();

    private static bool CharacterSettingsEqual(
        IReadOnlyDictionary<string, PyValue> left,
        IReadOnlyDictionary<string, PyValue> right) =>
        left.Count == right.Count && left.All(entry =>
            right.TryGetValue(entry.Key, out var value) && entry.Value.Equals(value));

    private static IReadOnlyDictionary<string, PyValue> ReadCharacterSettings(JsonNode? source) =>
        source is JsonObject settings
            ? settings.ToDictionary(entry => entry.Key, entry => CharMgrJsonToPy(entry.Value), StringComparer.Ordinal)
            : new Dictionary<string, PyValue>(StringComparer.Ordinal);

    private static PyValue CharMgrJsonToPy(JsonNode? node)
    {
        if (node is JsonObject obj)
            return new PyDict(obj.Select(pair => new KeyValuePair<PyValue, PyValue>(
                new PyStr(pair.Key), CharMgrJsonToPy(pair.Value))).ToArray());
        if (node is JsonArray array)
            return new PyList(array.Select(CharMgrJsonToPy).ToArray());
        if (node is not JsonValue value)
            return PyNone.Instance;
        if (value.TryGetValue<bool>(out var boolean))
            return boolean ? PyBool.True : PyBool.False;
        if (value.TryGetValue<int>(out var integer))
            return new PyInt(integer);
        if (value.TryGetValue<long>(out var longValue))
            return new PyLong(longValue);
        if (value.TryGetValue<double>(out var real))
            return Py.Number(real);
        if (value.TryGetValue<decimal>(out var decimalValue))
            return Py.Number((double)decimalValue);
        if (value.TryGetValue<string>(out var text))
            return new PyStr(text);
        return new PyStr(value.ToString());
    }

    private static IReadOnlyDictionary<long, string> ReadItemNotes(JsonNode? source)
    {
        var notes = new SortedDictionary<long, string>();
        if (source is not JsonObject byId)
        {
            return notes;
        }

        foreach (var (key, value) in byId)
        {
            var itemId = Number(value is JsonObject entry ? entry["itemID"] : null, NumberFromKey(key));
            if (itemId <= 0)
            {
                continue;
            }

            notes[itemId] = value is JsonObject row ? Text(row["note"]) : Text(value);
        }

        return notes;
    }

    private static IEnumerable<(string Key, JsonObject Row)> Rows(JsonNode? source)
    {
        if (source is JsonObject byKey)
        {
            foreach (var (key, value) in byKey)
            {
                if (value is JsonObject row)
                {
                    yield return (key, row);
                }
            }
        }
        else if (source is JsonArray rows)
        {
            for (var index = 0; index < rows.Count; index++)
            {
                if (rows[index] is JsonObject row)
                {
                    yield return (index.ToString(System.Globalization.CultureInfo.InvariantCulture), row);
                }
            }
        }
    }

    private static long NumberFromKey(string key) =>
        long.TryParse(key, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var value)
            ? value
            : 0;

    private static long Number(JsonNode? node, long fallback = 0)
    {
        if (node is JsonObject wrapper && wrapper["value"] is { } wrapped)
        {
            return Number(wrapped, fallback);
        }

        if (node is JsonValue value)
        {
            if (value.TryGetValue<long>(out var number))
            {
                return number;
            }
            if (value.TryGetValue<string>(out var text) &&
                long.TryParse(text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out number))
            {
                return number;
            }
        }

        return fallback;
    }

    private static bool Boolean(JsonNode? node)
    {
        if (node is JsonObject wrapper && wrapper["value"] is { } wrapped)
        {
            return Boolean(wrapped);
        }

        if (node is JsonValue value)
        {
            if (value.TryGetValue<bool>(out var boolean))
            {
                return boolean;
            }
            if (value.TryGetValue<long>(out var number))
            {
                return number != 0;
            }
            if (value.TryGetValue<string>(out var text))
            {
                return text.Trim().ToLowerInvariant() is "1" or "true";
            }
        }

        return false;
    }

    private static string Text(JsonNode? node)
    {
        if (node is JsonObject wrapper && wrapper["value"] is { } wrapped)
        {
            return Text(wrapped);
        }

        if (node is JsonValue value)
        {
            if (value.TryGetValue<string>(out var text))
            {
                return text;
            }
            return value.ToString();
        }

        return "";
    }
}
