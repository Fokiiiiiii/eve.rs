using Eve.Agents;

namespace Eve.Services.Core;

/// <summary>What the mission runtime needs from the space and the wallet. The host implements it.</summary>
public interface IMissionDungeonHost
{
    /// <summary>Whether the character is in space in the solar system.</summary>
    bool IsPilotInSystem(long characterId, int solarSystemId);

    /// <summary>
    /// Starts the mission's dungeon for the character in the system (the wave director's start). Returns null when the dungeon runs,
    /// or the refusal (the pilot has no known position, the content has no template, a wave cannot be planned).
    /// </summary>
    Task<string?> StartDungeonAsync(long characterId, int solarSystemId, long dungeonId, CancellationToken cancellationToken);

    /// <summary>Credits ISK once for the operation key (the wallet's idempotent adjustment, with the journal reference type).</summary>
    Task CreditIskAsync(long characterId, long amount, long counterpartyId, int refTypeId, string reason, string operationKey, CancellationToken cancellationToken);
}

/// <summary>Where an agent is: its solar system (the mission dungeon's system) and its corporation (the counterparty of the reward).</summary>
public sealed record MissionAgentPlace(long SolarSystemId, long CorporationId);

/// <summary>
/// The kill missions of the agents in space (AG / A-X1 slice): an accepted kill mission starts its dungeon when the pilot is in space in the
/// agent's system, the dungeon's outcome decides the mission (completed pays and completes; failed, abandoned or expired fails, pays nothing),
/// and a replayed outcome pays nothing twice. Rewards: the official base ISK of <c>missionRewards.reward</c> when its type is ISK (29);
/// the bonus window and the LP are not paid here (DEVIATIONS "Mission execution X1").
/// </summary>
public sealed class MissionDungeonRuntime(
    AgentMissionStore store,
    MissionStaticCatalog catalog,
    IMissionDungeonHost host,
    Func<long, MissionAgentPlace?> agentPlaceOf,
    Func<DateTimeOffset> now)
{
    /// <summary>The ISK type ID (EVE item type 29 "ISK" in the SDE reward records).</summary>
    public const long IskTypeId = 29;

    /// <summary>The wallet journal reference type of an agent mission reward (EveJS <c>JOURNAL_ENTRY_TYPE.AGENT_MISSION_REWARD</c>).</summary>
    public const int AgentMissionRewardRefType = 33;

    /// <summary>The mission dungeon's content definition is named <c>dungeon.{id}</c> (<c>DungeonWaveDirector.DefinitionOf</c>).</summary>
    public const string DefinitionPrefix = "dungeon.";

    /// <summary>
    /// Starts the dungeon of every accepted kill mission whose pilot is in space in the agent's system. A refusal leaves the mission accepted;
    /// the next sweep asks again. Returns the number of dungeons started.
    /// </summary>
    public async Task<int> SweepAsync(CancellationToken cancellationToken = default)
    {
        var started = 0;
        foreach (var record in await store.ReadAcceptedAsync(cancellationToken))
        {
            if (catalog.Mission(record.Content.MissionId)?.Kill?.DungeonId is not { } dungeonId) continue;
            if (agentPlaceOf(record.AgentId) is not { } place) continue;
            if (!host.IsPilotInSystem(record.CharacterId, (int)place.SolarSystemId)) continue;
            if (await host.StartDungeonAsync(record.CharacterId, (int)place.SolarSystemId, dungeonId, cancellationToken) is null) started++;
        }
        return started;
    }

    /// <summary>
    /// A content instance of a dungeon ended for the owner. <paramref name="outcome"/> is <c>completed</c>, <c>failed</c>, <c>abandoned</c> or <c>expired</c>.
    /// Only an accepted mission whose kill dungeon is the instance's definition is decided. The reward is credited under the instance's key before the
    /// completion, so a replay of the same outcome credits nothing.
    /// </summary>
    public async Task OnDungeonOutcomeAsync(string outcome, long owner, string definition, long instanceId, CancellationToken cancellationToken = default)
    {
        if (!definition.StartsWith(DefinitionPrefix, StringComparison.Ordinal) ||
            !long.TryParse(definition.AsSpan(DefinitionPrefix.Length), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var dungeonId))
            return;
        var record = (await store.ReadAsync(owner, cancellationToken)).FirstOrDefault(item =>
            item.State == AgentMissionState.Accepted && catalog.Mission(item.Content.MissionId)?.Kill?.DungeonId == dungeonId);
        if (record is null) return;

        if (outcome == "completed")
        {
            var mission = catalog.Mission(record.Content.MissionId)!;
            if (mission.Reward?.RewardTypeId == IskTypeId && mission.Reward.RewardQuantity > 0 && agentPlaceOf(record.AgentId) is { } place)
            {
                await host.CreditIskAsync(owner, mission.Reward.RewardQuantity, place.CorporationId, AgentMissionRewardRefType,
                    $"Agent mission reward: {mission.NameEn}", $"agent-mission:{owner}:{record.Content.MissionId}:{instanceId}", cancellationToken);
            }
            await TransitionAsync(() => store.CompleteAsync(owner, record.AgentId, now(), $"complete:{instanceId}", cancellationToken));
        }
        else
        {
            await TransitionAsync(() => store.FailAsync(owner, record.AgentId, now(), $"fail:{instanceId}:{outcome}", cancellationToken));
        }
    }

    private static async Task TransitionAsync(Func<Task<AgentMissionChange?>> transition)
    {
        try
        {
            await transition();
        }
        catch (AgentMissionRefusedException)
        {
            // the mission changed since it was read (a replay): nothing to do
        }
    }
}
