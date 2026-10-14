using Eve.Data;

namespace Eve.Events.Tests;

public sealed class IdempotentPublicationTests
{
    [PgFact]
    public async Task Concurrent_retries_and_a_restart_publish_once_even_after_outbox_retention()
    {
        await using var rig = await Rig.Create();
        var ids = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => rig.Bus.PublishOnceAsync("sovereignty.capture.changed", new { score = 1 }, "capture:one")));
        Assert.All(ids, id => Assert.Equal(ids[0], id));
        await using var connection = await rig.Source.OpenConnectionAsync();
        Assert.Equal(1L, await Sql.ScalarAsync(connection, null, $"SELECT count(*) FROM {rig.EventsSchema}.outbox", CancellationToken.None));
        Assert.Equal(1L, await Sql.ScalarAsync(connection, null, $"SELECT count(*) FROM {rig.EventsSchema}.publication_receipts", CancellationToken.None));
        rig.Now += (long)TimeSpan.FromDays(60).TotalMilliseconds;
        Assert.Equal(1, await rig.Bus.PurgeAsync());
        var (scheduler, restarted) = rig.Make();
        try
        {
            Assert.Equal(ids[0], await restarted.PublishOnceAsync("sovereignty.capture.changed", new { score = 1 }, "capture:one"));
            Assert.Equal(0L, await Sql.ScalarAsync(connection, null, $"SELECT count(*) FROM {rig.EventsSchema}.outbox", CancellationToken.None));
            Assert.NotEqual(ids[0], await restarted.PublishOnceAsync("sovereignty.capture.changed", new { score = 2 }, "capture:two"));
        }
        finally
        {
            await scheduler.StopAsync();
        }
    }
}
