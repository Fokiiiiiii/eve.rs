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

    /// <summary>
    /// Completes the character's accepted mission with the agent and changes the standings in one transaction. False when the mission is not
    /// accepted (a replay): then nothing is written.
    /// </summary>
    Task<bool> CompleteMissionAsync(long characterId, long agentId, DateTimeOffset now, string reference,
        IReadOnlyList<MissionStandingGain> standings, IReadOnlyList<(int TypeId, long Quantity)> consume, CancellationToken cancellationToken);

    /// <summary>
    /// Creates into the hangar of the station the objective items of a fetch step, up to the quantity the character holds (idempotent: the
    /// items held are counted, so a replayed dungeon outcome grants nothing).
    /// </summary>
    Task GrantObjectiveItemAsync(long characterId, int typeId, long quantity, long stationId, CancellationToken cancellationToken);

    /// <summary>How many of an item type the character holds, in any location.</summary>
    Task<long> HeldQuantityAsync(long characterId, int typeId, CancellationToken cancellationToken);
}

/// <summary>Where an agent is: its solar system (the mission dungeon's system) and its corporation (the counterparty of the reward).</summary>
/// <summary>The base station of the agent (its SDE location, where the objective items of a fetch step are delivered), when it has one.</summary>
public sealed record MissionAgentPlace(long SolarSystemId, long CorporationId, long? BaseStationId = null);

