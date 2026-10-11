using System.Globalization;
using Eve.Agents;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;

namespace Eve.Services.Core;

/// <summary>The dialogue buttons (eve-logic appConst.py:146-164). A button id is what the client's agent window names.</summary>
public static class AgentDialogueButton
{
    public const int ViewMission = 1;
    public const int RequestMission = 2;
    public const int Accept = 3;
    public const int Complete = 6;
    public const int Continue = 8;
    public const int Decline = 9;
    public const int Defer = 10;
    public const int Quit = 11;
    public const int StartResearch = 12;
    public const int CancelResearch = 13;
    public const int BuyDatacores = 14;
    public const int LocateCharacter = 15;
    public const int LocateAccept = 16;
    public const int LocateReject = 17;
}

/// <summary>
/// The action slots the server hands the window (EveJS agentMissionRuntime.js:122-130). The window sends the slot back to DoAction;
/// the slot maps to a stable button. The values are the reference's, and the client echoes them.
/// </summary>
public static class AgentDialogueToken
{
    public const int Request = 376;
    public const int Accept = 377;
    public const int Decline = 378;
    public const int Defer = 379;
    public const int Complete = 380;
    public const int Quit = 381;
    public const int CompleteReady = 382;
    public const int QuitReady = 383;
    public const int RequestAfterCompletion = 384;

    private static readonly Dictionary<long, int> ButtonByToken = new()
    {
        [Request] = AgentDialogueButton.RequestMission,
        [Accept] = AgentDialogueButton.Accept,
        [Decline] = AgentDialogueButton.Decline,
        [Defer] = AgentDialogueButton.Defer,
        [Complete] = AgentDialogueButton.Complete,
        [Quit] = AgentDialogueButton.Quit,
        [CompleteReady] = AgentDialogueButton.Complete,
        [QuitReady] = AgentDialogueButton.Quit,
        [RequestAfterCompletion] = AgentDialogueButton.RequestMission,
    };

    /// <summary>The button an action names: a slot maps to its button, a button id is itself; null for anything else.</summary>
    public static int? ButtonOf(long actionId) =>
        ButtonByToken.TryGetValue(actionId, out var button) ? button : actionId is > 0 and <= 19 ? (int)actionId : null;
}

/// <summary>What the dialogue needs from the rest of the server. Each is a read or an effect the host provides.</summary>
/// <param name="Store">The agents store: the offers and the mission states.</param>
/// <param name="ReadStandings">The character's standings toward the agent (agent, corporation and faction), read from the relations store.</param>
/// <param name="ReadSkillLevel">A trained skill level of the character (0 when untrained).</param>
/// <param name="FactionOfCorporation">The faction of an NPC corporation (0 when it has none).</param>
/// <param name="PickOffer">
/// The mission an agent would offer the character now, or null when the agent has none. The mission pool is not part of this
/// server yet (DEVIATIONS "Agents slice AG-2, A5"); the host answers null for every agent until a pool is loaded.
/// </param>
/// <param name="Now">The clock.</param>
/// <param name="OnConversation">Called when the character talks to the agent (a DoAction), with whether the character is with the agent (in the station, or in space in its system). Talk steps complete here; null: nothing.</param>
public sealed record AgentDialogueSources(
    AgentMissionStore Store,
    Func<long, long, long, long, CancellationToken, ValueTask<AgentStandings>> ReadStandings,
    Func<long, int, CancellationToken, ValueTask<int>> ReadSkillLevel,
    Func<long, long> FactionOfCorporation,
    Func<AgentMissionAgentData, long, CancellationToken, ValueTask<AgentMissionContent?>> PickOffer,
    Func<DateTimeOffset> Now,
    Func<long, AgentMissionAgentData, bool, CancellationToken, ValueTask>? OnConversation = null);

/// <summary>The answer of DoAction: the agent's line, the buttons, and the last-action record the window reads.</summary>
public sealed record AgentDialogueReply(PyValue AgentSays, IReadOnlyList<(long ActionId, int ButtonType)> Actions, PyDict LastActionInfo)
{
    /// <summary>
    /// <c>((agentSays, actions), lastActionInfo)</c> as the reference marshals it (agentMgrService.js buildDoActionResponsePayload):
    /// agentSays is (message, contentID); a message is a plain text or (localization label, keyword dictionary).
    /// </summary>
    public PyValue ToWire() => new PyTuple(
    [
        new PyTuple([AgentSays, new PyList([.. Actions.Select(action => (PyValue)new PyTuple([new PyInt(action.ActionId), new PyInt(action.ButtonType)]))])]),
        LastActionInfo,
    ]);
}

