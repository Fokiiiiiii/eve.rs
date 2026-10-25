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

public sealed partial class PersistenceNewEdenStoreRuntime(
    NpgsqlDataSource source,
    PersistenceStore store,
    OwnerFence ownerFence,
    PgWalletStore plexWallets,
    PgCharacterStore characters,
    PgItemStore items,
    ItemTypeCatalog itemTypes,
    CharacterSessions sessions,
    Func<long> nowUnixMs,
    PgAccountDirectory accountDirectory)
    : Eve.Services.Core.IRedeemCallbacks, Eve.Services.Core.IVaultManagerCallbacks, Eve.Services.Core.ILoginRewardCallbacks,
      Eve.Services.Core.IMultipleTrainingCallbacks
{
    private const string RedeemTable = "redeemTokens";
    private const string RedeemRow = "accounts";
    private const string LoginCampaignTable = "dailyLoginCampaignProgress";
    private const string LoginCampaignRow = "accountsByUserID";
    private const int DailyLoginCampaignId = 2187;
    private const int DailyLoginBucketId = 4;
    private const int DailyLoginTrackLength = 15;
    private const int DailyLoginResetHourUtc = 11;
    private const int DailyLoginRewardTypeId = 49810;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private long DeliveryStationId(Eve.Characters.Character recipient, long characterId)
    {
        var docked = sessions.Of(characterId)?.GetInt("stationid") ?? sessions.Of(characterId)?.GetInt("structureid") ?? 0;
        return docked > 0 ? docked : recipient.HomeStationId;
    }

    public async ValueTask<Eve.Services.Core.LoginCampaignSnapshot?> GetLoginCampaignStateAsync(
        Eve.Services.Core.LoginRewardPlayer player,
        CancellationToken cancellationToken = default)
    {
        if (!await HasLoginRewardCharacterAsync(player, cancellationToken))
            return null;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var state = await PersistenceReadHelpers.ReadOptionalTableAsync(
                store, LoginCampaignTable, cancellationToken);
            var accounts = state[LoginCampaignRow] as JsonObject;
            var accountKey = player.UserId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var progress = LoginCampaignProgress(accounts?[accountKey] as JsonObject);
            var now = DateTimeOffset.FromUnixTimeMilliseconds(nowUnixMs());
            var resetId = LoginCampaignResetId(now);
            var resetStart = LoginCampaignResetStart(now);
            var nextRewardTime = progress.LastClaimReset == resetId
                ? resetStart.AddDays(1)
                : resetStart;
            var nextRewardIndex = progress.Day >= DailyLoginTrackLength ? 1 : progress.Day + 1;
            var clientState = LoginCampaignClientState(progress.Day, progress.LastClaimReset, now);

            return new Eve.Services.Core.LoginCampaignSnapshot(
                DailyLoginCampaignId,
                clientState,
                nextRewardIndex,
                Py.FileTimeFromUnixMs(nextRewardTime.ToUnixTimeMilliseconds()));
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<Eve.Services.Core.LoginCampaignClaimResult?> TryClaimLoginCampaignRewardAsync(
        Eve.Services.Core.LoginCampaignClaimRequest request,
        CancellationToken cancellationToken = default)
    {
        var player = request.Player;
        if (request.CampaignId != DailyLoginCampaignId ||
            !await HasLoginRewardCharacterAsync(player, cancellationToken))
            return null;
        if (await itemTypes.GetAsync(DailyLoginRewardTypeId, cancellationToken) is null)
            throw new InvalidOperationException($"Daily login reward type {DailyLoginRewardTypeId} is missing from the item catalog.");

        var accountKey = player.UserId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var nowUnix = nowUnixMs();
            var now = DateTimeOffset.FromUnixTimeMilliseconds(nowUnix);
            var resetId = LoginCampaignResetId(now);
            var resetStart = LoginCampaignResetStart(now);
            var availableAt = Py.FileTimeFromUnixMs(resetStart.ToUnixTimeMilliseconds());

            var loginTable = await PersistenceReadHelpers.ReadOptionalTableAsync(
                store, LoginCampaignTable, cancellationToken);
            var loginAccounts = loginTable[LoginCampaignRow] as JsonObject ?? new JsonObject();
            var progress = LoginCampaignProgress(loginAccounts[accountKey] as JsonObject);
            if (progress.LastClaimReset == resetId)
                return null;

            var rewardIndex = progress.Day >= DailyLoginTrackLength ? 1 : progress.Day + 1;
            if (request.ExpectedNextRewardIndex != rewardIndex ||
                request.ExpectedNextRewardTimestampFiletime != availableAt ||
                availableAt >= Py.FileTimeFromUnixMs(nowUnix))
                return null;

            var redeemTable = await PersistenceReadHelpers.ReadOptionalTableAsync(
                store, RedeemTable, cancellationToken);
            var redeemAccounts = redeemTable[RedeemRow] as JsonObject ?? new JsonObject();
            var accountTokens = redeemAccounts[accountKey] as JsonObject ?? new JsonObject();
            var tokenId = TokenId($"daily-login:{accountKey}:{resetId}", 0);
            var tokenKey = tokenId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (accountTokens.ContainsKey(tokenKey))
                throw new InvalidOperationException("The daily login reward token already exists.");

            accountTokens[tokenKey] = new JsonObject
            {
                ["tokenID"] = tokenId,
                ["massTokenID"] = null,
                ["typeID"] = DailyLoginRewardTypeId,
                ["quantity"] = 1,
                ["stationID"] = 0,
                ["dateTime"] = Py.FileTimeFromUnixMs(nowUnix).ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["expireDateTime"] = "0",
                ["label"] = "Daily Login Reward",
                ["description"] = "Daily login campaign reward",
                ["available"] = true,
                ["addedByContext"] = 0,
                ["addedByExtra"] = null,
            };
            redeemAccounts[accountKey] = accountTokens;
            loginAccounts[accountKey] = new JsonObject
            {
                ["day"] = rewardIndex,
                ["lastClaimReset"] = resetId,
            };

            await using var connection = await source.OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await store.ApplyChangesAsync(
                connection, transaction, LoginCampaignTable,
                [new RowUpsert(LoginCampaignRow, loginAccounts.ToJsonString())], [], ownerFence, cancellationToken);
            await store.ApplyChangesAsync(
                connection, transaction, RedeemTable,
                [new RowUpsert(RedeemRow, redeemAccounts.ToJsonString())], [], ownerFence, cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            sessions.Of(player.CharacterId!.Value)?.SendNotification("OnRedeemingQueueUpdated", "clientID");
            var claimedProgress = LoginCampaignItemProgress(rewardIndex, resetId, now);
            return new Eve.Services.Core.LoginCampaignClaimResult(
                LoginCampaignReward(rewardIndex),
                claimedProgress,
                PyNone.Instance,
                LoginCampaignBucketProgress(rewardIndex));
        }
        finally
        {
            _gate.Release();
        }
    }

    private async ValueTask<bool> HasLoginRewardCharacterAsync(
        Eve.Services.Core.LoginRewardPlayer player,
        CancellationToken cancellationToken)
    {
        if (player.UserId <= 0 || player.UserId > int.MaxValue ||
            player.CharacterId is not { } characterId || characterId <= 0)
            return false;
        var character = await characters.GetAsync(characterId, cancellationToken);
        return character is not null && character.AccountId == (int)player.UserId;
    }

    public async ValueTask<long> GetPlexBalanceAsync(long authenticatedUserId, CancellationToken cancellationToken)
    {
        var accountCharacters = await characters.ForAccountAsync(checked((int)authenticatedUserId), cancellationToken);
        var balances = await plexWallets.ReadPlexBalancesAsync(
            accountCharacters.Select(character => character.Id).ToArray(), cancellationToken);
        return balances.Values.Aggregate(0L, (sum, balance) => checked(sum + balance));
    }

    public async ValueTask<PyValue> GetRedeemTokensAsync(
    long authenticatedUserId,
    CancellationToken cancellationToken)
{
    RequireAccount(authenticatedUserId);
    var state = await PersistenceReadHelpers.ReadOptionalTableAsync(store, RedeemTable, cancellationToken);
    var rows = (state[RedeemRow] as JsonObject)?[
        authenticatedUserId.ToString(System.Globalization.CultureInfo.InvariantCulture)] as JsonObject;
    if (rows is null)
        return new PyList([]);
    return new PyList([.. rows
        .Where(entry =>
            long.TryParse(entry.Key, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out _) &&
            entry.Value is JsonObject row && String(row["status"]) is "" or "pending")
        .OrderBy(entry => Integer((entry.Value as JsonObject)?["dateTime"]))
        .Select(entry => ToRedeemToken((JsonObject)entry.Value!, entry.Key))]);
}

    public async ValueTask<PyValue> CreateRedeemTokenQAAsync(ServiceCall call, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var accountId = RedeemQaPositiveInteger(call.Kwarg("userID"));
        if (accountId <= 0)
            accountId = call.Session.UserId ?? 0;
        var typeId = RedeemQaPositiveInteger(call.Kwarg("typeID"));
        if (accountId is <= 0 or > int.MaxValue || typeId is <= 0 or > int.MaxValue)
            return PyBool.False;
        RequireAccount(accountId);

        var quantityNumber = call.Kwarg("quantity") is { } requestedQuantity
            ? Eve.Macho.JsSemantics.ToNumber(requestedQuantity)
            : 1;
        var quantity = !double.IsFinite(quantityNumber) || quantityNumber == 0
            ? 1
            : Math.Max(1, Math.Truncate(quantityNumber));
        var quantityLong = RedeemQaBoundedInteger(quantity);
        var blueprintRuns = Math.Max(0, RedeemQaInteger(call.Kwarg("blueprintRuns")));
        var materialLevel = RedeemQaInteger(call.Kwarg("blueprintMaterialLevel"));
        var productivityLevel = RedeemQaInteger(call.Kwarg("blueprintProductivityLevel"));
        var soulboundValue = call.Kwarg("soulbound");
        var soulbound = soulboundValue is not null && Eve.Macho.JsSemantics.Truthy(soulboundValue);
        var availableValue = call.Kwarg("available");
        var available = availableValue is null or PyNone || Eve.Macho.JsSemantics.Truthy(availableValue);
        var labelValue = call.Kwarg("label");
        var label = labelValue is not null && Eve.Macho.JsSemantics.Truthy(labelValue)
            ? RedeemQaText(labelValue)
            : string.Empty;
        var descriptionValue = call.Kwarg("description");
        var description = descriptionValue is not null && Eve.Macho.JsSemantics.Truthy(descriptionValue)
            ? RedeemQaText(descriptionValue)
            : string.Empty;
        var expiry = RedeemQaFileTime(call.Kwarg("expiry"));
        var addedByContext = RedeemQaInteger(call.Kwarg("addedByContext"));
        var addedByExtra = RedeemQaJson(call.Kwarg("addedByExtra"));
        var nowFileTime = ToFileTime(nowUnixMs());
        var accountKey = accountId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var typeKey = typeId.ToString(System.Globalization.CultureInfo.InvariantCulture);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = await PersistenceReadHelpers.ReadOptionalTableAsync(store, RedeemTable, cancellationToken)
                .ConfigureAwait(false);
            var accounts = state[RedeemRow] as JsonObject ?? new JsonObject();
            var accountTokens = accounts[accountKey] as JsonObject ?? new JsonObject();
            var maximumId = MaximumRedeemTokenId(accountTokens);
            var tokenId = maximumId >= 700_000_000
                ? checked(maximumId + 1)
                : 700_000_000 + Math.Abs(nowUnixMs() % 100_000_000);
            var tokenKey = tokenId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var isAutoInject = soulbound || typeId is 52996 or 73277 or 54652 or 54653 or 63303 or 63304 or
                54650 or 63305 or 63306 or 63307 or 63308 or 54654 or 63309;
            var token = new JsonObject
            {
                ["tokenID"] = tokenId,
                ["massTokenID"] = tokenId,
                ["typeID"] = checked((int)typeId),
                ["quantity"] = quantityLong,
                ["stationID"] = 0,
                ["dateTime"] = nowFileTime.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["expireDateTime"] = expiry.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["availableDateTime"] = nowFileTime.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["label"] = label,
                ["description"] = description,
                ["blueprintRuns"] = blueprintRuns,
                ["blueprintMaterialLevel"] = materialLevel,
                ["blueprintProductivityLevel"] = productivityLevel,
                ["soulbound"] = soulbound,
                ["available"] = available,
                ["addedByContext"] = addedByContext,
                ["addedByExtra"] = addedByExtra,
                ["isAutoInject"] = isAutoInject,
                ["status"] = "pending",
            };
            accountTokens[tokenKey] = token;
            accounts[accountKey] = accountTokens;

            await using var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await store.ApplyChangesAsync(
                connection, transaction, RedeemTable,
                [new RowUpsert(RedeemRow, accounts.ToJsonString())], [], ownerFence, cancellationToken)
                .ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            var blueprintInfo = blueprintRuns > 0
                ? LoginCampaignKeyVal(
                    ("isCopy", PyBool.True),
                    ("runs", Py.Number(blueprintRuns)),
                    ("materialEfficiency", Py.Number(materialLevel)),
                    ("timeEfficiency", Py.Number(productivityLevel)))
                : PyNone.Instance;
            return LoginCampaignKeyVal(
                ("tokenID", Py.Number(tokenId)),
                ("massTokenID", Py.Number(tokenId)),
                ("typeID", Py.Number(typeId)),
                ("quantity", Py.Number(quantityLong)),
                ("stationID", new PyInt(0)),
                ("dateTime", new PyLong(nowFileTime)),
                ("expireDateTime", Py.Number(expiry)),
                ("label", new PyStr(label)),
                ("description", new PyStr(description)),
                ("blueprintRuns", Py.Number(blueprintRuns)),
                ("blueprintInfo", blueprintInfo),
                ("soulbound", soulbound ? PyBool.True : PyBool.False),
                ("isAutoInject", isAutoInject ? PyBool.True : PyBool.False));
        }
        finally
        {
            _gate.Release();
        }
    }

    private static PyValue? RedeemQaField(PyValue source, string name)
    {
        IEnumerable<KeyValuePair<PyValue, PyValue>> entries = source switch
        {
            PyDict dict => dict.Entries,
            PyObject { Args: PyDict dict } => dict.Entries,
            PyObjectEx objectEx => objectEx.Dict,
            _ => [],
        };
        return entries.FirstOrDefault(entry => Eve.Macho.JsSemantics.StringValue(entry.Key) == name).Value;
    }

    public async ValueTask<PyValue> ClaimRedeemTokensAsync(
        long authenticatedUserId,
        PyValue tokens,
        long characterId,
        bool useHomeStation,
        CancellationToken cancellationToken)
    {
        RequireAccount(authenticatedUserId);
        var character = await characters.GetAsync(characterId, cancellationToken);
        if (character is null || character.AccountId != checked((int)authenticatedUserId))
            throw new UnauthorizedAccessException("The requested character does not belong to this account.");
        var stationId = useHomeStation
            ? character.HomeStationId
            : sessions.Of(characterId)?.GetInt("stationid") ?? sessions.Of(characterId)?.GetInt("structureid") ?? 0;
        if (stationId <= 0)
            throw new InvalidOperationException("Redeem items can only be claimed at a station or structure.");
        var requested = TokenPairs(tokens);
        if (requested.Length == 0)
            return await RedeemResultAsync(characterId, stationId, []);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var state = await PersistenceReadHelpers.ReadOptionalTableAsync(store, RedeemTable, cancellationToken);
            var accounts = state[RedeemRow] as JsonObject ?? new JsonObject();
            var accountKey = authenticatedUserId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var accountTokens = accounts[accountKey] as JsonObject ?? new JsonObject();
            var claimed = accountTokens["claimed"] as JsonObject ?? new JsonObject();
            var pending = new List<(long TokenId, JsonObject Token, ItemType Type)>();
            var replayItems = new List<(long ItemId, int TypeId, long Quantity)>();
            foreach (var pair in requested.Distinct())
            {
                var key = pair.TokenId.ToString(System.Globalization.CultureInfo.InvariantCulture);
                if (claimed[key] is JsonObject prior)
                {
                    foreach (var item in prior["itemsCreated"] as JsonArray ?? [])
                        if (item is JsonObject row)
                            replayItems.Add((Integer(row["itemID"]), checked((int)Integer(row["typeID"])), Integer(row["quantity"])));
                    continue;
                }
                if (accountTokens[key] is not JsonObject token ||
                    String(token["status"]) is not ("" or "pending") ||
                    (pair.MassTokenId > 0 && Integer(token["massTokenID"]) != pair.MassTokenId))
                    throw new InvalidOperationException("A redeem token is unavailable or does not belong to this account.");
                var typeId = checked((int)Integer(token["typeID"]));
                var type = await itemTypes.GetAsync(typeId, cancellationToken);
                if (type is null || Integer(token["quantity"]) <= 0)
                    throw new InvalidOperationException("A redeem token contains an invalid item.");
                pending.Add((pair.TokenId, token, type));
            }

            var newCreated = new List<(long ItemId, int TypeId, long Quantity)>();
            await using (var connection = await source.OpenConnectionAsync(cancellationToken))
            await using (var transaction = await connection.BeginTransactionAsync(cancellationToken))
            {
                foreach (var entry in pending)
                {
                    var quantity = Integer(entry.Token["quantity"]);
                    var singleton = entry.Type.CategoryId is 6 or 9;
                    var itemCount = singleton ? quantity : 1;
                    var stackQuantity = singleton ? 1 : quantity;
                    var tokenItems = new JsonArray();
                    for (var index = 0L; index < itemCount; index++)
                    {
                        var itemId = await items.CreateAsync(
                            connection, transaction,
                            new NewItem(entry.Type.TypeId, characterId, stationId, Eve.Inventory.ItemFlags.Hangar,
                                stackQuantity, singleton),
                            cancellationToken);
                        newCreated.Add((itemId, entry.Type.TypeId, stackQuantity));
                        tokenItems.Add(new JsonObject
                        {
                            ["itemID"] = itemId,
                            ["typeID"] = entry.Type.TypeId,
                            ["quantity"] = stackQuantity,
                        });
                    }
                    claimed[entry.TokenId.ToString(System.Globalization.CultureInfo.InvariantCulture)] = new JsonObject
                    {
                        ["stationID"] = stationId,
                        ["itemsCreated"] = tokenItems,
                        ["claimedAt"] = ToFileTime(nowUnixMs()),
                    };
                    accountTokens.Remove(entry.TokenId.ToString(System.Globalization.CultureInfo.InvariantCulture));
                }
                accountTokens["claimed"] = claimed;
                accounts[accountKey] = accountTokens;
                await store.ApplyChangesAsync(
                    connection, transaction, RedeemTable,
                    [new RowUpsert(RedeemRow, accounts.ToJsonString())], [], ownerFence, cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            }

            return await RedeemResultAsync(characterId, stationId, [.. replayItems, .. newCreated]);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async ValueTask MutatePendingTokensAsync(
        long authenticatedUserId,
        IReadOnlyList<TokenPair> requested,
        string status,
        CancellationToken cancellationToken)
    {
        RequireAccount(authenticatedUserId);
        if (requested.Count == 0)
            return;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var state = await PersistenceReadHelpers.ReadOptionalTableAsync(store, RedeemTable, cancellationToken);
            var accounts = state[RedeemRow] as JsonObject ?? new JsonObject();
            var accountKey = authenticatedUserId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var accountTokens = accounts[accountKey] as JsonObject ?? new JsonObject();
            var changed = false;
            foreach (var pair in requested.Distinct())
            {
                var key = pair.TokenId.ToString(System.Globalization.CultureInfo.InvariantCulture);
                if (accountTokens[key] is not JsonObject token || String(token["status"]) is not ("" or "pending"))
                    continue;
                token["status"] = status;
                token["updatedAt"] = ToFileTime(nowUnixMs());
                changed = true;
            }
            if (!changed)
                return;
            accounts[accountKey] = accountTokens;
            await using var connection = await source.OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await store.ApplyChangesAsync(
                connection, transaction, RedeemTable,
                [new RowUpsert(RedeemRow, accounts.ToJsonString())], [], ownerFence, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static PyValue ToRedeemToken(JsonObject row, string tokenKey) =>
        Py.Dict(
            ("tokenID", new PyLong(Integer(row["tokenID"], long.TryParse(tokenKey, out var key) ? key : 0))),
            ("massTokenID", Integer(row["massTokenID"]) > 0 ? new PyLong(Integer(row["massTokenID"])) : PyNone.Instance),
            ("typeID", new PyInt(checked((int)Integer(row["typeID"])))),
            ("quantity", new PyLong(Integer(row["quantity"]))),
            ("stationID", Integer(row["stationID"]) > 0 ? new PyLong(Integer(row["stationID"])) : PyNone.Instance),
            ("expireDateTime", Integer(row["expireDateTime"]) > 0 ? new PyLong(Integer(row["expireDateTime"])) : PyNone.Instance),
            ("dateTime", new PyLong(Integer(row["dateTime"]))),
            ("description", new PyStr(String(row["description"]))),
            ("isAutoInject", row["isAutoInject"] is JsonValue autoInject && autoInject.TryGetValue<bool>(out var enabled) && enabled
                ? PyBool.True : PyBool.False));

    private async ValueTask<PyValue> RedeemResultAsync(
        long characterId,
        long stationId,
        IReadOnlyList<(long ItemId, int TypeId, long Quantity)> itemsCreated)
    {
        var wallet = await plexWallets.GetAsync(characterId);
        return Py.Dict(
            ("errors", new PyList([])),
            ("stationID", new PyLong(stationId)),
            ("itemsCreated", new PyList([.. itemsCreated.Select(item => Py.Dict(
                ("itemID", new PyLong(item.ItemId)),
                ("typeID", new PyInt(item.TypeId)),
                ("quantity", new PyLong(item.Quantity))))])),
            ("activated_licenses", new PyList([])),
            ("isk_rewarded", new PyReal(0)),
            ("isk_balance", wallet is null ? PyNone.Instance : new PyReal((double)wallet.Isk)),
            ("skillpoints", new PyInt(0)),
            ("charID", new PyLong(characterId)));
    }

    private static long TokenId(string operationKey, int productIndex)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes($"{operationKey}:{productIndex}"));
        var value = System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(bytes) & long.MaxValue;
        return value == 0 ? 1 : value;
    }

    /// <summary>A positive number a fulfillment must carry: a store offer without it is malformed, so it is refused rather than granted as nothing.</summary>
    private static long RequiredPositive(JsonObject fulfillment, string key) =>
        Integer(fulfillment[key]) is var value and > 0
            ? value
            : throw new InvalidOperationException($"The store fulfillment has no positive {key}.");

    private static long ToFileTime(long unixMilliseconds) =>
        checked(116444736000000000L + unixMilliseconds * 10_000L);

    private static void RequireAccount(long accountId)
    {
        if (accountId <= 0 || accountId > int.MaxValue)
            throw new UnauthorizedAccessException("A valid authenticated account is required.");
    }

    private static PyValue Get(PyDict dict, string key) =>
        dict.Entries.FirstOrDefault(entry => entry.Key is PyStr name && name.Value == key).Value ?? PyNone.Instance;

    private static long Positive(PyValue? value)
{
    var number = value switch
    {
        PyInt integer => (double)integer.Value,
        PyLong integer => integer.Value,
        PyReal real => real.Value,
        _ => double.NaN,
    };
    return double.IsFinite(number) && number > 0 && number == Math.Truncate(number) && number < long.MaxValue
        ? (long)number
        : 0;
}

    private static long PositiveOrZero(PyValue? value)
{
    if (value is PyNone)
        return 0;
    var number = value switch
    {
        PyInt integer => (double)integer.Value,
        PyLong integer => integer.Value,
        PyReal real => real.Value,
        _ => double.NaN,
    };
    return double.IsFinite(number) && number >= 0 && number == Math.Truncate(number) && number < long.MaxValue
        ? (long)number
        : 0;
}

    private static string Text(PyValue? value) => value switch
    {
        var text when Eve.Macho.JsSemantics.StringValue(text) is not null => Eve.Macho.JsSemantics.StringValue(text)!,
        PyInt number => number.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
        PyLong number => number.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
        _ => string.Empty,
    };

    private static long Integer(JsonNode? value, long fallback = 0) =>
        long.TryParse(value?.ToString(), System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var parsed) ? parsed : fallback;

    private static string String(JsonNode? value) =>
        value is JsonValue scalar && scalar.TryGetValue<string>(out var text) ? text : value?.ToString() ?? string.Empty;

    private readonly record struct TokenPair(long TokenId, long MassTokenId);

    private sealed record GrantedItem(long ItemId, int TypeId, long Quantity, int GroupId, int CategoryId);


}
