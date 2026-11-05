using Eve.Characters;
using Eve.Data;
using Eve.Identity;
using Eve.Marshal;
using Eve.Macho;
using Eve.Services;
using Eve.Services.Core;
using Npgsql;
using Xunit;

namespace Eve.Host.Tests;

/// <summary>
/// The Project Discovery tutorial and the season reads through the host's own registry (CH-4): the tutorial answer comes from the character's stored
/// tutorial, a character with none has not started, and no season is active.
/// </summary>
[Collection("host-composition")]
public sealed class ProjectDiscoveryHostTests : IAsyncLifetime
{
    /// <summary>The environment the host reads: every schema on its own scratch name, plus the no-simulation switch.</summary>
    private static readonly string[] SchemaVariables =
    [
        "EVE_IDENTITY_SCHEMA", "EVE_GLOBAL_CONFIG_SCHEMA", "EVE_CHARACTERS_SCHEMA", "EVE_INVENTORY_SCHEMA", "EVE_SKILLS_SCHEMA", "EVE_WALLET_SCHEMA",
        "EVE_FITTING_SCHEMA", "EVE_NPC_SCHEMA", "EVE_SPACE_SCHEMA", "EVE_GAMESTORE_SCHEMA", "EVE_ASSET_SAFETY_SCHEMA", "EVE_CORPORATIONS_SCHEMA",
        "EVE_MARKET_SCHEMA", "EVE_FLEET_SCHEMA", "EVE_INDUSTRY_SCHEMA", "EVE_SCHEDULER_SCHEMA", "EVE_EVENTS_SCHEMA", "EVE_RELATIONS_SCHEMA",
        "EVE_CONTENT_SCHEMA", "EVE_CRIMEWATCH_SCHEMA", "EVE_STRUCTURES_SCHEMA", "EVE_PROJECT_DISCOVERY_SCHEMA",
        "EVE_TRADE_SCHEMA", "EVE_BOOKMARKS_SCHEMA", "EVE_ESS_SCHEMA", "EVE_CONTRABAND_SCHEMA",
    ];

    private readonly string _prefix = "pd" + Guid.NewGuid().ToString("N")[..10];
    private readonly Dictionary<string, string?> _saved = [];
    private readonly List<string> _schemas = [];

    private string Schema(string variable) => $"{_prefix}_{variable["EVE_".Length..^"_SCHEMA".Length].ToLowerInvariant()}";

    private static string Url => Environment.GetEnvironmentVariable("EVE_PG_URL")!;

    public Task InitializeAsync()
    {
        foreach (var variable in SchemaVariables)
        {
            _saved[variable] = Environment.GetEnvironmentVariable(variable);
            var schema = Schema(variable);
            _schemas.Add(schema);
            Environment.SetEnvironmentVariable(variable, schema);
        }
        foreach (var (name, value) in new[] { ("EVE_SIM", "off"), ("EVE_MARKET_SEED", "skip"), ("EVE_WORMHOLES", "off") })
        {
            _saved[name] = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        var url = Environment.GetEnvironmentVariable("EVE_PG_URL");
        if (url is not null)
        {
            await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(url));
            foreach (var schema in _schemas)
            {
                await using var command = source.CreateCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE");
                await command.ExecuteNonQueryAsync();
            }
        }
        foreach (var (name, value) in _saved) Environment.SetEnvironmentVariable(name, value);
    }

