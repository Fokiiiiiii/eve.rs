using Eve.Agents;
using Eve.Events;

namespace Eve.Host;

/// <summary>Applies accepted-mission effects from the transactional outbox, so they survive a host restart.</summary>
internal sealed class AgentMissionAcceptedEvents(
    EventBus events,
    Func<AgentMissionChange, EventContext, CancellationToken, Task> apply)
{
    public const string SubscriberName = "agents.mission.accepted.effects";

    public void Register() => events.Subscribe(SubscriberName, [AgentMissionStore.AcceptedTopic], async (context, cancellationToken) =>
    {
        var change = context.Event.Payload<AgentMissionChange>()
            ?? throw new InvalidOperationException("accepted mission event has no transition");
        await apply(change, context, cancellationToken);
    });
}
