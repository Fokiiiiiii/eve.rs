using Eve.Characters;
using Eve.Corporations;
using Eve.Data;
using Eve.Host;
using Eve.Identity;
using Eve.Inventory;
using Eve.Macho;
using Eve.Marshal;
using Eve.Skills;
using Eve.Services;
using Eve.Wallet;
using Npgsql;

namespace Eve.Host.Tests;

[Collection("host-composition")]
public sealed class ReprocessingHostTests : IAsyncLifetime
{
    private sealed class Capture : IPacketSink
    {
        public List<PyValue> Packets { get; } = [];
        public void Send(PyValue packet) => Packets.Add(packet);
    }

    private const int AccountId = 98_000_031;
    private const long StationId = 60_003_760;
    private const int Jita = 30_000_142;
    private const int Veldspar = 1230;
    private const int Tritanium = 34;
    private readonly string _prefix = "rp" + Guid.NewGuid().ToString("N")[..10];
    private readonly Dictionary<string, string?> _saved = [];
    private readonly List<string> _schemas = [];

    private static readonly string[] SchemaVariables =
    [
        "EVE_IDENTITY_SCHEMA", "EVE_GLOBAL_CONFIG_SCHEMA", "EVE_CHARACTERS_SCHEMA", "EVE_INVENTORY_SCHEMA", "EVE_SKILLS_SCHEMA", "EVE_WALLET_SCHEMA",
        "EVE_FITTING_SCHEMA", "EVE_NPC_SCHEMA", "EVE_SPACE_SCHEMA", "EVE_GAMESTORE_SCHEMA", "EVE_ASSET_SAFETY_SCHEMA", "EVE_CORPORATIONS_SCHEMA",
        "EVE_MARKET_SCHEMA", "EVE_FLEET_SCHEMA", "EVE_INDUSTRY_SCHEMA", "EVE_SCHEDULER_SCHEMA", "EVE_EVENTS_SCHEMA", "EVE_RELATIONS_SCHEMA",
        "EVE_CONTENT_SCHEMA", "EVE_CRIMEWATCH_SCHEMA", "EVE_STRUCTURES_SCHEMA", "EVE_TRADE_SCHEMA", "EVE_BOOKMARKS_SCHEMA", "EVE_ESS_SCHEMA",
        "EVE_CONTRABAND_SCHEMA", "EVE_PLANETS_SCHEMA",
    ];

