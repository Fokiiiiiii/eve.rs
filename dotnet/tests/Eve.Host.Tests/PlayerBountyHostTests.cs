using System.Text.Json.Nodes;
using Eve.Characters;
using Eve.GameStore;
using Eve.Crimewatch;
using Eve.Data;
using Eve.Scheduler;
using Eve.Services.Crimewatch;
using Eve.Wallet;
using Eve.Identity;
using Eve.Marshal;
using Eve.Services;
using Npgsql;
using Xunit;

namespace Eve.Host.Tests;

/// <summary>
/// The player bounties through the host's own <c>bountyProxy</c> (CH-13): a placement debits the placer once (also when retried), is stored, and reads back
/// through the pools, the rankings and the search; an insufficient wallet and a placement below the minimum are refused; the bounties survive a restart.
/// </summary>
[Collection("host-composition")]
public sealed class PlayerBountyHostTests : IAsyncLifetime
{
    /// <summary>The environment the host reads: every schema on its own scratch name, plus the no-simulation switch.</summary>
    private static readonly string[] SchemaVariables =
    [
        "EVE_IDENTITY_SCHEMA", "EVE_GLOBAL_CONFIG_SCHEMA", "EVE_CHARACTERS_SCHEMA", "EVE_INVENTORY_SCHEMA", "EVE_SKILLS_SCHEMA", "EVE_WALLET_SCHEMA",
        "EVE_FITTING_SCHEMA", "EVE_NPC_SCHEMA", "EVE_SPACE_SCHEMA", "EVE_GAMESTORE_SCHEMA", "EVE_ASSET_SAFETY_SCHEMA", "EVE_CORPORATIONS_SCHEMA",
        "EVE_MARKET_SCHEMA", "EVE_FLEET_SCHEMA", "EVE_INDUSTRY_SCHEMA", "EVE_SCHEDULER_SCHEMA", "EVE_EVENTS_SCHEMA", "EVE_RELATIONS_SCHEMA",
        "EVE_CONTENT_SCHEMA", "EVE_CRIMEWATCH_SCHEMA", "EVE_STRUCTURES_SCHEMA",
        "EVE_TRADE_SCHEMA", "EVE_BOOKMARKS_SCHEMA", "EVE_ESS_SCHEMA", "EVE_CONTRABAND_SCHEMA",
    ];

