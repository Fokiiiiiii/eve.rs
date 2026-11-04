using System.Text.Json;
using Eve.Data;
using Eve.Host.ProjectDiscovery;
using Eve.Services;
using Npgsql;

namespace Eve.Host.Tests;

public sealed class ScientificReplayProviderTests
{
    [Fact]
    public async Task RealScientificTaskIsDurableActorScopedAndScoresNewAnswerAgainstAllThirty()
    {
        string schema = "pd_science_" + Guid.NewGuid().ToString("N")[..12];
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        string root = FindFixtures();
        var options = new ScientificReplayOptions
        {
            Enabled = true, DatasetKey = "frdr-1038-analysis-50", CsvGzipPath = Path.Combine(root, "covid-analysis50.csv.gz"),
            CsvGzipSha256 = "a458f60d4e299a8c5ca31626452be1f46e82bc2b05fde6611fae3ab87e905425",
            CohortPath = Path.Combine(root, "covid-analysis50-gates.json"), CohortSha256 = "9810f2bb0c77eb4f8f17e7ddb624f12a0f2218b94f690d8b6e1833fe23ff1539",
            AssetUri = new Uri("http://127.0.0.1:12345/projectdiscovery/covid-analysis50-science.png"), AssetSha256 = "766bb12193db2cd09f0e2881d1c8de865e7747afe102182f2d97e40520b469a4", Width = 530, Height = 530,
            MinX = 23.201007270813, MinY = -30.203549385070783, MaxX = 128.337999916077, MaxY = 196.826071739197
        };
        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => ScientificReplayProvider.CreateAsync(source, options with { CohortSha256 = new string('0', 64) }, schema));
            var provider = await ScientificReplayProvider.CreateAsync(source, options, schema);
            Assert.Single(await provider.MigrateAsync()); Assert.Empty(await provider.MigrateAsync());
            await using (var setupBonus = source.CreateCommand($"CREATE TABLE {schema}.project_discovery_bonus_days(character_id bigint,day date,used integer,PRIMARY KEY(character_id,day)); CREATE TABLE {schema}.project_discovery_bonus_receipts(character_id bigint,assignment text,day date,experience bigint,PRIMARY KEY(character_id,assignment)); CREATE TABLE {schema}.project_discovery_progress(character_id bigint PRIMARY KEY,experience bigint NOT NULL)")) await setupBonus.ExecuteNonQueryAsync();
            var bonus = new ProjectDiscoveryBonusPolicy(source, schema, 1, 75, 0);
            options = options with { Bonus = bonus }; provider = await ScientificReplayProvider.CreateAsync(source, options, schema);
            var alice = new ClientSession { CharacterId = 1001, UserId = 2001 };
            var bob = new ClientSession { CharacterId = 1002, UserId = 2002 };
            var task = await provider.GetTaskAsync(alice);
            Assert.Equal(options.AssetUri!.AbsoluteUri, task.GetProperty("assets").GetProperty("url").GetString());
            Assert.Equal(530, task.GetProperty("assets").GetProperty("dimensions")[0].GetInt32());
            Assert.False(task.TryGetProperty("taskID", out _));
            Assert.Equal(task.GetRawText(), (await provider.GetTaskAsync(alice)).GetRawText());
            await Assert.ThrowsAsync<DiscoveryAssignmentException>(() => provider.GetTaskAsync(new ClientSession { CharacterId = 1001, UserId = 9999 }));
            using var timing = JsonDocument.Parse("{\"t\":12,\"tPolygons\":[]}");
            using var skip = JsonDocument.Parse("{\"skip\":true,\"polygons\":[]}");
            await Assert.ThrowsAsync<DiscoveryAssignmentException>(() => provider.SubmitAsync(bob, "skip", skip.RootElement, timing.RootElement));
            using var cohort = JsonDocument.Parse(await File.ReadAllTextAsync(options.CohortPath));
            var vertices = cohort.RootElement[0].GetProperty("polygons").EnumerateArray().Select(x => new { vertices = x }).ToArray();
            var result = JsonSerializer.SerializeToElement(new { skip = false, polygons = vertices });
            await Assert.ThrowsAsync<DiscoveryAssignmentException>(() => provider.SubmitAsync(bob, "call", result, timing.RootElement));
            var response = await provider.SubmitAsync(alice, "call", result, timing.RootElement);
            Assert.InRange(Math.Abs(response.GetProperty("player").GetProperty("score").GetDouble() - 0.99115), 0, 1e-10);
            Assert.Equal(75, response.GetProperty("XP_Reward").GetInt32()); Assert.True(response.GetProperty("gotBonusXP").GetBoolean()); Assert.False(response.TryGetProperty("ISK_Reward", out _)); Assert.False(response.TryGetProperty("isSolved", out _));
            // Delivery port is not scientific task identity.
            provider = await ScientificReplayProvider.CreateAsync(source, options with { AssetUri = new Uri("http://127.0.0.1:12346/projectdiscovery/covid-analysis50-science.png") }, schema);
            Assert.Equal(response.GetProperty("player").GetProperty("score").GetDouble(), (await provider.SubmitAsync(alice, "call", result, timing.RootElement)).GetProperty("player").GetProperty("score").GetDouble());
            using var changedTime = JsonDocument.Parse("{\"t\":13}");
            await Assert.ThrowsAsync<DiscoveryAssignmentException>(() => provider.SubmitAsync(alice, "call", result, changedTime.RootElement));
            await Assert.ThrowsAsync<DiscoveryAssignmentException>(() => provider.SubmitAsync(new ClientSession { CharacterId = 1001, UserId = 9999 }, "call", result, timing.RootElement));
            await Assert.ThrowsAsync<ScientificReplayExhaustedException>(() => provider.GetTaskAsync(alice));
            await Assert.ThrowsAsync<ScientificReplayExhaustedException>(() => provider.SubmitAsync(alice, "another", result, timing.RootElement));
            Assert.Equal("http://127.0.0.1:12346/projectdiscovery/covid-analysis50-science.png", (await provider.GetTaskAsync(bob)).GetProperty("assets").GetProperty("url").GetString());
            var skipped = await provider.SubmitAsync(bob, "skip", skip.RootElement, timing.RootElement);
            Assert.True(skipped.GetProperty("skipped").GetBoolean()); Assert.False(skipped.TryGetProperty("player", out _));
            Assert.Equal(1, await bonus.ReadAsync(1002, "get_remaining_bonus_samples", default));
            Assert.Equal(0, await bonus.ReadAsync(1001, "get_remaining_bonus_samples", default));
            provider = await ScientificReplayProvider.CreateAsync(source, options, schema);
            Assert.True((await provider.SubmitAsync(bob, "skip", skip.RootElement, timing.RootElement)).GetProperty("skipped").GetBoolean());
            await Assert.ThrowsAsync<DiscoveryAssignmentException>(() => provider.SubmitAsync(bob, "skip", result, timing.RootElement));
            await Assert.ThrowsAsync<ScientificReplayExhaustedException>(() => provider.GetTaskAsync(bob));
        }
        finally { await using var cleanup = source.CreateCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE"); await cleanup.ExecuteNonQueryAsync(); }
    }
    private static string FindFixtures()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null) { var candidate = Path.Combine(dir.FullName, "dotnet", "tests", "Eve.Host.Tests", "fixtures", "project-discovery"); if (Directory.Exists(candidate)) return candidate; dir = dir.Parent; }
        throw new DirectoryNotFoundException();
    }
}