    private static string Url => Environment.GetEnvironmentVariable("EVE_PG_URL")!;
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
        foreach (var (name, value) in new[] { ("EVE_SIM", "off"), ("EVE_MARKET_SEED", "skip"), ("EVE_WORMHOLES", "off") })
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
        foreach (var (name, value) in _saved) Environment.SetEnvironmentVariable(name, value);
    }

    [PgFact]
    public async Task Reprocess_uses_the_registered_host_service_and_keeps_the_settlement_after_restart()
    {
        var host = await HostComposition.BuildAsync(Settings());
        try
        {
            await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(Url));
            var characters = new PgCharacterStore(source, Schema("EVE_CHARACTERS_SCHEMA"));
            var character = await InsertPilotAsync(source, characters);
            var items = new PgItemStore(source, Schema("EVE_INVENTORY_SCHEMA"));
            var wallet = new WalletTransactions(source, Schema("EVE_WALLET_SCHEMA"));
            var balances = new PgWalletStore(source, Schema("EVE_WALLET_SCHEMA"));
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            long input;
            await using (var connection = await source.OpenConnectionAsync())
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                await wallet.EnsureAsync(connection, transaction, character);
                await wallet.AdjustAsync(connection, transaction, character, 100_000m, 0, 0,
                    "reprocessing host test funds", now, $"reprocess-host-funds:{Guid.NewGuid():N}");
                input = await items.CreateAsync(connection, transaction,
                    new NewItem(Veldspar, character, StationId, ItemFlags.Hangar, 100));
                await transaction.CommitAsync();
            }

            var session = Session(character);
            var service = host.Registry.Lookup("reprocessingSvc") ?? throw new InvalidOperationException("reprocessingSvc is not registered");
            var call = PlantCall(session, "Reprocess",
                new PyList([new PyLong(input)]), new PyLong(StationId), new PyLong(character), new PyLong(StationId),
                new PyInt(ItemFlags.Hangar));
            var response = (PyTuple)await service.CallAsync(call);
            var result = (PyList)response.Items[1];
            var processed = Assert.IsType<PyList>(result.Items[0]);
            Assert.Equal(input, Assert.IsType<PyLong>(Assert.Single(processed.Items)).Value);
            Assert.Contains(Assert.IsType<PyDict>(result.Items[1]).Entries,
                entry => entry.Key is PyInt type && type.Value == Tritanium && entry.Value is PyLong quantity && quantity.Value > 0);

            await host.ShutdownAsync();
            host = await HostComposition.BuildAsync(Settings());

            Assert.Null(await items.GetAsync(input));
            var yields = await items.InLocationAsync(StationId, character, default);
            Assert.Contains(yields, item => item.TypeId == Tritanium && item.Flag == ItemFlags.Hangar);
            Assert.NotNull(await balances.GetAsync(character));
        }
        finally
        {
            await host.ShutdownAsync();
        }
    }

    [PgFact]
    public async Task Reprocess_persists_corporation_hangar_yields_and_checks_member_role_and_office()
    {
        var host = await HostComposition.BuildAsync(Settings());
        try
        {
            await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(Url));
            var characters = new PgCharacterStore(source, Schema("EVE_CHARACTERS_SCHEMA"));
            var founder = await InsertPilotAsync(source, characters);
            var member = await InsertPilotAsync(source, characters);
            var walletStore = new PgWalletStore(source, Schema("EVE_WALLET_SCHEMA"));
            var walletTransactions = new WalletTransactions(source, Schema("EVE_WALLET_SCHEMA"));
            var skills = new PgSkillStore(source, Schema("EVE_SKILLS_SCHEMA"));
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            await using (var connection = await source.OpenConnectionAsync())
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                await walletStore.OpenAsync(connection, transaction, founder, 5_000_000m, now);
                await walletStore.OpenAsync(connection, transaction, member, 100_000m, now);
                await transaction.CommitAsync();
            }

            var corporations = new CorporationStore(source, characters, walletStore, walletTransactions, skills,
                () => now, Schema("EVE_CORPORATIONS_SCHEMA"));
            var corporation = await corporations.CreateAsync(founder,
                new NewCorporation("Reprocessing Office Test", "ROT", "", "", 0.05m), $"reprocess-office:{founder}");
            var application = await corporations.ApplyAsync(member, corporation.Id, "");
            await corporations.RespondAsync(founder, application, 6, "", "");

            var offices = new CorporationOfficeStore(source, Schema("EVE_CORPORATIONS_SCHEMA"));
            var office = await offices.WriteAsync((connection, transaction) => offices.RentAsync(
                connection, transaction, corporation.Id, 900_003_760,
                new CorporationOfficeStation(StationId, 1531, 1_000_001, Jita, 0.9),
                CorporationOfficeStore.MinimumRentIsk, now));
            var items = new PgItemStore(source, Schema("EVE_INVENTORY_SCHEMA"));
            long founderInput;
            long memberInput;
            await using (var connection = await source.OpenConnectionAsync())
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                founderInput = await items.CreateAsync(connection, transaction,
                    new NewItem(Veldspar, founder, StationId, ItemFlags.Hangar, 100));
                memberInput = await items.CreateAsync(connection, transaction,
                    new NewItem(Veldspar, member, StationId, ItemFlags.Hangar, 100));
                await transaction.CommitAsync();
            }

            var service = host.Registry.Lookup("reprocessingSvc") ?? throw new InvalidOperationException("reprocessingSvc is not registered");
            await Assert.ThrowsAsync<MachoWrappedException>(async () => await service.CallAsync(PlantCall(Session(founder), "Reprocess",
                new PyList([new PyLong(founderInput)]), new PyLong(StationId), new PyLong(corporation.Id),
                new PyLong(office.OfficeId + 1), new PyInt(115))));
            Assert.NotNull(await items.GetAsync(founderInput));
            Assert.Empty(await items.InLocationAsync(office.OfficeId, corporation.Id, default));

            var capture = new Capture();
            var founderSession = Session(founder, capture);
            var dispatcher = new PacketDispatcher(host.Registry, () => now);
            Assert.True(await dispatcher.DispatchAsync(ClientCallPacket(1,
                new PyList([new PyLong(founderInput)]), new PyLong(StationId), new PyLong(corporation.Id),
                new PyLong(office.OfficeId), new PyInt(115)), founderSession));
            var founderResponsePacket = capture.Packets.Single(packet => packet is PyObject { Name: PyStr name } && name.Value.EndsWith(".CallRsp", StringComparison.Ordinal));
            var founderResponse = Assert.IsType<PyTuple>(Result(founderResponsePacket));
            var founderResult = Assert.IsType<PyList>(founderResponse.Items[1]);
            Assert.Equal(founderInput, Assert.IsType<PyLong>(Assert.Single(Assert.IsType<PyList>(founderResult.Items[0]).Items)).Value);
            var corporationYields = await items.InLocationAsync(office.OfficeId, corporation.Id, default);
            Assert.Contains(corporationYields, item => item.TypeId == Tritanium && item.Flag == 115);

            var yieldsBeforeDeniedCall = corporationYields.Count;
            await Assert.ThrowsAsync<MachoWrappedException>(async () => await service.CallAsync(PlantCall(Session(member), "Reprocess",
                new PyList([new PyLong(memberInput)]), new PyLong(StationId), new PyLong(corporation.Id),
                new PyLong(office.OfficeId), new PyInt(115))));
            Assert.NotNull(await items.GetAsync(memberInput));
            Assert.Equal(yieldsBeforeDeniedCall, (await items.InLocationAsync(office.OfficeId, corporation.Id, default)).Count);
        }
        finally
        {
            await host.ShutdownAsync();
        }
    }

    private static ServiceCall PlantCall(ClientSession session, string method, params PyValue[] args) => new()
    {
        Method = "MachoBindObject",
        Args =
        [
            new PyLong(StationId),
            new PyTuple([new PyStr(method), new PyTuple(args), PyNone.Instance]),
        ],
        Session = session,
    };

    private static async Task<long> InsertPilotAsync(NpgsqlDataSource source, PgCharacterStore characters)
    {
        var pilot = new NewCharacter(AccountId, $"Reprocessing Host Pilot {Guid.NewGuid():N}", Gender: 1, RaceId: 1, BloodlineId: 1, AncestryId: 10,
            SchoolId: 35, TypeId: 1373, CorporationId: 98_000_030, FactionId: 500001, StationId: StationId, SolarSystemId: Jita,
            ConstellationId: 20000020, RegionId: 10000002, DollInfo: null, PortraitInfo: null);
        await using var connection = await source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var id = await characters.InsertAsync(connection, transaction, pilot);
        await transaction.CommitAsync();
        return id;
    }

    private static PyValue ClientCallPacket(int callId, params PyValue[] reprocessArgs)
    {
        var source = new MachoAddress(AddressKind.Client, CallId: new PyInt(callId), ClientId: new PyInt(1));
        var destination = new MachoAddress(AddressKind.Node, Service: "reprocessingSvc",
            CallId: new PyInt(callId), NodeId: new PyInt(MachoMessage.ProxyNodeId));
        var kwargs = new PyDict([new KeyValuePair<PyValue, PyValue>(new PyStr("machoVersion"), new PyInt(1))]);
        var nested = new PyTuple([new PyStr("Reprocess"), new PyTuple(reprocessArgs), PyNone.Instance]);
        var body = new PyTuple([PyNone.Instance, new PyStr("MachoBindObject"), new PyTuple([new PyLong(StationId), nested]), kwargs]);
        var payload = new PyTuple([new PyTuple([PyBool.True, new PySubStream(body)])]);
        return new PyObject(
            new PyStr("carbon.common.script.net.machoNetPacket.CallReq"),
            new PyTuple([
                new PyInt(MachoMessage.CallReq), source.Encode(), destination.Encode(),
                new PyInt(7), payload, new PyDict([]),
                PyNone.Instance, PyNone.Instance, PyNone.Instance, PyNone.Instance,
                PyNone.Instance, PyNone.Instance, PyNone.Instance, PyNone.Instance,
            ]));
    }

    private static PyValue Result(PyValue response)
    {
        var packet = Assert.IsType<PyTuple>(Assert.IsType<PyObject>(response).Args).Items;
        Assert.Equal(MachoMessage.CallRsp, Assert.IsType<PyInt>(packet[0]).Value);
        return Assert.IsType<PySubStream>(Assert.IsType<PyTuple>(packet[4]).Items[0]).Decoded!;
    }

    private static ClientSession Session(long character, IPacketSink? sink = null)
    {
        var session = sink is null ? new ClientSession { SessionId = 42, CharacterId = character } : new ClientSession(sink) { SessionId = 42, CharacterId = character };
        session.Set("stationid", new PyLong(StationId));
        session.Set("solarsystemid2", new PyLong(Jita));
        return session;
    }

    private static HostSettings Settings() => new(
        "", "disabled", new ClusterOptions { NewCharacterExperienceEnabled = false, NewCharacterExperienceMode = "disabled" },
        new AccountOptions { AutoCreate = true }, StrictRpc: true);
}
