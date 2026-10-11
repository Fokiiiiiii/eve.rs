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

    /// <summary>The research action tokens (EveJS researchDialogue.js:29-31); they name the buttons 12 to 14.</summary>
    public const int StartResearch = 830;
    public const int CancelResearch = 831;
    public const int BuyDatacores = 832;

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
        [StartResearch] = AgentDialogueButton.StartResearch,
        [CancelResearch] = AgentDialogueButton.CancelResearch,
        [BuyDatacores] = AgentDialogueButton.BuyDatacores,
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
    Func<long, AgentMissionAgentData, bool, CancellationToken, ValueTask>? OnConversation = null,
    IAgentResearchPort? Research = null);

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

/// <summary>A client dialog the server raises before it answers (agents.YesNo, SingleChoiceBox or GetQuantity), with the continuation that takes the client's answer.</summary>
public sealed record AgentDialoguePrompt(string Method, IReadOnlyList<PyValue> Args, PyDict Kwargs, Func<PyValue?, ValueTask<AgentDialogueStep>> Then);

/// <summary>One step of the dialogue: the reply to send, or the prompt to raise first (its answer then leads to the next step).</summary>
public sealed record AgentDialogueStep(AgentDialogueReply? Reply, AgentDialoguePrompt? Prompt)
{
    public static AgentDialogueStep Of(AgentDialogueReply reply) => new(reply, null);
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

    /// <summary>One DoAction, as a reply. Refused when the step needs the client to answer a question first (use <see cref="StepAsync"/>).</summary>
    public async ValueTask<AgentDialogueReply> DoActionAsync(
        long characterId, AgentMissionAgentData agent, PyValue? action, bool atAgentLocation, CancellationToken cancellationToken) =>
        (await StepAsync(characterId, agent, action, atAgentLocation, cancellationToken)).Reply
            ?? throw new InvalidOperationException("the dialogue asks the client first: use StepAsync");

    /// <summary>One DoAction. <paramref name="action"/> is the argument the window sent (PyNone for the first contact).</summary>
    public async ValueTask<AgentDialogueStep> StepAsync(
        long characterId,
        AgentMissionAgentData agent,
        PyValue? action,
        bool atAgentLocation,
        CancellationToken cancellationToken,
        long stationId = 0)
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

            // The reference asks before it declines or quits (sendAgentDeclineConfirmation, sendAgentQuitConfirmation): the
            // transition runs only on a yes, and a no shows the conversation as it was.
            case AgentDialogueButton.Decline when mission?.State == AgentMissionState.Offered:
                return new AgentDialogueStep(null, YesNo(
                    "UI/Agents/StandardMission/DeclineMissionTitle", "UI/Agents/StandardMission/DeclineMessage",
                    new PyDict([new KeyValuePair<PyValue, PyValue>(new PyStr("when"), new PyLong(Py.FileTimeFromUnixMs(now.ToUnixTimeMilliseconds())))]),
                    agent.AgentId, DeclineContentId, "AgtDeclineMission",
                    async yes =>
                    {
                        if (yes)
                        {
                            try
                            {
                                await sources.Store.DeclineAsync(characterId, agent.AgentId, now,
                                    $"decline:{characterId}:{agent.AgentId}:{now.ToUnixTimeMilliseconds()}", cancellationToken);
                                return AgentDialogueStep.Of(await ConversationAsync(characterId, agent, standing, atAgentLocation, cancellationToken,
                                    LastInfo(declined: true)));
                            }
                            catch (AgentMissionRefusedException)
                            {
                            }
                        }
                        return AgentDialogueStep.Of(await ConversationAsync(characterId, agent, standing, atAgentLocation, cancellationToken, LastInfo()));
                    }));

