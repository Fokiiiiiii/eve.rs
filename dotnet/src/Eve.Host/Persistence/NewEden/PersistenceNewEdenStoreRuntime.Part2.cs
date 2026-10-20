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

public sealed partial class PersistenceNewEdenStoreRuntime
{
    private const string SeasonalCampaignTable = "seasonalLoginCampaignProgress";
    private const string SeasonalCampaignRow = "accountsByUserID";

    private sealed record SeasonalCampaignConfig(
        int CampaignId, DateOnly StartDate, DateOnly EndDate, TimeOnly TurnoverTimeUtc, bool RetroClaimEnabled,
        IReadOnlyDictionary<int, Eve.Services.Core.SeasonalLoginCampaignReward> AlphaRewards,
        IReadOnlyDictionary<int, Eve.Services.Core.SeasonalLoginCampaignReward> OmegaRewards);

    private SeasonalCampaignConfig? ReadSeasonalCampaignConfig()
    {
        var raw = Environment.GetEnvironmentVariable("EVE_SEASONAL_LOGIN_CAMPAIGN");
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var json = JsonNode.Parse(raw) as JsonObject ?? throw new InvalidOperationException("EVE_SEASONAL_LOGIN_CAMPAIGN must be a JSON object.");
        var campaignId = Integer(json["campaignId"]);
        if (campaignId is <= 0 or > int.MaxValue) throw new InvalidOperationException("The seasonal campaign ID must be positive.");
        if (!DateOnly.TryParse(String(json["startDate"]), System.Globalization.CultureInfo.InvariantCulture, out var startDate) ||
            !DateOnly.TryParse(String(json["endDate"]), System.Globalization.CultureInfo.InvariantCulture, out var endDate) || endDate < startDate)
            throw new InvalidOperationException("The seasonal campaign requires an ordered startDate and endDate.");
        if (!TimeOnly.TryParse(String(json["turnoverTimeUtc"]), System.Globalization.CultureInfo.InvariantCulture, out var turnover))
            throw new InvalidOperationException("The seasonal campaign requires turnoverTimeUtc in HH:mm:ss format.");
        static IReadOnlyDictionary<int, Eve.Services.Core.SeasonalLoginCampaignReward> Rewards(JsonNode? node, string name)
        {
            if (node is not JsonObject values || values.Count == 0) throw new InvalidOperationException($"The seasonal campaign requires {name} rewards.");
            var result = new Dictionary<int, Eve.Services.Core.SeasonalLoginCampaignReward>();
            foreach (var pair in values)
            {
                if (!int.TryParse(pair.Key, out var day) || day < 0 || pair.Value is not JsonObject reward ||
                    Integer(reward["typeId"]) is not (> 0 and <= int.MaxValue) || Integer(reward["quantity"]) is not > 0)
                    throw new InvalidOperationException($"The seasonal campaign {name} reward at index {pair.Key} is invalid.");
                result.Add(day, new Eve.Services.Core.SeasonalLoginCampaignReward((int)Integer(reward["typeId"]), Integer(reward["quantity"])));
            }
            return result;
        }
        return new SeasonalCampaignConfig((int)campaignId, startDate, endDate, turnover,
            json["retroClaimEnabled"]?.GetValue<bool>() ?? false,
            Rewards(json["alphaRewards"], "alpha"), Rewards(json["omegaRewards"], "omega"));
    }

    public async ValueTask<Eve.Services.Core.SeasonalLoginCampaignSnapshot?> GetSeasonalLoginCampaignAsync(
        Eve.Services.Core.LoginRewardPlayer player,
        CancellationToken cancellationToken = default)
    {
        var config = ReadSeasonalCampaignConfig();
        var now = DateTimeOffset.FromUnixTimeMilliseconds(nowUnixMs());
        var rewardDate = DateOnly.FromDateTime(now.UtcDateTime - (config?.TurnoverTimeUtc ?? TimeOnly.MinValue).ToTimeSpan());
        if (config is null || player.UserId <= 0 || rewardDate < config.StartDate || rewardDate > config.EndDate)
            return null;
        var accountKey = player.UserId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var table = await PersistenceReadHelpers.ReadOptionalTableAsync(store, SeasonalCampaignTable, cancellationToken);
        var accounts = table[SeasonalCampaignRow] as JsonObject ?? new JsonObject();
        var account = accounts[accountKey] as JsonObject;
        var historyJson = account?["history"] as JsonObject ?? new JsonObject();
        var history = historyJson.Select(pair => (int.TryParse(pair.Key, out var ordinal) ? ordinal : 0, Integer(pair.Value)))
            .Where(pair => pair.Item1 > 0).Select(pair => new Eve.Services.Core.SeasonalLoginCampaignClaimedDay(pair.Item1, checked((int)pair.Item2), pair.Item2 == 1)).ToArray();
        var expiry = player.UserId <= uint.MaxValue ? await accountDirectory.SubscriptionEndsAtAsync((uint)player.UserId, cancellationToken) : null;
        var omega = expiry > DateTimeOffset.FromUnixTimeMilliseconds(nowUnixMs());
        return new Eve.Services.Core.SeasonalLoginCampaignSnapshot(config.CampaignId, config.EndDate, config.TurnoverTimeUtc,
            Math.Max(0, Math.Max(config.AlphaRewards.Count, config.OmegaRewards.Count) - history.Length), omega,
            config.RetroClaimEnabled, config.AlphaRewards, config.OmegaRewards, history);
    }