    [PgFact]
    public async Task Initialize_tutorial_status_answers_the_stored_tutorial_through_the_host_registry()
    {
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(Url));
        var host = await HostComposition.BuildAsync(Settings());
        try
        {
            var characters = new PgCharacterStore(source, Schema("EVE_CHARACTERS_SCHEMA"));
            var finished = await InsertPilotAsync(source, characters, "Tutorial Finished");
            var fresh = await InsertPilotAsync(source, characters, "Tutorial Fresh");
            await new ProjectDiscoveryTutorialStore(source, Schema("EVE_CHARACTERS_SCHEMA"))
                .WriteAsync(finished, new ProjectDiscoveryTutorial(true, 8), 1_000);

            var projectDiscovery = host.Registry.Lookup("ProjectDiscovery")!;
            Assert.Same(PyBool.True, await projectDiscovery.CallAsync(new ServiceCall
            {
                Args = [],
                Method = "initialize_tutorial_status",
                Session = new ClientSession { CharacterId = finished },
            }));
            Assert.Same(PyBool.False, await projectDiscovery.CallAsync(new ServiceCall
            {
                Args = [],
                Method = "initialize_tutorial_status",
                Session = new ClientSession { CharacterId = fresh },
            }));
        }
        finally
        {
            await host.ShutdownAsync();
        }
    }

    [PgFact]
    public async Task Project_discovery_tutorial_transitions_persist_through_the_registered_service()
    {
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(Url));
        var host = await HostComposition.BuildAsync(Settings());
        try
        {
            var characters = new PgCharacterStore(source, Schema("EVE_CHARACTERS_SCHEMA"));
            var characterId = await InsertPilotAsync(source, characters, "Tutorial Transition");
            var schema = Schema("EVE_CHARACTERS_SCHEMA");
            var store = new ProjectDiscoveryTutorialStore(source, schema);
            Assert.Null(await store.ReadAsync(characterId));
            var service = host.Registry.Lookup("ProjectDiscovery")!;
            var session = new ClientSession { CharacterId = characterId };

            var first = await service.CallAsync(new ServiceCall { Method = "increase_tutorial_level", Args = [], Session = session });
            Assert.Equal(Py.KeyVal(
                ("rank", new PyInt(1)),
                ("experience", new PyInt(0)),
                ("analysisKredits", new PyInt(0)),
                ("finishedTutorial", PyBool.False),
                ("tutorialLevel", new PyInt(1))), first);
            Assert.Equal(new ProjectDiscoveryTutorial(false, 1),
                await new ProjectDiscoveryTutorialStore(source, schema).ReadAsync(characterId));

            var second = await service.CallAsync(new ServiceCall { Method = "increase_tutorial_level", Args = [], Session = session });
            Assert.Equal(Py.KeyVal(
                ("rank", new PyInt(1)),
                ("experience", new PyInt(0)),
                ("analysisKredits", new PyInt(0)),
                ("finishedTutorial", PyBool.False),
                ("tutorialLevel", new PyInt(2))), second);
            Assert.Same(PyBool.False, await service.CallAsync(new ServiceCall
            {
                Method = "get_tutorial_completion_status", Args = [], Session = session,
            }));

            for (var level = 3; level <= 8; level++)
            {
                _ = await service.CallAsync(new ServiceCall { Method = "increase_tutorial_level", Args = [], Session = session });
                Assert.Same(level == 8 ? PyBool.True : PyBool.False, await service.CallAsync(new ServiceCall
                {
                    Method = "get_tutorial_completion_status", Args = [], Session = session,
                }));
                Assert.Equal(new ProjectDiscoveryTutorial(level == 8, level), await store.ReadAsync(characterId));
            }

            var reset = await service.CallAsync(new ServiceCall { Method = "reset_tutorial", Args = [], Session = session });
            Assert.Equal(Py.KeyVal(
                ("rank", new PyInt(1)),
                ("experience", new PyInt(0)),
                ("analysisKredits", new PyInt(0)),
                ("finishedTutorial", PyBool.False),
                ("tutorialLevel", new PyInt(0))), reset);
            Assert.Equal(new ProjectDiscoveryTutorial(false, 0), await store.ReadAsync(characterId));

            await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => service.CallAsync(new ServiceCall
            {
                Method = "increase_tutorial_level", Args = [], Session = session,
            }).AsTask()));
            Assert.Equal(new ProjectDiscoveryTutorial(false, 2), await store.ReadAsync(characterId));

            var skipped = await service.CallAsync(new ServiceCall { Method = "skip_tutorial", Args = [], Session = session });
            Assert.Equal(Py.KeyVal(
                ("rank", new PyInt(1)),
                ("experience", new PyInt(0)),
                ("analysisKredits", new PyInt(0)),
                ("finishedTutorial", PyBool.True),
                ("tutorialLevel", new PyInt(8))), skipped);
            Assert.Equal(new ProjectDiscoveryTutorial(true, 8), await store.ReadAsync(characterId));
        }
        finally
        {
            await host.ShutdownAsync();
        }
    }

    [PgFact]
    public async Task Project_discovery_task_pins_are_character_scoped_and_survive_host_restart()
    {
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(Url));
        var host = await HostComposition.BuildAsync(Settings());
        try
        {
            var characters = new PgCharacterStore(source, Schema("EVE_CHARACTERS_SCHEMA"));
            var characterId = await InsertPilotAsync(source, characters, "Task Pin Owner");
            var otherCharacterId = await InsertPilotAsync(source, characters, "Other Task Pin Owner");
            var session = new ClientSession { CharacterId = characterId };
            var service = host.Registry.Lookup("ProjectDiscovery")!;
            await Assert.ThrowsAsync<MachoWrappedException>(() => service.CallAsync(new ServiceCall
            {
                Method = "fix_task_id",
                Args = [new PyLong(12)],
                Session = session,
            }).AsTask());
            session.Set("accountRole", new PyLong(Eve.Services.GameMasterRoles.Qa));
            await service.CallAsync(new ServiceCall
            {
                Method = "fix_task_id",
                Args = [new PyLong(1234567890123)],
                Session = session,
            });
            var pins = new ProjectDiscoveryTaskPinStore(source, Schema("EVE_CHARACTERS_SCHEMA"));
            Assert.Equal(1234567890123L, await pins.ReadAsync(characterId));
            Assert.Null(await pins.ReadAsync(otherCharacterId));

            await host.ShutdownAsync();
            host = await HostComposition.BuildAsync(Settings());
            Assert.Equal(1234567890123L, await pins.ReadAsync(characterId));

            await Assert.ThrowsAsync<MachoWrappedException>(() => host.Registry.Lookup("ProjectDiscovery")!.CallAsync(new ServiceCall
            {
                Method = "fix_task_id",
                Args = [new PyLong(9)],
                Session = new ClientSession(),
            }).AsTask());
            await host.Registry.Lookup("ProjectDiscovery")!.CallAsync(new ServiceCall
            {
                Method = "fix_task_id",
                Args = [new PyLong(-9)],
                Session = session,
            });
            Assert.Null(await pins.ReadAsync(characterId));
        }
        finally
        {
            await host.ShutdownAsync();
        }
    }

    [PgFact]
    public async Task Season_reads_answer_no_active_season_through_the_host_registry()
    {
        var host = await HostComposition.BuildAsync(Settings());
        try
        {
            var seasons = host.Registry.Lookup("seasonManager")!;
            var session = new ClientSession { CharacterId = 98000001 };
            Assert.Same(PyNone.Instance, await seasons.CallAsync(new ServiceCall { Args = [], Method = "get_season_data_for_character", Session = session }));
            Assert.Same(PyNone.Instance, await seasons.CallAsync(new ServiceCall { Method = "select_season", Args = [new PyInt(1)], Session = session }));
            Assert.IsType<PyLong>(await seasons.CallAsync(new ServiceCall { Args = [], Method = "get_season_end_time", Session = session }));
            Assert.Equal(new PyInt(0), await seasons.CallAsync(new ServiceCall { Args = [], Method = "get_max_active_challenges", Session = session }));
            Assert.Equal(
                Py.KeyVal(
                    ("character_id", new PyLong(98000001)),
                    ("season_id", PyNone.Instance),
                    ("seasonal_points", new PyInt(0)),
                    ("max_seasonal_points", new PyInt(1)),
                    ("seasonal_goals", new PyDict([])),
                    ("next_seasonal_goal", PyNone.Instance),
                    ("last_seasonal_goal", PyNone.Instance),
                    ("challenges", new PyDict([]))),
                await seasons.CallAsync(new ServiceCall { Args = [], Method = "get_character_progress_by_character_id", Session = session }));
        }
        finally
        {
            await host.ShutdownAsync();
        }
    }

    [PgFact]
    public async Task Bundled_replay_is_registered_and_restores_score_after_host_restart()
    {
        var settings = new Dictionary<string,string> { ["EVE_PROJECT_DISCOVERY_BONUS_POLICY"] = "daily", ["EVE_PROJECT_DISCOVERY_BONUS_DAILY_MAX"] = "1", ["EVE_PROJECT_DISCOVERY_BONUS_XP"] = "200", ["EVE_PROJECT_DISCOVERY_BONUS_RESET_UTC_HOUR"] = "0" };
        var savedBonus = settings.Keys.ToDictionary(k => k, Environment.GetEnvironmentVariable);
        foreach (var (key, value) in settings) Environment.SetEnvironmentVariable(key, value);
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(Url));
        var host = await HostComposition.BuildAsync(Settings());
        try
        {
            var characters = new PgCharacterStore(source, Schema("EVE_CHARACTERS_SCHEMA"));
            var characterId = await InsertPilotAsync(source, characters, "Replay Pilot");
            var session = new ClientSession { CharacterId = characterId, UserId = 1, SessionId = 77 };
            PyValue Field(PyValue value, string key) => Assert.Single(Assert.IsType<PyDict>(value).Entries, e => JsSemantics.StringValue(e.Key) == key).Value;
            var service = host.Registry.Lookup("ProjectDiscovery")!;
            Assert.Equal(new PyInt(1), await service.CallAsync(new ServiceCall { Method = "get_maximum_bonus_samples", Args = [], Session = session }));
            Assert.Equal(new PyInt(1), await service.CallAsync(new ServiceCall { Method = "get_remaining_bonus_samples", Args = [], Session = session }));
            var task = await service.CallAsync(new ServiceCall { Method = "get_new_task", Args = [], Session = session });
            var url = JsSemantics.StringValue(Field(Field(task, "assets"), "url"))!;
            using var http = new HttpClient();
            Assert.True((await http.GetByteArrayAsync(url)).AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }));
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "dotnet", "tests", "Eve.Host.Tests", "fixtures", "project-discovery"))) directory = directory.Parent;
            Assert.NotNull(directory);
            using var cohort = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory!.FullName, "dotnet", "tests", "Eve.Host.Tests", "fixtures", "project-discovery", "covid-analysis50-gates.json")));
            PyDict Dict(params (string Key, PyValue Value)[] entries) => new(entries.Select(e => new KeyValuePair<PyValue, PyValue>(new PyStr(e.Key), e.Value)).ToArray());
            var polygons = cohort.RootElement[0].GetProperty("polygons").EnumerateArray().Select(poly => (PyValue)Dict(("vertices", new PyList(poly.EnumerateArray().Select(point => (PyValue)new PyList([new PyReal(point[0].GetDouble()), new PyReal(point[1].GetDouble())])).ToArray())))).ToArray();
            var answer = Dict(("skip", PyBool.False), ("polygons", new PyList(polygons)));
            var timing = Dict(("t", new PyInt(10)), ("tPolygons", new PyList([])));
            var result = await service.CallAsync(new ServiceCall { Method = "post_classification", Args = [answer, timing], Session = session, CallId = new PyInt(2) });
            var score = Assert.IsType<PyReal>(Field(Field(result, "player"), "score")).Value;
            Assert.InRange(Math.Abs(score - .99115), 0, 1e-10);
            Assert.Equal(new PyInt(0), await service.CallAsync(new ServiceCall { Method = "get_remaining_bonus_samples", Args = [], Session = session }));
            await host.ShutdownAsync();
            host = await HostComposition.BuildAsync(Settings());
            var stats = await host.Registry.Lookup("ProjectDiscovery")!.CallAsync(new ServiceCall { Method = "get_player_statistics", Args = [], Session = session });
            Assert.Equal(score, Assert.IsType<PyReal>(Field(Field(stats, "project"), "score")).Value);
            var replay = await host.Registry.Lookup("ProjectDiscovery")!.CallAsync(new ServiceCall { Method = "post_classification", Args = [answer, timing], Session = session, CallId = new PyInt(2) });
            Assert.Equal(score, Assert.IsType<PyReal>(Field(Field(replay, "player"), "score")).Value);
            var state = await host.Registry.Lookup("ProjectDiscovery")!.CallAsync(new ServiceCall { Method = "get_player_state", Args = [], Session = session });
            Assert.Equal(Py.KeyVal(("rank", new PyInt(2)), ("experience", new PyInt(200)), ("analysisKredits", new PyInt(0)), ("finishedTutorial", PyBool.False), ("tutorialLevel", new PyInt(0))), state);
        }
        finally { await host.ShutdownAsync(); foreach (var (key, value) in savedBonus) Environment.SetEnvironmentVariable(key, value); }
    }

    [PgFact]
    public async Task Asset_listener_closes_after_shutdown_and_failed_host_startup()
    {
        var reserve = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        reserve.Start();
        int port = ((System.Net.IPEndPoint)reserve.LocalEndpoint).Port;
        reserve.Stop();
        string? oldPort = Environment.GetEnvironmentVariable("EVE_PROJECT_DISCOVERY_ASSET_PORT");
        string? oldSurvey = Environment.GetEnvironmentVariable("EVE_ACTIVE_SURVEY_ID");
        string? oldBonus = Environment.GetEnvironmentVariable("EVE_PROJECT_DISCOVERY_BONUS_POLICY");
        try
        {
            Environment.SetEnvironmentVariable("EVE_PROJECT_DISCOVERY_ASSET_PORT", port.ToString());
            Environment.SetEnvironmentVariable("EVE_ACTIVE_SURVEY_ID", null);
            var host = await HostComposition.BuildAsync(Settings());
            await host.ShutdownAsync();
            var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, port);
            probe.Start(); probe.Stop();
            Environment.SetEnvironmentVariable("EVE_ACTIVE_SURVEY_ID", "invalid");
            await Assert.ThrowsAsync<InvalidOperationException>(() => HostComposition.BuildAsync(Settings()));
            probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, port);
            probe.Start(); probe.Stop();
            await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(Url));
            await using var owner = source.CreateCommand($"SELECT active FROM {Schema("EVE_GAMESTORE_SCHEMA")}._persistence_owners WHERE owner_role = 'world'");
            Assert.False(Assert.IsType<bool>(await owner.ExecuteScalarAsync()));
            Environment.SetEnvironmentVariable("EVE_ACTIVE_SURVEY_ID", null);
            Environment.SetEnvironmentVariable("EVE_PROJECT_DISCOVERY_BONUS_POLICY", "invalid");
            await Assert.ThrowsAsync<InvalidOperationException>(() => HostComposition.BuildAsync(Settings()));
            Assert.False(Assert.IsType<bool>(await owner.ExecuteScalarAsync()));
            await using var activity = source.CreateCommand("SELECT count(*) FROM pg_stat_activity WHERE datname = current_database() AND pid <> pg_backend_pid() AND (position($1 in query) > 0 OR position($2 in query) > 0)");
            activity.Parameters.AddWithValue(Schema("EVE_EVENTS_SCHEMA"));
            activity.Parameters.AddWithValue(Schema("EVE_SCHEDULER_SCHEMA"));
            Assert.Equal(0L, Assert.IsType<long>(await activity.ExecuteScalarAsync()));
        }
        finally
        {
            Environment.SetEnvironmentVariable("EVE_PROJECT_DISCOVERY_ASSET_PORT", oldPort);
            Environment.SetEnvironmentVariable("EVE_ACTIVE_SURVEY_ID", oldSurvey);
            Environment.SetEnvironmentVariable("EVE_PROJECT_DISCOVERY_BONUS_POLICY", oldBonus);
        }
    }

    [PgFact]
    public async Task Explicit_remote_mode_uses_mock_upstream_through_host_registry()
    {
        var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0); probe.Start();
        int port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
        using var upstream = new System.Net.HttpListener(); upstream.Prefixes.Add($"http://127.0.0.1:{port}/"); upstream.Start();
        var variables = new Dictionary<string,string>
        {
            ["EVE_PROJECT_DISCOVERY_MODE"] = "remote", ["EVE_PROJECT_DISCOVERY_MMOS_ORIGIN"] = $"http://127.0.0.1:{port}/",
            ["EVE_PROJECT_DISCOVERY_MMOS_CREDENTIAL"] = "fixture", ["EVE_PROJECT_DISCOVERY_MMOS_SECRET"] = "fixture",
            ["EVE_PROJECT_DISCOVERY_MMOS_GAME"] = "demo", ["EVE_PROJECT_DISCOVERY_MMOS_PROJECT"] = "mcgill-polyseg"
        };
        var saved = variables.Keys.ToDictionary(k => k, Environment.GetEnvironmentVariable);
        foreach (var (key, value) in variables) Environment.SetEnvironmentVariable(key, value);
        HostRuntime? host = null;
        try
        {
            host = await HostComposition.BuildAsync(Settings());
            var service = host.Registry.Lookup("ProjectDiscovery")!;
            var session = new ClientSession { CharacterId = 101, UserId = 201, SessionId = 9 };
            var pending = service.CallAsync(new ServiceCall { Method = "get_new_task", Args = [], Session = session }).AsTask();
            var req = await upstream.GetContextAsync();
            Assert.Equal("/games/demo/players/101/tasks", req.Request.RawUrl);
            Assert.Equal("fixture", req.Request.Headers["X-MMOS-Credential"]);
            async Task Reply(System.Net.HttpListenerContext context, string body)
            { var bytes = System.Text.Encoding.UTF8.GetBytes(body); context.Response.ContentLength64 = bytes.Length; await context.Response.OutputStream.WriteAsync(bytes); context.Response.Close(); }
            await Reply(req, "{\"uid\":\"u\",\"game\":\"demo\",\"player\":{},\"task\":{\"id\":\"123\",\"assets\":{\"url\":\"https://example.test/a\"}}}");
            Assert.IsType<PyDict>(await pending);
            var result = new PyDict([new(new PyStr("skip"), PyBool.False), new(new PyStr("polygons"), new PyList([]))]);
            var timing = new PyDict([new(new PyStr("t"), new PyInt(10))]);
            var post = service.CallAsync(new ServiceCall { Method = "post_classification", Args = [result, timing], Session = session, CallId = new PyInt(2) }).AsTask();
            req = await upstream.GetContextAsync(); Assert.Equal("/classifications", req.Request.RawUrl);
            await Reply(req, "{\"uid\":\"c\",\"player\":{\"score\":0.75},\"task\":{\"id\":\"123\"}}");
            Assert.IsType<PyDict>(await post);
            await host.ShutdownAsync();
            host = await HostComposition.BuildAsync(Settings());
            service = host.Registry.Lookup("ProjectDiscovery")!;
            Assert.IsType<PyDict>(await service.CallAsync(new ServiceCall { Method = "post_classification", Args = [result, timing], Session = session, CallId = new PyInt(2) }));
            var statsTask = service.CallAsync(new ServiceCall { Method = "get_player_statistics", Args = [], Session = session }).AsTask();
            req = await upstream.GetContextAsync(); Assert.Equal("/games/demo/players/101?project=mcgill-polyseg", req.Request.RawUrl);
            await Reply(req, "{\"uid\":\"u\",\"game\":\"demo\",\"player\":{},\"project\":{\"score\":0.75}}");
            var stats = Assert.IsType<PyDict>(await statsTask);
            var project = Assert.IsType<PyDict>(Assert.Single(stats.Entries, e => JsSemantics.StringValue(e.Key) == "project").Value);
            Assert.Equal(.75, Assert.IsType<PyReal>(Assert.Single(project.Entries, e => JsSemantics.StringValue(e.Key) == "score").Value).Value);
        }
        finally
        {
            if (host is not null) await host.ShutdownAsync();
            foreach (var (key, value) in saved) Environment.SetEnvironmentVariable(key, value);
        }
    }

    [PgFact]
    public async Task Participation_policy_grants_xp_once_and_skip_is_not_eligible()
    {
        string? oldPolicy = Environment.GetEnvironmentVariable("EVE_PROJECT_DISCOVERY_TUTORIAL_REWARD_POLICY");
        string? oldXp = Environment.GetEnvironmentVariable("EVE_PROJECT_DISCOVERY_TUTORIAL_XP");
        string? oldMode = Environment.GetEnvironmentVariable("EVE_PROJECT_DISCOVERY_MODE");
        string? oldCatalog = Environment.GetEnvironmentVariable("EVE_PROJECT_DISCOVERY_TRAINING_CATALOG");
        string catalogPath = Path.GetTempFileName();
        var catalog = new { projectId = 17, source = "controlled test fixture", license = "test only", tasks = Enumerable.Range(0, 24).Select(i => new { taskNumber = 170000000 + i, task = new { taskId = i, assets = new { url = $"https://example.test/{i}.png" }, solution = new[] { 9 } } }).ToArray() };
        await File.WriteAllTextAsync(catalogPath, System.Text.Json.JsonSerializer.Serialize(catalog));
        Environment.SetEnvironmentVariable("EVE_PROJECT_DISCOVERY_TRAINING_CATALOG", catalogPath);
        Environment.SetEnvironmentVariable("EVE_PROJECT_DISCOVERY_MODE", "disabled");
        Environment.SetEnvironmentVariable("EVE_PROJECT_DISCOVERY_TUTORIAL_REWARD_POLICY", "subcell-levels");
        Environment.SetEnvironmentVariable("EVE_PROJECT_DISCOVERY_TUTORIAL_XP", "300");
        HostRuntime? host = null;
        try
        {
            await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(Url));
            host = await HostComposition.BuildAsync(Settings());
            var characters = new PgCharacterStore(source, Schema("EVE_CHARACTERS_SCHEMA"));
            var id = await InsertPilotAsync(source, characters, "Tutorial Participant");
            var skippedId = await InsertPilotAsync(source, characters, "Tutorial Skipped");
            var service = host.Registry.Lookup("ProjectDiscovery")!;
            var trainingSink = new DiscoveryWireSink();
            var session = new ClientSession(trainingSink) { CharacterId = id, UserId = 1, SessionId = 101 };
            var skipped = new ClientSession { CharacterId = skippedId, UserId = 1, SessionId = 102 };
            int callId = 0;
            async Task<PyValue> Call(string method, ClientSession who) => await service.CallAsync(new ServiceCall { Method = method, Args = [], Session = who, CallId = new PyInt(Interlocked.Increment(ref callId)) });
            Assert.Equal(new PyInt(300), await Call("get_tutorial_xp_reward", session));
            Assert.Equal(new PyInt(0), await Call("get_maximum_bonus_samples", session));
            Assert.Equal(new PyInt(0), await Call("get_remaining_bonus_samples", session));
            await Call("skip_tutorial", skipped);
            Assert.Same(PyBool.False, await Call("get_is_player_entitled_to_tutorial_reward", skipped));
            Assert.Same(PyBool.False, await Call("give_tutorial_rewards", skipped));
            long projectId = Assert.IsType<PyInt>(await Call("get_project_id", session)).Value;
            Assert.Equal(17, projectId);
            var levels = new[] { new[] {4,9}, new[] {20,19}, new[] {13}, new[] {7}, new[] {8}, new[] {16}, new[] {10}, new[] {1} };
            await Assert.ThrowsAsync<MachoWrappedException>(() => Call("increase_tutorial_level", session));
            bool restartedDuringTraining = false;
            async Task CompleteLevels()
            {
                foreach (var level in levels)
                {
                    foreach (var task in level)
                    {
                        PyValue issued;
                        if (task == 4 && !restartedDuringTraining)
                        {
                            var from = new PyTuple([new PyInt(MachoAddress.TypeClient), new PyLong(101), new PyInt(501), PyNone.Instance]);
                            var body = new PyTuple([new PyInt(1), new PyStr("new_training_task"), new PyTuple([new PyLong(projectId * 10000000 + task)]), PyNone.Instance]);
                            var payload = new PyTuple([new PyTuple([new PyInt(0), new PySubStream(body)])]);
                            var head = new PyValue[] { new PyInt(6), from, MachoAddress.Any("ProjectDiscovery").Encode(), new PyInt(101), payload, PyNone.Instance };
                            var request = new PyObject(new PyStr("carbon.common.script.net.machoNetPacket.CallReq"), new PyTuple([.. head, .. Enumerable.Repeat<PyValue>(PyNone.Instance, 14-head.Length)]));
                            trainingSink.Packets.Clear();
                            await new PacketDispatcher(host!.Registry) { StrictRpc = true }.DispatchAsync(MarshalCodec.Decode(MarshalCodec.Encode(request)), session);
                            var packet = Assert.Single(trainingSink.Packets, p => MachoPacket.Decode(p)?.Type == MachoMessage.CallRsp);
                            var fields = Assert.IsType<PyTuple>(Assert.IsType<PyObject>(MarshalCodec.Decode(MarshalCodec.Encode(packet))).Args).Items;
                            var result = Assert.IsType<PyTuple>(fields[4]).Items[0];
                            issued = result is PySubStream { Decoded: { } value } ? value : result;
                        }
                        else issued = await service.CallAsync(new ServiceCall { Method = "new_training_task", Args = [new PyLong(projectId * 10000000 + task)], Session = session });
                        var issuedTask = Assert.IsType<PyDict>(issued);
                        Assert.Contains(issuedTask.Entries, e => JsSemantics.StringValue(e.Key) == "taskId");
                        Assert.Contains(issuedTask.Entries, e => JsSemantics.StringValue(e.Key) == "solution");
                    }
                    if (!restartedDuringTraining)
                    {
                        var wrong = new ClientSession { CharacterId = id, UserId = 2 };
                        await Assert.ThrowsAsync<MachoWrappedException>(() => Call("increase_tutorial_level", wrong));
                        await host!.ShutdownAsync(); host = await HostComposition.BuildAsync(Settings());
                        service = host.Registry.Lookup("ProjectDiscovery")!;
                        restartedDuringTraining = true;
                    }
                    await Call("increase_tutorial_level", session);
                }
            }
            await CompleteLevels();
            Assert.Same(PyBool.True, await Call("get_is_player_entitled_to_tutorial_reward", session));
            var claim = new ServiceCall { Method = "give_tutorial_rewards", Args = [], Session = session, CallId = new PyInt(5000) };
            var claimFrom = new PyTuple([new PyInt(MachoAddress.TypeClient), new PyLong(101), new PyInt(5000), PyNone.Instance]);
            var claimBody = new PyTuple([new PyInt(1), new PyStr("give_tutorial_rewards"), new PyTuple([]), PyNone.Instance]);
            var claimPayload = new PyTuple([new PyTuple([new PyInt(0), new PySubStream(claimBody)])]);
            var claimHead = new PyValue[] { new PyInt(6), claimFrom, MachoAddress.Any("ProjectDiscovery").Encode(), new PyInt(101), claimPayload, PyNone.Instance };
            var claimRequest = new PyObject(new PyStr("carbon.common.script.net.machoNetPacket.CallReq"), new PyTuple([.. claimHead, .. Enumerable.Repeat<PyValue>(PyNone.Instance, 14-claimHead.Length)]));
            trainingSink.Packets.Clear();
            await new PacketDispatcher(host!.Registry) { StrictRpc = true }.DispatchAsync(MarshalCodec.Decode(MarshalCodec.Encode(claimRequest)), session);
            var claimPacket = Assert.Single(trainingSink.Packets, p => MachoPacket.Decode(p)?.Type == MachoMessage.CallRsp);
            var claimFields = Assert.IsType<PyTuple>(Assert.IsType<PyObject>(MarshalCodec.Decode(MarshalCodec.Encode(claimPacket))).Args).Items;
            var claimResult = Assert.IsType<PyTuple>(claimFields[4]).Items[0];
            Assert.Same(PyBool.True, claimResult is PySubStream { Decoded: { } value } ? value : claimResult);
            Assert.Same(PyBool.True, await service.CallAsync(claim));
            Assert.Same(PyBool.False, await Call("give_tutorial_rewards", session));
            await host.ShutdownAsync(); host = await HostComposition.BuildAsync(Settings());
            service = host.Registry.Lookup("ProjectDiscovery")!;
            Assert.Same(PyBool.True, await service.CallAsync(claim));
            var state = await Call("get_player_state", session);
            Assert.Equal(Py.KeyVal(("rank", new PyInt(3)), ("experience", new PyInt(300)), ("analysisKredits", new PyInt(0)), ("finishedTutorial", PyBool.True), ("tutorialLevel", new PyInt(8))), state);
            await Call("reset_tutorial", session);
            await CompleteLevels();
            Assert.Same(PyBool.False, await Call("get_is_player_entitled_to_tutorial_reward", session));
            Assert.Same(PyBool.False, await Call("give_tutorial_rewards", session));
        }
        finally
        {
            if (host is not null) await host.ShutdownAsync();
            Environment.SetEnvironmentVariable("EVE_PROJECT_DISCOVERY_TUTORIAL_REWARD_POLICY", oldPolicy);
            Environment.SetEnvironmentVariable("EVE_PROJECT_DISCOVERY_TUTORIAL_XP", oldXp);
            Environment.SetEnvironmentVariable("EVE_PROJECT_DISCOVERY_MODE", oldMode);
            Environment.SetEnvironmentVariable("EVE_PROJECT_DISCOVERY_TRAINING_CATALOG", oldCatalog);
            File.Delete(catalogPath);
        }
    }

    [PgFact]
    public async Task Exoplanet_local_finish_grants_atomic_receipt_and_replays_same_call()
    {
        var settings = new Dictionary<string,string> { ["EVE_PROJECT_DISCOVERY_MODE"] = "disabled", ["EVE_PROJECT_DISCOVERY_TUTORIAL_REWARD_POLICY"] = "exoplanet-local-finish", ["EVE_PROJECT_DISCOVERY_TUTORIAL_CRATE_TYPE"] = "45996", ["EVE_PROJECT_DISCOVERY_TUTORIAL_WALLET_REF_TYPE"] = "1" };
        var saved = settings.Keys.ToDictionary(k => k, Environment.GetEnvironmentVariable);
        foreach (var (key,value) in settings) Environment.SetEnvironmentVariable(key,value);
        HostRuntime? host = null;
        try
        {
            await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(Url));
            host = await HostComposition.BuildAsync(Settings());
            var chars = new PgCharacterStore(source, Schema("EVE_CHARACTERS_SCHEMA"));
            var id = await InsertPilotAsync(source, chars, "Exo Participant");
            var skippedId = await InsertPilotAsync(source, chars, "Exo Skipped");
            var wallet = new Eve.Wallet.WalletTransactions(source, Schema("EVE_WALLET_SCHEMA"));
            await using (var conn = await source.OpenConnectionAsync()) await using (var tx = await conn.BeginTransactionAsync())
            { await wallet.EnsureAsync(conn,tx,id); await wallet.EnsureAsync(conn,tx,skippedId); await tx.CommitAsync(); }
            var service = host.Registry.Lookup("ProjectDiscovery")!;
            var sink = new DiscoveryWireSink();
            var session = new ClientSession(sink) { CharacterId = id, UserId = 1, SessionId = 99 };
            var skipped = new ClientSession { CharacterId = skippedId, UserId = 1, SessionId = 100 };
            ServiceCall Call(string method, ClientSession who, int id) => new() { Method = method, Args = [], Session = who, CallId = new PyInt(id) };
            Assert.Same(PyBool.True, await service.CallAsync(Call("get_is_player_entitled_to_tutorial_reward",session,1)));
            await service.CallAsync(Call("skip_tutorial",skipped,1));
            Assert.Same(PyBool.False, await service.CallAsync(Call("get_is_player_entitled_to_tutorial_reward",skipped,2)));
            Assert.Same(PyBool.False, await service.CallAsync(Call("give_tutorial_rewards",skipped,3)));
            var entitlementFrom = new PyTuple([new PyInt(MachoAddress.TypeClient), new PyLong(99), new PyInt(1), PyNone.Instance]);
            var entitlementBody = new PyTuple([new PyInt(1), new PyStr("get_is_player_entitled_to_tutorial_reward"), new PyTuple([]), PyNone.Instance]);
            var entitlementPayload = new PyTuple([new PyTuple([new PyInt(0), new PySubStream(entitlementBody)])]);
            var entitlementHead = new PyValue[] { new PyInt(6), entitlementFrom, MachoAddress.Any("ProjectDiscovery").Encode(), new PyInt(101), entitlementPayload, PyNone.Instance };
            var entitlementRequest = new PyObject(new PyStr("carbon.common.script.net.machoNetPacket.CallReq"), new PyTuple([.. entitlementHead, .. Enumerable.Repeat<PyValue>(PyNone.Instance, 14-entitlementHead.Length)]));
            await new PacketDispatcher(host.Registry) { StrictRpc = true }.DispatchAsync(MarshalCodec.Decode(MarshalCodec.Encode(entitlementRequest)), session);
            var entitlementPacket = Assert.Single(sink.Packets);
            Assert.Equal(MachoMessage.CallRsp, MachoPacket.Decode(entitlementPacket)!.Type);
            var entitlementFields = Assert.IsType<PyTuple>(Assert.IsType<PyObject>(MarshalCodec.Decode(MarshalCodec.Encode(entitlementPacket))).Args).Items;
            var entitlementResult = Assert.IsType<PyTuple>(entitlementFields[4]).Items[0];
            Assert.Same(PyBool.True, entitlementResult is PySubStream { Decoded: { } value } ? value : entitlementResult);
            sink.Packets.Clear();
            var claim = Call("give_tutorial_rewards", session, 2);
            var from = new PyTuple([new PyInt(MachoAddress.TypeClient), new PyLong(99), new PyInt(2), PyNone.Instance]);
            var body = new PyTuple([new PyInt(1), new PyStr("give_tutorial_rewards"), new PyTuple([]), PyNone.Instance]);
            var payload = new PyTuple([new PyTuple([new PyInt(0), new PySubStream(body)])]);
            var head = new PyValue[] { new PyInt(6), from, MachoAddress.Any("ProjectDiscovery").Encode(), new PyInt(101), payload, PyNone.Instance };
            var request = new PyObject(new PyStr("carbon.common.script.net.machoNetPacket.CallReq"), new PyTuple([.. head, .. Enumerable.Repeat<PyValue>(PyNone.Instance, 14-head.Length)]));
            await new PacketDispatcher(host.Registry) { StrictRpc = true }.DispatchAsync(MarshalCodec.Decode(MarshalCodec.Encode(request)), session);
            var packets = sink.Packets.Select(p => MachoPacket.Decode(p)!).ToArray();
            Assert.Equal(MachoMessage.CallRsp, packets[0].Type);
            Assert.Contains(packets, p => p.Type == MachoMessage.Notification && p.Dest.BroadcastId == "OnItemChange");
            Assert.Contains(packets, p => p.Type == MachoMessage.Notification && p.Dest.BroadcastId == "OnPlayerStateUpdated");
            var failingId = await InsertPilotAsync(source, chars, "Exo Failing Sink");
            await using (var conn = await source.OpenConnectionAsync()) await using (var tx = await conn.BeginTransactionAsync())
            { await wallet.EnsureAsync(conn, tx, failingId); await tx.CommitAsync(); }
            var failingSink = new FailingNotificationSink();
            var failingSession = new ClientSession(failingSink) { CharacterId = failingId, UserId = 1, SessionId = 103 };
            await new PacketDispatcher(host.Registry) { StrictRpc = true }.DispatchAsync(MarshalCodec.Decode(MarshalCodec.Encode(request)), failingSession);
            Assert.Single(failingSink.Responses); Assert.Equal(MachoMessage.CallRsp, MachoPacket.Decode(failingSink.Responses[0])!.Type);
            Assert.True(failingSink.FailedNotifications >= 1);
            await using (var paid = source.CreateCommand($"SELECT experience FROM {Schema("EVE_CHARACTERS_SCHEMA")}.project_discovery_progress WHERE character_id=$1"))
            { paid.Parameters.AddWithValue(failingId); Assert.Equal(900L, await paid.ExecuteScalarAsync()); }
            Assert.Same(PyBool.True, await service.CallAsync(claim));
            Assert.Same(PyBool.False, await service.CallAsync(Call("give_tutorial_rewards",session,3)));
            await service.CallAsync(Call("increase_tutorial_level",session,4));
            var state = await service.CallAsync(Call("get_player_state",session,5));
            Assert.Equal(Py.KeyVal(("rank",new PyInt(4)),("experience",new PyInt(900)),("analysisKredits",new PyInt(0)),("finishedTutorial",PyBool.True),("tutorialLevel",new PyInt(8))), state);
            await using (var check = source.CreateCommand($"SELECT b.isk,r.item_id,r.item_type_id,r.delivery_station_id,i.type_id,i.owner_id,i.location_id,i.quantity FROM {Schema("EVE_WALLET_SCHEMA")}.balances b JOIN {Schema("EVE_CHARACTERS_SCHEMA")}.project_discovery_tutorial_receipts r ON r.character_id=b.owner_id JOIN {Schema("EVE_INVENTORY_SCHEMA")}.items i ON i.id=r.item_id WHERE b.owner_id=$1 AND b.account_key=1000"))
            {
                check.Parameters.AddWithValue(id); await using var reader = await check.ExecuteReaderAsync(); Assert.True(await reader.ReadAsync());
                Assert.Equal(250000m,reader.GetDecimal(0)); Assert.Equal(45996,reader.GetInt32(2)); Assert.Equal(60000610,reader.GetInt64(3));
                Assert.Equal(45996,reader.GetInt32(4)); Assert.Equal(id,reader.GetInt64(5)); Assert.Equal(60000610,reader.GetInt64(6)); Assert.Equal(1,reader.GetInt64(7)); Assert.False(await reader.ReadAsync());
            }
            await host.ShutdownAsync(); host = await HostComposition.BuildAsync(Settings()); service = host.Registry.Lookup("ProjectDiscovery")!;
            Assert.Same(PyBool.True, await service.CallAsync(claim));
            await service.CallAsync(Call("reset_tutorial",session,6));
            Assert.Same(PyBool.False, await service.CallAsync(Call("get_is_player_entitled_to_tutorial_reward",session,7)));
            Assert.Same(PyBool.False, await service.CallAsync(Call("give_tutorial_rewards",session,8)));
        }
        finally { if (host is not null) await host.ShutdownAsync(); foreach (var (key,value) in saved) Environment.SetEnvironmentVariable(key,value); }
    }

    private sealed class DiscoveryWireSink : IPacketSink
    { public List<PyValue> Packets { get; } = []; public void Send(PyValue value) => Packets.Add(value); }
    private sealed class FailingNotificationSink : IPacketSink
    {
        public List<PyValue> Responses { get; } = [];
        public int FailedNotifications { get; private set; }
        public void Send(PyValue value)
        {
            if (MachoPacket.Decode(value)?.Type == MachoMessage.Notification) { FailedNotifications++; throw new IOException("fixture sink failure"); }
            Responses.Add(value);
        }
    }

    [Theory]
    [InlineData("exoplanet", false)]
    [InlineData("subcellular", false)]
    [InlineData("exoplanet", true)]
    [InlineData("subcellular", true)]
    public async Task Legacy_remote_profile_roundtrips_full_task_and_local_rewards_on_wire(string protocol, bool training)
    {
        var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0); probe.Start(); int port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
        using var upstream = new System.Net.HttpListener(); upstream.Prefixes.Add($"http://127.0.0.1:{port}/"); upstream.Start();
        var variables = new Dictionary<string,string> { ["EVE_PROJECT_DISCOVERY_MODE"] = "remote", ["EVE_PROJECT_DISCOVERY_REMOTE_PROTOCOL"] = protocol, ["EVE_PROJECT_DISCOVERY_MMOS_ORIGIN"] = $"http://127.0.0.1:{port}/", ["EVE_PROJECT_DISCOVERY_MMOS_CREDENTIAL"] = "fixture", ["EVE_PROJECT_DISCOVERY_MMOS_SECRET"] = "fixture", ["EVE_PROJECT_DISCOVERY_MMOS_GAME"] = "demo", ["EVE_PROJECT_DISCOVERY_MMOS_PROJECT"] = "fixture-project", ["EVE_PROJECT_DISCOVERY_BONUS_POLICY"] = "daily", ["EVE_PROJECT_DISCOVERY_BONUS_DAILY_MAX"] = "2", ["EVE_PROJECT_DISCOVERY_BONUS_XP"] = "75", ["EVE_PROJECT_DISCOVERY_BONUS_RESET_UTC_HOUR"] = "0" };
        var saved = variables.Keys.ToDictionary(k => k, Environment.GetEnvironmentVariable);
        foreach (var (key, value) in variables) Environment.SetEnvironmentVariable(key, value);
        HostRuntime? host = null;
        try
        {
            host = await HostComposition.BuildAsync(Settings());
            var dispatcher = new PacketDispatcher(host.Registry) { StrictRpc = true };
            var sink = new DiscoveryWireSink(); var session = new ClientSession(sink) { CharacterId = 101, UserId = 201, SessionId = 7 };
            PyValue Request(int id, string method, PyValue[] args, PyValue kwargs)
            {
                var from = new PyTuple([new PyInt(MachoAddress.TypeClient), new PyLong(7), new PyInt(id), PyNone.Instance]);
                var body = new PyTuple([new PyInt(1), new PyStr(method), new PyTuple(args), kwargs]);
                var payload = new PyTuple([new PyTuple([new PyInt(0), new PySubStream(body)])]);
                var head = new PyValue[] { new PyInt(6), from, MachoAddress.Any("ProjectDiscovery").Encode(), new PyInt(101), payload, PyNone.Instance };
                return new PyObject(new PyStr("carbon.common.script.net.machoNetPacket.CallReq"), new PyTuple([.. head, .. Enumerable.Repeat<PyValue>(PyNone.Instance, 14-head.Length)]));
            }
            async Task<PyValue> Call(int id, string method, PyValue[] args, PyValue kwargs)
            {
                sink.Packets.Clear(); await dispatcher.DispatchAsync(MarshalCodec.Decode(MarshalCodec.Encode(Request(id,method,args,kwargs))), session);
                var packet = Assert.Single(sink.Packets, p => MachoPacket.Decode(p)?.Type == MachoMessage.CallRsp); var fields = Assert.IsType<PyTuple>(Assert.IsType<PyObject>(MarshalCodec.Decode(MarshalCodec.Encode(packet))).Args).Items;
                Assert.Equal(MachoMessage.CallRsp, Assert.IsType<PyInt>(fields[0]).Value);
                var payload = Assert.IsType<PyTuple>(fields[4]); return payload.Items[0] is PySubStream { Decoded: { } value } ? value : payload.Items[0];
            }
            async Task Reply(System.Net.HttpListenerContext context, string body)
            { var bytes = System.Text.Encoding.UTF8.GetBytes(body); context.Response.ContentLength64 = bytes.Length; await context.Response.OutputStream.WriteAsync(bytes); context.Response.Close(); }
            Assert.Equal(2, JsSemantics.NumberValue(await Call(31,"get_maximum_bonus_samples", [], PyNone.Instance)));
            Assert.Equal(2, JsSemantics.NumberValue(await Call(32,"get_remaining_bonus_samples", [], PyNone.Instance)));
            var pending = Call(1,"get_new_task", [], PyNone.Instance); var request = await upstream.GetContextAsync();
            string taskJson = protocol == "exoplanet" ? "{\"id\":\"123\",\"url\":\"https://example.test/task\"}" : "{\"id\":\"123\",\"assets\":{\"url\":\"https://example.test/image.png\"}}";
            await Reply(request, $"{{\"uid\":\"u\",\"game\":\"demo\",\"player\":{{}},\"task\":{taskJson}}}");
            var task = Assert.IsType<PyDict>(await pending);
            PyValue Field(PyValue value, string key) => Assert.Single(Assert.IsType<PyDict>(value).Entries, e => JsSemantics.StringValue(e.Key) == key).Value;
            if (protocol == "subcellular") Assert.Equal("123", JsSemantics.StringValue(Field(task, "taskId")));
            PyValue classification = protocol == "exoplanet" ? new PyDict([new(new PyStr("transits"), new PyList([])), new(new PyStr("stellarActivity"), new PyList([]))]) : new PyList([new PyInt(9), new PyInt(10)]);
            var mismatched = new PyDict([new(new PyStr("id"), new PyStr("other"))]);
            await Assert.ThrowsAsync<MachoWrappedException>(() => host.Registry.Lookup("ProjectDiscovery")!.CallAsync(new ServiceCall { Method = "post_classification", Args = [mismatched, classification, new PyReal(3.5), PyBool.False], Session = session, CallId = new PyInt(88) }).AsTask());
            var postArgs = protocol == "exoplanet" ? new PyValue[] { task, classification, new PyReal(3.5) } : [task, classification, new PyReal(3.5), PyBool.False];
            PyValue postKwargs = protocol == "exoplanet" ? new PyDict([new(new PyStr("remark"), PyBool.False)]) : new PyDict([]);
            await Assert.ThrowsAsync<MachoWrappedException>(() => host.Registry.Lookup("ProjectDiscovery")!.CallAsync(new ServiceCall { Method = "post_classification", Args = [task, classification, new PyReal(3.5)], Kwargs = new PyDict([new(new PyStr("unknown"), PyBool.False)]), Session = session, CallId = new PyInt(89) }).AsTask());
            await Assert.ThrowsAsync<MachoWrappedException>(() => host.Registry.Lookup("ProjectDiscovery")!.CallAsync(new ServiceCall { Method = "post_classification", Args = [task, classification, new PyReal(3.5), PyBool.False], Kwargs = new PyDict([new(new PyStr("remark"), PyBool.True)]), Session = session, CallId = new PyInt(90) }).AsTask());
            pending = Call(2,"post_classification", postArgs, postKwargs);
            request = await upstream.GetContextAsync(); Assert.Equal("/classifications", request.Request.RawUrl);
            string resultTask = protocol == "exoplanet" ? $"{{\"id\":\"123\",\"isTrainingSet\":{training.ToString().ToLowerInvariant()}}}" : "{\"taskId\":\"123\"}";
            await Reply(request, $"{{\"uid\":\"c\",\"player\":{{\"score\":0.8}},\"task\":{resultTask},\"isTraining\":{training.ToString().ToLowerInvariant()},\"XP_Reward\":999,\"ISK_Reward\":999}}");
            var response = await pending;
            Assert.Equal(training ? 2 : 1, JsSemantics.NumberValue(await Call(33,"get_remaining_bonus_samples", [], PyNone.Instance)));
            Assert.Equal(training ? 0 : 75, Assert.IsType<PyLong>(Field(response,"XP_Reward")).Value);
            Assert.Equal(0, Assert.IsType<PyLong>(Field(response,"ISK_Reward")).Value);
            var state = Assert.IsType<PyObject>(Field(response,"playerState"));
            Assert.Equal("util.KeyVal", JsSemantics.StringValue(state.Name));
            Assert.Equal(1, JsSemantics.NumberValue(Field(state.Args,"rank")));
            Assert.Equal(training ? 0 : 75, JsSemantics.NumberValue(Field(state.Args,"experience")));
            if (protocol == "exoplanet")
            {
                Assert.Same(PyBool.False, Field(response,"tier_reward"));
                Assert.Equal(training ? 2 : 1, Assert.IsType<PyLong>(Field(response,"bonusSamplesAfterClassification")).Value);
            }
            else Assert.Equal(0, Assert.IsType<PyLong>(Field(response,"AK_Reward")).Value);
            await host.ShutdownAsync(); host = await HostComposition.BuildAsync(Settings());
            dispatcher = new PacketDispatcher(host.Registry) { StrictRpc = true };
            var replay = await Call(2,"post_classification", postArgs, postKwargs);
            Assert.Equal(JsSemantics.NumberValue(Field(response,"XP_Reward")), JsSemantics.NumberValue(Field(replay,"XP_Reward")));
        }
        finally { if (host is not null) await host.ShutdownAsync(); foreach (var (key, value) in saved) Environment.SetEnvironmentVariable(key, value); }
    }

    private static async Task<long> InsertPilotAsync(NpgsqlDataSource source, PgCharacterStore characters, string name)
    {
        var pilot = new NewCharacter(1, name, Gender: 1, RaceId: 1, BloodlineId: 1, AncestryId: 10, SchoolId: 35, TypeId: 1373,
            CorporationId: 1000006, FactionId: 500001, StationId: 60000610, SolarSystemId: 30002775, ConstellationId: 20000406, RegionId: 10000033,
            DollInfo: null, PortraitInfo: null);
        await using var connection = await source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var id = await characters.InsertAsync(connection, transaction, pilot);
        await transaction.CommitAsync();
        return id;
    }

    private static HostSettings Settings() => new(
        "", "disabled", new ClusterOptions { NewCharacterExperienceEnabled = false, NewCharacterExperienceMode = "disabled" },
        new AccountOptions { AutoCreate = true }, StrictRpc: true);
}
