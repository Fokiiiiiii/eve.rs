using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Eve.Characters;
using Eve.Data;
using Eve.Host.ProjectDiscovery;
using Eve.Services;
using Npgsql;

namespace Eve.Host.Tests;

public sealed class RemoteDiscoveryProviderTests
{
    [Fact]
    public async Task AssignmentAndSubmissionSurviveRestartAndUnknownNeverResends()
    {
        string suffix = Guid.NewGuid().ToString("N")[..12]; string schema = "pd_" + suffix, pinsSchema = "pd_pin_" + suffix;
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        using var server = new Loopback(); using var http = new HttpClient();
        try
        {
            await using (var init = source.CreateCommand($"CREATE SCHEMA {pinsSchema}; CREATE TABLE {pinsSchema}.project_discovery_task_pins(character_id bigint PRIMARY KEY, task_id bigint NOT NULL, updated_ms bigint NOT NULL)")) await init.ExecuteNonQueryAsync();
            var pins = new ProjectDiscoveryTaskPinStore(source, pinsSchema);
            var client = new MmosApiClient(http, new MmosApiOptions { Enabled = true, BaseUri = server.Uri, Credential = "test", Secret = "test" });
            ProjectDiscoveryBonusPolicy? bonus = null;
            RemoteDiscoveryProvider Make() => new(source, pins, client, new RemoteDiscoveryOptions { Enabled = true, Game = "demo", Project = "mcgill-polyseg" }, schema, bonus);
            // Seed the versionless schema produced by the previous provider revision.
            await using (var legacy = source.CreateCommand($"""
                CREATE SCHEMA {schema};
                CREATE TABLE {schema}.assignments (actor bigint NOT NULL, project text NOT NULL, task_id text NOT NULL, task jsonb NOT NULL, active boolean NOT NULL DEFAULT true, PRIMARY KEY(actor,project,task_id));
                INSERT INTO {schema}.assignments VALUES (999,'legacy','legacy-task',jsonb_build_object('assets',jsonb_build_object('url','legacy')),false);
                """)) await legacy.ExecuteNonQueryAsync();
            var provider = Make();
            Assert.Equal(2, (await provider.MigrateAsync()).Count); Assert.Empty(await provider.MigrateAsync());
            await using (var setupBonus = source.CreateCommand($"CREATE TABLE {schema}.project_discovery_bonus_days(character_id bigint,day date,used integer,PRIMARY KEY(character_id,day)); CREATE TABLE {schema}.project_discovery_bonus_receipts(character_id bigint,assignment text,day date,experience bigint,PRIMARY KEY(character_id,assignment)); CREATE TABLE {schema}.project_discovery_progress(character_id bigint PRIMARY KEY,experience bigint NOT NULL)")) await setupBonus.ExecuteNonQueryAsync();
            bonus = new(source, schema, 2, 75, 0); provider = Make();
            await using (var preserved = source.CreateCommand($"SELECT task->'assets'->>'url' FROM {schema}.assignments WHERE actor=999"))
                Assert.Equal("legacy", await preserved.ExecuteScalarAsync());
            var alice = new ClientSession { CharacterId = 101, UserId = 201 }; var bob = new ClientSession { CharacterId = 102, UserId = 202 };
            await pins.SetAsync(101, 987, 0);
            await Assert.ThrowsAsync<DiscoveryAssignmentException>(() => provider.GetTaskAsync(alice));
            await pins.SetAsync(101, null, 0);
            var allocate = provider.GetTaskAsync(alice); var request = await server.AcceptAsync();
            Assert.Equal("/games/demo/players/101/tasks", request.Request.RawUrl);
            using (var input = new StreamReader(request.Request.InputStream, Encoding.UTF8))
            using (var allocationBody = JsonDocument.Parse(await input.ReadToEndAsync()))
                Assert.Equal("201", allocationBody.RootElement.GetProperty("player").GetProperty("accountCode").GetString());
            await ReplyAsync(request, "{\"uid\":\"u\",\"game\":\"demo\",\"player\":{\"code\":\"101\"},\"task\":{\"id\":\"123\",\"assets\":{\"url\":\"https://example.test/a\",\"dimensions\":[400,300]}}}");
            Assert.Equal("123", (await allocate).GetProperty("id").GetString());
            provider = Make(); await pins.SetAsync(101, 123, 0);
            Assert.Equal(400, (await provider.GetTaskAsync(alice)).GetProperty("assets").GetProperty("dimensions")[0].GetInt32());
            await pins.SetAsync(102, 123, 0);
            await Assert.ThrowsAsync<DiscoveryAssignmentException>(() => provider.GetTaskAsync(bob));
            await pins.SetAsync(101, null, 0);
            var wrongAccount = new ClientSession { CharacterId = 101, UserId = 999 };
            await Assert.ThrowsAsync<DiscoveryAssignmentException>(() => provider.GetTaskAsync(wrongAccount));
            using var result = JsonDocument.Parse("{\"skip\":false,\"polygons\":[]}"); using var time = JsonDocument.Parse("{\"t\":100}");
            await Assert.ThrowsAsync<DiscoveryAssignmentException>(() => provider.SubmitAsync(wrongAccount, "wrong", result.RootElement, time.RootElement));
            var submit = provider.SubmitAsync(alice, "call-1", result.RootElement, time.RootElement);
            request = await server.AcceptAsync(); Assert.Equal("/classifications", request.Request.RawUrl);
            await ReplyAsync(request, "{\"uid\":\"c\",\"player\":{\"score\":0.8},\"task\":{\"id\":\"123\"},\"XP_Reward\":999,\"ISK_Reward\":999,\"playerState\":{\"rank\":99},\"AK_Reward\":9,\"loot_crates\":9,\"tier_reward\":true,\"bonusSamplesAfterClassification\":9,\"custom\":42}");
            var submitted = await submit;
            Assert.Equal(42, submitted.GetProperty("custom").GetInt32());
            Assert.Equal(75, submitted.GetProperty("XP_Reward").GetInt32());
            Assert.True(submitted.GetProperty("gotBonusXP").GetBoolean());
            Assert.False(submitted.TryGetProperty("ISK_Reward", out _)); Assert.False(submitted.TryGetProperty("playerState", out _));
            foreach (var key in new[] { "AK_Reward", "loot_crates", "tier_reward", "bonusSamplesAfterClassification" }) Assert.False(submitted.TryGetProperty(key, out _));
            Assert.Equal(1, await bonus.ReadAsync(101, "get_remaining_bonus_samples", default));
            provider = Make();
            var replay = await provider.SubmitAsync(alice, "call-1", result.RootElement, time.RootElement);
            Assert.True(JsonElement.DeepEquals(submitted, replay));
            await Assert.ThrowsAsync<DiscoveryAssignmentException>(() => provider.SubmitAsync(wrongAccount, "call-1", result.RootElement, time.RootElement));
            using var changed = JsonDocument.Parse("{\"skip\":true}");
            await Assert.ThrowsAsync<DiscoveryAssignmentException>(() => provider.SubmitAsync(alice, "call-1", changed.RootElement, time.RootElement));
            var allocateNext = provider.GetTaskAsync(alice); request = await server.AcceptAsync();
            await ReplyAsync(request, "{\"uid\":\"u2\",\"game\":\"demo\",\"player\":{},\"task\":{\"id\":\"124\",\"assets\":{\"url\":\"https://example.test/b\"}}}"); await allocateNext;
            var unknown = provider.SubmitAsync(alice, "call-2", result.RootElement, time.RootElement); request = await server.AcceptAsync(); request.Response.Abort();
            await Assert.ThrowsAsync<DiscoverySubmissionUnknownException>(() => unknown);
            Assert.Equal(1, await bonus.ReadAsync(101, "get_remaining_bonus_samples", default));
            await using (var xp = source.CreateCommand($"SELECT experience FROM {schema}.project_discovery_progress WHERE character_id=101")) Assert.Equal(75L, await xp.ExecuteScalarAsync());
            provider = Make(); await Assert.ThrowsAsync<DiscoverySubmissionUnknownException>(() => provider.SubmitAsync(alice, "call-2", result.RootElement, time.RootElement));
            await Assert.ThrowsAsync<DiscoverySubmissionUnknownException>(() => provider.SubmitAsync(alice, "call-3", result.RootElement, time.RootElement));
            // Simulate a crash after the durable send claim and before any result.
            await using (var crash = source.CreateCommand($"UPDATE {schema}.submissions SET state='sending' WHERE actor=101 AND call_id='call-2'")) await crash.ExecuteNonQueryAsync();
            provider = Make();
            await Assert.ThrowsAsync<DiscoverySubmissionUnknownException>(() => provider.SubmitAsync(alice, "call-2", result.RootElement, time.RootElement));
            await Assert.ThrowsAsync<DiscoveryAssignmentException>(() => provider.GetTaskAsync(new ClientSession { CharacterId = 103 }));
            using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await server.AcceptAsync().WaitAsync(timeout.Token));
        }
        finally { await using var clean = source.CreateCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE; DROP SCHEMA IF EXISTS {pinsSchema} CASCADE"); await clean.ExecuteNonQueryAsync(); }
    }
    private static async Task ReplyAsync(HttpListenerContext context, string body)
    { byte[] bytes = Encoding.UTF8.GetBytes(body); context.Response.StatusCode = 201; context.Response.ContentLength64 = bytes.Length; await context.Response.OutputStream.WriteAsync(bytes); context.Response.Close(); }
    private sealed class Loopback : IDisposable
    {
        private readonly HttpListener listener = new(); public Uri Uri { get; }
        public Loopback() { var probe = new TcpListener(IPAddress.Loopback, 0); probe.Start(); int port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop(); Uri = new Uri($"http://127.0.0.1:{port}/"); listener.Prefixes.Add(Uri.ToString()); listener.Start(); }
        public Task<HttpListenerContext> AcceptAsync() => listener.GetContextAsync(); public void Dispose() => listener.Close();
    }
}
