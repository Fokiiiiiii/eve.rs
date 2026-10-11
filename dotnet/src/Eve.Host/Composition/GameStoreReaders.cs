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

internal sealed partial class CharacterServicesComposition
{
    Task<Eve.Relations.Affiliation> AffiliationOfAsync(long character, CancellationToken token) =>
        foundations.Relations.AffiliationOfAsync(character, cancellationToken: token);

    Task<JsonObject> ReadGameStoreTableAsync(string table, CancellationToken token) =>
        table == "structures" ? ReadStructuresAsync(token) : ReadGameStoreRawAsync(table, token);

    Task<JsonObject> ReadGameStoreRawAsync(string table, CancellationToken token) =>
        PersistenceReadHelpers.ReadOptionalTableAsync(
            (name, cancellation) => mailStore.LoadTableAsync(name, createIfMissing: false, cancellation),
            table,
            token);

    static long CharMgrJsonLong(JsonNode? value) =>
        long.TryParse(value?.ToString(), System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var number) ? number : 0;

    static PyValue CharMgrJsonToPy(JsonNode? node)
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

    static IEnumerable<(string Key, JsonObject Row)> CharMgrRows(JsonNode? source)
    {
        if (source is JsonObject byKey)
        {
            foreach (var (key, value) in byKey)
                if (value is JsonObject row)
                    yield return (key, row);
        }
        else if (source is JsonArray rows)
        {
            for (var index = 0; index < rows.Count; index++)
                if (rows[index] is JsonObject row)
                    yield return (index.ToString(System.Globalization.CultureInfo.InvariantCulture), row);
        }
    }

    static long CharMgrKillmailTime(JsonObject row)
    {
        var value = row["killmailTime"]?.ToString();
        if (long.TryParse(value, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var unixMs))
            return unixMs;
        return DateTimeOffset.TryParse(value, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal, out var timestamp)
            ? timestamp.ToUnixTimeMilliseconds()
            : 0;
    }

    async ValueTask<IReadOnlyList<PyValue>> ReadRecentShipKillmailPageAsync(
        long characterId, int num, int startIndex, CancellationToken cancellationToken)
    {
        if (characterId <= 0 || num <= 0 || startIndex < 0)
            return [];
        var table = await ReadGameStoreTableAsync("killmails", cancellationToken);
        var records = new Dictionary<long, JsonObject>();
        void Add(JsonNode? source)
        {
            foreach (var (key, row) in CharMgrRows(source))
            {
                var killmailId = CharMgrJsonLong(row["killmailID"]);
                if (killmailId <= 0)
                    killmailId = CharMgrJsonLong(JsonValue.Create(key));
                var victim = CharMgrJsonLong(row["victimCharacterID"]);
                var finalCharacter = CharMgrJsonLong(row["finalCharacterID"]);
                if (killmailId <= 0 || victim != characterId && finalCharacter != characterId)
                    continue;
                records[killmailId] = (JsonObject)row.DeepClone();
            }
        }
        Add(table["pending"]);
        Add(table["records"]);
        var page = records
            .OrderByDescending(entry => CharMgrKillmailTime(entry.Value))
            .ThenByDescending(entry => entry.Key)
            .Skip(startIndex)
            .Take(Math.Min(num, 1000))
            .Select(entry => entry.Value)
            .ToArray();
        return PersistenceCharMgrReadModel.PackKillmailRows(page, CharMgrJsonToPy);
    }

      async ValueTask<IReadOnlyList<PyValue>> ReadRecentCorporationKillmailPageAsync(
          long corporationId,bool losses,int limit,long? startKillId,CancellationToken cancellationToken)
      {
          if (corporationId <= 0)
              return [];
          var table = await ReadGameStoreTableAsync("killmails",cancellationToken);
          var records = new Dictionary<long,JsonObject>();
          void Add(JsonNode? source)
          {
              foreach (var (key,row) in CharMgrRows(source))
              {
                  var killmailId = CharMgrJsonLong(row["killID"]);
                  if (killmailId <= 0)
                      killmailId = CharMgrJsonLong(row["killmailID"]);
                  if (killmailId <= 0)
                      killmailId = CharMgrJsonLong(JsonValue.Create(key));
                  if (killmailId <= 0 || startKillId is > 0 && killmailId >= startKillId.Value)
                      continue;

                  var victimCharacter = CharMgrJsonLong(row["victimCharacterID"]);
                  var victimCorporation = CharMgrJsonLong(row["victimCorporationID"]);
                  var belongs = losses
                      ? victimCorporation == corporationId
                      : (victimCharacter > 0 || victimCorporation >= 2_000_000) &&
                        (CharMgrJsonLong(row["finalCorporationID"]) == corporationId ||
                         row["attackers"] is JsonArray attackers && attackers
                             .OfType<JsonObject>()
                             .Any(attacker => CharMgrJsonLong(attacker["corporationID"]) == corporationId));
                  if (!belongs)
                      continue;

                  var copy = (JsonObject)row.DeepClone();
                  copy["killID"] ??= JsonValue.Create(killmailId);
                  if (copy["killTime"] is null)
                  {
                      var unixMs = CharMgrKillmailTime(copy);
                      if (unixMs > 0)
                          copy["killTime"] = JsonValue.Create(DateTimeOffset.FromUnixTimeMilliseconds(unixMs).ToFileTime());
                  }
                  records[killmailId] = copy;
              }
          }
          Add(table["pending"]);
          Add(table["records"]);
          var ordered = records
              .OrderByDescending(entry => CorporationKillmailTime(entry.Value))
              .ThenByDescending(entry => entry.Key);
          var page = (limit > 0 ? ordered.Take(limit) : ordered).Select(entry => entry.Value).ToArray();
          return page.Select(row => CharMgrJsonToPy(row)).ToArray();
      }

