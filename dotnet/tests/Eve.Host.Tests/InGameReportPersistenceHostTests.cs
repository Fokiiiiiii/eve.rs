using Eve.Data;
using Eve.Host;
using Eve.Host.Persistence;
using Eve.Identity;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Npgsql;

namespace Eve.Host.Tests;

[Collection("host-composition")]
public sealed class CoreServicesAuditHostTests : IAsyncLifetime
{
    private static readonly string[] SchemaVariables =
    [
        "EVE_ACCESS_GROUPS_SCHEMA", "EVE_AGENTS_SCHEMA", "EVE_ASSET_SAFETY_SCHEMA", "EVE_BOOKMARKS_SCHEMA",
        "EVE_CHARACTERS_SCHEMA", "EVE_COMMUNITY_TRACKS_SCHEMA", "EVE_CONTRABAND_SCHEMA", "EVE_CONTRACTS_SCHEMA", "EVE_CORPORATIONS_SCHEMA",
        "EVE_CRIMEWATCH_SCHEMA", "EVE_ESS_SCHEMA", "EVE_FACTIONWARFARE_SCHEMA", "EVE_FITTING_SCHEMA",
        "EVE_FLEET_SCHEMA", "EVE_GAMESTORE_SCHEMA", "EVE_GLOBAL_CONFIG_SCHEMA", "EVE_IDENTITY_SCHEMA",
        "EVE_INDUSTRY_SCHEMA", "EVE_INSURGENCY_SCHEMA", "EVE_INVENTORY_SCHEMA", "EVE_LOGIN_SCHEMA",
        "EVE_MARKET_SCHEMA", "EVE_NPC_SCHEMA", "EVE_PLANETS_SCHEMA", "EVE_PROJECT_DISCOVERY_SCHEMA",
        "EVE_RAFFLE_SCHEMA", "EVE_SKILLS_SCHEMA", "EVE_SKINR_SCHEMA", "EVE_SPACE_SCHEMA",
        "EVE_STRUCTURES_SCHEMA", "EVE_SUPPORT_SCHEMA", "EVE_TRADE_SCHEMA", "EVE_WALLET_SCHEMA",
        "EVE_WORMHOLE_SCHEMA", "EVE_XMPP_SCHEMA",
    ];

    private readonly string _prefix = "ir" + Guid.NewGuid().ToString("N")[..10];
    private readonly Dictionary<string, string?> _saved = [];
    private readonly List<string> _schemas = [];
    private HostRuntime? _host;

    private string Schema(string variable) =>
        _prefix + "_" + variable["EVE_".Length..^"_SCHEMA".Length].ToLowerInvariant();

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
        if (_host is not null)
        {
            await _host.ShutdownAsync();
            _host = null;
        }

