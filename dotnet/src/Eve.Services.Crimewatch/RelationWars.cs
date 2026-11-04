using System.Collections.Concurrent;
using Eve.Crimewatch;
using Eve.Marshal;
using Eve.Scheduler;
using Eve.Universe;

namespace Eve.Services.Crimewatch;

/// <summary>Resolves active war relations and their accepted allies for crimewatch.</summary>
public sealed class RelationWars(Eve.Relations.RelationStore relations) : Eve.Services.IWars
{
    private enum WarSide { Aggressor, Defender }

    public readonly record struct AssistanceFacts(bool TargetInWar, bool HelperInWar, bool SameSide);
    private sealed record WarAllyAttributes(string? Side);

    public async Task<bool> AreAtWarAsync(long attackerCharacterId, long targetCharacterId, long nowUnixMs, CancellationToken cancellationToken = default)
    {
        var attackerOwners = await relations.ChainOfAsync(attackerCharacterId, cancellationToken);
        var targetOwners = await relations.ChainOfAsync(targetCharacterId, cancellationToken);
        var directWars = await relations.BetweenAsync(Eve.Relations.RelationKinds.War.Name, attackerOwners, targetOwners, cancellationToken);
        if (directWars.Any(war => Active(war, nowUnixMs)))
        {
            return true;
        }

        var owners = attackerOwners.Concat(targetOwners).Distinct().ToArray();
        var allyBatches = await Task.WhenAll(owners.Select(owner =>
            relations.FromAsync(Eve.Relations.RelationKinds.WarAlly.Name, owner, cancellationToken: cancellationToken)));
        var allies = allyBatches.SelectMany(rows => rows).Where(ally => Active(ally, nowUnixMs)).DistinctBy(ally => ally.Id).ToArray();
        if (allies.Length == 0)
        {
            return false;
        }

        var warReads = owners.SelectMany(owner => new[]
        {
            relations.FromAsync(Eve.Relations.RelationKinds.War.Name, owner, cancellationToken: cancellationToken),
            relations.ToAsync(Eve.Relations.RelationKinds.War.Name, owner, cancellationToken: cancellationToken),
        });
        var warBatches = await Task.WhenAll(warReads);
        var wars = warBatches.SelectMany(rows => rows).Concat(directWars).DistinctBy(war => war.Id).ToArray();
        var knownWarIds = wars.Select(war => war.Id).ToHashSet();

        bool OnSide(IReadOnlyList<long> chain, Eve.Relations.Relation war, WarSide side) =>
            chain.Contains(side == WarSide.Aggressor ? war.Subject : war.Object) ||
            allies.Any(ally => chain.Contains(ally.Subject) && ally.Object == war.Id && SideOf(ally) == side);

        foreach (var war in wars)
        {
            if (!Active(war, nowUnixMs))
            {
                continue;
            }
            var attackerIsAggressor = OnSide(attackerOwners, war, WarSide.Aggressor);
            var attackerIsDefender = OnSide(attackerOwners, war, WarSide.Defender);
            var targetIsAggressor = OnSide(targetOwners, war, WarSide.Aggressor);
            var targetIsDefender = OnSide(targetOwners, war, WarSide.Defender);
            if ((attackerIsAggressor && targetIsDefender) || (attackerIsDefender && targetIsAggressor))
            {
                return true;
            }
        }

        foreach (var attackerAlly in allies.Where(ally => attackerOwners.Contains(ally.Subject)))
        {
            if (knownWarIds.Contains(attackerAlly.Object) || SideOf(attackerAlly) is not { } attackerSide)
            {
                continue;
            }
            if (allies.Any(targetAlly =>
                    targetOwners.Contains(targetAlly.Subject) &&
                    targetAlly.Object == attackerAlly.Object &&
                    SideOf(targetAlly) is { } targetSide &&
                    targetSide != attackerSide))
            {
                return true;
            }
        }

        return false;
    }

    public async Task<AssistanceFacts> AssistanceFactsAsync(
        long assistantCharacterId,
        long targetCharacterId,
        long nowUnixMs,
        CancellationToken cancellationToken = default)
    {
        if (assistantCharacterId <= 0 || targetCharacterId <= 0 || assistantCharacterId == targetCharacterId)
            return default;

        var chains = await Task.WhenAll(
            relations.ChainOfAsync(assistantCharacterId, cancellationToken),
            relations.ChainOfAsync(targetCharacterId, cancellationToken));
        var assistantOwners = chains[0];
        var targetOwners = chains[1];
        var owners = assistantOwners.Concat(targetOwners).Distinct().ToArray();
        var warReads = owners.SelectMany(owner => new[]
        {
            relations.FromAsync(Eve.Relations.RelationKinds.War.Name, owner, cancellationToken: cancellationToken),
            relations.ToAsync(Eve.Relations.RelationKinds.War.Name, owner, cancellationToken: cancellationToken),
        });
        var allyBatches = await Task.WhenAll(owners.Select(owner =>
            relations.FromAsync(Eve.Relations.RelationKinds.WarAlly.Name, owner, cancellationToken: cancellationToken)));
        var allies = allyBatches.SelectMany(rows => rows)
            .Where(ally => Active(ally, nowUnixMs))
            .DistinctBy(ally => ally.Id)
            .ToArray();
        var referencedWars = await Task.WhenAll(allies.Select(ally =>
            relations.GetByIdAsync(ally.Object, cancellationToken: cancellationToken)));
        var warBatches = await Task.WhenAll(warReads);
        var wars = warBatches.SelectMany(rows => rows)
            .Concat(referencedWars.Where(war => war?.Kind == Eve.Relations.RelationKinds.War.Name).Select(war => war!))
            .Where(war => Active(war, nowUnixMs))
            .DistinctBy(war => war.Id)
            .ToArray();

        bool OnSide(IReadOnlyList<long> chain, Eve.Relations.Relation war, WarSide side) =>
            chain.Contains(side == WarSide.Aggressor ? war.Subject : war.Object)
            || allies.Any(ally => chain.Contains(ally.Subject) && ally.Object == war.Id && SideOf(ally) == side);

        var targetWars = wars.Where(war =>
            OnSide(targetOwners, war, WarSide.Aggressor) || OnSide(targetOwners, war, WarSide.Defender)).ToArray();
        if (targetWars.Length == 0)
            return default;

        var helperInEveryWar = targetWars.All(war =>
            OnSide(assistantOwners, war, WarSide.Aggressor) || OnSide(assistantOwners, war, WarSide.Defender));
        var onSameSideInEveryWar = targetWars.All(war =>
            (OnSide(targetOwners, war, WarSide.Aggressor) && OnSide(assistantOwners, war, WarSide.Aggressor))
            || (OnSide(targetOwners, war, WarSide.Defender) && OnSide(assistantOwners, war, WarSide.Defender)));
        return new(true, helperInEveryWar, onSameSideInEveryWar);
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