    private readonly string _prefix = "pb" + Guid.NewGuid().ToString("N")[..10];
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
    public async Task A_placed_bounty_debits_once_is_stored_ranked_and_survives_a_restart()
    {
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(Url));
        var pilotA = 0L;
        var pilotB = 0L;
        var placer = 0L;
        var host = await HostComposition.BuildAsync(Settings());
        try
        {
            var characters = new PgCharacterStore(source, Schema("EVE_CHARACTERS_SCHEMA"));
            pilotA = await InsertPilotAsync(source, characters, "Bounty Target A");
            pilotB = await InsertPilotAsync(source, characters, "Bounty Target B");
            placer = await InsertPilotAsync(source, characters, "Bounty Placer");
            await OpenWalletAsync(source, placer, 10_000_000m);
            var bounty = host.Registry.Lookup("bountyProxy")!;

            var place = new ServiceCall
            {
                Args = [new PyInt(pilotA), new PyReal(2_000_000)],
                Method = "AddToBounty",
                Session = new ClientSession { CharacterId = placer },
                CallId = new PyInt(7),
            };
            var placed = await bounty.CallAsync(place);
            Assert.Equal(new PyReal(2_000_000), Field(placed, "bounty"));
            Assert.Equal(8_000_000m, await WalletIskAsync(source, placer));

            // the same call again (a retry): the pool is what the first placement made, and nothing is debited again
            Assert.Equal(placed, await bounty.CallAsync(place));
            Assert.Equal(8_000_000m, await WalletIskAsync(source, placer));

            await bounty.CallAsync(new ServiceCall
            {
                Args = [new PyInt(pilotB), new PyReal(3_000_000)],
                Method = "AddToBounty",
                Session = new ClientSession { CharacterId = placer },
                CallId = new PyInt(8),
            });

            var stored = await bounty.CallAsync(new ServiceCall
            {
                Args = [new PyList([new PyInt(pilotA)])],
                Method = "GetBounties",
                Session = new ClientSession { CharacterId = placer },
            });
            Assert.Equal(new PyReal(2_000_000), Field(((PyTuple)Assert.IsType<PyList>(stored).Items[0]).Items[1], "bounty"));

            var top = await bounty.CallAsync(new ServiceCall { Args = [], Method = "GetTopPilotBounties", Session = new ClientSession { CharacterId = placer } });
            var rows = Assert.IsType<PyList>(Assert.IsType<PyTuple>(top).Items[0]).Items;
            Assert.Equal(new[] { pilotB, pilotA }, rows.Select(row => Num(Field(row, "targetID"))).ToArray());

            var search = await bounty.CallAsync(new ServiceCall
            {
                Args = [new PyInt(pilotA)],
                Method = "SearchCharBounties",
                Session = new ClientSession { CharacterId = placer },
            });
            Assert.Equal(new PyInt(1), ((PyTuple)Assert.IsType<PyList>(search).Items[0]).Items[0]);

            var mine = await bounty.CallAsync(new ServiceCall { Args = [], Method = "GetMyBounties", Session = new ClientSession { CharacterId = placer } });
            Assert.Equal(2, Assert.IsType<PyList>(mine).Items.Count);

            // below the minimum for a pilot (1,000,000 ISK): refused, nothing debited
            await Assert.ThrowsAnyAsync<Exception>(() => bounty.CallAsync(new ServiceCall
            {
                Args = [new PyInt(pilotA), new PyReal(500_000)],
                Method = "AddToBounty",
                Session = new ClientSession { CharacterId = placer },
                CallId = new PyInt(9),
            }).AsTask());
            Assert.Equal(5_000_000m, await WalletIskAsync(source, placer));
        }
        finally
        {
            await host.ShutdownAsync();
        }