/// <summary>
/// One standing change of a completed mission toward an entity (an agent, a corporation or a faction). An SDE gain is added as it is. A basic
/// mission's gain is diminishing, the wiki's rule: new = old + (10 - old) x increase, so the change is (10 - old) x increase.
/// </summary>
public sealed record MissionStandingGain(long EntityId, double Amount, bool Diminishing)
{
    public double DeltaFrom(double current) => Diminishing ? (10 - current) * Amount : Amount;
}

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
            // a fetch step whose dungeon already gave the objective: its dungeon is done, a second one would fail the mission when the pilot leaves
            if (FetchOf(record.Content.MissionId) is { } fetched && await host.HeldQuantityAsync(record.CharacterId, (int)fetched.TypeId, cancellationToken) >= fetched.Quantity)
                continue;
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
            if (FetchOf(record.Content.MissionId) is { } fetch)
            {
                // a fetch step: the dungeon yields the objective items into the hangar of the agent's station. Only the turn-in completes the step
                if (agentPlaceOf(record.AgentId)?.BaseStationId is { } station)
                {
                    await host.GrantObjectiveItemAsync(owner, (int)fetch.TypeId, fetch.Quantity, station, cancellationToken);
                }
                return;
            }
            await CompleteWithRewardAsync(owner, record, $"dungeon:{instanceId}", $"complete:{instanceId}", cancellationToken);
        }
        else
        {
            await TransitionAsync(() => store.FailAsync(owner, record.AgentId, now(), $"fail:{instanceId}:{outcome}", cancellationToken));
        }
    }

    /// <summary>
    /// A talk step completes when the character talks to the agent of the step after it, while they are together (in the station, or in space in
    /// the agent's system). The reward is paid under the step's key, so a replayed conversation pays nothing.
    /// </summary>
    public async Task OnConversationAsync(long characterId, long agentId, bool together, CancellationToken cancellationToken = default)
    {
        if (!together) return;
        foreach (var record in (await store.ReadAsync(characterId, cancellationToken)).Where(item => item.State == AgentMissionState.Accepted))
        {
            var missionId = record.Content.MissionId;
            if (FetchOf(missionId) is { } fetch)
            {
                // the turn-in: the objective items are consumed at the mission's agent and the step completes; with fewer held, nothing happens
                if (record.AgentId == agentId && await host.HeldQuantityAsync(characterId, (int)fetch.TypeId, cancellationToken) >= fetch.Quantity)
                {
                    await CompleteWithRewardAsync(characterId, record, $"turnin:{missionId}", $"turnin:{missionId}:{agentId}", cancellationToken,
                        [((int)fetch.TypeId, fetch.Quantity)]);
                }
                continue;
            }
            if (!catalog.IsTalkStep(missionId) || !catalog.NextAgentsOf(missionId).Contains(agentId)) continue;
            await CompleteWithRewardAsync(characterId, record, $"talk:{missionId}", $"talk:{missionId}:{agentId}", cancellationToken);
        }
    }

    // The ISK base of a basic level 4 or 5 security mission, which the official build does not give (its reward is null): the security scale of
    // EveJS (server/src/services/agent/agentMissionRuntime.js, REWARD_BY_SCALE_AND_LEVEL). The wiki gives "large rewards" without figures, so these
    // are the behaviour reference's numbers. shortcut: no timed bonus (EveJS pays 1.23x in time) and no Negotiation (wiki: 5% per level); upgrade with a sourced rule.
    // The standing increases are EveJS's agentRaw and corpRaw of the same table, the increase of the wiki's formula (EVE University, Standings).
    // shortcut: EveJS's numbers, not a CCP table; EveJS puts the corporation gain above the agent gain, a 2012 forum count the other way round.
    private sealed record BasicRate(long Isk, double AgentIncrease, double CorporationIncrease);

    private static readonly IReadOnlyDictionary<int, BasicRate> BasicRewards = new Dictionary<int, BasicRate>
    {
        [4] = new(675_000, 0.028, 0.055),
        [5] = new(1_470_000, 0.036, 0.072),
    };

    /// <summary>
    /// The objective item of a fetch step: a kill mission's objective type and quantity (the SDE's <c>killMission</c>). The mining records of the SDE
    /// use the same field; eve.rs offers no mining mission, so it is read here as a fetch.
    /// </summary>
    private (long TypeId, long Quantity)? FetchOf(long missionId) =>
        catalog.Mission(missionId)?.Kill is { ObjectiveTypeId: { } typeId, ObjectiveQuantity: > 0 } kill ? (typeId, kill.ObjectiveQuantity) : null;

    private async Task CompleteWithRewardAsync(long owner, AgentMissionRecord record, string rewardKey, string reference, CancellationToken cancellationToken,
        IReadOnlyList<(int TypeId, long Quantity)>? consume = null)
    {
        var mission = catalog.Mission(record.Content.MissionId)!;
        var place = agentPlaceOf(record.AgentId);
        var sdeIsk = mission.Reward?.RewardTypeId == IskTypeId ? mission.Reward.RewardQuantity : 0;
        var level = catalog.Level(record.Content.MissionId).Level;
        var basic = mission.Reward is null && level is { } tagged && BasicRewards.TryGetValue(tagged, out var rates) ? rates : null;
        var isk = sdeIsk > 0 ? sdeIsk : basic?.Isk ?? 0;
        if (isk > 0 && place is not null)
        {
            await host.CreditIskAsync(owner, isk, place.CorporationId, AgentMissionRewardRefType,
                $"Agent mission reward: {mission.NameEn}", $"agent-mission:{owner}:{record.Content.MissionId}:{rewardKey}", cancellationToken);
        }
        // the SDE's standings are in tenths (7.0 is +0.7), added as they are; a basic mission's are the wiki's diminishing gains toward the
        // agent and the corporation. The completion and the standings are one transaction
        var gains = mission.ExtraStandings.Select(item => new MissionStandingGain(item.Id, item.Value / 10, false)).ToList();
        if (gains.Count == 0 && basic is not null && place is not null)
        {
            gains.Add(new MissionStandingGain(record.AgentId, basic.AgentIncrease, true));
            gains.Add(new MissionStandingGain(place.CorporationId, basic.CorporationIncrease, true));
        }
        await host.CompleteMissionAsync(owner, record.AgentId, now(), reference, gains, consume ?? [], cancellationToken);
    }

    private static async Task<AgentMissionChange?> TransitionAsync(Func<Task<AgentMissionChange?>> transition)
    {
        try
        {
            return await transition();
        }
        catch (AgentMissionRefusedException)
        {
            // the mission changed since it was read (a replay): nothing to do
            return null;
        }
    }
}