/// <summary>
/// The agent dialogue behind DoAction (A4, narrowed). It answers the conversation the client asks for and makes the mission
/// transitions it names through the agents store. Implemented: the greeting, the standing refusal, request (an offer from
/// <see cref="AgentDialogueSources.PickOffer"/>), view, accept, decline (the four-hour replay), quit at the agent's station, and
/// the states that show them. Not implemented, and refused by name: research (A6) and the locator (A7). The reference's
/// confirmation windows for decline and quit are not part of this slice: the transition is made when the button is pressed
/// (DEVIATIONS "Agents slice AG-2, A4").
/// </summary>
public sealed class AgentDialogueMachine(AgentDialogueSources sources)
{
    /// <summary>The first line of an agent with no conversation yet (appConst labels, resolved by the client's localization).</summary>
    public const string GreetingLabel = "UI/Agents/DefaultMessages/RootAgentSays/GenericGreetings";

    /// <summary>A research agent's line when the standings are too low (researchRuntime.js LABEL_TOO_LOW_STANDINGS).</summary>
    public const string ResearchTooLowLabel = "UI/Agents/Research/TooLowStandings";

    /// <summary>The reference's own line for a basic agent when the standings are too low (EveJS agentMissionRuntime.js:8057).</summary>
    public const string TooLowStandingsText = "Your current standings are not high enough for this agent to issue you a mission yet.";

    private static readonly int[] AccessSkills = [AgentAccessRule.ConnectionsSkill, AgentAccessRule.DiplomacySkill, AgentAccessRule.CriminalConnectionsSkill];

    /// <summary>One DoAction. <paramref name="action"/> is the argument the window sent (PyNone for the first contact).</summary>
    public async ValueTask<AgentDialogueReply> DoActionAsync(
        long characterId,
        AgentMissionAgentData agent,
        PyValue? action,
        bool atAgentLocation,
        CancellationToken cancellationToken)
    {
        var now = sources.Now();
        if (sources.OnConversation is { } onConversation)
        {
            await onConversation(characterId, agent, atAgentLocation, cancellationToken);
        }
        var button = ButtonOfArgument(action);
        var standing = await ReadAccessAsync(characterId, agent, cancellationToken);
        var mission = await sources.Store.ReadAsync(characterId, agent.AgentId, cancellationToken);

        switch (button)
        {
            case AgentDialogueButton.RequestMission when mission?.State is not (AgentMissionState.Offered or AgentMissionState.Accepted) && standing.CanUse:
                if (mission?.DeclineUntil is { } until && until > now) break;
                if (await sources.PickOffer(agent, characterId, cancellationToken) is { } content)
                {
                    try
                    {
                        await sources.Store.OfferAsync(characterId, agent.AgentId, content, now,
                            $"offer:{characterId}:{agent.AgentId}:{now.ToUnixTimeMilliseconds()}", cancellationToken);
                    }
                    catch (AgentMissionRefusedException)
                    {
                        // the state changed since it was read: the conversation shows what is held now
                    }
                }
                break;

            case AgentDialogueButton.Accept when mission?.State == AgentMissionState.Offered:
                try
                {
                    await sources.Store.AcceptAsync(characterId, agent.AgentId, now,
                        $"accept:{characterId}:{agent.AgentId}:{now.ToUnixTimeMilliseconds()}", cancellationToken);
                }
                catch (AgentMissionRefusedException)
                {
                    // expired or already changed: the greeting is shown again
                }
                break;

            case AgentDialogueButton.Decline when mission?.State == AgentMissionState.Offered:
                try
                {
                    await sources.Store.DeclineAsync(characterId, agent.AgentId, now,
                        $"decline:{characterId}:{agent.AgentId}:{now.ToUnixTimeMilliseconds()}", cancellationToken);
                    return await ConversationAsync(characterId, agent, standing, atAgentLocation, cancellationToken,
                        LastInfo(declined: true));
                }
                catch (AgentMissionRefusedException)
                {
                }
                break;

            case AgentDialogueButton.Quit when mission?.State == AgentMissionState.Accepted && atAgentLocation:
                try
                {
                    await sources.Store.QuitAsync(characterId, agent.AgentId, now,
                        $"quit:{characterId}:{agent.AgentId}:{now.ToUnixTimeMilliseconds()}", cancellationToken);
                    return await ConversationAsync(characterId, agent, standing, atAgentLocation, cancellationToken,
                        LastInfo(quit: true));
                }
                catch (AgentMissionRefusedException)
                {
                }
                break;

            case AgentDialogueButton.StartResearch or AgentDialogueButton.CancelResearch or AgentDialogueButton.BuyDatacores
                or AgentDialogueButton.LocateCharacter or AgentDialogueButton.LocateAccept or AgentDialogueButton.LocateReject:
                throw MachoErrors.ThrowUserError("CustomInfo",
                    ("info", new PyStr("This agent service is not available on this server yet.")));
        }

        return await ConversationAsync(characterId, agent, standing, atAgentLocation, cancellationToken, LastInfo());
    }

