using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Eve.GameStore;
using Eve.Inventory;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Npgsql;

namespace Eve.Host;

public sealed class ShipCosmeticsMgrRpcService : IService
{
    private static readonly string[] Methods = ["ActivateSkinLicense", "ApplySkinToShip", "GMExpireSkinLicense", "GetEnabledCosmetics", "GetFirstPartySkinData", "GetLicencedSkins", "GetLicencedSkinsForShipType", "GiveSkin", "RemoveSkin"];
    private readonly GameStoreCache? _store;
    private readonly GameStoreCache? _logoStore;
    private readonly PgItemStore? _items;
    private readonly NpgsqlDataSource? _source;
    private readonly ShipCosmeticsCatalog? _catalog;
    private readonly Func<long> _now;
    private readonly Action<long, long, long?>? _shipSkinChanged;
    private readonly Action<long, long>? _skinLicenseActivated;
    private readonly Func<long, Item, long, CancellationToken, ValueTask>? _inventoryItemConsumed;

    public ShipCosmeticsMgrRpcService(GameStoreCache? store = null, GameStoreCache? logoStore = null, PgItemStore? items = null,
        NpgsqlDataSource? source = null, ShipCosmeticsCatalog? catalog = null, Func<long>? nowUnixMs = null, Action<long, long, long?>? shipSkinChanged = null, Action<long, long>? skinLicenseActivated = null, Func<long, Item, long, CancellationToken, ValueTask>? inventoryItemConsumed = null)
    {
        _store = store;
        _logoStore = logoStore ?? store;
        _items = items;
        _source = source;
        _catalog = catalog;
        _now = nowUnixMs ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        _shipSkinChanged = shipSkinChanged;
        _skinLicenseActivated = skinLicenseActivated;
        _inventoryItemConsumed = inventoryItemConsumed;
    }

    public string Name => "shipCosmeticsMgr";
    public bool HasMethod(string method) => Methods.Contains(method, StringComparer.Ordinal);

    public async ValueTask<PyValue> CallAsync(ServiceCall call)
    {
        if (!HasMethod(call.Method)) return PyNone.Instance;
        call.Cancellation.ThrowIfCancellationRequested();
        return call.Method switch
        {
            "GetEnabledCosmetics" => GetEnabledCosmetics(call),
            "GetFirstPartySkinData" => GetFirstPartySkinData(call),
            "GetLicencedSkins" => GetLicencedSkins(call),
            "GetLicencedSkinsForShipType" => GetLicencedSkinsForShipType(call),
            "GiveSkin" => GiveSkin(call),
            "RemoveSkin" => RemoveSkin(call),
            "GMExpireSkinLicense" => GMExpireSkinLicense(call),
            "ApplySkinToShip" => await ApplySkinToShipAsync(call),
            "ActivateSkinLicense" => await ActivateSkinLicenseAsync(call),
            _ => PyNone.Instance,
        };
    }

    private PyValue GetEnabledCosmetics(ServiceCall call)
    {
        var shipId = Positive(call.Arg(0));
        var slots = (_logoStore?.Read("shipLogoFittings", $"/ships/{shipId}").Data as JsonObject)?["enabledByBackendSlot"] as JsonObject;
        if (slots is null) return new PyDict([]);
        var entries = new List<KeyValuePair<PyValue, PyValue>>();
        foreach (var (key, value) in slots.OrderBy(pair => Integer(pair.Key)))
        {
            if (!int.TryParse(key, NumberStyles.Integer, CultureInfo.InvariantCulture, out var slot)) continue;
            var typeId = value is JsonObject row ? Integer(row["cosmeticType"] ?? row["typeID"]) : Integer(value);
            if (typeId > 0) entries.Add(new(new PyInt(slot), new PyInt(typeId)));
        }
        return new PyDict(entries);
    }

    private PyValue GetLicencedSkins(ServiceCall call) =>
        LicensedSkinRows(call.Session.CharacterId ?? 0, null);

    private PyValue GetLicencedSkinsForShipType(ServiceCall call)
    {
        var characterId = call.Args.Count >= 2 ? Positive(call.Arg(0)) : call.Session.CharacterId ?? 0;
        var shipTypeId = Positive(call.Args.Count >= 2 ? call.Arg(1) : call.Arg(0));
        return shipTypeId is > 0 and <= int.MaxValue
            ? LicensedSkinRows(characterId, (int)shipTypeId)
            : new PyList([]);
    }