        // a restart reads the same bounties back from PostgreSQL
        var restarted = await HostComposition.BuildAsync(Settings());
        try
        {
            var bounty = restarted.Registry.Lookup("bountyProxy")!;
            var stored = await bounty.CallAsync(new ServiceCall
            {
                Args = [new PyList([new PyInt(pilotA), new PyInt(pilotB)])],
                Method = "GetBounties",
                Session = new ClientSession { CharacterId = placer },
            });
            var amounts = Assert.IsType<PyList>(stored).Items.Select(item => Field(((PyTuple)item).Items[1], "bounty")).ToArray();
            Assert.Equal(new PyValue[] { new PyReal(2_000_000), new PyReal(3_000_000) }, amounts);
        }
        finally
        {
            await restarted.ShutdownAsync();
        }
    }

    [PgFact]
    public async Task A_placer_without_the_money_is_refused_and_nothing_is_stored()
    {
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(Url));
        var host = await HostComposition.BuildAsync(Settings());
        try
        {
            var characters = new PgCharacterStore(source, Schema("EVE_CHARACTERS_SCHEMA"));
            var target = await InsertPilotAsync(source, characters, "Refused Target");
            var poor = await InsertPilotAsync(source, characters, "Refused Placer");
            await OpenWalletAsync(source, poor, 500_000m);

            var error = await Assert.ThrowsAnyAsync<Exception>(() => host.Registry.Lookup("bountyProxy")!.CallAsync(new ServiceCall
            {
                Args = [new PyInt(target), new PyReal(1_000_000)],
                Method = "AddToBounty",
                Session = new ClientSession { CharacterId = poor },
                CallId = new PyInt(3),
            }).AsTask());
            Assert.Contains("NotEnoughMoney", Assert.IsType<MachoWrappedException>(error).Error.ToString());
            Assert.Equal(500_000m, await WalletIskAsync(source, poor));
            Assert.Equal(0L, await ScalarAsync<long>(source,
                $"SELECT count(*) FROM {Schema("EVE_CRIMEWATCH_SCHEMA")}.player_bounty_pools WHERE target_id = {target}"));
        }
        finally
        {
            await host.ShutdownAsync();
        }
    }

    [PgFact]
    public async Task A_pending_payout_of_a_kill_is_credited_by_the_host_payout_job_once()
    {
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(Url));
        var host = await HostComposition.BuildAsync(Settings());
        try
        {
            var characters = new PgCharacterStore(source, Schema("EVE_CHARACTERS_SCHEMA"));
            var victim = await InsertPilotAsync(source, characters, "Payout Victim");
            var hunter = await InsertPilotAsync(source, characters, "Payout Hunter");
            await OpenWalletAsync(source, hunter, 0m);
            var store = new PlayerBountyStore(source, Schema("EVE_CRIMEWATCH_SCHEMA"));
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            await using (var connection = await source.OpenConnectionAsync())
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                await store.RecordPlacementAsync(connection, transaction, new BountyPool(victim, BountyOwnerKind.Character, 1000006, 0, 0), hunter, 2_000_000m, "host-placement", now);
                await transaction.CommitAsync();
            }

            // the kill claims 2,000,000 (a fifth of a 10,000,000 loss) and schedules the payout job in the claim's transaction
            var scheduler = new DurableScheduler(source, () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), null, Schema("EVE_SCHEDULER_SCHEMA"));
            var payoutId = "kill:9" + Guid.NewGuid().ToString("N")[..6];
            var claimed = await store.ClaimKillAsync(new PlayerBountyClaim(payoutId, "player-bounty:" + payoutId + ":credit", hunter, 0, 0, [victim], 10_000_000m, now),
                async (connection, transaction) => await scheduler.ScheduleAsync(connection, transaction, new JobSpec("player-bounty.payout", now)
                {
                    Key = "player-bounty:" + payoutId,
                    Payload = new PlayerBountyPayoutDue(payoutId),
                }));
            Assert.Equal(payoutId, claimed);
            Assert.Equal(0m, await ScalarAsync<decimal>(source, $"SELECT bounty FROM {Schema("EVE_CRIMEWATCH_SCHEMA")}.player_bounty_pools WHERE target_id = {victim}"));

            // the host's runner credits the wallet when the job is due, and the second claim of the same kill does nothing
            var paid = "";
            for (var attempt = 0; attempt < 100 && paid != "paid"; attempt++)
            {
                await Task.Delay(100);
                paid = await ScalarAsync<string>(source, $"SELECT status FROM {Schema("EVE_CRIMEWATCH_SCHEMA")}.player_bounty_payouts WHERE payout_id = '{payoutId}'");
            }
            Assert.Equal("paid", paid);
            Assert.Equal(2_000_000m, await WalletIskAsync(source, hunter));
            Assert.Equal(payoutId, await store.ClaimKillAsync(new PlayerBountyClaim(payoutId, "player-bounty:" + payoutId + ":credit", hunter, 0, 0, [victim], 10_000_000m, now),
                (_, _) => throw new InvalidOperationException("a claimed kill schedules nothing again")));
            Assert.Equal(2_000_000m, await WalletIskAsync(source, hunter));
        }
        finally
        {
            await host.ShutdownAsync();
        }
    }

    /// <summary>One field of a <c>util.KeyVal</c> payload (the entries of its dict).</summary>
    private static PyValue Field(PyValue value, string name)
    {
        var dict = value is PyObject { Args: PyDict args } ? args : throw new InvalidOperationException("not a KeyVal payload");
        return dict.Entries.Single(entry => entry.Key is PyStr key && key.Value == name).Value;
    }

    private static long Num(PyValue value) => value switch
    {
        PyInt integer => integer.Value,
        PyLong integer => integer.Value,
        _ => throw new InvalidOperationException($"not an integer: {value}"),
    };

    private async Task OpenWalletAsync(NpgsqlDataSource source, long owner, decimal isk)
    {
        await using var connection = await source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await new PgWalletStore(source, Schema("EVE_WALLET_SCHEMA")).OpenAsync(connection, transaction, owner, isk, 0);
        await transaction.CommitAsync();
    }

    private async Task<decimal> WalletIskAsync(NpgsqlDataSource source, long owner) =>
        (await new PgWalletStore(source, Schema("EVE_WALLET_SCHEMA")).GetAsync(owner))?.Isk ?? 0m;

    [PgFact]
    public async Task A_placed_bounty_tells_the_target_pilot_or_the_members_of_its_corporation_once_and_never_the_placer()
    {
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(Url));
        var host = await HostComposition.BuildAsync(Settings());
        var characters = new PgCharacterStore(source, Schema("EVE_CHARACTERS_SCHEMA"));
        var placer = await InsertPilotAsync(source, characters, "Notice Placer");
        var target = await InsertPilotAsync(source, characters, "Notice Target");
        const long corporation = 98_000_001;
        var member = await InsertPilotInCorporationAsync(source, characters, "Corporation Member", corporation);
        var otherMember = await InsertPilotInCorporationAsync(source, characters, "Other Corporation Member", corporation);
        await OpenWalletAsync(source, placer, 100_000_000m);
        var bounty = host.Registry.Lookup("bountyProxy")!;

        async Task<PyValue> PlaceAsync(long targetId, double amount, int callId) => await bounty.CallAsync(new ServiceCall
        {
            Args = [new PyInt(targetId), new PyReal(amount)],
            Method = "AddToBounty",
            Session = new ClientSession { CharacterId = placer },
            CallId = new PyInt(callId),
        });
        async Task<List<JsonObject>> RecordsAsync(long recipient)
        {
            await using var persisted = new PersistenceStore(source, Schema("EVE_GAMESTORE_SCHEMA"));
            var boxes = (await persisted.LoadTableAsync("notifications"))["boxes"] as JsonObject;
            var rows = (boxes?[recipient.ToString(System.Globalization.CultureInfo.InvariantCulture)] as JsonObject)?["byID"] as JsonObject;
            return rows?.Select(row => (JsonObject)row.Value!).ToList() ?? [];
        }

        // a pilot: the target is told (112) with the placer and the amount, once, even when the placement is retried; the placer is not told
        await PlaceAsync(target, 2_000_000, 21);
        await PlaceAsync(target, 2_000_000, 21);
        var toTarget = Assert.Single(await RecordsAsync(target));
        Assert.Equal(112, toTarget["typeID"]!.GetValue<int>());
        Assert.Equal(placer, toTarget["senderID"]!.GetValue<long>());
        Assert.Equal(10, toTarget["groupID"]!.GetValue<int>());
        Assert.Equal(placer, toTarget["data"]!["bountyPlacerID"]!.GetValue<long>());
        Assert.Equal(2_000_000d, toTarget["data"]!["bounty"]!.GetValue<double>());
        Assert.Empty(await RecordsAsync(placer));

        // a corporation: each member is told (113), the placer is not, and the members are told once each
        await PlaceAsync(corporation, 20_000_000, 22);
        Assert.Equal(113, Assert.Single(await RecordsAsync(member))["typeID"]!.GetValue<int>());
        Assert.Equal(113, Assert.Single(await RecordsAsync(otherMember))["typeID"]!.GetValue<int>());
        Assert.Empty(await RecordsAsync(placer));
        Assert.Single(await RecordsAsync(target));
    }

    private static async Task<long> InsertPilotInCorporationAsync(NpgsqlDataSource source, PgCharacterStore characters, string name, long corporationId)
    {
        var pilot = new NewCharacter(1, name, Gender: 1, RaceId: 1, BloodlineId: 1, AncestryId: 10, SchoolId: 35, TypeId: 1373,
            CorporationId: corporationId, FactionId: 500001, StationId: 60000610, SolarSystemId: 30002775, ConstellationId: 20000406, RegionId: 10000033,
            DollInfo: null, PortraitInfo: null);
        await using var connection = await source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var id = await characters.InsertAsync(connection, transaction, pilot);
        await transaction.CommitAsync();
        return id;
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

    private static async Task<T> ScalarAsync<T>(NpgsqlDataSource source, string sql)
    {
        await using var command = source.CreateCommand(sql);
        var value = await command.ExecuteScalarAsync() ?? throw new InvalidOperationException("no row");
        return (T)Convert.ChangeType(value, typeof(T));
    }

    private static HostSettings Settings() => new(
        "", "disabled", new ClusterOptions { NewCharacterExperienceEnabled = false, NewCharacterExperienceMode = "disabled" },
        new AccountOptions { AutoCreate = true }, StrictRpc: true);
}
