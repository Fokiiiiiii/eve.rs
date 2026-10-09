using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Eve.Services.Core;

namespace Eve.Host;

public static class PersistenceCharacterRpcReadModel
{
    private const long FileTimeEpochOffset = 116_444_736_000_000_000;
    private const long FileTimeTicksPerMillisecond = 10_000;

    public static JumpTimerState? ReadJumpTimers(JsonObject? character, DateTimeOffset? nowUtc = null)
    {
        if (character is null)
            return null;

        var now = nowUtc ?? DateTimeOffset.UtcNow;
        var timers = character["jumpTimers"] as JsonObject;
        var activation = ActiveTimer(FirstTruthy(
            timers?["jumpActivation"],
            timers?["jumpActivationExpireDate"],
            character["jumpActivation"],
            character["jumpActivationExpireDate"]), now);
        var fatigue = ActiveTimer(FirstTruthy(
            timers?["jumpFatigue"],
            timers?["jumpFatigueExpireDate"],
            character["jumpFatigue"],
            character["jumpFatigueExpireDate"]), now);
        if (activation is null && fatigue is null)
            return null;

        var updated = FileTime(FirstTruthy(
            timers?["lastUpdated"],
            timers?["lastUpdateDate"],
            character["jumpTimersLastUpdated"],
            character["lastJumpTimerUpdate"]));
        return new JumpTimerState(
            activation,
            fatigue,
            updated ?? FileTimeNow(now));
    }

    public static int ReadNonDiminishingInjections(JsonObject? table, long characterId)
    {
        if (characterId <= 0 || table?[characterId.ToString(CultureInfo.InvariantCulture)] is not JsonObject state ||
            !long.TryParse(state["nonDiminishingInjectionsRemaining"]?.ToString(),
                NumberStyles.Integer, CultureInfo.InvariantCulture, out var remaining))
            return 0;

        return (int)Math.Clamp(remaining, 0, int.MaxValue);
    }

    private static JsonNode? FirstTruthy(params JsonNode?[] values) => values.FirstOrDefault(IsTruthy);

    private static bool IsTruthy(JsonNode? value)
    {
        if (value is null)
            return false;
        if (value is JsonObject or JsonArray)
            return true;
        return value.GetValueKind() switch
        {
            JsonValueKind.True => true,
            JsonValueKind.Number => double.TryParse(value.ToJsonString(), NumberStyles.Float,
                CultureInfo.InvariantCulture, out var number) && number != 0 && !double.IsNaN(number),
            JsonValueKind.String => value.GetValue<string>().Length > 0,
            _ => false,
        };
    }

    private static long? ActiveTimer(JsonNode? value, DateTimeOffset nowUtc)
    {
        var fileTime = FileTime(value);
        if (fileTime is null || fileTime <= FileTimeEpochOffset)
            return null;
        var milliseconds = (fileTime.Value - FileTimeEpochOffset) / FileTimeTicksPerMillisecond;
        return milliseconds > nowUtc.ToUnixTimeMilliseconds() ? fileTime : null;
    }

    private static long? FileTime(JsonNode? value)
    {
        if (value is JsonObject { } wrapper && wrapper["type"]?.ToString() == "long")
            value = wrapper["value"];
        return long.TryParse(value?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var result)
            ? result
            : null;
    }

    private static long FileTimeNow(DateTimeOffset nowUtc) =>
        nowUtc.ToUnixTimeMilliseconds() * FileTimeTicksPerMillisecond + FileTimeEpochOffset;
}