    private PyValue LicensedSkinRows(long characterId, int? shipTypeId)
    {
        if (characterId <= 0 || _store is null || _catalog is null)
        {
            return new PyList([]);
        }

        var rows = _catalog.Skins.Values
            .Where(skin => shipTypeId is null || skin.ShipTypeIds.Contains(shipTypeId.Value))
            .OrderBy(skin => skin.SkinId)
            .Select(skin => (skin.SkinId, Record: EffectiveLicense(characterId, skin.SkinId)))
            .Where(entry => entry.Record is not null)
            .Select(entry => LicenseKeyVal(entry.SkinId, entry.Record!));
        return new PyList(rows.ToArray());
    }

    private PyValue GetFirstPartySkinData(ServiceCall call)
    {
        var record = EffectiveLicense(Positive(call.Arg(0)), Positive(call.Arg(1)));
        return record is null ? PyNone.Instance : LicenseKeyVal(Positive(call.Arg(1)), record);
    }

    private PyValue GiveSkin(ServiceCall call)
    {
        if (!GameMasterRoles.Has(call.Session, GameMasterRoles.Staff))
            throw MachoErrors.ThrowUserError(
                "CustomNotify", ("notify", new PyStr("ROLE_STAFF_REQUIRED")));
        var characterId = call.Session.CharacterId ?? 0;
        var skinId = Positive(call.Arg(0));
        if (characterId <= 0 || skinId <= 0 || _store is null) return PyNone.Instance;
        var skin = _catalog?.Skins.GetValueOrDefault(skinId);
        if (_catalog is { Skins.Count: > 0 } && skin is null) Notify("That SKIN could not be found.");
        var license = skin is null ? null : _catalog?.LicenseTypes.Values.Where(x => x.SkinId == skinId).OrderBy(x => x.LicenseTypeId).FirstOrDefault();
        var requestedDuration = Math.Max(0, Number(call.Kwarg("duration")));
        var duration = requestedDuration > 0 ? requestedDuration : license?.DurationDays ?? 0;
        var singleUse = call.Kwarg("isSingleUse") is { } value ? Boolean(value) : license?.IsSingleUse ?? false;
        WriteOverride(characterId, skinId, new JsonObject
        {
            ["skinID"] = skinId,
            ["licenseTypeID"] = license?.LicenseTypeId > 0 ? license.LicenseTypeId : null,
            ["skinMaterialID"] = license?.SkinMaterialId > 0 ? license.SkinMaterialId : skin?.SkinMaterialId > 0 ? skin.SkinMaterialId : null,
            ["expiresAtFileTime"] = duration > 0 ? FileTimeAfterDays(duration) : null,
            ["isSingleUse"] = singleUse,
            ["revoked"] = false,
            ["updatedAt"] = NowIso(),
            ["source"] = "shipCosmeticsMgr.GiveSkin",
        });
        return PyNone.Instance;
    }

    private PyValue RemoveSkin(ServiceCall call)
    {
        var skinId = Positive(call.Arg(0));
        var licenseeId = Positive(call.Arg(1));
        if (call.Session.CharacterId != licenseeId) Notify("You can only remove a SKIN license you own.");
        Revoke(licenseeId, skinId, "RemoveSkin");
        return PyNone.Instance;
    }

    private PyValue GMExpireSkinLicense(ServiceCall call)
    {
        if (!GameMasterRoles.Has(call.Session, GameMasterRoles.Gml))
            throw MachoErrors.ThrowUserError(
                "CustomNotify", ("notify", new PyStr("ROLE_GML is required")));
        var characterId = call.Session.CharacterId ?? 0;
        var skinId = Positive(call.Arg(0));
        var path = OverridePath(characterId, skinId);
        if (_store?.Read("shipCosmetics", path).Data is JsonObject current)
        {
            current["expiresAtFileTime"] = (Py.FileTimeFromUnixMs(_now()) - 1).ToString(CultureInfo.InvariantCulture);
            current["updatedAt"] = NowIso();
            _store.Write("shipCosmetics", path, current);
        }
        return PyNone.Instance;
    }

