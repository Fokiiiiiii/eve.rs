using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Eve.Dogma;
using Eve.Inventory;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;

namespace Eve.Host;

public sealed class DynamicItemRpcService : IService
{
    private const string DynamicItemMetadataKey = "evejsDynamicItem";
    private readonly PgItemStore? _items;
    private readonly Func<long, long, long, CancellationToken, ValueTask<long?>>? _create;
    private readonly DogmaStatic? _dogma;
    private readonly Func<int, bool>? _isKnownDynamicResultType;

    public DynamicItemRpcService(PgItemStore? items = null,
        Func<long, long, long, CancellationToken, ValueTask<long?>>? create = null,
        DogmaStatic? dogma = null,
        Func<int, bool>? isKnownDynamicResultType = null)
        => (_items, _create, _dogma, _isKnownDynamicResultType) = (items, create, dogma, isKnownDynamicResultType);

    public string Name => "dynamicItemService";
    public bool HasMethod(string method) => method is "CreateDynamicItem" or "GetDynamicItemInfo";

    public async ValueTask<PyValue> CallAsync(ServiceCall call)
    {
        if (!HasMethod(call.Method)) return PyNone.Instance;
        call.Cancellation.ThrowIfCancellationRequested();
        if (call.Method == "GetDynamicItemInfo") return await GetDynamicItemInfoAsync(call);
        var characterId = call.Session.CharacterId ?? 0;
        var mutatorId = Long(call.Arg(0));
        var sourceId = Long(call.Arg(1));
        if (characterId <= 0) Notify("You need an active character to mutate an item.");
        if (mutatorId <= 0 || sourceId <= 0 || mutatorId == sourceId)
            Notify("The selected mutaplasmid or source item could not be found.");
        if (_items is not null)
        {
            var mutator = await _items.GetAsync(mutatorId, call.Cancellation);
            var source = await _items.GetAsync(sourceId, call.Cancellation);
            if (mutator is null || source is null) Notify("The selected mutaplasmid or source item could not be found.");
            if (mutator!.OwnerId != characterId || source!.OwnerId != characterId)
                Notify("You can only mutate items owned by the active character.");
        }
        var resultId = _create is null ? null : await _create(mutatorId, sourceId, characterId, call.Cancellation);
        return resultId is > 0 ? new PyInt(resultId.Value) : PyNone.Instance;
    }

    private async ValueTask<PyValue> GetDynamicItemInfoAsync(ServiceCall call)
    {
        var itemId = Long(call.Arg(0));
        if (itemId <= 0 || _items is null || await _items.GetAsync(itemId, call.Cancellation) is not { } item)
            return PyNone.Instance;

        JsonObject? customInfo = null;
        if (!string.IsNullOrWhiteSpace(item.CustomInfo))
        {
            try { customInfo = JsonNode.Parse(item.CustomInfo) as JsonObject; }
            catch (JsonException) { }
        }
        var metadata = customInfo?[DynamicItemMetadataKey] as JsonObject;
        if (metadata is null)
        {
            if (_isKnownDynamicResultType?.Invoke(item.TypeId) != true) return PyNone.Instance;
            metadata = new JsonObject
            {
                ["itemID"] = item.Id,
                ["resultTypeID"] = item.TypeId,
                ["sourceTypeID"] = item.TypeId,
                ["sourceItemID"] = 0,
                ["mutatorTypeID"] = item.TypeId,
                ["mutatorItemID"] = 0,
                ["characterID"] = item.OwnerId,
                ["createdAtMs"] = 0,
                ["modifiers"] = new JsonObject(),
                ["attributes"] = new JsonObject(),
                ["baseAttributes"] = new JsonObject(),
            };
        }

        var sourceTypeId = Positive(metadata["sourceTypeID"]);
        var mutatorTypeId = Positive(metadata["mutatorTypeID"]);
        if (sourceTypeId <= 0 || sourceTypeId > int.MaxValue || mutatorTypeId <= 0)
            return PyNone.Instance;

        var attributes = new Dictionary<int, double>();
        if (_dogma is not null)
        {
            await _dogma.EnsureTypesAsync([(int)sourceTypeId], call.Cancellation);
            if (_dogma.Type((int)sourceTypeId) is { } sourceType)
                foreach (var (attributeId, value) in sourceType.Attributes)
                    attributes[attributeId] = value;
        }
        MergeNumericObject(metadata["attributes"], attributes);

        var resultTypeId = Positive(metadata["resultTypeID"]);
        var storedItemId = Positive(metadata["itemID"]);
        return Py.KeyVal(
            ("itemID", new PyInt(storedItemId > 0 ? storedItemId : item.Id)),
            ("typeID", new PyInt(resultTypeId > 0 ? resultTypeId : item.TypeId)),
            ("sourceTypeID", new PyInt(sourceTypeId)),
            ("sourceItemID", new PyInt(Positive(metadata["sourceItemID"]))),
            ("mutatorTypeID", new PyInt(mutatorTypeId)),
            ("mutatorItemID", new PyInt(Positive(metadata["mutatorItemID"]))),
            ("characterID", new PyInt(Positive(metadata["characterID"]))),
            ("createdAtMs", new PyInt(Positive(metadata["createdAtMs"]))),
            ("attributes", NumericDict(attributes)),
            ("modifiers", NumericDict(metadata["modifiers"])),
            ("baseAttributes", NumericDict(metadata["baseAttributes"])));
    }

    private static long Positive(JsonNode? value) =>
        TryNumber(value, out var number) && number > 0 ? (long)Math.Truncate(number) : 0;

    private static PyDict NumericDict(JsonNode? value)
    {
        var numbers = new Dictionary<int, double>();
        MergeNumericObject(value, numbers);
        return NumericDict(numbers);
    }

    private static PyDict NumericDict(IReadOnlyDictionary<int, double> values) => new(values
        .OrderBy(pair => pair.Key)
        .Select(pair => new KeyValuePair<PyValue, PyValue>(new PyInt(pair.Key), Py.Number(pair.Value)))
        .ToArray());

    private static void MergeNumericObject(JsonNode? value, IDictionary<int, double> destination)
    {
        if (value is not JsonObject values) return;
        foreach (var (key, item) in values)
            if (int.TryParse(key, NumberStyles.Integer, CultureInfo.InvariantCulture, out var attributeId)
                && attributeId > 0 && TryNumber(item, out var number))
                destination[attributeId] = number;
    }

    private static bool TryNumber(JsonNode? value, out double number)
    {
        if (value is JsonValue jsonValue)
        {
            if (jsonValue.TryGetValue<int>(out var smallInteger)) { number = smallInteger; return true; }
            if (jsonValue.TryGetValue<long>(out var integer)) { number = integer; return true; }
            if (jsonValue.TryGetValue<double>(out number)) return double.IsFinite(number);
            if (jsonValue.TryGetValue<string>(out var text)
                && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out number)) return double.IsFinite(number);
        }
        number = 0;
        return false;
    }

    private static long Long(PyValue value) => value switch { PyInt i => i.Value, PyLong l => l.Value, PyReal r => (long)r.Value, _ => 0 };
    [DoesNotReturn]
    private static void Notify(string message) => throw MachoErrors.ThrowUserError("CustomInfo", ("info", new PyStr(message)));
}
