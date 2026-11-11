using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Eve.Characters;
using Eve.Crimewatch;
using Eve.Events;
using Eve.GameStore;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Space;
using Eve.Ships;
using Npgsql;

namespace Eve.Host.Persistence.Killmails;

/// <summary>Persists a ship destruction and its kill report in the event transaction.</summary>
internal sealed class PersistenceShipKillmailRecorder(
    PersistenceStore store,
    OwnerSession owner,
    GameStoreCache cache,
    Eve.Host.Persistence.Mail.PersistenceTransactionMailbox mailbox,
    PgCharacterStore characters,
    PgCrimewatchStore crimewatch,
    Func<IReadOnlyList<long>, CancellationToken, Task<IReadOnlyDictionary<long, decimal>>> readBasePrices,
    Func<long, CancellationToken, Task<double>> readSecurityStatus,
    Eve.Services.Crimewatch.PlayerBountyKills playerBountyKills,
    Func<long, CancellationToken, Task<IReadOnlyList<long>>> readFleetSharers,
    Func<NpgsqlConnection, NpgsqlTransaction, string, CancellationToken, Task> schedulePlayerBountyPayout,
    Action<long, string, string, PyValue[]> sendNotification)
{
    public const string SubscriberName = "killmail.ship-destruction";
    private const string Table = "killmails";
    private const long ConcordCorporationId = 1_000_125;

    public void Subscribe(EventBus events) =>
        events.Subscribe(SubscriberName, [ShipKillmailEvent.Topic], RecordAsync);

    private async Task RecordAsync(EventContext context, CancellationToken cancellationToken)
    {
        var occurred = context.Event.Payload<ShipKillmailEvent>()
            ?? throw new InvalidOperationException("The ship killmail event has no payload.");
        if (occurred.CharacterId is < 0 || occurred.ShipId <= 0 || occurred.ShipTypeId <= 0 ||
            occurred.SolarSystemId <= 0 || occurred.OccurredAtMs <= 0)
            throw new InvalidOperationException("The ship killmail event is missing required victim data.");

        await using (var command = new NpgsqlCommand(
            "SELECT pg_advisory_xact_lock(hashtextextended('eve-host.killmail.next-id', 0))",
            context.Connection, context.Transaction))
            await command.ExecuteScalarAsync(cancellationToken);

        var metadataJson = await store.ReadRowAsync(context.Connection, context.Transaction, Table, "_meta", cancellationToken);
        var metadata = metadataJson is null ? new JsonObject() :
            JsonNode.Parse(metadataJson) as JsonObject ?? throw new InvalidOperationException("The killmail metadata row is not an object.");
        var killId = PositiveLong(metadata["nextKillID"], 1);
        metadata["version"] ??= 1;
        metadata["nextKillID"] = checked(killId + 1);

        var attackerCharacterIds = occurred.Attackers
            .Where(attacker => attacker.CharacterId is > 0)
            .Select(attacker => attacker.CharacterId!.Value)
            .Distinct()
            .ToArray();
        var attackerCharacters = attackerCharacterIds.Length == 0
            ? new Dictionary<long, Character>()
            : (await characters.GetManyAsync(attackerCharacterIds, cancellationToken)).ToDictionary(character => character.Id);
        var basePriceTypeIds = new HashSet<long> { occurred.ShipTypeId };
        AddItemTypes(occurred.Items, basePriceTypeIds);
        var prices = await readBasePrices([.. basePriceTypeIds], cancellationToken);
        var killTime = Py.FileTimeFromUnixMs(occurred.OccurredAtMs);

        JsonObject AttackerRecord(ShipKillmailAttacker attacker)
        {
            attackerCharacters.TryGetValue(attacker.CharacterId ?? 0, out var character);
            return new JsonObject
            {
                ["characterID"] = Positive(character is null ? 0 : attacker.CharacterId ?? 0),
                ["corporationID"] = Positive(attacker.CorporationId > 0 ? attacker.CorporationId : character?.CorporationId ?? 0),
                ["allianceID"] = Positive(attacker.AllianceId > 0 ? attacker.AllianceId : character?.AllianceId ?? 0),
                ["factionID"] = Positive(attacker.FactionId > 0 ? attacker.FactionId : character?.FactionId ?? 0),
                ["shipTypeID"] = Positive(attacker.ShipTypeId),
                ["weaponTypeID"] = Positive(attacker.WeaponTypeId),
                ["damageDone"] = Math.Max(0, attacker.DamageDone),
                ["securityStatus"] = attacker.SecurityStatus is { } status ? JsonValue.Create(status) : null,
            };
        }

        var finalInput = occurred.KillerShipId > 0
            ? occurred.Attackers.Where(attacker => attacker.SourceShipId == occurred.KillerShipId)
                .OrderByDescending(attacker => attacker.LastHitOrder).FirstOrDefault()
            : null;
        var finalCharacter = finalInput?.CharacterId is { } finalCharacterId && finalCharacterId > 0
            ? attackerCharacters.GetValueOrDefault(finalCharacterId)
            : null;
        var bountyClaimed = occurred.BountyClaimed;
        if (bountyClaimed is null && occurred.CharacterId is > 0 && finalCharacter is { } hunter)
        {
            var contents = KillmailContents(occurred.Items).ToArray();
            var sharers = await readFleetSharers(hunter.Id, cancellationToken);
            bountyClaimed = await playerBountyKills.ClaimShipLossInTransactionAsync(
                context.Connection, context.Transaction, occurred.CharacterId.Value, occurred.ShipId, occurred.ShipTypeId,
                contents, hunter.Id, occurred.OccurredAtMs, prices,
                (connection, transaction, payoutId) => schedulePlayerBountyPayout(
                    connection, transaction, payoutId, cancellationToken),
                sharers, cancellationToken);
        }
        var finalCorp = finalInput is null ? 0 : finalInput.CorporationId > 0 ? finalInput.CorporationId : finalCharacter?.CorporationId ?? 0;
        var finalAlliance = finalInput is null ? 0 : finalInput.AllianceId > 0 ? finalInput.AllianceId : finalCharacter?.AllianceId ?? 0;
        var finalFaction = finalInput is null ? 0 : finalInput.FactionId > 0 ? finalInput.FactionId : finalCharacter?.FactionId ?? 0;
        double? finalSecurityStatus = finalInput?.SecurityStatus;
        if (finalSecurityStatus is null && finalCharacter is not null)
            finalSecurityStatus = await readSecurityStatus(finalCharacter.Id, cancellationToken);

        var killRight = occurred.CharacterId is > 0
            ? await crimewatch.ConsumeKillRightAfterKillAsync(
                occurred.CharacterId.Value, finalInput?.CharacterId ?? 0, occurred.ShipId,
                occurred.OccurredAtMs, cancellationToken)
            : null;

        var attackRecords = new JsonArray();
        foreach (var attacker in occurred.Attackers.Where(attacker =>
                     finalInput is null || attacker.SourceShipId != finalInput.SourceShipId || attacker.WeaponTypeId != finalInput.WeaponTypeId))
        {
            var record = AttackerRecord(attacker);
            if (record["securityStatus"] is null && attacker.CharacterId is { } characterId &&
                attackerCharacters.ContainsKey(characterId))
                record["securityStatus"] = await readSecurityStatus(characterId, cancellationToken);
            attackRecords.Add(record);
        }

        var itemRecords = new JsonArray();
        foreach (var item in occurred.Items)
            itemRecords.Add(ItemRecord(item));

        var itemLost = ItemValue(occurred.Items, prices, destroyedOnly: false);
        var itemDestroyed = ItemValue(occurred.Items, prices, destroyedOnly: true);
        var shipPrice = prices.GetValueOrDefault(occurred.ShipTypeId);
        var finalDamage = finalInput?.DamageDone ?? 0;
        var recordObject = new JsonObject
        {
            ["killID"] = killId,
            ["killmailID"] = killId,
            ["killTime"] = killTime,
            ["killmailTime"] = occurred.OccurredAtMs,
            ["solarSystemID"] = occurred.SolarSystemId,
            ["moonID"] = null,
            ["victimCharacterID"] = Positive(occurred.CharacterId ?? 0),
            ["victimCorporationID"] = Positive(occurred.CorporationId),
            ["victimAllianceID"] = Positive(occurred.AllianceId),
            ["victimFactionID"] = Positive(occurred.FactionId),
            ["victimShipTypeID"] = occurred.ShipTypeId,
            ["victimDamageTaken"] = Math.Max(0, occurred.DamageTaken),
            ["finalCharacterID"] = Positive(finalCharacter?.Id ?? 0),
            ["finalCorporationID"] = Positive(finalCorp),
            ["finalAllianceID"] = Positive(finalAlliance),
            ["finalFactionID"] = Positive(finalFaction),
            ["finalShipTypeID"] = Positive(finalInput?.ShipTypeId ?? 0),
            ["finalWeaponTypeID"] = Positive(finalInput?.WeaponTypeId ?? 0),
            ["finalSecurityStatus"] = finalSecurityStatus,
            ["finalDamageDone"] = Math.Max(0, finalDamage),
            ["warID"] = null,
            ["iskLost"] = Math.Max(0, shipPrice + itemLost),
            ["iskDestroyed"] = Math.Max(0, shipPrice + itemDestroyed),
            ["bountyClaimed"] = bountyClaimed,
            ["loyaltyPoints"] = null,
            ["killRightSupplied"] = Positive(killRight?.OwnerCharacterId ?? 0),
            ["attackers"] = attackRecords,
            ["items"] = itemRecords,
        };
        recordObject["killBlob"] = BuildKillBlob(recordObject);

        var recordKey = RowCodec.Explode(Table, new JsonObject
        {
            ["records"] = new JsonObject { [Key(killId)] = recordObject },
        }).Keys.Single(key => key != "records");
        var recordUpserts = new[]
        {
            new RowUpsert("_meta", metadata.ToJsonString()),
            new RowUpsert(recordKey, recordObject.ToJsonString()),
        };
        owner.AssertHealthy();
        await store.ApplyChangesAsync(context.Connection, context.Transaction, Table, recordUpserts, [], owner.Fence, cancellationToken);

        var killmailHash = KillmailHash(recordObject);
        var notificationData = new JsonObject
        {
            ["killMailHash"] = killmailHash,
            ["killMailID"] = killId,
            ["victimShipTypeID"] = occurred.ShipTypeId,
        };
        context.AfterCommit(_ =>
        {
            var metadataResult = cache.Write(Table, "/_meta", metadata);
            if (!metadataResult.Success)
                throw new InvalidOperationException("The committed killmail metadata could not update the GameStore cache.");
            var cacheResult = cache.Write(Table, $"/records/{Key(killId)}", recordObject);
            if (!cacheResult.Success)
                throw new InvalidOperationException("The committed killmail could not update the GameStore cache.");
            return Task.CompletedTask;
        });
        if (occurred.CharacterId is { } victimCharacterId && victimCharacterId > 0)
        {
            await mailbox.WriteNotificationsAsync(ConcordCorporationId, 140, 8, [victimCharacterId],
                $"killmail:{killId}", notificationData, cancellationToken, context, killTime);
            context.AfterCommit(_ =>
            {
                sendNotification(victimCharacterId, "OnKillNotification", "charid", []);
                sendNotification(victimCharacterId, "OnShipDeath", "charid", []);
                return Task.CompletedTask;
            });

            var finalBlowCharacterId = finalCharacter?.Id ?? 0;
            if (finalBlowCharacterId > 0 && finalBlowCharacterId != victimCharacterId)
            {
                var finalBlowData = (JsonObject)notificationData.DeepClone();
                finalBlowData["victimID"] = victimCharacterId;
                await mailbox.WriteNotificationsAsync(ConcordCorporationId, 141, 8, [finalBlowCharacterId],
                    $"killmail:finalblow:{killId}", finalBlowData, cancellationToken, context, killTime);
                context.AfterCommit(_ =>
                {
                    sendNotification(finalBlowCharacterId, "OnKillNotification", "charid", []);
                    return Task.CompletedTask;
                });
            }
        }
    }

    private static void AddItemTypes(IEnumerable<ShipDeathItem> items, ISet<long> typeIds)
    {
        foreach (var item in items)
        {
            if (item.TypeId > 0)
                typeIds.Add(item.TypeId);
            AddItemTypes(item.Contents, typeIds);
        }
    }

    private static IEnumerable<(int TypeId, long Quantity)> KillmailContents(IEnumerable<ShipDeathItem> items)
    {
        foreach (var item in items)
        {
            yield return (item.TypeId, checked(item.QuantityDropped + item.QuantityDestroyed));
            foreach (var child in KillmailContents(item.Contents))
                yield return child;
        }
    }

    private static JsonObject ItemRecord(ShipDeathItem item)
    {
        var contents = new JsonArray();
        foreach (var child in item.Contents)
            contents.Add(ItemRecord(child));
        return new JsonObject
        {
            ["typeID"] = item.TypeId,
            ["flag"] = item.Flag,
            ["singleton"] = item.Singleton ? 1 : 0,
            ["qtyDropped"] = item.QuantityDropped,
            ["qtyDestroyed"] = item.QuantityDestroyed,
            ["contents"] = contents,
        };
    }

    private static decimal ItemValue(IEnumerable<ShipDeathItem> items, IReadOnlyDictionary<long, decimal> prices, bool destroyedOnly) =>
        items.Sum(item => prices.GetValueOrDefault(item.TypeId) *
            (destroyedOnly ? item.QuantityDestroyed : item.QuantityDropped + item.QuantityDestroyed) +
            ItemValue(item.Contents, prices, destroyedOnly));

    private static string BuildKillBlob(JsonObject record)
    {
        var attackers = record["attackers"] as JsonArray ?? [];
        var items = record["items"] as JsonArray ?? [];
        var xml = new StringBuilder("<attackers>");
        foreach (var attacker in attackers.OfType<JsonObject>())
        {
            xml.Append("<a");
            XmlAttribute(xml, "c", attacker["characterID"]);
            XmlAttribute(xml, "r", attacker["corporationID"]);
            XmlAttribute(xml, "a", attacker["allianceID"]);
            XmlAttribute(xml, "f", attacker["factionID"]);
            XmlAttribute(xml, "s", attacker["shipTypeID"]);
            XmlAttribute(xml, "w", attacker["weaponTypeID"]);
            XmlAttribute(xml, "d", JsonValue.Create(Math.Truncate(Number(attacker["damageDone"]))));
            xml.Append(" t=\"").Append(XmlEscape(attacker["securityStatus"] is null ? "0.0" :
                Number(attacker["securityStatus"]).ToString("0.0", CultureInfo.InvariantCulture))).Append("\" />");
        }
        xml.Append("</attackers><items>");
        foreach (var item in items.OfType<JsonObject>())
            AppendItemXml(xml, item, 0);
        return xml.Append("</items>").ToString();
    }

    private static void AppendItemXml(StringBuilder xml, JsonObject item, int depth)
    {
        xml.Append("<i");
        XmlAttribute(xml, "t", item["typeID"]);
        XmlAttribute(xml, "f", item["flag"]);
        XmlAttribute(xml, "s", item["singleton"]);
        XmlAttribute(xml, "d", item["qtyDropped"]);
        XmlAttribute(xml, "x", item["qtyDestroyed"]);
        xml.Append('>');
        if (depth == 0 && item["contents"] is JsonArray children)
            foreach (var child in children.OfType<JsonObject>())
                AppendItemXml(xml, child, depth + 1);
        xml.Append("</i>");
    }

    private static void XmlAttribute(StringBuilder xml, string name, JsonNode? value)
    {
        if (value is null)
            return;
        xml.Append(' ').Append(name).Append("=\"").Append(XmlEscape(value.ToString())).Append('"');
    }

    private static string XmlEscape(string value) => value.Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("\"", "&quot;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal);

    private static double Number(JsonNode? value) =>
        double.TryParse(value?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var result) && double.IsFinite(result)
            ? result
            : 0;

    private static string KillmailHash(JsonObject record)
    {
        var text = string.Concat(PythonString(record["victimCharacterID"]), PythonString(record["finalCharacterID"]),
            PythonString(record["victimShipTypeID"]), PythonString(record["killTime"]));
        return Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    }

    private static string PythonString(JsonNode? value) => value?.ToString() ?? "None";

    private static JsonNode? Positive(long value) => value > 0 ? JsonValue.Create(value) : null;

    private static long PositiveLong(JsonNode? value, long fallback) =>
        long.TryParse(value?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed > 0
            ? parsed
            : fallback;

    private static string Key(long value) => value.ToString(CultureInfo.InvariantCulture);
}