    private async ValueTask<PyValue> ApplySkinToShipAsync(ServiceCall call)
    {
        if (_store is null || _items is null) return PyNone.Instance;
        var shipId = Positive(call.Arg(0));
        var skinId = call.Arg(1) is PyNone ? 0 : Positive(call.Arg(1));
        var characterId = call.Session.CharacterId ?? 0;
        var item = await _items.GetAsync(shipId, call.Cancellation);
        var activeShipId = Positive(call.Session.Get("shipid") ?? PyNone.Instance);
        if (item is null || (item.OwnerId != characterId && shipId != activeShipId))
        {
            Notify("You can only apply a SKIN to a ship you own or are actively piloting.");
            return PyNone.Instance;
        }
        if (skinId > 0)
        {
            var skin = _catalog?.Skins.GetValueOrDefault(skinId);
            if (_catalog is { Skins.Count: > 0 } && skin is null) Notify("That SKIN could not be found.");
            if (EffectiveLicense(characterId > 0 ? characterId : item!.OwnerId, skinId) is null) Notify("That SKIN is not licensed on this character.");
            if (skin is { ShipTypeIds.Count: > 0 } && !skin.ShipTypeIds.Contains(item!.TypeId)) Notify("That SKIN is not valid for this ship type.");
        }
        var write = _store.Write("shipCosmetics", $"/ships/{shipId}", new JsonObject
        {
            ["shipID"] = shipId, ["ownerID"] = item!.OwnerId, ["characterID"] = characterId > 0 ? characterId : item.OwnerId,
            ["typeID"] = item.TypeId, ["skinID"] = skinId > 0 ? skinId : null, ["updatedAt"] = NowIso(),
        });
        if (!write.Success) throw new InvalidOperationException($"Could not save ship skin: {write.ErrorMsg}");
        _shipSkinChanged?.Invoke(characterId > 0 ? characterId : item.OwnerId, shipId, skinId > 0 ? skinId : null);
        return PyNone.Instance;
    }

    private async ValueTask<PyValue> ActivateSkinLicenseAsync(ServiceCall call)
    {
        var characterId = call.Session.CharacterId ?? 0;
        var itemIds = ItemIds(call.Arg(0));
        if (characterId <= 0) Notify("You need an active character to activate a SKIN license.");
        if (itemIds.Count == 0) return PyNone.Instance;
        if (_store is null || _items is null || _source is null || _catalog is null) return PyNone.Instance;
        var plan = new List<(Item Item, SkinLicenseDefinition License, JsonObject? Previous)>();
        var skinsPlanned = new HashSet<int>();
        foreach (var itemId in itemIds)
        {
            var item = await _items.GetAsync(itemId, call.Cancellation);
            if (item is null || item.OwnerId != characterId) Notify("That SKIN license is not in your inventory.");
            if (!_catalog.LicenseTypes.TryGetValue(item!.TypeId, out var license) || !_catalog.Skins.ContainsKey(license.SkinId)) Notify("That item is not a ship SKIN license.");
            if (EffectiveLicense(characterId, license.SkinId) is not null || !skinsPlanned.Add(license.SkinId)) Notify("That SKIN license is already active on this character.");
            plan.Add((item, license, _store.Read("shipCosmetics", OverridePath(characterId, license.SkinId)).Data as JsonObject));
        }
        foreach (var entry in plan)
        {
            var skin = _catalog.Skins[entry.License.SkinId];
            var write = _store.Write("shipCosmetics", OverridePath(characterId, entry.License.SkinId), new JsonObject
            {
                ["skinID"] = entry.License.SkinId, ["licenseTypeID"] = entry.License.LicenseTypeId,
                ["skinMaterialID"] = entry.License.SkinMaterialId > 0 ? entry.License.SkinMaterialId : skin.SkinMaterialId,
                ["expiresAtFileTime"] = entry.License.DurationDays > 0 ? FileTimeAfterDays(entry.License.DurationDays) : null,
                ["isSingleUse"] = entry.License.IsSingleUse, ["revoked"] = false, ["updatedAt"] = NowIso(),
                ["source"] = "shipCosmeticsMgr.ActivateSkinLicense",
            });
            if (!write.Success) { RestoreOverrides(characterId, plan); Notify("That SKIN license could not be activated."); }
        }
        try
        {
            await using var connection = await _source.OpenConnectionAsync(call.Cancellation);
            await using var transaction = await connection.BeginTransactionAsync(call.Cancellation);
            foreach (var entry in plan)
            {
                var locked = await _items.LockAndGetAsync(connection, transaction, entry.Item.Id, call.Cancellation);
                if (locked is null || locked.OwnerId != characterId || locked.Quantity <= 0) Notify("That SKIN license could not be consumed.");
                if (locked!.Quantity == 1) await _items.DeleteAsync(connection, transaction, locked.Id, call.Cancellation);
                else await _items.SetQuantityAsync(connection, transaction, locked.Id, locked.Quantity - 1, call.Cancellation);
            }
            await transaction.CommitAsync(call.Cancellation);
        }
        catch { RestoreOverrides(characterId, plan); throw; }
        foreach (var entry in plan)
        {
            if (_inventoryItemConsumed is not null)
                await _inventoryItemConsumed(characterId, entry.Item, Math.Max(0, entry.Item.Quantity - 1), call.Cancellation);
            _skinLicenseActivated?.Invoke(characterId, entry.License.SkinId);
        }
        return PyNone.Instance;
    }

