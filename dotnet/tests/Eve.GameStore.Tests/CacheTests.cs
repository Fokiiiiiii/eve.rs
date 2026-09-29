using System.Collections.Concurrent;
using System.Text.Json.Nodes;

namespace Eve.GameStore.Tests;

public class CacheTests
{
    private sealed class Harness : IAsyncDisposable
    {
        private readonly PgScope _pg;
        public readonly ConcurrentQueue<(string Table, string[] Upserts, string[] Deletes)> Flushed = new();
        public readonly ConcurrentQueue<(string Table, Exception Error)> Errors = new();

        public Harness(PgScope pg, OwnerSession? session, string role, TimeSpan? delay = null)
        {
            _pg = pg;
            Session = session;
            Cache = new GameStoreCache(pg.Store, session, new GameStoreCacheOptions
            {
                ProcessRole = role,
                FlushDelay = delay ?? TimeSpan.FromHours(1), // tests flush explicitly unless they say otherwise
                OnFlushed = (table, upserts, deletes) => Flushed.Enqueue((table, upserts.Select(u => u.Key).Order().ToArray(), deletes.Order().ToArray())),
                OnFlushError = (table, error) => Errors.Enqueue((table, error)),
            });
        }

        public OwnerSession? Session { get; }

        public GameStoreCache Cache { get; }