            case AgentDialogueButton.Quit when mission?.State == AgentMissionState.Accepted && atAgentLocation:
                return new AgentDialogueStep(null, YesNo(
                    "UI/Agents/StandardMission/QuitMissionTitle", "UI/Agents/StandardMission/QuitMissionMessage",
                    EmptyDict(), agent.AgentId, mission.Content.MissionId, "AgtQuitMission",
                    async yes =>
                    {
                        if (yes)
                        {
                            try
                            {
                                await sources.Store.QuitAsync(characterId, agent.AgentId, now,
                                    $"quit:{characterId}:{agent.AgentId}:{now.ToUnixTimeMilliseconds()}", cancellationToken);
                                return AgentDialogueStep.Of(await ConversationAsync(characterId, agent, standing, atAgentLocation, cancellationToken,
                                    LastInfo(quit: true)));
                            }
                            catch (AgentMissionRefusedException)
                            {
                            }
                        }
                        return AgentDialogueStep.Of(await ConversationAsync(characterId, agent, standing, atAgentLocation, cancellationToken, LastInfo()));
                    }));

            case AgentDialogueButton.StartResearch when sources.Research is not null:
                return await StartResearchAsync(characterId, agent, standing, now, cancellationToken);

            case AgentDialogueButton.CancelResearch when sources.Research is not null:
                return await CancelResearchAsync(characterId, agent, mission, now, cancellationToken);

            case AgentDialogueButton.BuyDatacores when sources.Research is not null:
                return await BuyDatacoresAsync(characterId, agent, now, stationId, cancellationToken);