    private void RestoreOverrides(long characterId, IEnumerable<(Item Item, SkinLicenseDefinition License, JsonObject? Previous)> plan)
    {
        foreach (var entry in plan)
        {
            var path = OverridePath(characterId, entry.License.SkinId);
            if (entry.Previous is null) _store?.Remove("shipCosmetics", path);
            else _store?.Write("shipCosmetics", path, entry.Previous);
        }
    }

    private JsonObject? EffectiveLicense(long characterId, long skinId)
    {
        if (characterId <= 0 || skinId <= 0 || _store?.Read("shipCosmetics", OverridePath(characterId, skinId)).Data is not JsonObject record) return null;
        if (Boolean(record["revoked"]) || (_catalog is { Skins.Count: > 0 } && !_catalog.Skins.ContainsKey((int)skinId))) return null;
        var expires = Long(record["expiresAtFileTime"]);
        return expires > 0 && expires <= Py.FileTimeFromUnixMs(_now()) ? null : record;
    }

    private PyValue LicenseKeyVal(long skinId, JsonObject record)
    {
        var materialId = Integer(record["skinMaterialID"]);
        var licenseTypeId = Integer(record["licenseTypeID"]);
        if (licenseTypeId <= 0 && _catalog is not null)
            licenseTypeId = _catalog.LicenseTypes.Values.Where(x => x.SkinId == skinId).Select(x => x.LicenseTypeId).DefaultIfEmpty().Min();
        return Py.KeyVal(("skinID", new PyInt(skinId)),
            ("expires", record["expiresAtFileTime"] is { } expiry ? new PyLong(Long(expiry)) : PyNone.Instance),
            ("isSingleUse", PyValue.Of(Boolean(record["isSingleUse"]))),
            ("licenseTypeID", licenseTypeId > 0 ? new PyInt(licenseTypeId) : PyNone.Instance),
            ("skinMaterialID", materialId > 0 ? new PyInt(materialId) : PyNone.Instance),
            ("materialID", materialId > 0 ? new PyInt(materialId) : PyNone.Instance));
    }

    private void Revoke(long characterId, long skinId, string source)
    {
        var path = OverridePath(characterId, skinId);
        var record = _store?.Read("shipCosmetics", path).Data as JsonObject;
        if (record is null) { Notify("That SKIN license could not be found."); return; }
        record["revoked"] = true; record["source"] = source; record["updatedAt"] = NowIso();
        if (!_store!.Write("shipCosmetics", path, record).Success) Notify("That SKIN license could not be changed.");
    }

    private void WriteOverride(long characterId, long skinId, JsonObject value)
    {
        var result = _store!.Write("shipCosmetics", OverridePath(characterId, skinId), value);
        if (!result.Success) throw new InvalidOperationException($"Could not save SKIN license: {result.ErrorMsg}");
    }

    private string FileTimeAfterDays(double days) => Py.FileTimeFromUnixMs(checked(_now() + (long)(days * 86_400_000d))).ToString(CultureInfo.InvariantCulture);
    private string NowIso() => DateTimeOffset.FromUnixTimeMilliseconds(_now()).ToString("O", CultureInfo.InvariantCulture);
    private static string OverridePath(long characterId, long skinId) => $"/characters/{characterId}/skinOverridesBySkinID/{skinId}";
    private static List<long> ItemIds(PyValue value) => value switch { PyList list => list.Items.Select(Long).Where(x => x > 0).Distinct().ToList(), PyTuple tuple => tuple.Items.Select(Long).Where(x => x > 0).Distinct().ToList(), _ when Long(value) > 0 => [Long(value)], _ => [] };
    private static int Positive(PyValue? value) => checked((int)Math.Clamp(Long(value), 0, int.MaxValue));
    private static long Long(PyValue? value) => value switch { PyInt i => i.Value, PyLong l => l.Value, PyReal r when double.IsFinite(r.Value) => (long)r.Value, _ => 0 };
    private static long Long(JsonNode? value) => long.TryParse(value?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) ? result : 0;
    private static int Integer(JsonNode? value) => (int)Math.Clamp(Long(value), int.MinValue, int.MaxValue);
    private static double Number(PyValue? value) => value switch { PyInt i => i.Value, PyLong l => l.Value, PyReal r => r.Value, _ => 0 };
    private static bool Boolean(PyValue? value) => value switch { PyBool b => b == PyBool.True, PyInt i => i.Value != 0, PyLong l => l.Value != 0, PyStr s => s.Value.Length > 0, _ => false };
    private static bool Boolean(JsonNode? value) => value?.GetValueKind() switch { JsonValueKind.True => true, JsonValueKind.Number => Integer(value) != 0, JsonValueKind.String => value.ToString() is "1" or "true", _ => false };
    [DoesNotReturn]
    private static void Notify(string message) => throw MachoErrors.ThrowUserError("CustomInfo", ("info", new PyStr(message)));
}