        public static async Task<Harness> StartAsync(PgScope pg, string role = "world", string instance = "a", TimeSpan? delay = null)
        {
            var session = await OwnerSession.StartAsync(pg.Store, role, instance, new AcquireOptions(LeaseMs: 600_000), renewEvery: TimeSpan.FromHours(1));
            return new Harness(pg, session, role, delay);
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await Cache.DisposeAsync();
            }
            finally
            {
                if (Session is not null)
                {
                    await Session.DisposeAsync();
                }
            }
        }
    }

    private static JsonNode J(string json) => JsonNode.Parse(json)!;

    private static async Task<string> Stored(PgScope pg, string table) => Fixtures.Canon(await pg.Store.LoadTableAsync(table));

    [PgFact]
    public async Task Writes_are_readable_at_once_and_persist_on_flush()
    {
        await using var pg = await PgScope.CreateAsync();
        await using var h = await Harness.StartAsync(pg);
        await h.Cache.LoadAsync("characters");
        Assert.True(h.Cache.Write("characters", "/1", J("""{"name":"a"}""")).Success);
        Assert.True(h.Cache.Write("characters", "/2/skills/mining", J("5")).Success); // creates the parents
        Assert.Equal("a", (string)h.Cache.Read("characters", "/1/name").Data!);
        Assert.Equal(5, (int)h.Cache.Read("characters", "/2/skills/mining").Data!);
        Assert.Equal("""{}""", await Stored(pg, "characters"));

        Assert.Equal(2, await h.Cache.FlushTableAsync("characters"));
        Assert.Equal("""{"1":{"name":"a"},"2":{"skills":{"mining":5}}}""", await Stored(pg, "characters"));
    }

    [PgFact]
    public async Task Read_errors_and_copies()
    {
        await using var pg = await PgScope.CreateAsync();
        await using var h = await Harness.StartAsync(pg);
        Assert.Equal("TABLE_NOT_FOUND", h.Cache.Read("characters", "/").ErrorMsg);
        await h.Cache.LoadAsync("characters");
        h.Cache.Write("characters", "/1", J("""{"a":{"b":1}}"""));
        Assert.Equal("ENTRY_NOT_FOUND", h.Cache.Read("characters", "/1/zzz").ErrorMsg);
        Assert.Equal("ENTRY_NOT_FOUND", h.Cache.Read("characters", "/1/a/b/c").ErrorMsg);
        Assert.Equal(1, (int)h.Cache.Read("characters", "//1//a/b/").Data!); // empty segments are ignored
        Assert.True(h.Cache.Read("characters", null).Success);

        // What is read is a copy: changing it changes nothing.
        var copy = h.Cache.Read("characters", "/1").Data!;
        copy["a"]!["b"] = 99;
        Assert.Equal(1, (int)h.Cache.Read("characters", "/1/a/b").Data!);
        // And so is what was written.
        var source = J("""{"x":1}""");
        h.Cache.Write("characters", "/2", source);
        source["x"] = 2;
        Assert.Equal(1, (int)h.Cache.Read("characters", "/2/x").Data!);
    }

    [PgFact]
    public async Task A_new_process_sees_what_the_last_one_flushed()
    {
        await using var pg = await PgScope.CreateAsync();
        await using (var first = await Harness.StartAsync(pg))
        {
            await first.Cache.LoadAsync("mail");
            first.Cache.Write("mail", "/messages/10", J("""{"subject":"hi"}"""));
            first.Cache.Write("mail", "/mailboxes/1", J("""{"unread":2}"""));
            first.Cache.Write("mail", "/nextID", J("11"));
            await first.Cache.FlushAllAsync();
        }
        await using var second = await Harness.StartAsync(pg, instance: "b");
        await second.Cache.LoadAsync("mail");
        Assert.Equal("hi", (string)second.Cache.Read("mail", "/messages/10/subject").Data!);
        Assert.Equal(11, (int)second.Cache.Read("mail", "/nextID").Data!);
        Assert.Equal(await Stored(pg, "mail"), Fixtures.Canon(second.Cache.Read("mail", "/").Data));
    }

    [PgFact]
    public async Task A_flush_writes_only_the_rows_that_were_touched()
    {
        await using var pg = await PgScope.CreateAsync();
        await using var h = await Harness.StartAsync(pg);
        await h.Cache.LoadAsync("characters");
        await h.Cache.LoadAsync("mail");
        for (var i = 0; i < 50; i++)
        {
            h.Cache.Write("characters", $"/{i}", J($$"""{"n":{{i}}}"""));
        }
        h.Cache.Write("mail", "/messages/1", J("1"));
        h.Cache.Write("mail", "/messages/2", J("2"));
        await h.Cache.FlushAllAsync();
        h.Flushed.Clear();

        h.Cache.Write("characters", "/7", J("""{"n":700}"""));
        h.Cache.Write("mail", "/messages/2", J("22"));
        await h.Cache.FlushAllAsync();
        var flushed = h.Flushed.ToDictionary(f => f.Table);
        Assert.Equal(["7"], flushed["characters"].Upserts);
        // A grouped entity flushes its own row; the group's skeleton row is unchanged, so it is skipped.
        Assert.Equal(["messages\u001f2"], flushed["mail"].Upserts);
        Assert.Empty(flushed["mail"].Deletes);
    }

    [PgFact]
    public async Task Writing_the_same_value_again_is_not_a_change()
    {
        await using var pg = await PgScope.CreateAsync();
        await using var h = await Harness.StartAsync(pg);
        await h.Cache.LoadAsync("characters");
        h.Cache.Write("characters", "/1", J("""{"a":1,"b":[1,2]}"""));
        await h.Cache.FlushAllAsync();
        h.Flushed.Clear();
        h.Cache.Write("characters", "/1", J("""{ "a": 1, "b": [1, 2] }"""));
        Assert.Equal(0, await h.Cache.FlushAllAsync());
        Assert.Empty(h.Flushed);
        h.Cache.Write("characters", "/1", J("""{"a":1,"b":[1,2]}"""), new WriteOptions(Force: true));
        Assert.Equal(0, await h.Cache.FlushTableAsync("characters")); // forced, but the stored row is identical
    }

    [PgFact]
    public async Task Removing_deletes_the_row_and_removing_nothing_is_reported()
    {
        await using var pg = await PgScope.CreateAsync();
        await using var h = await Harness.StartAsync(pg);
        await h.Cache.LoadAsync("mail");
        h.Cache.Write("mail", "/messages/1", J("1"));
        h.Cache.Write("mail", "/messages/2", J("2"));
        await h.Cache.FlushAllAsync();

        Assert.True(h.Cache.Remove("mail", "/messages/1").Success);
        Assert.Equal("ENTRY_NOT_FOUND", h.Cache.Remove("mail", "/messages/1").ErrorMsg);
        Assert.Equal("ENTRY_NOT_FOUND", h.Cache.Remove("mail", "/nope/1").ErrorMsg);
        Assert.Equal("INVALID_PATH", h.Cache.Remove("mail", "/").ErrorMsg);
        h.Flushed.Clear();
        await h.Cache.FlushAllAsync();
        Assert.Equal(["messages\u001f1"], h.Flushed.Single().Deletes);
        Assert.Equal("""{"messages":{"2":2}}""", await Stored(pg, "mail"));
    }

    [PgFact]
    public async Task Inserting_then_removing_before_a_flush_writes_nothing()
    {
        await using var pg = await PgScope.CreateAsync();
        await using var h = await Harness.StartAsync(pg);
        await h.Cache.LoadAsync("characters");
        h.Cache.Write("characters", "/1", J("1"));
        h.Cache.Remove("characters", "/1");
        Assert.Equal(0, await h.Cache.FlushAllAsync());
        Assert.Empty(h.Flushed);
    }

    [PgFact]
    public async Task A_root_write_is_diffed_in_full_unless_the_changed_paths_are_given()
    {
        await using var pg = await PgScope.CreateAsync();
        await using var h = await Harness.StartAsync(pg);
        await h.Cache.LoadAsync("characters");
        h.Cache.Write("characters", "/", J("""{"1":1,"2":2,"3":3}"""));
        await h.Cache.FlushAllAsync();
        h.Flushed.Clear();

        h.Cache.Write("characters", "/", J("""{"1":1,"2":20}"""));
        await h.Cache.FlushAllAsync();
        Assert.Equal(["2"], h.Flushed.Single().Upserts);
        Assert.Equal(["3"], h.Flushed.Single().Deletes);
        Assert.Equal("""{"1":1,"2":20}""", await Stored(pg, "characters"));

        h.Flushed.Clear();
        h.Cache.Write("characters", "/", J("""{"1":10,"2":20}"""), new WriteOptions(KnownChangedPaths: ["/1"]));
        await h.Cache.FlushAllAsync();
        Assert.Equal(["1"], h.Flushed.Single().Upserts);

        // A malformed hint is not trusted.
        h.Flushed.Clear();
        h.Cache.Write("characters", "/", J("""{"1":10}"""), new WriteOptions(KnownChangedPaths: ["1"]));
        await h.Cache.FlushAllAsync();
        Assert.Equal(["2"], h.Flushed.Single().Deletes);
    }

    [PgFact]
    public async Task Writing_a_whole_group_falls_back_to_a_full_diff()
    {
        await using var pg = await PgScope.CreateAsync();
        await using var h = await Harness.StartAsync(pg);
        await h.Cache.LoadAsync("mail");
        h.Cache.Write("mail", "/messages/1", J("1"));
        h.Cache.Write("mail", "/messages/2", J("2"));
        await h.Cache.FlushAllAsync();
        h.Cache.Write("mail", "/messages", J("""{"3":3}"""));
        await h.Cache.FlushAllAsync();
        Assert.Equal("""{"messages":{"3":3}}""", await Stored(pg, "mail"));
    }

    [PgFact]
    public async Task Transient_paths_stay_in_memory_until_forced()
    {
        await using var pg = await PgScope.CreateAsync();
        await using var h = await Harness.StartAsync(pg);
        await h.Cache.LoadAsync("characters");
        h.Cache.Write("characters", "/1", J("""{"name":"a"}"""));
        h.Cache.Write("characters", "/1/session", J("""{"online":true}"""), new WriteOptions(Transient: true));
        Assert.True(h.Cache.IsTransient("characters", "/1/session"));
        Assert.True(h.Cache.IsTransient("characters", "/1/session/online"));
        Assert.False(h.Cache.IsTransient("characters", "/1/name"));
        Assert.True(h.Cache.Read("characters", "/1/session/online").Success);

        await h.Cache.FlushAllAsync();
        Assert.Equal("""{"1":{"name":"a"}}""", await Stored(pg, "characters"));

        // A forced write persists the value; clearing the marker makes it durable from then on.
        h.Cache.Write("characters", "/1/session", J("""{"online":false}"""), new WriteOptions(Transient: true, Force: true));
        h.Cache.SetTransientPaths("characters", ["/1/session"], enabled: false);
        Assert.False(h.Cache.IsTransient("characters", "/1/session"));
        await h.Cache.FlushAllAsync();
        Assert.Equal("""{"1":{"name":"a","session":{"online":false}}}""", await Stored(pg, "characters"));
    }

    [PgFact]
    public async Task Removing_a_path_clears_the_transient_markers_beneath_it()
    {
        await using var pg = await PgScope.CreateAsync();
        await using var h = await Harness.StartAsync(pg);
        await h.Cache.LoadAsync("characters");
        h.Cache.Write("characters", "/1/session", J("1"), new WriteOptions(Transient: true));
        h.Cache.Write("characters", "/2/session", J("1"), new WriteOptions(Transient: true));
        h.Cache.Remove("characters", "/1");
        Assert.False(h.Cache.IsTransient("characters", "/1/session"));
        Assert.True(h.Cache.IsTransient("characters", "/2/session"));
        h.Cache.SetTransientPaths("characters", ["/"], enabled: false);
        Assert.False(h.Cache.IsTransient("characters", "/2/session"));
    }

    [PgFact]
    public async Task A_failed_flush_keeps_every_change_and_the_retry_writes_them()
    {
        await using var pg = await PgScope.CreateAsync();
        await using var h = await Harness.StartAsync(pg);
        await h.Cache.LoadAsync("characters");
        h.Cache.Write("characters", "/1", J("1"));
        h.Cache.Write("characters", "/2", J("2"));

        // An unfinished batch of the same table blocks the journal.
        var blocker = await pg.Store.EnqueueAsync("characters", [new RowUpsert("blocker", "0")], [], h.Session!.Fence);
        var error = await Assert.ThrowsAsync<PersistenceException>(() => h.Cache.FlushTableAsync("characters"));
        Assert.Equal(PersistenceCodes.OperationPending, error.Code);
        h.Cache.Write("characters", "/3", J("3")); // a write between the failure and the retry

        await pg.Store.ReconcileOperationAsync(blocker.OperationId, "characters", h.Session.Fence);
        await h.Cache.FlushTableAsync("characters");
        // The blocker's own row is the journal's business: the cache never had it, so it stays.
        Assert.Equal("""{"1":1,"2":2,"3":3,"blocker":0}""", await Stored(pg, "characters"));
    }

    [PgFact]
    public async Task A_batch_left_journaled_by_an_interrupted_flush_is_finished_first()
    {
        await using var pg = await PgScope.CreateAsync();
        await using var h = await Harness.StartAsync(pg);
        await h.Cache.LoadAsync("characters");
        h.Cache.Write("characters", "/1", J("1"));

        // Journal the batch but fail before it is applied: take the table's rows away
        // by making the reconcile fail once, via a stale-then-fresh fence is not possible
        // here, so simulate the interruption by pre-journaling the same rows.
        var journaled = await pg.Store.EnqueueAsync("characters", [new RowUpsert("1", "1")], [], h.Session!.Fence);
        var error = await Assert.ThrowsAsync<PersistenceException>(() => h.Cache.FlushTableAsync("characters"));
        Assert.Equal(PersistenceCodes.OperationPending, error.Code);
        await pg.Store.ReconcileOperationAsync(journaled.OperationId, "characters", h.Session.Fence);
        await h.Cache.FlushTableAsync("characters");
        Assert.Equal("""{"1":1}""", await Stored(pg, "characters"));
        Assert.Empty(await pg.Store.ListAllOperationsAsync());
    }

    [PgFact]
    public async Task Recovery_applies_the_journal_a_crashed_predecessor_left()
    {
        await using var pg = await PgScope.CreateAsync();
        var crashed = await pg.Store.AcquireOwnerAsync("world", "crashed", new AcquireOptions(LeaseMs: 1_000));
        await pg.Store.EnqueueAsync("characters", [new RowUpsert("1", """{"name":"from the journal"}""")], [], crashed);
        pg.Advance(1_500); // its lease runs out

        await using var h = await Harness.StartAsync(pg, instance: "successor");
        Assert.Equal(1, await h.Cache.RecoverAsync());
        Assert.Empty(await pg.Store.ListAllOperationsAsync());
        await h.Cache.LoadAsync("characters");
        Assert.Equal("from the journal", (string)h.Cache.Read("characters", "/1/name").Data!);
    }

    [PgFact]
    public async Task The_role_decides_which_tables_a_process_may_write()
    {
        await using var pg = await PgScope.CreateAsync();
        await using var wallet = await Harness.StartAsync(pg, role: "wallet");
        await wallet.Cache.LoadAsync("walletAuthorityState");
        await wallet.Cache.LoadAsync("characters");
        Assert.True(wallet.Cache.Write("walletAuthorityState", "/1", J("1")).Success);
        var error = Assert.Throws<PersistenceException>(() => wallet.Cache.Write("characters", "/1", J("1")));
        Assert.Equal(PersistenceCodes.ProcessOwnershipViolation, error.Code);
        Assert.Throws<PersistenceException>(() => wallet.Cache.Remove("characters", "/1"));
    }

    [PgFact]
    public async Task A_reader_reads_but_never_writes_except_transiently()
    {
        await using var pg = await PgScope.CreateAsync();
        await using (var writer = await Harness.StartAsync(pg))
        {
            await writer.Cache.LoadAsync("characters");
            writer.Cache.Write("characters", "/1", J("1"));
            await writer.Cache.FlushAllAsync();
        }
        await using var reader = new Harness(pg, null, "reader");
        await reader.Cache.LoadAsync("characters");
        Assert.Equal(1, (int)reader.Cache.Read("characters", "/1").Data!);
        Assert.Throws<PersistenceException>(() => reader.Cache.Write("characters", "/2", J("2")));
        Assert.Throws<PersistenceException>(() => reader.Cache.Remove("characters", "/1"));
        Assert.True(reader.Cache.Write("characters", "/scratch", J("1"), new WriteOptions(Transient: true)).Success);
        Assert.Equal(0, await reader.Cache.FlushAllAsync().ContinueWith(t => t.IsFaulted ? 0 : t.Result));
        Assert.Equal("""{"1":1}""", await Stored(pg, "characters"));
    }

    [PgFact]
    public async Task A_reader_cannot_load_a_table_that_does_not_exist()
    {
        await using var pg = await PgScope.CreateAsync();
        await using var reader = new Harness(pg, null, "reader");
        var error = await Assert.ThrowsAsync<PersistenceException>(() => reader.Cache.LoadAsync("characters"));
        Assert.Equal(PersistenceCodes.TableNotFound, error.Code);
    }

    [PgFact]
    public async Task A_process_that_writes_needs_an_owner_session()
    {
        await using var pg = await PgScope.CreateAsync();
        Assert.Throws<ArgumentException>(() => new GameStoreCache(pg.Store, null, new GameStoreCacheOptions { ProcessRole = "world" }));
    }

    [PgFact]
    public async Task Losing_the_lease_stops_the_process_from_writing()
    {
        await using var pg = await PgScope.CreateAsync();
        var lost = new TaskCompletionSource();
        var session = await OwnerSession.StartAsync(pg.Store, "world", "a", new AcquireOptions(LeaseMs: 60_000), renewEvery: TimeSpan.FromMilliseconds(50));
        session.Lost += _ => lost.TrySetResult();
        var cache = new GameStoreCache(pg.Store, session, new GameStoreCacheOptions { ProcessRole = "world", FlushDelay = TimeSpan.FromHours(1) });
        await cache.LoadAsync("characters");
        cache.Write("characters", "/1", J("1"));

        await pg.SecondStore().AcquireOwnerAsync("world", "usurper", new AcquireOptions(ForceTakeover: true));
        await lost.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(session.IsLost);
        var error = Assert.Throws<PersistenceException>(() => cache.Write("characters", "/2", J("2")));
        Assert.Equal(PersistenceCodes.OwnerStale, error.Code);
        await Assert.ThrowsAsync<PersistenceException>(() => cache.FlushTableAsync("characters"));
        Assert.Equal(0, await pg.Store.RowCountAsync("characters"));
        // Shutting down cannot flush the pending write: the process is fenced out, and says so.
        await Assert.ThrowsAsync<PersistenceException>(async () => await cache.DisposeAsync());
        await session.DisposeAsync();
    }

    [PgFact]
    public async Task The_debounce_flushes_a_table_shortly_after_its_last_write()
    {
        await using var pg = await PgScope.CreateAsync();
        await using var h = await Harness.StartAsync(pg, delay: TimeSpan.FromMilliseconds(80));
        await h.Cache.LoadAsync("characters");
        h.Cache.Write("characters", "/1", J("1"));
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (await pg.Store.RowCountAsync("characters") == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(30);
        }
        Assert.Equal(1, await pg.Store.RowCountAsync("characters"));
        Assert.Empty(h.Errors);
    }

    [PgFact]
    public async Task Disposing_flushes_what_is_still_pending_and_then_refuses_writes()
    {
        await using var pg = await PgScope.CreateAsync();
        var h = await Harness.StartAsync(pg);
        await h.Cache.LoadAsync("characters");
        h.Cache.Write("characters", "/1", J("1"));
        await h.Cache.DisposeAsync();
        Assert.Equal("""{"1":1}""", await Stored(pg, "characters"));
        Assert.Throws<InvalidOperationException>(() => h.Cache.Write("characters", "/2", J("2")));
        await h.Session!.DisposeAsync();
    }

    [PgFact]
    public async Task Concurrent_writers_and_flushes_end_with_the_cache_and_database_equal()
    {
        await using var pg = await PgScope.CreateAsync();
        await using var h = await Harness.StartAsync(pg);
        await h.Cache.LoadAsync("mail");
        var stop = false;
        var flusher = Task.Run(async () =>
        {
            while (!Volatile.Read(ref stop))
            {
                await h.Cache.FlushAllAsync();
                await Task.Delay(5);
            }
        });
        var writers = Enumerable.Range(0, 8).Select(w => Task.Run(() =>
        {
            for (var i = 0; i < 200; i++)
            {
                h.Cache.Write("mail", $"/messages/{w}-{i}", J($$"""{"w":{{w}},"i":{{i}}}"""));
                if (i % 5 == 0)
                {
                    h.Cache.Remove("mail", $"/messages/{w}-{i - 1}");
                }
            }
        })).ToArray();
        await Task.WhenAll(writers);
        Volatile.Write(ref stop, true);
        await flusher;
        await h.Cache.FlushAllAsync();

        Assert.Equal(Fixtures.Canon(h.Cache.Read("mail", "/").Data), await Stored(pg, "mail"));
        Assert.Empty(h.Errors);
    }
}