      static long CorporationKillmailTime(JsonObject row)
      {
          var value = row["killmailTime"]?.ToString();
          if (long.TryParse(value,System.Globalization.NumberStyles.Integer,
                  System.Globalization.CultureInfo.InvariantCulture,out var unixMs))
              return unixMs;
          value = row["killTime"]?.ToString();
          if (long.TryParse(value,System.Globalization.NumberStyles.Integer,
                  System.Globalization.CultureInfo.InvariantCulture,out var filetime))
              return filetime > 100_000_000_000_000L
                  ? (filetime-116_444_736_000_000_000L)/10_000
                  : filetime;
          return DateTimeOffset.TryParse(value,System.Globalization.CultureInfo.InvariantCulture,
              System.Globalization.DateTimeStyles.AssumeUniversal,out var timestamp)
              ? timestamp.ToUnixTimeMilliseconds()
              : 0;
      }

      async ValueTask<long> ReadCharacterAllyBaseCostAsync(long characterId,CancellationToken cancellationToken)
      {
          const int diplomaticRelationsTypeId = 3368;
          const int baseCostAttributeId = 1820;
          const int modifierAttributeId = 1821;
          const double defaultBaseCost = 10_000_000;
          var attributes = dogma.Type(diplomaticRelationsTypeId)?.Attributes;
          var baseCost = attributes is not null && attributes.TryGetValue(baseCostAttributeId,out var baseAttribute) &&
              double.IsFinite(baseAttribute)
              ? Math.Round(baseAttribute*100,MidpointRounding.AwayFromZero)/100
              : defaultBaseCost;
          var modifier = attributes is not null && attributes.TryGetValue(modifierAttributeId,out var modifierAttribute) &&
              double.IsFinite(modifierAttribute)
              ? Math.Round(modifierAttribute*100,MidpointRounding.AwayFromZero)/100
              : 0;
          await using var connection = await source.OpenConnectionAsync(cancellationToken);
          await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
          var skillLevel = await skills.LevelAsync(connection,transaction,characterId,diplomaticRelationsTypeId,cancellationToken);
          await transaction.CommitAsync(cancellationToken);
          var multiplier = Math.Max(0,1+(modifier*skillLevel)/100);
          return Math.Max(0,(long)Math.Round(baseCost*multiplier,MidpointRounding.AwayFromZero));
      }

    async ValueTask<string?> ReadCharacterDescriptionAsync(long characterId, CancellationToken cancellationToken)
    {
        if (characterId <= 0)
            return null;
        var table = await ReadGameStoreTableAsync("characters", cancellationToken);
        var key = characterId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return table[key] is JsonObject row && row["description"] is JsonValue value &&
            value.TryGetValue<string>(out var description) ? description : null;
    }

    async ValueTask WriteCharacterDescriptionAsync(long characterId, string description, CancellationToken cancellationToken)
    {
        if (characterId <= 0)
            throw new ArgumentOutOfRangeException(nameof(characterId));
        var key = characterId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var write = mailCache.Write("characters", $"/{key}/description", JsonValue.Create(description));
        if (!write.Success)
            throw new InvalidOperationException($"Could not persist character {characterId} description: {write.ErrorMsg}");
        for (var attempt = 0; attempt < 500; attempt++)
        {
            await mailCache.FlushTableAsync("characters", cancellationToken);
            var persisted = await mailStore.LoadTableAsync("characters", createIfMissing: false, cancellationToken);
            if (persisted[key] is JsonObject row && row["description"] is JsonValue value &&
                value.TryGetValue<string>(out var stored) && stored == description)
                return;
            await Task.Delay(10, cancellationToken);
        }
        throw new TimeoutException($"Character {characterId} description did not become durable.");
    }

    async ValueTask<long?> ReadMedal1GraphicIdAsync(long characterId, CancellationToken cancellationToken)
    {
        if (characterId <= 0)
            return null;
        var table = await ReadGameStoreTableAsync("characters", cancellationToken);
        var key = characterId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (table[key] is not JsonObject row)
            return null;
        var graphic = CharMgrJsonLong(row["medal1GraphicID"]);
        if (graphic <= 0)
            graphic = CharMgrJsonLong(row["medal1GraphicId"]);
        return graphic > 0 ? graphic : null;
    }

    async ValueTask<double> ReadCharacterSecurityStatusAsync(long characterId, CancellationToken cancellationToken) =>
        await securityStatusOf(characterId, cancellationToken);
}
