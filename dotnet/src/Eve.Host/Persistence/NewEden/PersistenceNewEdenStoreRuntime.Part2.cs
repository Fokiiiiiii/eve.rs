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
    public ValueTask<Eve.Services.Core.SeasonalLoginCampaignSnapshot?> GetSeasonalLoginCampaignAsync(
        Eve.Services.Core.LoginRewardPlayer player,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<Eve.Services.Core.SeasonalLoginCampaignSnapshot?>(null);

    public ValueTask<bool> TryClaimSeasonalLoginCampaignAsync(
        Eve.Services.Core.SeasonalLoginCampaignClaimRequest request,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(false);

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
        PyStr text => text.Value,
        PyWString text => text.Value,
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
        PyStr text => JsonValue.Create(text.Value),
        PyWString text => JsonValue.Create(text.Value),
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
