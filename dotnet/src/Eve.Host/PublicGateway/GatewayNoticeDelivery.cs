using Eve.Scheduler;

namespace Eve.Host.PublicGateway;

/// <summary>Persists a public-gateway notice until matching live streams flush its frames.</summary>
internal static class GatewayNoticeDelivery
{
    public const string Kind = "public-gateway.notice.deliver";
    private const long RetryMs = 5_000;
    private const long RetentionMs = 7L * 24 * 60 * 60 * 1000;

    internal sealed record Notice(string TypeName, byte[] Payload);
    internal sealed record Delivery(string Key, Notice[] Notices, long CharacterId, long ExpiresAtMs);

    public static void Register(DurableScheduler scheduler, NoticeHub notices)
    {
        scheduler.Register(Kind, (job, _) =>
        {
            var delivery = job.Payload<Delivery>()
                ?? throw new InvalidOperationException("a public-gateway notice job has no payload");
            if (delivery.ExpiresAtMs <= job.NowMs)
            {
                notices.Forget(delivery.Key);
                return Task.CompletedTask;
            }

            var delivered = true;
            for (var index = 0; index < delivery.Notices.Length; index++)
            {
                if (notices.IsDelivered(delivery.Key, index))
                    continue;
                delivered = false;
                var notice = delivery.Notices[index];
                notices.PublishDurable(delivery.Key, index, notice.TypeName, notice.Payload, delivery.CharacterId);
            }
            if (delivered)
                notices.Forget(delivery.Key);
            else
                job.RunAgainAt(Math.Min(delivery.ExpiresAtMs, job.NowMs + RetryMs), delivery);
            return Task.CompletedTask;
        });
    }

    public static Task<long> EnqueueAsync(
        DurableScheduler scheduler,
        Npgsql.NpgsqlConnection connection,
        Npgsql.NpgsqlTransaction transaction,
        string key,
        IReadOnlyList<Notice> notices,
        long characterId,
        CancellationToken cancellationToken)
    {
        var now = scheduler.Now;
        return scheduler.ScheduleAsync(connection, transaction, new JobSpec(Kind, now)
        {
            Key = key,
            OnDuplicate = OnDuplicate.Keep,
            Payload = new Delivery(key, [.. notices], characterId, checked(now + RetentionMs)),
        }, cancellationToken);
    }
}
