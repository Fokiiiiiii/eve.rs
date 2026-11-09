using System.Collections.Concurrent;
using Eve.Crimewatch;
using Eve.FactionWarfare;
using Eve.Marshal;
using Eve.Scheduler;
using Eve.Universe;

namespace Eve.Services.Crimewatch;

/// <summary>Resolves active war relations and their accepted allies for crimewatch.</summary>
public sealed class RelationWars(
    Eve.Relations.RelationStore relations,
    Func<long, CancellationToken, ValueTask<long?>>? resolveWarFactionAsync = null) : Eve.Services.IWars
{
    private enum WarSide { Aggressor, Defender }

    public readonly record struct AssistanceFacts(bool TargetInWar, bool HelperInWar, bool SameSide);
    private sealed record WarAllyAttributes(string? Side);

    public async Task<bool> AreAtWarAsync(long attackerCharacterId, long targetCharacterId, long nowUnixMs, CancellationToken cancellationToken = default)
    {
        if (resolveWarFactionAsync is { } resolveWarFaction)
        {
            if (attackerCharacterId > 0)
                _ = await resolveWarFaction(attackerCharacterId, cancellationToken);
            if (targetCharacterId > 0 && targetCharacterId != attackerCharacterId)
                _ = await resolveWarFaction(targetCharacterId, cancellationToken);
        }
        return await relations.ReadSnapshotAsync<bool>(async (connection, transaction, token) =>
        {
            var attackerOwners = await relations.ChainOfAsync(attackerCharacterId, connection, transaction, token);
            var targetOwners = await relations.ChainOfAsync(targetCharacterId, connection, transaction, token);
            var attackerFaction = attackerOwners.LastOrDefault(FwFactions.IsOccupier);
            var targetFaction = targetOwners.LastOrDefault(FwFactions.IsOccupier);
            if (attackerFaction > 0 && targetFaction > 0 && FwFactions.IsCombatEnemy(attackerFaction, targetFaction))
                return true;

            var directWars = await relations.BetweenAsync(Eve.Relations.RelationKinds.War.Name,
                attackerOwners, targetOwners, connection, transaction, token);
            if (directWars.Any(war => Active(war, nowUnixMs)))
                return true;

            var owners = attackerOwners.Concat(targetOwners).Distinct().ToArray();
            var allies = (await relations.FromAnyAsync(Eve.Relations.RelationKinds.WarAlly.Name,
                    owners, connection, transaction, token))
                .Where(ally => Active(ally, nowUnixMs))
                .DistinctBy(ally => ally.Id)
                .ToArray();
            if (allies.Length == 0)
                return false;

            var wars = (await relations.InvolvingAnyAsync(Eve.Relations.RelationKinds.War.Name,
                    owners, connection, transaction, token))
                .Concat(directWars)
                .DistinctBy(war => war.Id)
                .ToArray();
            var knownWarIds = wars.Select(war => war.Id).ToHashSet();

            bool OnSide(IReadOnlyList<long> chain, Eve.Relations.Relation war, WarSide side) =>
                chain.Contains(side == WarSide.Aggressor ? war.Subject : war.Object) ||
                allies.Any(ally => chain.Contains(ally.Subject) && ally.Object == war.Id && SideOf(ally) == side);

            foreach (var war in wars)
            {
                if (!Active(war, nowUnixMs))
                    continue;
                var attackerIsAggressor = OnSide(attackerOwners, war, WarSide.Aggressor);
                var attackerIsDefender = OnSide(attackerOwners, war, WarSide.Defender);
                var targetIsAggressor = OnSide(targetOwners, war, WarSide.Aggressor);
                var targetIsDefender = OnSide(targetOwners, war, WarSide.Defender);
                if ((attackerIsAggressor && targetIsDefender) || (attackerIsDefender && targetIsAggressor))
                    return true;
            }

            foreach (var attackerAlly in allies.Where(ally => attackerOwners.Contains(ally.Subject)))
            {
                if (knownWarIds.Contains(attackerAlly.Object) || SideOf(attackerAlly) is not { } attackerSide)
                    continue;
                if (allies.Any(targetAlly =>
                        targetOwners.Contains(targetAlly.Subject) &&
                        targetAlly.Object == attackerAlly.Object &&
                        SideOf(targetAlly) is { } targetSide &&
                        targetSide != attackerSide))
                    return true;
            }

            return false;
        }, cancellationToken);
    }

    public async Task<AssistanceFacts> AssistanceFactsAsync(
        long assistantCharacterId,
        long targetCharacterId,
        long nowUnixMs,
        CancellationToken cancellationToken = default)
    {
        if (assistantCharacterId <= 0 || targetCharacterId <= 0 || assistantCharacterId == targetCharacterId)
            return default;

        return await relations.ReadSnapshotAsync<AssistanceFacts>(async (connection, transaction, token) =>
        {
            var assistantOwners = await relations.ChainOfAsync(assistantCharacterId, connection, transaction, token);
            var targetOwners = await relations.ChainOfAsync(targetCharacterId, connection, transaction, token);
            var owners = assistantOwners.Concat(targetOwners).Distinct().ToArray();
            var allies = (await relations.FromAnyAsync(Eve.Relations.RelationKinds.WarAlly.Name,
                    owners, connection, transaction, token))
                .Where(ally => Active(ally, nowUnixMs))
                .DistinctBy(ally => ally.Id)
                .ToArray();
            var referencedWars = await relations.GetManyByIdAsync(Eve.Relations.RelationKinds.War.Name,
                allies.Select(ally => ally.Object).Distinct(), connection, transaction, token);
            var wars = (await relations.InvolvingAnyAsync(Eve.Relations.RelationKinds.War.Name,
                    owners, connection, transaction, token))
                .Concat(referencedWars)
                .Where(war => Active(war, nowUnixMs))
                .DistinctBy(war => war.Id)
                .ToArray();

            bool OnSide(IReadOnlyList<long> chain, Eve.Relations.Relation war, WarSide side) =>
                chain.Contains(side == WarSide.Aggressor ? war.Subject : war.Object)
                || allies.Any(ally => chain.Contains(ally.Subject) && ally.Object == war.Id && SideOf(ally) == side);

            var targetWars = wars.Where(war =>
                OnSide(targetOwners, war, WarSide.Aggressor) || OnSide(targetOwners, war, WarSide.Defender)).ToArray();
            if (targetWars.Length == 0)
                return default(AssistanceFacts);

            var helperInEveryWar = targetWars.All(war =>
                OnSide(assistantOwners, war, WarSide.Aggressor) || OnSide(assistantOwners, war, WarSide.Defender));
            var onSameSideInEveryWar = targetWars.All(war =>
                (OnSide(targetOwners, war, WarSide.Aggressor) && OnSide(assistantOwners, war, WarSide.Aggressor))
                || (OnSide(targetOwners, war, WarSide.Defender) && OnSide(assistantOwners, war, WarSide.Defender)));
            return new(true, helperInEveryWar, onSameSideInEveryWar);
        }, cancellationToken);
    }

    private static bool Active(Eve.Relations.Relation relation, long nowUnixMs) =>
        relation.SinceMs <= nowUnixMs && (relation.UntilMs is null || relation.UntilMs > nowUnixMs);

    private static WarSide? SideOf(Eve.Relations.Relation ally)
    {
        try
        {
            return ally.Attrs<WarAllyAttributes>()?.Side?.ToLowerInvariant() switch
            {
                "aggressor" => WarSide.Aggressor,
                "defender" => WarSide.Defender,
                _ => null,
            };
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }
}
