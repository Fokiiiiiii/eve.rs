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

public sealed class PersistenceCrimewatchCallbacks(
    Eve.Relations.RelationStore relations,
    Eve.GameStore.PersistenceStore store,
    Eve.GameStore.GameStoreCache cache,
    Eve.Services.CharacterSessions sessions,
    Func<long, CancellationToken, Task<Eve.Relations.Affiliation>> affiliationOf,
    Func<long> nowUnixMs)
    : Eve.Services.Crimewatch.ICrimewatchPersistenceCallbacks
{
    private const string RuntimeTable = "crimewatchRuntime";
    private const long DuelChallengeLifetimeMs = 120_000;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<IReadOnlyList<Eve.Services.Crimewatch.SecurityStatusTransaction>> SecurityStatusTransactionsOfAsync(
        long characterId, CancellationToken cancellationToken)
    {
        if (characterId <= 0)
            return [];

        var rows = await relations.HistoryAsync(
            Eve.Relations.RelationKinds.SecurityStatus.Name,
            characterId,
            Eve.Relations.RelationStore.World,
            int.MaxValue,
            cancellationToken);
        return rows.Select(row =>
        {
            var attrs = row.Attrs;
            var eventType = Int(attrs, "eventTypeID") ??
                Eve.Crimewatch.SecurityStatusEventTypeIds.FromHistoryReason(row.Reason);
            return new Eve.Services.Crimewatch.SecurityStatusTransaction(
                eventType,
                row.AtMs,
                Long(attrs, "otherOwnerID"),
                Int(attrs, "otherTypeID"),
                Long(attrs, "locationID"),
                Double(attrs, "modification"),
                row.NewValue);
        }).ToArray();
    }

    public async Task<long?> StartDuelChallengeAsync(
        long challengerCharacterId,
        long targetCharacterId,
        long challengerCorporationId,
        long challengerAllianceId,
        CancellationToken cancellationToken)
    {
        if (challengerCharacterId <= 0 || targetCharacterId <= 0 ||
            challengerCharacterId == targetCharacterId ||
            challengerCorporationId <= 0 || challengerAllianceId < 0)
            return null;

        var challenger = sessions.Of(challengerCharacterId);
        var target = sessions.Of(targetCharacterId);
        if (challenger?.CharacterId != challengerCharacterId || target?.CharacterId != targetCharacterId)
            return null;

        var affiliation = await affiliationOf(challengerCharacterId, cancellationToken);
        if ((affiliation.Corporation ?? 0) != challengerCorporationId ||
            (affiliation.Alliance ?? 0) != challengerAllianceId ||
            (challenger.GetInt("corpid") ?? 0) != challengerCorporationId ||
            (challenger.GetInt("allianceid") ?? 0) != challengerAllianceId)
            return null;

        var issuedAt = nowUnixMs();
        var expiresAtUnixMs = checked(issuedAt + DuelChallengeLifetimeMs);
        var expiresAtFileTime = Py.FileTimeFromUnixMs(expiresAtUnixMs);
        var key = ChallengeKey(challengerCharacterId, targetCharacterId);
        var created = await MutateRootAsync(root =>
        {
            if (HasActiveEngagement(root, challengerCharacterId, targetCharacterId, issuedAt))
                return (false, false);

            root[key] = new JsonObject
            {
                ["kind"] = "duelChallenge",
                ["challengerCharacterID"] = challengerCharacterId,
                ["targetCharacterID"] = targetCharacterId,
                ["challengerCorporationID"] = challengerCorporationId,
                ["challengerAllianceID"] = challengerAllianceId,
                ["expiresAtFileTime"] = expiresAtFileTime,
                ["expiresAtMs"] = expiresAtUnixMs,
            };
            return (true, true);
        }, cancellationToken);

        if (!created)
            return null;

        target.SendNotification(
            "OnDuelChallenge",
            "charid",
            new PyLong(challengerCharacterId),
            new PyLong(challengerCorporationId),
            new PyLong(challengerAllianceId),
            new PyLong(expiresAtFileTime));
        return expiresAtFileTime;
    }

    public async Task<IReadOnlyList<Eve.Services.Crimewatch.LimitedEngagement>> LimitedEngagementsAsync(
        long nowUnixMs, long? characterId, CancellationToken cancellationToken)
    {
        var root = await store.LoadTableAsync(RuntimeTable, createIfMissing: false, cancellationToken);
        var result = new List<Eve.Services.Crimewatch.LimitedEngagement>();
        foreach (var (_, value) in root)
        {
            if (value is not JsonObject row || Text(row["kind"]) != "limitedEngagement")
                continue;

            var first = Number(row["firstCharacterID"]);
            var second = Number(row["secondCharacterID"]);
            var expiresAt = Number((row["engagement"] as JsonObject)?["expiresAtMs"]);
            if (expiresAt <= nowUnixMs ||
                characterId is { } character && first != character && second != character)
                continue;

            result.Add(new Eve.Services.Crimewatch.LimitedEngagement(first, second, expiresAt));
        }
        return result.OrderBy(row => row.FirstCharacterID).ThenBy(row => row.SecondCharacterID).ToArray();
    }

    public Task UpsertLimitedEngagementAsync(
        long firstCharacterId, long secondCharacterId, long expiresAtUnixMs,
        CancellationToken cancellationToken)
    {
        if (firstCharacterId <= 0 || secondCharacterId <= 0 ||
            firstCharacterId == secondCharacterId || expiresAtUnixMs <= nowUnixMs())
            throw new ArgumentOutOfRangeException(nameof(expiresAtUnixMs));

        var first = Math.Min(firstCharacterId, secondCharacterId);
        var second = Math.Max(firstCharacterId, secondCharacterId);
        return MutateRootAsync(root =>
        {
            root[EngagementKey(first, second)] = EngagementRow(first, second, expiresAtUnixMs);
            return (true, true);
        }, cancellationToken);
    }

    public Task<bool> ExpireLimitedEngagementAsync(
        long firstCharacterId,
        long secondCharacterId,
        long expectedExpiresAtUnixMs,
        long nowUnixMs,
        CancellationToken cancellationToken)
    {
        var first = Math.Min(firstCharacterId, secondCharacterId);
        var second = Math.Max(firstCharacterId, secondCharacterId);
        return MutateRootAsync(root =>
        {
            var key = EngagementKey(first, second);
            if (root[key] is not JsonObject row ||
                Text(row["kind"]) != "limitedEngagement" ||
                Number((row["engagement"] as JsonObject)?["expiresAtMs"]) != expectedExpiresAtUnixMs ||
                expectedExpiresAtUnixMs > nowUnixMs)
                return (false, false);

            root.Remove(key);
            return (true, true);
        }, cancellationToken);
    }

    public Task<bool> RespondToDuelChallengeAsync(
        long responderCharacterId,
        long challengerCharacterId,
        long challengeExpiresAtFileTime,
        bool accept,
        long? engagementExpiresAtUnixMs,
        CancellationToken cancellationToken)
    {
        if (responderCharacterId <= 0 || challengerCharacterId <= 0 ||
            responderCharacterId == challengerCharacterId || challengeExpiresAtFileTime <= 0)
            return Task.FromResult(false);

        var now = nowUnixMs();
        var nowFileTime = Py.FileTimeFromUnixMs(now);
        if (accept && (engagementExpiresAtUnixMs is not { } due || due <= now))
            return Task.FromResult(false);

        return MutateRootAsync(root =>
        {
            var key = ChallengeKey(challengerCharacterId, responderCharacterId);
            if (root[key] is not JsonObject challenge ||
                Text(challenge["kind"]) != "duelChallenge" ||
                Number(challenge["challengerCharacterID"]) != challengerCharacterId ||
                Number(challenge["targetCharacterID"]) != responderCharacterId ||
                Number(challenge["expiresAtFileTime"]) != challengeExpiresAtFileTime)
                return (false, false);

            root.Remove(key);
            if (challengeExpiresAtFileTime <= nowFileTime)
                return (true, false);
            if (!accept)
                return (true, false);

            var expiresAt = engagementExpiresAtUnixMs!.Value;
            var first = Math.Min(responderCharacterId, challengerCharacterId);
            var second = Math.Max(responderCharacterId, challengerCharacterId);
            root[EngagementKey(first, second)] = EngagementRow(first, second, expiresAt);
            return (true, true);
        }, cancellationToken);
    }

    private async Task<T> MutateRootAsync<T>(
        Func<JsonObject, (bool Changed, T Result)> mutation,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var persisted = await store.LoadTableAsync(RuntimeTable, createIfMissing: true, cancellationToken);
            var next = (JsonObject)persisted.DeepClone();
            var (changed, result) = mutation(next);
            if (!changed)
                return result;

            var write = cache.Write(RuntimeTable, "/", next);
            if (!write.Success)
                throw new InvalidOperationException(
                    $"Could not update {RuntimeTable}: {write.ErrorMsg ?? "GameStore rejected the write."}");

            await FlushUntilVisibleAsync(next, cancellationToken);
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task FlushUntilVisibleAsync(JsonObject expected, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 500; attempt++)
        {
            await cache.FlushTableAsync(RuntimeTable, cancellationToken);
            var actual = await store.LoadTableAsync(RuntimeTable, createIfMissing: false, cancellationToken);
            if (JsonNode.DeepEquals(actual, expected))
                return;
            await Task.Delay(10, cancellationToken);
        }

        throw new TimeoutException($"The {RuntimeTable} GameStore write did not become durable.");
    }

    private bool HasActiveEngagement(JsonObject root, long firstCharacterId, long secondCharacterId, long now)
    {
        var key = EngagementKey(
            Math.Min(firstCharacterId, secondCharacterId),
            Math.Max(firstCharacterId, secondCharacterId));
        return root[key] is JsonObject row &&
            Text(row["kind"]) == "limitedEngagement" &&
            Number((row["engagement"] as JsonObject)?["expiresAtMs"]) > now;
    }

    private static JsonObject EngagementRow(long first, long second, long expiresAtUnixMs) => new()
    {
        ["kind"] = "limitedEngagement",
        ["firstCharacterID"] = first,
        ["secondCharacterID"] = second,
        ["expiresAtMs"] = expiresAtUnixMs,
        ["engagement"] = new JsonObject { ["expiresAtMs"] = expiresAtUnixMs },
    };

    private static string EngagementKey(long first, long second) => $"le:{first}:{second}";
    private static string ChallengeKey(long challenger, long target) => $"duel:{challenger}:{target}";

    private static long Number(JsonNode? value) =>
        long.TryParse(value?.ToString(), System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var number) ? number : 0;

    private static string Text(JsonNode? value) =>
        value is JsonValue scalar && scalar.TryGetValue<string>(out var text) ? text : string.Empty;

    private static System.Text.Json.JsonElement? Attribute(System.Text.Json.JsonElement? attrs, string name)
    {
        if (attrs is { } document &&
            document.ValueKind == System.Text.Json.JsonValueKind.Object &&
            document.TryGetProperty(name, out var value))
            return value;
        return null;
    }

    private static int? Int(System.Text.Json.JsonElement? attrs, string name) =>
        Attribute(attrs, name) is { } value && value.TryGetInt32(out var number) ? number : null;

    private static long? Long(System.Text.Json.JsonElement? attrs, string name) =>
        Attribute(attrs, name) is { } value && value.TryGetInt64(out var number) ? number : null;

    private static double? Double(System.Text.Json.JsonElement? attrs, string name) =>
        Attribute(attrs, name) is { } value && value.TryGetDouble(out var number) ? number : null;
}
