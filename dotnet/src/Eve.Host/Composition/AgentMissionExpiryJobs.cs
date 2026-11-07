using Eve.Agents;
using Eve.Events;
using Eve.Scheduler;

namespace Eve.Host;

/// <summary>Schedules mission offer and accepted-mission deadlines and announces the committed expiry transition.</summary>
internal sealed class AgentMissionExpiryJobs(
    AgentMissionStore store,
    DurableScheduler scheduler,
    EventBus events)
{
    public const string ExpiredTopic = "agent.mission.expired";
    private const string ExpiredSubscriber = "agents.mission.expired.notify";

    public void Register()
    {
        scheduler.Register(AgentMissionStore.ExpiryJobKind, async (job, cancellationToken) =>
        {
            var expiry = job.Payload<AgentMissionExpiry>()
                ?? throw new InvalidOperationException("agent mission expiry job has no deadline payload");
            var change = await store.ExpireInAsync(
                job.Connection, job.Transaction, expiry,
                DateTimeOffset.FromUnixTimeMilliseconds(job.NowMs), cancellationToken);
            if (change is null) return;
            await events.PublishAsync(job.Connection, job.Transaction, ExpiredTopic, change,
                AgentMissionStore.ExpiryJobKey(change.CharacterId, change.AgentId), cancellationToken);
            events.Wake();
        });

        events.Subscribe(ExpiredSubscriber, [ExpiredTopic], (context, _) =>
        {
            var change = context.Event.Payload<AgentMissionChange>()
                ?? throw new InvalidOperationException("agent mission expiry event has no transition");
            context.AfterCommit(_ =>
            {
                store.Announce(change);
                return Task.CompletedTask;
            });
            return Task.CompletedTask;
        });
    }

    public async Task RestoreAsync(Func<long, long?> expirationMinutesOf, CancellationToken cancellationToken = default)
    {
        foreach (var mission in await store.ReadActiveAsync(cancellationToken))
            await store.EnsureExpiryScheduledAsync(mission, expirationMinutesOf(mission.Content.MissionId), cancellationToken);
    }
}
