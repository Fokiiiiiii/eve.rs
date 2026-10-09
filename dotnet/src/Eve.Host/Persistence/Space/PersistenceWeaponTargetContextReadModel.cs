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

public sealed class PersistenceWeaponTargetContextReadModel(
    Func<long, CancellationToken, Task<Eve.Relations.Affiliation>> affiliationOf,
    Func<long, long, long, CancellationToken, Task<bool>> activeWar,
    Func<long, CancellationToken, Task<Eve.Crimewatch.CombatState>> crimewatchStateOf,
    Func<long, CancellationToken, Task<double>> securityStatusOf,
    Func<string, CancellationToken, Task<JsonObject>> readTable,
    Func<long> nowUnixMs)
{
    public async Task<Eve.Services.Space.WeaponTargetContext> ReadAsync(
        long attackerCharacterId,
        long targetCharacterId,
        CancellationToken cancellationToken)
    {
        if (attackerCharacterId <= 0 || targetCharacterId <= 0)
            return default;

        var now = nowUnixMs();
        var attackerAffiliationTask = affiliationOf(attackerCharacterId, cancellationToken);
        var targetAffiliationTask = affiliationOf(targetCharacterId, cancellationToken);
        var targetStateTask = crimewatchStateOf(targetCharacterId, cancellationToken);
        var targetSecurityTask = securityStatusOf(targetCharacterId, cancellationToken);
        var crimewatchRowsTask = readTable("crimewatchRuntime", cancellationToken);
        var corporationRowsTask = readTable("corporationRuntime", cancellationToken);
        await Task.WhenAll(attackerAffiliationTask, targetAffiliationTask, targetStateTask, targetSecurityTask, crimewatchRowsTask, corporationRowsTask);

        var attackerAffiliation = await attackerAffiliationTask;
        var targetAffiliation = await targetAffiliationTask;
        var targetState = await targetStateTask;
        var targetSecurity = await targetSecurityTask;
        var activeWarState = await activeWar(attackerCharacterId, targetCharacterId, now, cancellationToken);
        var activeLimitedEngagement = HasActiveLimitedEngagement(
            await crimewatchRowsTask, attackerCharacterId, targetCharacterId, now);
        var legalFriendlyFire = attackerAffiliation.Corporation is { } corporationId &&
            targetAffiliation.Corporation == corporationId &&
            FriendlyFireIsEnabled(await corporationRowsTask, corporationId, now);

        return new Eve.Services.Space.WeaponTargetContext(
            AttackerIsPlayer: true,
            TargetIsPlayerProperty: true,
            SamePilot: attackerCharacterId == targetCharacterId,
            TargetIsCriminal: targetState.IsCriminal(now),
            TargetIsSuspect: targetState.IsSuspect(now),
            TargetIsOutlaw: targetSecurity <= -5.0,
            ActiveWar: activeWarState,
            ActiveLimitedEngagement: activeLimitedEngagement,
            LegalFriendlyFire: legalFriendlyFire);
    }

    private static bool HasActiveLimitedEngagement(
        JsonObject state,
        long firstCharacterId,
        long secondCharacterId,
        long nowUnixMs)
    {
        var low = Math.Min(firstCharacterId, secondCharacterId);
        var high = Math.Max(firstCharacterId, secondCharacterId);
        foreach (var node in state)
        {
            if (node.Value is not JsonObject row ||
                !string.Equals(Text(row["kind"]), "limitedEngagement", StringComparison.Ordinal))
                continue;
            var first = Integer(row["firstCharacterID"]);
            var second = Integer(row["secondCharacterID"]);
            if (Math.Min(first, second) != low || Math.Max(first, second) != high)
                continue;
            var engagement = row["engagement"] as JsonObject;
            var expiresAtMs = Integer(engagement?["expiresAtMs"]);
            if (expiresAtMs > nowUnixMs)
                return true;
        }
        return false;
    }

    private static bool FriendlyFireIsEnabled(JsonObject state, long corporationId, long nowUnixMs)
    {
        var corporations = state["corporations"] as JsonObject;
        if (corporations?[corporationId.ToString(System.Globalization.CultureInfo.InvariantCulture)] is not JsonObject corporation)
            return false;
        if (corporation["aggressionSettings"] is not JsonObject settings)
            return Integer(corporation["friendlyFire"]) == 1;

        var nowFileTime = checked((nowUnixMs + 11_644_473_600_000L) * 10_000L);
        var enableAfter = NullableInteger(settings["enableAfter"]);
        var disableAfter = NullableInteger(settings["disableAfter"]);
        var enablePast = enableAfter is { } enable && enable <= nowFileTime;
        var disablePast = disableAfter is { } disable && disable <= nowFileTime;
        if (disablePast && enablePast)
            return disableAfter < enableAfter;
        if (disablePast)
            return false;
        if (enablePast)
            return true;
        return true;
    }

    private static long? NullableInteger(JsonNode? value) =>
        long.TryParse(value?.ToString(), System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var number) ? number : null;

    private static long Integer(JsonNode? value) =>
        long.TryParse(value?.ToString(), System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var number) ? number : 0;

    private static string Text(JsonNode? value) =>
        value is JsonValue scalar && scalar.TryGetValue<string>(out var text) ? text : "";
}