        if (Environment.GetEnvironmentVariable("EVE_PG_URL") is { } url)
        {
            await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(url));
            foreach (var schema in _schemas)
            {
                await using var command = source.CreateCommand($"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE");
                await command.ExecuteNonQueryAsync();
            }
        }

        foreach (var (name, value) in _saved)
            Environment.SetEnvironmentVariable(name, value);
    }

    private static HostSettings Settings() => new(
        "", "disabled", new ClusterOptions { NewCharacterExperienceEnabled = false, NewCharacterExperienceMode = "disabled" },
        new AccountOptions { AutoCreate = true }, StrictRpc: true);

    private static ServiceCall Call(ClientSession session, string method, params PyValue[] args) =>
        new() { Session = session, Method = method, Args = args };

    private static async Task<long> ReportCountAsync(NpgsqlDataSource source)
    {
        var schema = Environment.GetEnvironmentVariable("EVE_SUPPORT_SCHEMA")!;
        await using var command = source.CreateCommand($"SELECT count(*) FROM \"{schema}\".ingame_reports");
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    [PgFact]
    public async Task Registered_ingame_reports_are_durable_and_preserve_the_client_payload()
    {
        var url = Environment.GetEnvironmentVariable("EVE_PG_URL")!;
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(url));
        _host = await HostComposition.BuildAsync(Settings());
        var service = _host.Registry.Lookup("ingamereport") ?? throw new InvalidOperationException("ingamereport is not registered");
        var session = new ClientSession { UserId = 77, CharacterId = 42 };

        var methods = new (string Method, PyValue[] Args)[]
        {
            ("report_inappropriate_language", [new PyLong(9001), new PyWString("reason"), new PyWString("time"), new PyWString("channel"), new PyWString("chat content")]),
            ("report_offensive_mail", [new PyLong(9002), new PyWString("mail reason")]),
            ("report_macro_use", [new PyLong(9003), new PyWString("macro reason")]),
            ("report_questionable_transaction", [new PyLong(9004), new PyWString("transaction reason")]),
            ("report_inappropriate_character_name", [new PyLong(9005), new PyWString("name reason")]),
            ("report_exploit_abuse", [new PyLong(9006), new PyWString("exploit reason")]),
            ("report_gambling", [new PyLong(9007), new PyWString("gambling reason")]),
            ("report_other", [new PyLong(9008), new PyWString("other reason")]),
        };

        foreach (var (method, args) in methods)
            Assert.Same(PyNone.Instance, await service.CallAsync(Call(session, method, args)));

        var invalid = await Assert.ThrowsAsync<MachoWrappedException>(() => service.CallAsync(
            Call(session, "report_other", new PyWString("9009"), new PyWString("not an id"))).AsTask());
        Assert.StartsWith("INGAME_REPORT_", invalid.Error.UserErrorMessage);

        await Task.WhenAll(Enumerable.Range(0, 4).Select(index => service.CallAsync(Call(session, "report_other",
            new PyLong(9100 + index), new PyWString($"concurrent report {index}"))).AsTask()));

        await _host.ShutdownAsync();
        _host = null;

        var schemaName = Schema("EVE_SUPPORT_SCHEMA");
        await using var tableCommand = source.CreateCommand("SELECT to_regclass($1)::text");
        tableCommand.Parameters.AddWithValue($"\"{schemaName}\".ingame_reports");
        var table = await tableCommand.ExecuteScalarAsync();
        Assert.NotNull(table);

        var rows = new List<(string Type, long Reporter, long? Account, long Target, string Comments, string? ChatTime, string? ChatChannel, string? ChatContent)>();
        await using (var command = source.CreateCommand($"""
            SELECT report_type, reporter_character_id, reporter_account_id, target_character_id,
                   comments, chat_time, chat_channel, chat_content
            FROM "{schemaName}".ingame_reports ORDER BY report_id
            """))
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                rows.Add((reader.GetString(0), reader.GetInt64(1), reader.IsDBNull(2) ? null : reader.GetInt64(2),
                    reader.GetInt64(3), reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetString(5),
                    reader.IsDBNull(6) ? null : reader.GetString(6), reader.IsDBNull(7) ? null : reader.GetString(7)));
            }
        }

        Assert.Equal(12, rows.Count);
        Assert.Equal("inappropriate_language", rows[0].Type);
        Assert.Equal(42L, rows[0].Reporter);
        Assert.Equal<long?>(77L, rows[0].Account);
        Assert.Equal(9001L, rows[0].Target);
        Assert.Equal("reason", rows[0].Comments);
        Assert.Equal("time", rows[0].ChatTime);
        Assert.Equal("channel", rows[0].ChatChannel);
        Assert.Equal("chat content", rows[0].ChatContent);
        Assert.Equal(methods.Select(method => method.Method.Replace("report_", "", StringComparison.Ordinal)),
            rows.Take(methods.Length).Select(row => row.Type));

        _host = await HostComposition.BuildAsync(Settings());
        service = _host.Registry.Lookup("ingamereport") ?? throw new InvalidOperationException("ingamereport is not registered after restart");
        Assert.Same(PyNone.Instance, await service.CallAsync(Call(session, "report_other", new PyLong(9010), new PyWString("after restart"))));
        Assert.Equal(13, await ReportCountAsync(source));
    }

    [PgFact]
    public async Task Community_track_attributes_are_read_from_persisted_instance_state()
    {
        var url = Environment.GetEnvironmentVariable("EVE_PG_URL")!;
        var schemaName = Schema("EVE_COMMUNITY_TRACKS_SCHEMA");
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(url));
        await PersistenceCommunityTrackStore.MigrateAsync(source, schemaName);
        var trackStore = new PersistenceCommunityTrackStore(source, schemaName);
        await trackStore.SetInstanceAttributesAsync(1337, 1, 7);

        _host = await HostComposition.BuildAsync(Settings());
        var service = _host.Registry.Lookup("communityTrackMgr") ?? throw new InvalidOperationException("communityTrackMgr is not registered");
        var session = new ClientSession { UserId = 77, CharacterId = 42 };

        Assert.Equal(Py.Tuple(new PyLong(1), new PyLong(7)), await service.CallAsync(
            Call(session, "get_instance_attributes_public", new PyLong(1337))));
        Assert.Equal(Py.Tuple(PyNone.Instance, PyNone.Instance), await service.CallAsync(
            Call(session, "get_instance_attributes_public", new PyLong(1338))));

        var progress = await Task.WhenAll(Enumerable.Range(0, 20)
            .Select(_ => trackStore.AddProgressAsync(1337, 1)));
        Assert.Equal(20, progress.Distinct().Count());
        Assert.All(progress, value => Assert.NotNull(value));
        Assert.Equal(Py.Tuple(new PyLong(1), new PyLong(27)), await service.CallAsync(
            Call(session, "get_instance_attributes_public", new PyLong(1337))));

        await _host.ShutdownAsync();
        _host = await HostComposition.BuildAsync(Settings());
        service = _host.Registry.Lookup("communityTrackMgr") ?? throw new InvalidOperationException("communityTrackMgr is not registered after restart");
        Assert.Equal(Py.Tuple(new PyLong(1), new PyLong(27)), await service.CallAsync(
            Call(session, "get_instance_attributes_public", new PyLong(1337))));
    }
}
