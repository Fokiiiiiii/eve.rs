using Eve.Data;
using Eve.Host;
using Eve.Identity;
using Eve.Marshal;
using Eve.Macho;
using Eve.Services;
using Npgsql;

namespace Eve.Host.Tests;

/// <summary>The real host registers the SDE's claimable nullsec ESS systems on boot and preserves each bank deadline across restarts.</summary>
[Collection("host-composition")]
public sealed class EssSystemDeploymentCompositionTests : IAsyncLifetime
{
    private const int ClaimableNullsec = 30_000_208; // LZ-6SU: region 10000003, security -0.172783, no faction marker (SDE build 3396210)
    private const int NpcNullsec = 30_000_326; // WF-1LM: NPC-held region 10000004
    private const int Pochven = 30_000_021; // Kuharah: Triglavian region 10000070
    private const int Highsec = 30_000_142; // Jita: security 0.945913, faction 500006
    private const long EssItemIdBase = 8_440_000_000_000_000;

    private static readonly string[] SchemaVariables =
    [
        "EVE_IDENTITY_SCHEMA", "EVE_GLOBAL_CONFIG_SCHEMA", "EVE_CHARACTERS_SCHEMA", "EVE_INVENTORY_SCHEMA", "EVE_SKILLS_SCHEMA", "EVE_WALLET_SCHEMA",
        "EVE_FITTING_SCHEMA", "EVE_NPC_SCHEMA", "EVE_SPACE_SCHEMA", "EVE_GAMESTORE_SCHEMA", "EVE_ASSET_SAFETY_SCHEMA", "EVE_CORPORATIONS_SCHEMA",
        "EVE_MARKET_SCHEMA", "EVE_FLEET_SCHEMA", "EVE_INDUSTRY_SCHEMA", "EVE_SCHEDULER_SCHEMA", "EVE_EVENTS_SCHEMA", "EVE_RELATIONS_SCHEMA",
        "EVE_CONTENT_SCHEMA", "EVE_CRIMEWATCH_SCHEMA", "EVE_STRUCTURES_SCHEMA", "EVE_ACCESS_GROUPS_SCHEMA", "EVE_TRADE_SCHEMA", "EVE_BOOKMARKS_SCHEMA",
        "EVE_ESS_SCHEMA",
    ];

    private readonly string _prefix = "ee" + Guid.NewGuid().ToString("N")[..10];
    private readonly Dictionary<string, string?> _saved = [];
    private readonly List<string> _schemas = [];

    private string Schema(string variable) => $"{_prefix}_{variable["EVE_".Length..^"_SCHEMA".Length].ToLowerInvariant()}";

    public Task InitializeAsync()
    {
        foreach (var variable in SchemaVariables)
        {
            _saved[variable] = Environment.GetEnvironmentVariable(variable);
            var schema = Schema(variable);
            _schemas.Add(schema);
            Environment.SetEnvironmentVariable(variable, schema);
        }
        foreach (var (name, value) in new[]
        {
            ("EVE_SIM", "off"), ("EVE_MARKET_SEED", "skip"), ("EVE_WORMHOLES", "off"), ("EVE_ESS_SYSTEMS", ""),
        })
        {
            _saved[name] = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (Environment.GetEnvironmentVariable("EVE_PG_URL") is { } url)
        {
            await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(url));
            foreach (var schema in _schemas)
            {
                await using var command = source.CreateCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE");
                await command.ExecuteNonQueryAsync();
            }
        }
        foreach (var (name, value) in _saved)
            Environment.SetEnvironmentVariable(name, value);
    }

    private sealed class Sink : IPacketSink
    {
        public void Send(PyValue packet) { }
    }

    private static HostSettings Settings() => new(
        "", "disabled", new ClusterOptions { NewCharacterExperienceEnabled = false, NewCharacterExperienceMode = "disabled" },
        new AccountOptions { AutoCreate = true }, StrictRpc: true);

    private static async Task<PyValue> ReadEssAsync(IService service, ClientSession session, int systemId)
    {
        var value = await service.CallAsync(new ServiceCall
        {
            Session = session,
            Method = "GetDataForClientSolarSystem",
            Args = [new PyInt(systemId)],
        });
        session.RunAfterResponse();
        return value;
    }