    public async ValueTask<bool> TryClaimSeasonalLoginCampaignAsync(
        Eve.Services.Core.SeasonalLoginCampaignClaimRequest request,
        CancellationToken cancellationToken = default)
    {
        var config = ReadSeasonalCampaignConfig();
        if (config is null || request.CampaignId != config.CampaignId || request.Player.CharacterId is null ||
            !await HasLoginRewardCharacterAsync(request.Player, cancellationToken)) return false;
        var now = DateTimeOffset.FromUnixTimeMilliseconds(nowUnixMs());
        var today = DateOnly.FromDateTime(now.UtcDateTime - config.TurnoverTimeUtc.ToTimeSpan());
        var ordinal = today.DayNumber + 1;
        if (today < config.StartDate || today > config.EndDate || request.ClaimDateOrdinal != ordinal || request.Rewards.Count == 0) return false;
        var subscriptionEndsAt = request.Player.UserId <= uint.MaxValue
            ? await accountDirectory.SubscriptionEndsAtAsync((uint)request.Player.UserId, cancellationToken)
            : null;
        var isOmega = subscriptionEndsAt > now;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var accountsTable = await PersistenceReadHelpers.ReadOptionalTableAsync(store, SeasonalCampaignTable, cancellationToken);
            var accounts = accountsTable[SeasonalCampaignRow] as JsonObject ?? new JsonObject();
            var accountKey = request.Player.UserId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var account = accounts[accountKey] as JsonObject ?? new JsonObject();
            if (Integer(account["campaignId"]) != config.CampaignId) account = new JsonObject { ["campaignId"] = config.CampaignId, ["history"] = new JsonObject() };
            var history = account["history"] as JsonObject ?? new JsonObject();
            var todayKey = ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var hasToday = history.TryGetPropertyValue(todayKey, out var todayValue);
            var todayGrade = Integer(todayValue);
            if (hasToday && (todayGrade != 0 || !isOmega || !config.RetroClaimEnabled)) return false;
            var priorClaims = history
                .Select(pair => (Key: pair.Key, Ordinal: int.TryParse(pair.Key, out var date) ? date : 0, Grade: Integer(pair.Value)))
                .Where(claim => claim.Ordinal > 0 && claim.Ordinal < ordinal)
                .OrderBy(claim => claim.Ordinal)
                .ToArray();
            var latest = history.Select(pair => int.TryParse(pair.Key, out var date) ? date : 0).DefaultIfEmpty(0).Max();
            if (ordinal < latest) return false;

            var dayIndex = priorClaims.Length;
            var expected = new List<Eve.Services.Core.SeasonalRewardGrant>();
            void AddExpected(IReadOnlyDictionary<int, Eve.Services.Core.SeasonalLoginCampaignReward> catalog,
                Eve.Services.Core.SeasonalLoginRewardTrack track, int index, bool retroactive)
            {
                if (catalog.TryGetValue(index, out var reward))
                    expected.Add(new Eve.Services.Core.SeasonalRewardGrant(index, track, retroactive, reward.TypeId, reward.Quantity));
            }
            if (!hasToday)
                AddExpected(config.AlphaRewards, Eve.Services.Core.SeasonalLoginRewardTrack.Alpha, dayIndex, retroactive: false);
            if (isOmega)
                AddExpected(config.OmegaRewards, Eve.Services.Core.SeasonalLoginRewardTrack.Omega, dayIndex, retroactive: false);
            if (isOmega && config.RetroClaimEnabled)
            {
                for (var index = 0; index < priorClaims.Length; index++)
                {
                    if (priorClaims[index].Grade == 0)
                        AddExpected(config.OmegaRewards, Eve.Services.Core.SeasonalLoginRewardTrack.Omega, index, retroactive: true);
                }
            }
            if (expected.Count == 0 || !request.Rewards.SequenceEqual(expected)) return false;
            foreach (var reward in expected)
            {
                if (reward.TypeId <= 0 || reward.Quantity <= 0 || await itemTypes.GetAsync(reward.TypeId, cancellationToken) is null)
                    return false;
            }

            var redeemTable = await PersistenceReadHelpers.ReadOptionalTableAsync(store, RedeemTable, cancellationToken);
            var redeemAccounts = redeemTable[RedeemRow] as JsonObject ?? new JsonObject();
            var tokens = redeemAccounts[accountKey] as JsonObject ?? new JsonObject();
            var newTokens = new JsonObject();
            for (var i = 0; i < expected.Count; i++)
            {
                var reward = expected[i];
                var claimKey = $"seasonal-login:{request.Player.UserId}:{config.CampaignId}:{ordinal}:{reward.Track}:{reward.DayIndex}:{reward.Retroactive}";
                var tokenId = TokenId(claimKey, 0);
                var tokenKey = tokenId.ToString(System.Globalization.CultureInfo.InvariantCulture);
                if (tokens.ContainsKey(tokenKey)) return false;
                newTokens[tokenKey] = new JsonObject
                {
                    ["tokenID"] = tokenId, ["massTokenID"] = null, ["typeID"] = reward.TypeId, ["quantity"] = reward.Quantity,
                    ["stationID"] = 0, ["dateTime"] = Py.FileTimeFromUnixMs(now.ToUnixTimeMilliseconds()).ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["expireDateTime"] = "0", ["label"] = $"Seasonal Login: Day {reward.DayIndex + 1} ({reward.Track})",
                    ["description"] = "Seasonal login campaign reward", ["available"] = true, ["addedByContext"] = 0, ["addedByExtra"] = null,
                };
            }
            foreach (var pair in newTokens) tokens[pair.Key] = pair.Value?.DeepClone();
            if (isOmega && config.RetroClaimEnabled)
                foreach (var prior in priorClaims.Where(claim => claim.Grade == 0)) history[prior.Key] = 1;
            history[ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture)] = isOmega ? 1 : 0;
            account["history"] = history;
            accounts[accountKey] = account;
            redeemAccounts[accountKey] = tokens;
            await using var connection = await source.OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await using (var lockCommand = new NpgsqlCommand("SELECT pg_advisory_xact_lock(882701, 1)", connection, transaction))
                await lockCommand.ExecuteNonQueryAsync(cancellationToken);
            await store.ApplyChangesAsync(connection, transaction, SeasonalCampaignTable,
                [new RowUpsert(SeasonalCampaignRow, accounts.ToJsonString())], [], ownerFence, cancellationToken);
            await store.ApplyChangesAsync(connection, transaction, RedeemTable,
                [new RowUpsert(RedeemRow, redeemAccounts.ToJsonString())], [], ownerFence, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            sessions.Of(request.Player.CharacterId.Value)?.SendNotification("OnRedeemingQueueUpdated", "clientID");
            return true;
        }
        finally { _gate.Release(); }
    }

    private static (int Day, string LastClaimReset) LoginCampaignProgress(JsonObject? state)
    {
        var storedDay = Integer(state?["day"]);
        var day = storedDay is >= 0 and <= DailyLoginTrackLength ? (int)storedDay : 0;
        return (day, String(state?["lastClaimReset"]));
    }

    private static DateTimeOffset LoginCampaignResetStart(DateTimeOffset value)
    {
        var utc = value.ToUniversalTime();
        var reset = new DateTimeOffset(
            utc.Year, utc.Month, utc.Day, DailyLoginResetHourUtc, 0, 0, TimeSpan.Zero);
        return utc < reset ? reset.AddDays(-1) : reset;
    }

    private static string LoginCampaignResetId(DateTimeOffset value) =>
        value.ToUniversalTime().AddHours(-DailyLoginResetHourUtc)
            .ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    private static PyValue LoginCampaignClientState(
        int day,
        string lastClaimReset,
        DateTimeOffset now) =>
        LoginCampaignKeyVal(
            ("static_data", LoginCampaignStaticData()),
            ("bucket_progress", LoginCampaignBucketProgress(day)),
            ("item_progress", LoginCampaignItemProgress(day, lastClaimReset, now)));

    private static PyValue LoginCampaignStaticData()
    {
        var rewards = new List<KeyValuePair<PyValue, PyValue>>(DailyLoginTrackLength);
        for (var day = 1; day <= DailyLoginTrackLength; day++)
            rewards.Add(new KeyValuePair<PyValue, PyValue>(new PyInt(day), LoginCampaignReward(day)));
        return LoginCampaignKeyVal(
            ("campaign_duration", new PyInt(DailyLoginTrackLength)),
            ("subtitleMessageID", new PyInt(598039)),
            ("campaign_id", new PyInt(DailyLoginCampaignId)),
            ("titleMessageID", new PyInt(598040)),
            ("is_rookie_campaign", PyBool.True),
            ("windowHeaderBackgroundNarrow", new PyStr("res:/UI/Texture/classes/LoginCampaign/backgrounds/dliBackground.png")),
            ("windowHeaderBackgroundWide", new PyStr("res:/UI/Texture/classes/LoginCampaign/backgrounds/dliBackground.png")),
            ("item_rewards_by_day", new PyDict(rewards)));
    }

    private static PyValue LoginCampaignReward(int day) =>
        LoginCampaignKeyVal(
            ("typeID", new PyInt(DailyLoginRewardTypeId)),
            ("blueprintProductivityLevel", PyNone.Instance),
            ("blueprintMaterialLevel", PyNone.Instance),
            ("labelMessageID", new PyInt(598041 + day - 1)),
            ("tier", new PyInt(day == DailyLoginTrackLength ? 4 : 1)),
            ("quantity", new PyInt(1)),
            ("icon", PyNone.Instance));

    private static PyValue LoginCampaignBucketProgress(int day) =>
        LoginCampaignKeyVal(
            ("bucketID", new PyInt(DailyLoginBucketId)),
            ("fill_level", new PyReal((double)day / DailyLoginTrackLength)),
            ("next_fill_level", new PyReal((double)Math.Min(day + 1, DailyLoginTrackLength) / DailyLoginTrackLength)));

    private static PyValue LoginCampaignItemProgress(int day, string lastClaimReset, DateTimeOffset now)
    {
        var resetStart = LoginCampaignResetStart(now);
        var claimedToday = lastClaimReset == LoginCampaignResetId(now);
        var nextRewardTime = claimedToday ? resetStart.AddDays(1) : resetStart;
        var nextRewardIndex = day >= DailyLoginTrackLength ? 1 : day + 1;
        return LoginCampaignKeyVal(
            ("next_reward_index", new PyInt(nextRewardIndex)),
            ("num_claimed_rewards", new PyInt(day)),
            ("next_reward_timestamp", new PyLong(Py.FileTimeFromUnixMs(nextRewardTime.ToUnixTimeMilliseconds()))));
    }

    private static PyValue LoginCampaignKeyVal(params (string Name, PyValue Value)[] entries) =>
        Eve.Services.Py.KeyVal(entries.Select(entry =>
            new KeyValuePair<PyValue, PyValue>(new PyStr(entry.Name), entry.Value)));

    private static long MaximumRedeemTokenId(JsonObject tokens)
    {
        var maximum = 0L;
        foreach (var (key, value) in tokens)
        {
            if (long.TryParse(key, System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out var tokenKey))
                maximum = Math.Max(maximum, tokenKey);
            if (value is JsonObject token)
            {
                maximum = Math.Max(maximum, Integer(token["tokenID"]));
                maximum = Math.Max(maximum, Integer(token["massTokenID"]));
            }
            if (key == "claimed" && value is JsonObject claimed)
            {
                foreach (var (claimedKey, claimedValue) in claimed)
                {
                    if (long.TryParse(claimedKey, System.Globalization.NumberStyles.None,
                            System.Globalization.CultureInfo.InvariantCulture, out var claimedId))
                        maximum = Math.Max(maximum, claimedId);
                    if (claimedValue is JsonObject claimedToken)
                    {
                        maximum = Math.Max(maximum, Integer(claimedToken["tokenID"]));
                        maximum = Math.Max(maximum, Integer(claimedToken["massTokenID"]));
                    }
                }
            }
        }
        return maximum;
    }

    private static long RedeemQaPositiveInteger(PyValue? value)
    {
        var number = value is null ? double.NaN : Eve.Macho.JsSemantics.ToNumber(value);
        return double.IsFinite(number) && number > 0 && number == Math.Truncate(number) &&
            number < 9_223_372_036_854_775_808d ? (long)number : 0;
    }

    private static long RedeemQaInteger(PyValue? value)
    {
        var number = value is null ? 0 : Eve.Macho.JsSemantics.ToNumber(value);
        return double.IsFinite(number) ? RedeemQaBoundedInteger(Math.Truncate(number)) : 0;
    }

    private static long RedeemQaBoundedInteger(double number) =>
        number >= 9_223_372_036_854_775_808d ? long.MaxValue :
        number <= -9_223_372_036_854_775_808d ? long.MinValue : (long)number;

    private static long RedeemQaFileTime(PyValue? value)
    {
        if (value is null or PyNone)
            return 0;
        if (RedeemQaField(value, "value") is { } wrapped)
            return RedeemQaFileTime(wrapped);
        if (value is PyInt or PyLong or PyReal)
        {
            var number = Eve.Macho.JsSemantics.ToNumber(value);
            return double.IsFinite(number) && number > 0 ? RedeemQaBoundedInteger(Math.Truncate(number)) : 0;
        }
        var text = RedeemQaText(value).Trim();
        return long.TryParse(text, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
    }

    private static string RedeemQaText(PyValue? value) => value switch
    {
        null or PyNone => string.Empty,
        var text when Eve.Macho.JsSemantics.StringValue(text) is not null => Eve.Macho.JsSemantics.StringValue(text)!,
        PyBytes text => System.Text.Encoding.UTF8.GetString(text.Value.Span),
        PyBuffer buffer => System.Text.Encoding.UTF8.GetString(buffer.Value.Span),
        _ => Eve.Macho.JsSemantics.StringValue(value) ?? string.Empty,
    };

    private static JsonNode? RedeemQaJson(PyValue? value) => value switch
    {
        null or PyNone => null,
        PyBool boolean => JsonValue.Create(boolean == PyBool.True),
        PyInt number => JsonValue.Create(number.Value),
        PyLong number => JsonValue.Create(number.Value),
        PyReal number when double.IsFinite(number.Value) => JsonValue.Create(number.Value),
        PyReal => null,
        var text when Eve.Macho.JsSemantics.StringValue(text) is not null => JsonValue.Create(Eve.Macho.JsSemantics.StringValue(text)!),
        PyToken text => JsonValue.Create(text.Value),
        PyBytes bytes => JsonValue.Create(System.Text.Encoding.UTF8.GetString(bytes.Value.Span)),
        PyBuffer buffer => JsonValue.Create(System.Text.Encoding.UTF8.GetString(buffer.Value.Span)),
        PyList list => new JsonArray(list.Items.Select(RedeemQaJson).ToArray()),
        PyTuple tuple => new JsonArray(tuple.Items.Select(RedeemQaJson).ToArray()),
        PyDict dict => RedeemQaJson(dict.Entries),
        PyObject { Args: PyDict dict } => RedeemQaJson(dict.Entries),
        PyObjectEx { Dict.Count: > 0 } objectEx => RedeemQaJson(objectEx.Dict),
        PyObjectEx { List.Count: > 0 } objectEx => new JsonArray(objectEx.List.Select(RedeemQaJson).ToArray()),
        _ => null,
    };

    private static JsonObject RedeemQaJson(IReadOnlyList<KeyValuePair<PyValue, PyValue>> entries)
    {
        var result = new JsonObject();
        foreach (var (key, value) in entries)
        {
            var name = Eve.Macho.JsSemantics.StringValue(key);
            if (name is not null)
                result[name] = RedeemQaJson(value);
        }
        return result;
    }

    public async ValueTask ReverseRedeemAsync(
        long authenticatedUserId,
        PyValue itemId,
        CancellationToken cancellationToken)
    {
        await MutatePendingTokensAsync(authenticatedUserId, [new TokenPair(Positive(itemId), 0)], "reversed", cancellationToken);
    }

    public ValueTask TrashRedeemTokensAsync(
        long authenticatedUserId,
        PyValue tokens,
        CancellationToken cancellationToken) =>
        MutatePendingTokensAsync(authenticatedUserId, TokenPairs(tokens), "trashed", cancellationToken);

    private static TokenPair[] TokenPairs(PyValue value)
    {
        if (value is not PyList list)
            return [];
        var result = new List<TokenPair>();
        foreach (var item in list.Items)
        {
            if (item is PyTuple tuple && tuple.Items.Count >= 2)
            {
                var tokenId = Positive(tuple.Items[0]);
                var massTokenId = PositiveOrZero(tuple.Items[1]);
                if (tokenId > 0)
                    result.Add(new TokenPair(tokenId, massTokenId));
            }
            else if (item is PyDict dict)
            {
                var tokenId = Positive(Get(dict, "tokenID"));
                var massTokenId = PositiveOrZero(Get(dict, "massTokenID"));
                if (tokenId > 0)
                    result.Add(new TokenPair(tokenId, massTokenId));
            }
            else
            {
                var tokenId = Positive(item);
                if (tokenId > 0)
                    result.Add(new TokenPair(tokenId, 0));
            }
        }
        return [.. result];
    }
}