            case AgentDialogueButton.StartResearch or AgentDialogueButton.CancelResearch or AgentDialogueButton.BuyDatacores
                or AgentDialogueButton.LocateCharacter or AgentDialogueButton.LocateAccept or AgentDialogueButton.LocateReject:
                throw MachoErrors.ThrowUserError("CustomInfo",
                    ("info", new PyStr("This agent service is not available on this server yet.")));
        }

        return AgentDialogueStep.Of(await ConversationAsync(characterId, agent, standing, atAgentLocation, cancellationToken, LastInfo()));
    }

    /// <summary>What the window is told when a prompt could not be answered (a client call that failed or raised).</summary>
    public static AgentDialogueReply FailureReply() => new(PlainText("The agent could not answer that request."), [], LastInfo());

    /// <summary>The contentID the client's decline confirmation carries (EveJS agentMgrService.js:391-396).</summary>
    private const long DeclineContentId = 4802;

    /// <summary>A YesNo prompt with the reference's arguments: the title and body labels, the agent, the content and the suppression id.</summary>
    private static AgentDialoguePrompt YesNo(string titleLabel, string bodyLabel, PyDict bodyKwargs, long agentId, long contentId, string suppressId,
        Func<bool, ValueTask<AgentDialogueStep>> then) => new(
            "YesNo",
            [
                new PyTuple([new PyStr(titleLabel), EmptyDict()]),
                new PyTuple([new PyStr(bodyLabel), bodyKwargs]),
                new PyInt(agentId),
                new PyInt(contentId),
                new PyStr(suppressId),
            ],
            new PyDict([new KeyValuePair<PyValue, PyValue>(new PyStr("machoVersion"), new PyInt(1))]),
            answer => then(answer is PyBool { Value: true }));

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
        IReadOnlyList<(long ActionId, int ButtonType)> actions = offerable ? [(AgentDialogueToken.Request, AgentDialogueButton.RequestMission)] : [];
        return new AgentDialogueReply(
            Greeting(null),
            [.. actions, .. await ResearchIdleActionsAsync(characterId, agent, cancellationToken)],
            lastInfo);
    }

    // The R&D labels (EveJS researchRuntime.js:69-88), resolved by the client's localization.
    private const string SkillsNotSufficientLabel = "UI/Agents/Research/CharacterSkillsNotSufficient";
    private const string TooLowRpmLabel = "UI/Agents/Research/TooLowRpm";
    private const string ResearchStartedLabel = "UI/Agents/Research/ResearchStarted";
    private const string ResearchCancelledLabel = "UI/Agents/Research/ResearchCancelled";
    private const string BoughtDatacoresLabel = "UI/Agents/Research/BoughtDatacores";
    private const string NotEnoughRpLabel = "UI/Agents/Research/NotEnoughRPForDatacores";
    private const string NotEnoughIskLabel = "UI/Agents/Research/NotEnoughISKForDatacores";
    private const string InvalidInputLabel = "UI/Agents/Research/DatacoreInvalidInput";
    private const string DatacorePriceLabel = "UI/Agents/Research/DatacorePrice";
    private const string DatacoresCaptionLabel = "UI/Agents/Research/Datacores";
    private const string SelectTypeTitleLabel = "UI/Agents/Research/SelectResearchTypeTitle";
    private const string SelectTypeMessageLabel = "UI/Agents/Research/SelectResearchTypeMessage";
    private const string SkillListingLabel = "UI/Agents/Research/SkillListing";
    private const string CancelQuestionLabel = "UI/Agents/Research/CancelResearchQuestion";
    private const string SureToCancelLabel = "UI/Agents/Research/SureToCancelResearch";
    private const string SureToCancelAndMissionLabel = "UI/Agents/Research/SureToCancelResearchAndMission";

    /// <summary>Start research (button 12): the refusals in the reference's order, then the field choice; the answer starts the project.</summary>
    private async ValueTask<AgentDialogueStep> StartResearchAsync(
        long characterId, AgentMissionAgentData agent, AccessReading standing, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var research = sources.Research!;
        if (!standing.CanUse) return Refusal(ResearchTooLowLabel);
        var science = await SkillAsync(characterId, ResearchRules.ScienceSkillTypeId, cancellationToken);
        if (science < ResearchRules.ScienceRequiredLevel) return Refusal(SkillsNotSufficientLabel);
        var fields = new List<AgentResearchField>();
        foreach (var field in research.Fields)
        {
            if (ResearchRules.Qualifies(science, await SkillAsync(characterId, (int)field.SkillTypeId, cancellationToken), (int)agent.Level))
                fields.Add(field);
        }
        if (fields.Count == 0) return Refusal(SkillsNotSufficientLabel);
        var slots = ResearchRules.MaxConcurrentProjects(await SkillAsync(characterId, ResearchRules.ResearchProjectManagementSkillTypeId, cancellationToken));
        if ((await research.Projects.ReadAllAsync(characterId, cancellationToken)).Count >= slots) return Refusal(TooLowRpmLabel);

        var choices = fields.Select(field => (PyValue)new PyTuple(
            [new PyStr(SkillListingLabel), Dict(("skillID", new PyInt(field.SkillTypeId)), ("skillLevel", new PyInt(agent.Level)))])).ToArray();
        return new AgentDialogueStep(null, new AgentDialoguePrompt("SingleChoiceBox",
            [Label(SelectTypeTitleLabel), Label(SelectTypeMessageLabel), new PyList(choices), new PyInt(agent.AgentId)],
            EmptyDict(),
            async answer =>
            {
                var index = ChoiceIndex(answer);
                if (index is not { } chosen || chosen < 0 || chosen >= fields.Count)
                    return await AnswerAsync(characterId, agent, InvalidInputLabel, null, idle: true, cancellationToken);
                var field = fields[chosen];
                var negotiation = await SkillAsync(characterId, ResearchRules.NegotiationSkillTypeId, cancellationToken);
                var specialty = await SkillAsync(characterId, (int)field.SkillTypeId, cancellationToken);
                var effective = AgentAccessRule.EffectiveStanding(standing.Standings.Character, standing.FactionId,
                    skill => standing.Levels.GetValueOrDefault(skill));
                var perDay = ResearchRules.PointsPerDay(negotiation, effective, specialty, (int)agent.Level);
                try
                {
                    await research.Projects.StartAsync(new ResearchProject(characterId, agent.AgentId, field.SkillTypeId, sources.Now(), perDay, 0),
                        slots, cancellationToken);
                }
                catch (ResearchRefusedException)
                {
                    return await AnswerAsync(characterId, agent, TooLowRpmLabel, null, idle: true, cancellationToken);
                }
                return await AnswerAsync(characterId, agent, ResearchStartedLabel, null, idle: true, cancellationToken);
            }));
    }

    /// <summary>Cancel research (button 13): a YesNo, and a yes deletes the project (its points are lost) and quits an accepted mission.</summary>
    private async ValueTask<AgentDialogueStep> CancelResearchAsync(
        long characterId, AgentMissionAgentData agent, AgentMissionRecord? mission, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var research = sources.Research!;
        if (await research.Projects.ReadAsync(characterId, agent.AgentId, cancellationToken) is null)
            return await AnswerAsync(characterId, agent, InvalidInputLabel, null, idle: true, cancellationToken);
        var accepted = mission?.State == AgentMissionState.Accepted;
        return new AgentDialogueStep(null, new AgentDialoguePrompt("YesNo",
            [Label(CancelQuestionLabel), Label(accepted ? SureToCancelAndMissionLabel : SureToCancelLabel), new PyInt(agent.AgentId)],
            EmptyDict(),
            async answer =>
            {
                // The reference answers a no with the invalid-input line (researchDialogue.js planCancelResearch).
                if (answer is not PyBool { Value: true })
                    return await AnswerAsync(characterId, agent, InvalidInputLabel, null, idle: true, cancellationToken);
                if (accepted)
                {
                    try
                    {
                        await sources.Store.QuitAsync(characterId, agent.AgentId, now,
                            $"research-cancel:{characterId}:{agent.AgentId}:{now.ToUnixTimeMilliseconds()}", cancellationToken);
                    }
                    catch (AgentMissionRefusedException)
                    {
                        // the mission changed since it was read: the project is cancelled all the same
                    }
                }
                await research.Projects.CancelAsync(characterId, agent.AgentId, cancellationToken);
                return await AnswerAsync(characterId, agent, ResearchCancelledLabel, null, idle: true, cancellationToken);
            }));
    }

    /// <summary>Buy datacores (button 14): the affordable quantity is asked; the purchase is the port's (RP, ISK and the item in one transaction).</summary>
    private async ValueTask<AgentDialogueStep> BuyDatacoresAsync(
        long characterId, AgentMissionAgentData agent, DateTimeOffset now, long stationId, CancellationToken cancellationToken)
    {
        var research = sources.Research!;
        var project = await research.Projects.ReadAsync(characterId, agent.AgentId, cancellationToken);
        var field = project is null ? null : research.Fields.FirstOrDefault(candidate => candidate.SkillTypeId == project.SkillTypeId);
        if (project is null || field is null)
            return await AnswerAsync(characterId, agent, InvalidInputLabel, null, idle: true, cancellationToken);
        var affordable = (long)Math.Floor(ResearchProjectStore.AccruedPoints(project, now) / ResearchRules.DatacoreResearchPoints);
        if (affordable < 1)
            return Refusal(NotEnoughRpLabel, Dict(("datacoreTypeID", new PyInt(field.DatacoreTypeId)), ("rpAmount", new PyInt(ResearchRules.DatacoreResearchPoints))));

        return new AgentDialogueStep(null, new AgentDialoguePrompt("GetQuantity", [],
            Dict(
                ("maxvalue", new PyInt(affordable)),
                ("minvalue", new PyInt(1)),
                ("setvalue", new PyInt(affordable)),
                ("caption", Label(DatacoresCaptionLabel)),
                ("label", new PyTuple([new PyStr(DatacorePriceLabel), Dict(
                    ("datacoreTypeID", new PyInt(field.DatacoreTypeId)),
                    ("rpAmount", new PyInt(ResearchRules.DatacoreResearchPoints)),
                    ("iskAmount", new PyInt(ResearchRules.DatacoreIsk)))])),
                ("digits", new PyInt(0))),
            async answer =>
            {
                var quantity = Quantity(answer);
                if (quantity <= 0 || stationId <= 0)
                    return await AnswerAsync(characterId, agent, InvalidInputLabel, null, idle: true, cancellationToken);
                var purchase = await research.BuyDatacoresAsync(characterId, agent.AgentId, field.SkillTypeId, quantity, stationId, cancellationToken);
                var kwargs = Dict(("datacoreTypeID", new PyInt(field.DatacoreTypeId)), ("rpAmount", new PyInt(ResearchRules.DatacoreResearchPoints)),
                    ("iskAmount", new PyInt(ResearchRules.DatacoreIsk)));
                return purchase.Refusal switch
                {
                    AgentDatacoreRefusal.None => await AnswerAsync(characterId, agent, BoughtDatacoresLabel, null, idle: true, cancellationToken),
                    AgentDatacoreRefusal.NotEnoughRp => await AnswerAsync(characterId, agent, NotEnoughRpLabel, kwargs, idle: true, cancellationToken),
                    AgentDatacoreRefusal.NotEnoughIsk => await AnswerAsync(characterId, agent, NotEnoughIskLabel, kwargs, idle: true, cancellationToken),
                    _ => await AnswerAsync(characterId, agent, InvalidInputLabel, null, idle: true, cancellationToken),
                };
            }));
    }

    /// <summary>The buttons an R&amp;D agent shows: start with no project; datacores and cancel with one (EveJS buildResearchIdleActions).</summary>
    private async ValueTask<IReadOnlyList<(long ActionId, int ButtonType)>> ResearchIdleActionsAsync(
        long characterId, AgentMissionAgentData agent, CancellationToken cancellationToken)
    {
        if (agent.AgentTypeId != AgentAccessRule.ResearchAgentType || sources.Research is not { } research) return [];
        return await research.Projects.ReadAsync(characterId, agent.AgentId, cancellationToken) is null
            ? [(AgentDialogueToken.StartResearch, AgentDialogueButton.StartResearch)]
            : [(AgentDialogueToken.BuyDatacores, AgentDialogueButton.BuyDatacores), (AgentDialogueToken.CancelResearch, AgentDialogueButton.CancelResearch)];
    }

    /// <summary>The agent's line after a research action, with the idle buttons (EveJS buildPostActionConversation).</summary>
    private async ValueTask<AgentDialogueStep> AnswerAsync(
        long characterId, AgentMissionAgentData agent, string label, PyDict? kwargs, bool idle, CancellationToken cancellationToken) =>
        AgentDialogueStep.Of(new AgentDialogueReply(
            new PyTuple([new PyTuple([new PyStr(label), kwargs ?? EmptyDict()]), PyNone.Instance]),
            idle ? await ResearchIdleActionsAsync(characterId, agent, cancellationToken) : [],
            LastInfo()));

    /// <summary>A refusal by name: the agent's line with no buttons (EveJS saysConversation for a refusal).</summary>
    private static AgentDialogueStep Refusal(string label, PyDict? kwargs = null) => AgentDialogueStep.Of(new AgentDialogueReply(
        new PyTuple([new PyTuple([new PyStr(label), kwargs ?? EmptyDict()]), PyNone.Instance]), [], LastInfo()));

    private static PyTuple Label(string label) => new([new PyStr(label), EmptyDict()]);

    private static PyDict Dict(params (string Key, PyValue Value)[] entries) => new(entries
        .Select(entry => new KeyValuePair<PyValue, PyValue>(new PyStr(entry.Key), entry.Value)).ToList());

    private async ValueTask<int> SkillAsync(long characterId, int skillTypeId, CancellationToken cancellationToken) =>
        await sources.ReadSkillLevel(characterId, skillTypeId, cancellationToken);

    /// <summary>The index of a SingleChoiceBox answer: (true, "radioboxOption&lt;n&gt;Selected"), n counted from one (EveJS readChoiceIndex).</summary>
    private static int? ChoiceIndex(PyValue? answer) => answer is PyTuple { Items.Count: >= 2 } tuple &&
        tuple.Items[0] is PyBool { Value: true } &&
        tuple.Items[1] is PyStr { Value: var selection } &&
        System.Text.RegularExpressions.Regex.Match(selection, "^radioboxOption(\\d+)Selected$") is { Success: true } match
            ? int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) - 1
            : null;

    /// <summary>The quantity a GetQuantity answer carries, or 0 when it was cancelled.</summary>
    private static long Quantity(PyValue? answer) => answer switch
    {
        PyInt integer => integer.Value,
        PyLong integer => integer.Value,
        PyReal real => (long)real.Value,
        _ => 0,
    };

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
        return new AccessReading(canUse, standings, factionId, levels);
    }

    private sealed record AccessReading(bool CanUse, AgentStandings Standings, long FactionId, IReadOnlyDictionary<int, int> Levels);

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