    /// <summary>The conversation the window shows after an action: the held mission's, or the greeting and what can be asked.</summary>
    private async ValueTask<AgentDialogueReply> ConversationAsync(
        long characterId, AgentMissionAgentData agent, AccessReading standing, bool atAgentLocation, CancellationToken cancellationToken, PyDict lastInfo)
    {
        var now = sources.Now();
        var mission = await sources.Store.ReadAsync(characterId, agent.AgentId, cancellationToken);
        if (mission?.State == AgentMissionState.Offered)
        {
            return new AgentDialogueReply(
                Greeting(mission.Content.MissionId),
                [(AgentDialogueToken.Accept, AgentDialogueButton.Accept), (AgentDialogueToken.Decline, AgentDialogueButton.Decline)],
                lastInfo);
        }
        if (mission?.State == AgentMissionState.Accepted)
        {
            return new AgentDialogueReply(
                Greeting(mission.Content.MissionId),
                atAgentLocation ? [(AgentDialogueToken.Quit, AgentDialogueButton.Quit)] : [],
                lastInfo);
        }
        if (!standing.CanUse)
        {
            return agent.AgentTypeId == AgentAccessRule.ResearchAgentType
                ? new AgentDialogueReply(ResearchTooLow(), [], lastInfo)
                : new AgentDialogueReply(PlainText(TooLowStandingsText), [], lastInfo);
        }
        var canRequest = mission?.DeclineUntil is not { } until || until <= now;
        var offerable = canRequest && await sources.PickOffer(agent, characterId, cancellationToken) is not null;
        return new AgentDialogueReply(
            Greeting(null),
            offerable ? [(AgentDialogueToken.Request, AgentDialogueButton.RequestMission)] : [],
            lastInfo);
    }

    private static int? ButtonOfArgument(PyValue? action) => action switch
    {
        null or PyNone => null,
        PyInt integer => AgentDialogueToken.ButtonOf(integer.Value) is { } button ? button : null,
        PyLong longValue => AgentDialogueToken.ButtonOf(longValue.Value) is { } button ? button : null,
        _ => null,
    };

    private async ValueTask<AccessReading> ReadAccessAsync(long characterId, AgentMissionAgentData agent, CancellationToken cancellationToken)
    {
        var factionId = sources.FactionOfCorporation(agent.CorporationId);
        var standings = await sources.ReadStandings(characterId, agent.AgentId, agent.CorporationId, factionId, cancellationToken);
        var levels = new Dictionary<int, int>();
        foreach (var skill in AccessSkills)
        {
            levels[skill] = await sources.ReadSkillLevel(characterId, skill, cancellationToken);
        }
        var canUse = AgentAccessRule.CanUse(agent.Level, agent.AgentTypeId, standings, factionId, skill => levels.GetValueOrDefault(skill));
        return new AccessReading(canUse);
    }

    private sealed record AccessReading(bool CanUse);

    private static PyValue Greeting(long? contentId) => new PyTuple(
    [
        new PyTuple([new PyStr(GreetingLabel), EmptyDict()]),
        contentId is > 0 and var id ? new PyInt(id) : PyNone.Instance,
    ]);

    private static PyValue PlainText(string text) => new PyTuple([new PyStr(text), PyNone.Instance]);

    private static PyValue ResearchTooLow() => new PyTuple([new PyTuple([new PyStr(ResearchTooLowLabel), EmptyDict()]), PyNone.Instance]);

    private static PyDict EmptyDict() => new(new List<KeyValuePair<PyValue, PyValue>>());

    /// <summary>The window's last-action record (EveJS buildDefaultMissionLastActionInfo, with the flags a transition sets).</summary>
    private static PyDict LastInfo(bool quit = false, bool declined = false) => new(
    [
        new KeyValuePair<PyValue, PyValue>(new PyStr("missionCompleted"), PyNone.Instance),
        new KeyValuePair<PyValue, PyValue>(new PyStr("missionQuit"), quit ? PyBool.True : PyNone.Instance),
        new KeyValuePair<PyValue, PyValue>(new PyStr("missionCantReplay"), PyNone.Instance),
        new KeyValuePair<PyValue, PyValue>(new PyStr("loyaltyPoints"), new PyInt(0)),
        new KeyValuePair<PyValue, PyValue>(new PyStr("missionDeclined"), declined ? PyBool.True : PyNone.Instance),
    ]);
}