    private static PyValue Field(PyDict dict, string name) =>
        dict.Entries.Single(pair => pair.Key == new PyStr(name)).Value;

    [PgFact]
    public async Task Host_registers_all_claimable_nullsec_banks_and_keeps_their_payout_clock_on_restart()
    {
        var url = Environment.GetEnvironmentVariable("EVE_PG_URL")!;
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(url));
        var essSchema = Schema("EVE_ESS_SCHEMA");
        var session = new ClientSession(new Sink());
        var host = await HostComposition.BuildAsync(Settings());
        long nextPayoutMs;
        try
        {
            var service = Assert.IsAssignableFrom<IService>(host.Registry.Lookup("essMgr"));
            var data = Assert.IsType<PyDict>(await ReadEssAsync(service, session, ClaimableNullsec));
            var expectedEssId = EssItemIdBase + ClaimableNullsec;
            Assert.Equal(new PyInt(expectedEssId), Field(data, "essID"));
            Assert.True(Field(data, "beaconID") is PyInt beacon && beacon.Value > 0);
            Assert.Empty(Assert.IsType<PyDict>(Field(data, "mainBankLink")).Entries);
            Assert.Equal(new PyInt(0), Field(data, "mainValue"));
            Assert.Equal(new PyInt(0), Field(data, "reserveValue"));

            await using (var query = source.CreateCommand($"SELECT next_payout_ms FROM {essSchema}.systems WHERE solar_system_id = $1"))
            {
                query.Parameters.AddWithValue(ClaimableNullsec);
                nextPayoutMs = (long)(await query.ExecuteScalarAsync())!;
            }
            Assert.Equal(new PyLong(DateTimeOffset.FromUnixTimeMilliseconds(nextPayoutMs).ToFileTime()),
                Field(data, "nextScheduledAutopayment"));
            Assert.Equal(new PyLong(DateTimeOffset.FromUnixTimeMilliseconds(nextPayoutMs - 9_900_000).ToFileTime()),
                Field(data, "lastAutopayment"));

            await using (var expected = source.CreateCommand(
                "SELECT count(*) FROM sde.\"mapSolarSystems\" WHERE key::bigint BETWEEN 30000000 AND 30999999 " +
                "AND key::bigint <> 30100000 AND (doc->>'regionID')::integer NOT IN (10000070,10000004,10000017,10000019) " +
                "AND (doc->>'securityStatus')::double precision <= 0 AND COALESCE((doc->>'factionID')::bigint,0) <= 0"))
            await using (var actual = source.CreateCommand($"SELECT count(*) FROM {essSchema}.systems"))
                Assert.Equal((long)(await expected.ExecuteScalarAsync())!, (long)(await actual.ExecuteScalarAsync())!);

            Assert.Same(PyNone.Instance, await ReadEssAsync(service, session, NpcNullsec));
            Assert.Same(PyNone.Instance, await ReadEssAsync(service, session, Pochven));
            Assert.Same(PyNone.Instance, await ReadEssAsync(service, session, Highsec));
        }
        finally
        {
            await host.ShutdownAsync();
        }

        var restarted = await HostComposition.BuildAsync(Settings());
        try
        {
            var service = Assert.IsAssignableFrom<IService>(restarted.Registry.Lookup("essMgr"));
            var data = Assert.IsType<PyDict>(await ReadEssAsync(service, session, ClaimableNullsec));
            Assert.Equal(new PyInt(EssItemIdBase + ClaimableNullsec), Field(data, "essID"));
            Assert.Equal(new PyLong(DateTimeOffset.FromUnixTimeMilliseconds(nextPayoutMs).ToFileTime()),
                Field(data, "nextScheduledAutopayment"));
            Assert.Equal(new PyLong(DateTimeOffset.FromUnixTimeMilliseconds(nextPayoutMs - 9_900_000).ToFileTime()),
                Field(data, "lastAutopayment"));
        }
        finally
        {
            await restarted.ShutdownAsync();
        }
    }
}
