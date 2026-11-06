using Eve.Characters;
using Eve.Crimewatch;
using Eve.Data;
using Eve.Scheduler;
using Eve.Services.Crimewatch;
using Eve.Wallet;
using Eve.Identity;
using Eve.Inventory;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Npgsql;
using Xunit;

namespace Eve.Host.Tests;

/// <summary>
/// The re-sculpt certificate through the host (CH-8): <c>userSvc.ActivateCharacterReSculpt</c> consumes a certificate of the account's character once and
/// sets its recustomization state (<c>charMgr.GetPaperdollState</c>); a refusal changes nothing; the state survives a restart; a save ends it.
/// </summary>
[Collection("host-composition")]
public sealed class CharacterReSculptHostTests : IAsyncLifetime
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

    private readonly string _prefix = "rs" + Guid.NewGuid().ToString("N")[..10];
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
    public async Task A_certificate_is_consumed_once_sets_the_state_across_a_restart_and_a_save_ends_it()
    {
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(Url));
        long pilot;
        long certificate;
        long notACertificate;
        var host = await HostComposition.BuildAsync(Settings());
        try
        {
            var characters = new PgCharacterStore(source, Schema("EVE_CHARACTERS_SCHEMA"));
            pilot = await InsertPilotAsync(source, characters, "Resculpt Pilot");
            await characters.SetAppearanceAsync(pilot, MarshalCodec.Encode(new PyList([])), true, null, true);
            var items = new PgItemStore(source, Schema("EVE_INVENTORY_SCHEMA"));
            certificate = (await CreateItemsAsync(source, items, new NewItem(34132, pilot, 60000610, ItemFlags.Hangar)))[0];
            notACertificate = (await CreateItemsAsync(source, items, new NewItem(96_000_020, pilot, 60000610, ItemFlags.Hangar)))[0];
            var sink = new Sink();
            var session = new ClientSession(sink) { UserId = 1, CharacterId = pilot };
            session.Set("stationid", new PyLong(60000610));
            host.CharacterSessions!.Enter(pilot, session);
            var userSvc = host.Registry.Lookup("userSvc")!;
            var charMgr = host.Registry.Lookup("charMgr")!;

            // An existing appearance is state 0, where a resculpt certificate is required.
            Assert.Equal(new PyInt(0), await charMgr.CallAsync(new ServiceCall { Args = [new PyInt(pilot)], Method = "GetPaperdollState", Session = session }));
            Assert.Equal(PyBool.True, await userSvc.CallAsync(new ServiceCall { Args = [new PyLong(certificate)], Method = "ActivateCharacterReSculpt", Session = session }));
            Assert.Null(await items.GetAsync(certificate));
            Assert.Equal(new PyInt(3), await charMgr.CallAsync(new ServiceCall { Args = [new PyInt(pilot)], Method = "GetPaperdollState", Session = session }));
            Assert.Equal(new[] { "OnItemChange", "OnCharacterHandler" }, sink.Packets.Select(packet =>
            {
                var packetFields = Assert.IsType<PyTuple>(Assert.IsType<PyObject>(packet).Args);
                return MachoAddress.Decode(packetFields.Items[2]).BroadcastId;
            }));

            // the same certificate again, and an item that is not one: refused, and the items stay
            Assert.Equal(PyBool.False, await userSvc.CallAsync(new ServiceCall { Args = [new PyLong(certificate)], Method = "ActivateCharacterReSculpt", Session = session }));
            Assert.Equal(PyBool.False, await userSvc.CallAsync(new ServiceCall { Args = [new PyLong(notACertificate)], Method = "ActivateCharacterReSculpt", Session = session }));
            Assert.NotNull(await items.GetAsync(notACertificate));
        }
        finally
        {
            await host.ShutdownAsync();
        }

        // a restart reads the same state
        var restarted = await HostComposition.BuildAsync(Settings());
        try
        {
            var session = new ClientSession { UserId = 1, CharacterId = pilot };
            Assert.Equal(new PyInt(3), await restarted.Registry.Lookup("charMgr")!.CallAsync(new ServiceCall { Args = [new PyInt(pilot)], Method = "GetPaperdollState", Session = session }));

            // a save of the appearance ends the state
            await new PgCharacterStore(source, Schema("EVE_CHARACTERS_SCHEMA")).SetAppearanceAsync(pilot, null, false, null, false);
            Assert.Equal(new PyInt(0), await restarted.Registry.Lookup("charMgr")!.CallAsync(new ServiceCall { Args = [new PyInt(pilot)], Method = "GetPaperdollState", Session = session }));
        }
        finally
        {
            await restarted.ShutdownAsync();
        }
    }

    [PgFact]
    public async Task Concurrent_resculpt_activation_cannot_reuse_one_certificate()
    {
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(Url));
        var host = await HostComposition.BuildAsync(Settings());
        try
        {
            var characters = new PgCharacterStore(source, Schema("EVE_CHARACTERS_SCHEMA"));
            var pilot = await InsertPilotAsync(source, characters, "Concurrent Resculpt Pilot");
            await characters.SetAppearanceAsync(pilot, MarshalCodec.Encode(new PyList([])), true, null, true);
            var items = new PgItemStore(source, Schema("EVE_INVENTORY_SCHEMA"));
            var certificate = (await CreateItemsAsync(source, items, new NewItem(34132, pilot, 60000610, ItemFlags.Hangar)))[0];
            var session = new ClientSession { UserId = 1, CharacterId = pilot };
            session.Set("stationid", new PyLong(60000610));
            var userSvc = host.Registry.Lookup("userSvc")!;
            var start = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            async Task<PyValue> ActivateAsync()
            {
                await start.Task;
                return await userSvc.CallAsync(new ServiceCall
                {
                    Args = [new PyLong(certificate)],
                    Method = "ActivateCharacterReSculpt",
                    Session = session,
                });
            }

            var activations = Task.WhenAll(ActivateAsync(), ActivateAsync());
            start.SetResult(true);
            var results = await activations;

            Assert.Single(results, result => ReferenceEquals(result, PyBool.True));
            Assert.Single(results, result => ReferenceEquals(result, PyBool.False));
            Assert.Null(await items.GetAsync(certificate));
        }
        finally
        {
            await host.ShutdownAsync();
        }
    }

    [PgFact]
    public async Task Resculpt_activation_cannot_target_another_character_on_the_account()
    {
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(Url));
        var host = await HostComposition.BuildAsync(Settings());
        try
        {
            var active = await InsertPilotAsync(source, new PgCharacterStore(source, Schema("EVE_CHARACTERS_SCHEMA")), "Active Resculpt Pilot");
            var other = await InsertPilotAsync(source, new PgCharacterStore(source, Schema("EVE_CHARACTERS_SCHEMA")), "Other Resculpt Pilot");
            var items = new PgItemStore(source, Schema("EVE_INVENTORY_SCHEMA"));
            var certificate = (await CreateItemsAsync(source, items, new NewItem(34132, other, 60000610, ItemFlags.Hangar)))[0];
            var session = new ClientSession { UserId = 1, CharacterId = active };
            session.Set("stationid", new PyLong(60000610));

            var result = await host.Registry.Lookup("userSvc")!.CallAsync(new ServiceCall
            {
                Args = [new PyLong(certificate)],
                Method = "ActivateCharacterReSculpt",
                Session = session,
            });

            Assert.Same(PyBool.False, result);
            Assert.NotNull(await items.GetAsync(certificate));
            Assert.Null(await new PgCharacterStore(source, Schema("EVE_CHARACTERS_SCHEMA")).PaperdollStateAsync(other));
        }
        finally
        {
            await host.ShutdownAsync();
        }
    }

    [PgFact]
    public async Task Resculpt_activation_consumes_one_certificate_from_a_stack_and_sets_the_no_gender_state()
    {
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(Url));
        var host = await HostComposition.BuildAsync(Settings());
        try
        {
            var characters = new PgCharacterStore(source, Schema("EVE_CHARACTERS_SCHEMA"));
            var pilot = await InsertPilotAsync(source, characters, "Stacked Resculpt Pilot");
            await characters.SetAppearanceAsync(pilot, MarshalCodec.Encode(new PyList([])), true, null, true);
            var items = new PgItemStore(source, Schema("EVE_INVENTORY_SCHEMA"));
            var certificate = (await CreateItemsAsync(source, items,
                new NewItem(89956, pilot, 60000610, ItemFlags.Hangar, 2)))[0];
            var session = new ClientSession { UserId = 1, CharacterId = pilot };
            session.Set("stationid", new PyLong(60000610));
            var userSvc = host.Registry.Lookup("userSvc")!;
            var charMgr = host.Registry.Lookup("charMgr")!;

            Assert.Same(PyBool.True, await userSvc.CallAsync(new ServiceCall
            {
                Args = [new PyLong(certificate)],
                Method = "ActivateCharacterReSculpt",
                Session = session,
            }));

            Assert.Equal(1, (await items.GetAsync(certificate))?.Quantity);
            Assert.Equal(new PyInt(1), await charMgr.CallAsync(new ServiceCall
            {
                Args = [new PyInt(pilot)],
                Method = "GetPaperdollState",
                Session = session,
            }));
        }
        finally
        {
            await host.ShutdownAsync();
        }
    }

    [PgFact]
    public async Task Resculpt_activation_refuses_when_initial_customization_is_available()
    {
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(Url));
        var host = await HostComposition.BuildAsync(Settings());
        try
        {
            var pilot = await InsertPilotAsync(source, new PgCharacterStore(source, Schema("EVE_CHARACTERS_SCHEMA")), "Initial Resculpt Pilot");
            var items = new PgItemStore(source, Schema("EVE_INVENTORY_SCHEMA"));
            var certificate = (await CreateItemsAsync(source, items,
                new NewItem(34132, pilot, 60000610, ItemFlags.Hangar)))[0];
            var session = new ClientSession { UserId = 1, CharacterId = pilot };
            session.Set("stationid", new PyLong(60000610));

            var result = await host.Registry.Lookup("userSvc")!.CallAsync(new ServiceCall
            {
                Args = [new PyLong(certificate)],
                Method = "ActivateCharacterReSculpt",
                Session = session,
            });

            Assert.Same(PyBool.False, result);
            Assert.NotNull(await items.GetAsync(certificate));
            Assert.Null(await new PgCharacterStore(source, Schema("EVE_CHARACTERS_SCHEMA")).PaperdollStateAsync(pilot));
        }
        finally
        {
            await host.ShutdownAsync();
        }
    }

    [PgFact]
    public async Task Resculpt_activation_requires_the_item_in_the_current_hangar_or_active_ship_cargo()
    {
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(Url));
        var host = await HostComposition.BuildAsync(Settings());
        try
        {
            var characters = new PgCharacterStore(source, Schema("EVE_CHARACTERS_SCHEMA"));
            var pilot = await InsertPilotAsync(source, characters, "Resculpt Location Pilot");
            await characters.SetAppearanceAsync(pilot, MarshalCodec.Encode(new PyList([])), true, null, true);
            var items = new PgItemStore(source, Schema("EVE_INVENTORY_SCHEMA"));
            var remoteCertificate = (await CreateItemsAsync(source, items,
                new NewItem(34132, pilot, 60000611, ItemFlags.Hangar)))[0];
            var session = new ClientSession { UserId = 1, CharacterId = pilot };
            session.Set("stationid", new PyLong(60000610));
            var userSvc = host.Registry.Lookup("userSvc")!;

            var remoteResult = await userSvc.CallAsync(new ServiceCall
            {
                Args = [new PyLong(remoteCertificate)],
                Method = "ActivateCharacterReSculpt",
                Session = session,
            });

            Assert.Same(PyBool.False, remoteResult);
            Assert.NotNull(await items.GetAsync(remoteCertificate));

            long shipId;
            await using (var connection = await source.OpenConnectionAsync())
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                shipId = await items.CreateAsync(connection, transaction,
                    new NewItem(587, pilot, 60000610, ItemFlags.Hangar));
                await characters.SetShipAsync(connection, transaction, pilot, shipId);
                await transaction.CommitAsync();
            }
            var cargoCertificate = (await CreateItemsAsync(source, items,
                new NewItem(34132, pilot, shipId, ItemFlags.Cargo)))[0];
            session.Set("stationid", PyNone.Instance);
            session.Set("shipid", new PyLong(shipId));

            var cargoResult = await userSvc.CallAsync(new ServiceCall
            {
                Args = [new PyLong(cargoCertificate)],
                Method = "ActivateCharacterReSculpt",
                Session = session,
            });

            Assert.Same(PyBool.True, cargoResult);
            Assert.Null(await items.GetAsync(cargoCertificate));
        }
        finally
        {
            await host.ShutdownAsync();
        }
    }

    [PgFact]
    public async Task Character_selection_dispatch_projects_paperdoll_state_and_a_saved_avatar_can_enter()
    {
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(Url));
        var bootstrap = await HostComposition.BuildAsync(Settings());
        await bootstrap.ShutdownAsync();

        var characters = new PgCharacterStore(source, Schema("EVE_CHARACTERS_SCHEMA"));
        var noAppearance = await InsertPilotAsync(source, characters, "No Doll Pilot");
        var savedAppearance = await InsertPilotAsync(source, characters, "Saved Doll Pilot");
        var forced = await InsertPilotAsync(source, characters, "Forced Doll Pilot");
        var doll = MarshalCodec.Encode(new PyList([]));
        await characters.SetAppearanceAsync(savedAppearance, doll, true, null, true);
        await using (var connection = await source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await characters.SetPaperdollStateAsync(connection, transaction, forced, 4);
            await transaction.CommitAsync();
        }

        var host = await HostComposition.BuildAsync(Settings());
        try
        {
            var session = new ClientSession { UserId = 1 };
            var select = host.Registry.Lookup("charUnboundMgr")!;
            async Task<Dictionary<long, PyValue>> ReadStatesAsync()
            {
                var result = Assert.IsType<PyTuple>(await select.CallAsync(new ServiceCall
                {
                    Args = [],
                    Method = "GetCharacterSelectionData",
                    Session = session,
                }));
                Assert.Equal(4, result.Items.Count);
                Assert.Equal(new PyTuple([PyNone.Instance, PyNone.Instance]), result.Items[1]);
                Assert.IsType<PyList>(result.Items[3]);

                var states = new Dictionary<long, PyValue>();
                foreach (var row in Assert.IsType<PyList>(result.Items[2]).Items)
                {
                    var keyVal = Assert.IsType<PyObject>(row);
                    Assert.Equal(new PyStr("util.KeyVal"), keyVal.Name);
                    var fields = Assert.IsType<PyDict>(keyVal.Args).Entries.ToDictionary(
                        entry => Assert.IsType<PyStr>(entry.Key).Value,
                        entry => entry.Value,
                        StringComparer.Ordinal);
                    var id = Assert.IsType<PyInt>(fields["characterID"]).Value;
                    Assert.IsType<PyInt>(fields["paperdollState"]);
                    states.Add(id, fields["paperdollState"]);
                }
                return states;
            }

            var states = await ReadStatesAsync();
            Assert.Equal(new PyInt(2), states[noAppearance]);
            Assert.Equal(new PyInt(0), states[savedAppearance]);
            Assert.Equal(new PyInt(4), states[forced]);

            // Re-reading the row after a retry preserves the persisted state.
            Assert.Equal(new PyInt(4), (await ReadStatesAsync())[forced]);

            var enter = await select.CallAsync(new ServiceCall
            {
                Args = [new PyInt(savedAppearance)],
                Method = "SelectCharacterID",
                Session = session,
            });
            Assert.Same(PyNone.Instance, enter);
            Assert.Equal(savedAppearance, session.CharacterId);
        }
        finally
        {
            await host.ShutdownAsync();
        }

        // The forced state survives Host restart and a successful appearance save clears it.
        var restarted = await HostComposition.BuildAsync(Settings());
        try
        {
            var session = new ClientSession { UserId = 1 };
            var select = restarted.Registry.Lookup("charUnboundMgr")!;
            var data = Assert.IsType<PyTuple>(await select.CallAsync(new ServiceCall
            {
                Args = [],
                Method = "GetCharacterSelectionData",
                Session = session,
            }));
            var forcedRow = Assert.IsType<PyObject>(Assert.Single(
                Assert.IsType<PyList>(data.Items[2]).Items,
                row =>
                {
                    var fields = Assert.IsType<PyDict>(Assert.IsType<PyObject>(row).Args).Entries;
                    return fields.Any(entry => entry.Key == new PyStr("characterID") && entry.Value == new PyInt(forced));
                }));
            var forcedFields = Assert.IsType<PyDict>(forcedRow.Args).Entries.ToDictionary(
                entry => Assert.IsType<PyStr>(entry.Key).Value,
                entry => entry.Value,
                StringComparer.Ordinal);
            Assert.Equal(new PyInt(4), forcedFields["paperdollState"]);

            await restarted.Registry.Lookup("paperDollServer")!.CallAsync(new ServiceCall
            {
                Args = [new PyInt(forced), new PyList([]), PyNone.Instance, PyBool.True],
                Method = "UpdateExistingCharacterFull",
                Session = new ClientSession { UserId = 1 },
            });
            var saved = Assert.IsType<PyTuple>(await select.CallAsync(new ServiceCall
            {
                Args = [],
                Method = "GetCharacterSelectionData",
                Session = new ClientSession { UserId = 1 },
            }));
            var savedRow = Assert.IsType<PyObject>(Assert.Single(
                Assert.IsType<PyList>(saved.Items[2]).Items,
                row => Assert.IsType<PyDict>(Assert.IsType<PyObject>(row).Args).Entries.Any(entry =>
                    entry.Key == new PyStr("characterID") && entry.Value == new PyInt(forced))));
            var savedFields = Assert.IsType<PyDict>(savedRow.Args).Entries.ToDictionary(
                entry => Assert.IsType<PyStr>(entry.Key).Value,
                entry => entry.Value,
                StringComparer.Ordinal);
            Assert.Equal(new PyInt(0), savedFields["paperdollState"]);
            var entered = new ClientSession { UserId = 1 };
            await select.CallAsync(new ServiceCall
            {
                Args = [new PyInt(forced)],
                Method = "SelectCharacterID",
                Session = entered,
            });
            Assert.Equal(forced, entered.CharacterId);
        }
        finally
        {
            await restarted.ShutdownAsync();
        }
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

    private static async Task<long[]> CreateItemsAsync(NpgsqlDataSource source, PgItemStore items, params NewItem[] newItems)
    {
        await using var connection = await source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var ids = new long[newItems.Length];
        for (var index = 0; index < newItems.Length; index++)
            ids[index] = await items.CreateAsync(connection, transaction, newItems[index]);
        await transaction.CommitAsync();
        return ids;
    }

    private static HostSettings Settings() => new(
        "", "disabled", new ClusterOptions { NewCharacterExperienceEnabled = false, NewCharacterExperienceMode = "disabled" },
        new AccountOptions { AutoCreate = true }, StrictRpc: true);

    private sealed class Sink : IPacketSink
    {
        public List<PyValue> Packets { get; } = [];

        public void Send(PyValue packet) => Packets.Add(packet);
    }
}
