using System.Globalization;
using System.Text.Json.Nodes;
using Eve.Services.Core;

namespace Eve.Host;

public static class PersistencePeaceTreatyReadModel
{
    private readonly record struct Treaty(long Id, long OwnerId, long OtherOwnerId, PeaceTreaty Row);

    public static PeaceTreatySnapshot Read(JsonObject state, long ownerId, long nowFiletime)
    {
        if (ownerId <= 0 || state["peaceTreaties"] is not JsonObject records)
            return new PeaceTreatySnapshot([], []);

        var active = new List<Treaty>();
        foreach (var (key, node) in records)
        {
            if (node is not JsonObject record)
                continue;
            var expiry = Integer(record["expiryDate"]);
            if (expiry <= nowFiletime)
                continue;

            var treatyId = Integer(record["treatyID"], Integer(JsonValue.Create(key)));
            var owner = Integer(record["ownerID"]);
            var otherOwner = Integer(record["otherOwnerID"]);
            var warHq = Integer(record["warHQ"]);
            if (warHq == 0)
                warHq = Integer(record["warHQID"]);
            active.Add(new Treaty(treatyId, owner, otherOwner, new PeaceTreaty(
                otherOwner,
                expiry,
                SmallInteger(record["peaceReason"]),
                Integer(record["warID"]),
                SmallInteger(record["reasonEnded"]),
                warHq == 0 ? null : warHq)));
        }

        return new PeaceTreatySnapshot(
            [.. active.Where(treaty => treaty.OwnerId == ownerId)
                .OrderByDescending(treaty => treaty.Id)
                .Select(treaty => treaty.Row)],
            [.. active.Where(treaty => treaty.OtherOwnerId == ownerId)
                .OrderByDescending(treaty => treaty.Id)
                .Select(treaty => treaty.Row)]);
    }

    private static int SmallInteger(JsonNode? value) =>
        (int)Math.Clamp(Integer(value), int.MinValue, int.MaxValue);

    private static long Integer(JsonNode? value, long fallback = 0)
    {
        if (value is JsonObject wrapper)
            value = wrapper["$long"] ?? wrapper["$bigint"];
        if (value is not JsonValue scalar)
            return fallback;
        if (scalar.TryGetValue<long>(out var number))
            return number;
        if (scalar.TryGetValue<int>(out var small))
            return small;
        if (scalar.TryGetValue<string>(out var text) &&
            long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out number))
            return number;
        if (scalar.TryGetValue<decimal>(out var precise) && decimal.Truncate(precise) == precise &&
            precise >= long.MinValue && precise <= long.MaxValue)
            return (long)precise;
        return fallback;
    }
}
