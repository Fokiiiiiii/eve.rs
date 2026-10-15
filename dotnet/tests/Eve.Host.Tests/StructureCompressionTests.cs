using Eve.Dogma;
using Eve.Host;
using Eve.Inventory;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;
using Eve.Services.Inventory;
using Eve.Sde;
using Eve.Skills;
using Eve.Structures;
using Npgsql;
using Xunit;

namespace Eve.Host.Tests;

/// <summary>
/// <c>structureCompressionMgr</c> on the real stores and the pinned static data (skipped without EVE_PG_URL; skipped is not passed): a compression in a structure's
/// hangar, the join with a stack of the output, the named refusals, gas decompression with the structure's and the pilot's share, and the removal of a stack
/// that gives nothing.
/// </summary>
public sealed class StructureCompressionTests
{
    private const long Character = 20, Structure = 1_000_000_000_001, Station = 60015249, Ship = 1_000_000_000_050;
    private const int Veldspar = 1230, CompressedVeldspar = 62516, Tritanium = 34, Cytoserocin = 25268, CompressedCytoserocin = 62396;
    private const int Astrahus = 35832, GasDecompressionSkill = 62452;
    private const long Corporation = 98_000_001, Outsider = 98_000_002;
    private const int CorporationDivisionOne = 115;

    private sealed class Route : IAsyncDisposable
    {
        private readonly string[] _schemas;

        private Route(string[] schemas, NpgsqlDataSource source, PgItemStore items, StructureStore structures, PgSkillStore skills, StructureCompressionAuthority authority, Sink packets)
        {
            _schemas = schemas;
            Source = source;
            Items = items;
            Structures = structures;
            Skills = skills;
            Authority = authority;
            Packets = packets;
            Session = new ClientSession(packets);
            Session.Set("charid", new PyInt(Character));
        }

        public NpgsqlDataSource Source { get; }
        public PgItemStore Items { get; }
        public StructureStore Structures { get; }
        public PgSkillStore Skills { get; }
        public StructureCompressionAuthority Authority { get; }
        public Sink Packets { get; }
        public ClientSession Session { get; }
        public Corporate Corporate { get; init; } = new();
        public CharacterSessions Sessions { get; init; } = new();

        public static async Task<Route> CreateAsync(string pgUrl)
        {
            var source = NpgsqlDataSource.Create(Eve.Data.PgConnection.ForTest(pgUrl));
            var sde = new SdeReader(source);
            Assert.NotNull(await sde.BuildAsync());
            var dogma = await DogmaStatic.LoadAsync(sde);
            var id = Guid.NewGuid().ToString("N")[..12];
            var schemas = new[] { $"cmp_i_{id}", $"cmp_s_{id}", $"cmp_k_{id}" };
            await PgItemStore.MigrateAsync(source, schemas[0]);
            await StructureStore.MigrateAsync(source, schemas[1]);
            await PgSkillStore.MigrateAsync(source, schemas[2]);
            var items = new PgItemStore(source, schemas[0]);
            var structures = new StructureStore(source, schemas[1]);
            var skills = new PgSkillStore(source, schemas[2]);
            var corporate = new Corporate();
            var sessions = new CharacterSessions();
            var authority = new StructureCompressionAuthority(source, structures, items, dogma, sde, skills,
                (character, corporation, station, division, operation, token) =>
                {
                    corporate.Asked.Add((character, corporation, station, division, operation));
                    return ValueTask.FromResult(corporate.Take);
                },
                (connection, transaction, corporation, station, token) => Task.FromResult(corporate.Office),
                sessions);
            return new Route(schemas, source, items, structures, skills, authority, new Sink()) { Corporate = corporate, Sessions = sessions };
        }

        /// <summary>A structure of the owner with its Reprocessing service online (or offline).</summary>
        public async Task AnchorAsync(bool reprocessingOnline = true)
        {
            await using var connection = await Source.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            await Structures.CreateAsync(connection, transaction, new NewStructure(Structure, Astrahus, 30_000_142, 0, 0, 0, 0, 98_000_001, 0, "Test", ""));
            await Structures.SetServiceAsync(connection, transaction, Structure, StructureServiceIds.Reprocessing, reprocessingOnline ? ServiceState.Online : ServiceState.Offline, 0);
            await transaction.CommitAsync();
        }

        public async Task<long> GiveAsync(int type, long location, int flag = ItemFlags.Hangar, long quantity = 1, bool singleton = false, long owner = Character)
        {
            await using var connection = await Source.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            var id = await Items.CreateAsync(connection, transaction, new NewItem(type, owner, location, flag, quantity, singleton));
            await transaction.CommitAsync();
            return id;
        }

        public async Task SetSkillAsync(int level)
        {
            await using var connection = await Source.OpenConnectionAsync();
            await using var command = new NpgsqlCommand(
                "INSERT INTO " + _schemas[2] + ".character_skills (character_id, type_id, level, skill_points) VALUES ($1, $2, $3, 0)", connection);
            command.Parameters.AddWithValue(Character);
            command.Parameters.AddWithValue(GasDecompressionSkill);
            command.Parameters.AddWithValue((short)level);
            await command.ExecuteNonQueryAsync();
        }

        public ServiceCall Call(ClientSession? session = null, string method = "CompressItemInStructure") =>
            new() { Method = method, Args = [], Session = session ?? Session };

        public async ValueTask DisposeAsync()
        {
            foreach (var schema in _schemas)
            {
                await using var connection = await Source.OpenConnectionAsync();
                await using var command = new NpgsqlCommand($"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE", connection);
                await command.ExecuteNonQueryAsync();
            }
            await Source.DisposeAsync();
        }
    }

    /// <summary>What the corporation rules of the test grant: an office at the structure, and the take and query access to its division.</summary>
    public sealed class Corporate
    {
        public bool Office { get; set; } = true;
        public bool Take { get; set; } = true;
        public List<(long Character, long Corporation, long Station, int Division, CorporationHangarOperation Operation)> Asked { get; } = [];
    }

    /// <summary>The packets a session was sent, in order.</summary>
    private sealed class Sink : IPacketSink
    {
        public List<PyValue> Sent { get; } = [];
        public void Send(PyValue packet) => Sent.Add(packet);
    }

    /// <summary>The <c>OnItemChange</c> notifications among the packets, by the item they are about.</summary>
    private static List<long> ItemChangeNotices(Sink sink)
    {
        var ids = new List<long>();
        foreach (var packet in sink.Sent.OfType<PyObject>())
        {
            var state = ((PyTuple)packet.Args).Items;
            if (state[0] is not PyInt { Value: MachoMessage.Notification }) continue;
            var address = ((PyTuple)((PyObject)state[2]).Args).Items;
            var body = (PyBuffer)((PyTuple)((PyTuple)state[4]).Items[0]).Items[1];
            var inner = (PyTuple)MarshalCodec.Decode(body.Value.ToArray());
            var name = JsSemantics.StringValue(address[1]);
            if (name != "OnItemChange") continue;
            var row = ((PyPackedRow)((PyTuple)((PyTuple)inner.Items[1]).Items[1]).Items[0]).Values;
            ids.Add(row[0] is PyInt i ? i.Value : ((PyLong)row[0]).Value);
        }
        return ids;
    }

    /// <summary>The message of a refusal: the client's <c>CustomNotify</c> error and the text it shows.</summary>
    private static string Notice(Exception exception)
    {
        var error = Assert.IsType<MachoWrappedException>(exception).Error;
        Assert.Equal("CustomNotify", error.UserErrorMessage);
        var arguments = ((PyTuple)((PyTuple)((PyObjectEx)error.Payload).Header).Items[1]).Items;
        return ((PyStr)((PyDict)arguments[1]).Entries.Single(e => JsSemantics.StringValue(e.Key) == "notify").Value).Value;
    }

    private static string PgUrl() => Environment.GetEnvironmentVariable("EVE_PG_URL") ?? throw new InvalidOperationException("EVE_PG_URL is not set");

    [PgFact]
    public async Task Compressing_ore_in_a_structure_hangar_replaces_the_stack_and_answers_the_six_values()
    {
        await using var route = await Route.CreateAsync(PgUrl());
        await route.AnchorAsync();
        var ore = await route.GiveAsync(Veldspar, Structure, quantity: 1000);
        route.Session.Set("structureid", new PyLong(Structure));

        var outcome = await route.Authority.CompressAsync(route.Call(), ore);

        Assert.Equal(ore, outcome.SourceItemId);
        Assert.Equal(Veldspar, outcome.SourceTypeId);
        Assert.Equal(1000, outcome.SourceQuantity);
        Assert.Equal(CompressedVeldspar, outcome.OutputTypeId);
        Assert.Equal(1000, outcome.OutputQuantity);
        var output = await route.Items.GetAsync(outcome.OutputItemId);
        Assert.NotNull(output);
        Assert.Equal((Character, Structure, ItemFlags.Hangar, CompressedVeldspar, 1000L), (output.OwnerId, output.LocationId, output.Flag, output.TypeId, output.Quantity));
        Assert.Null(await route.Items.GetAsync(ore));
        Assert.NotEmpty(ItemChangeNotices(route.Packets));
    }

    [PgFact]
    public async Task The_output_joins_the_stack_of_its_type_the_hangar_already_has()
    {
        await using var route = await Route.CreateAsync(PgUrl());
        await route.AnchorAsync();
        var stack = await route.GiveAsync(CompressedVeldspar, Structure, quantity: 500);
        var ore = await route.GiveAsync(Veldspar, Structure, quantity: 1000);
        route.Session.Set("structureid", new PyLong(Structure));

        var outcome = await route.Authority.CompressAsync(route.Call(), ore);

        Assert.Equal(stack, outcome.OutputItemId);
        Assert.Equal(1500, (await route.Items.GetAsync(stack))!.Quantity);
    }

    [PgFact]
    public async Task Each_refusal_is_a_named_notice_and_changes_nothing()
    {
        await using var route = await Route.CreateAsync(PgUrl());
        await route.AnchorAsync(reprocessingOnline: false);
        var ore = await route.GiveAsync(Veldspar, Structure, quantity: 1000);

        route.Session.Set("structureid", new PyLong(Structure));
        Assert.Equal("The reprocessing service of this structure is offline.",
            Notice(await Assert.ThrowsAsync<MachoWrappedException>(() => route.Authority.CompressAsync(route.Call(), ore))));

        await route.AnchorAsync(reprocessingOnline: true);
        var notDocked = new ClientSession(new Sink());
        notDocked.Set("charid", new PyInt(Character));
        Assert.Equal("You must be docked at a structure to compress here.",
            Notice(await Assert.ThrowsAsync<MachoWrappedException>(() => route.Authority.CompressAsync(route.Call(notDocked), ore))));

        var mineral = await route.GiveAsync(Tritanium, Structure, quantity: 1000);
        Assert.Equal("That item cannot be compressed in a structure.",
            Notice(await Assert.ThrowsAsync<MachoWrappedException>(() => route.Authority.CompressAsync(route.Call(), mineral))));

        var atStation = await route.GiveAsync(Veldspar, Station, quantity: 1000);
        Assert.Equal("That item is not in your hangar or your ship at this structure.",
            Notice(await Assert.ThrowsAsync<MachoWrappedException>(() => route.Authority.CompressAsync(route.Call(), atStation))));

        Assert.Equal(1000, (await route.Items.GetAsync(ore))!.Quantity);
        Assert.Equal(1000, (await route.Items.GetAsync(mineral))!.Quantity);
    }

    [PgFact]
    public async Task Gas_decompresses_at_the_structure_share_and_the_pilot_share_adds_a_percent_a_level()
    {
        await using var route = await Route.CreateAsync(PgUrl());
        await route.AnchorAsync();
        route.Session.Set("structureid", new PyLong(Structure));

        // a Reprocessing facility gives 80 % (SDE 3262) and an Astrahus adds nothing; with no skill the total is the structure's share
        Assert.Equal((0.8, 0.0), await route.Authority.EfficiencyAsync(route.Call(method: "GetMyGasDecompressionEfficiency")));
        var gas = await route.GiveAsync(CompressedCytoserocin, Structure, quantity: 1000);
        var outcome = await route.Authority.DecompressAsync(route.Call(), gas);
        Assert.Equal((Cytoserocin, 800L), (outcome.OutputTypeId, outcome.OutputQuantity));
        var output = await route.Items.GetAsync(outcome.OutputItemId);
        Assert.Equal((Cytoserocin, 800L, Structure), (output!.TypeId, output.Quantity, output.LocationId));

        // three levels of the skill add three percent
        var second = await route.GiveAsync(CompressedCytoserocin, Structure, quantity: 1000);
        await route.SetSkillAsync(3);
        Assert.Equal((0.8, 0.03), await route.Authority.EfficiencyAsync(route.Call(method: "GetMyGasDecompressionEfficiency")));
        var next = await route.Authority.DecompressAsync(route.Call(), second);
        Assert.Equal(830L, next.OutputQuantity);
    }

    [PgFact]
    public async Task A_gas_stack_too_small_to_give_a_unit_is_removed_and_answers_no_output()
    {
        await using var route = await Route.CreateAsync(PgUrl());
        await route.AnchorAsync();
        route.Session.Set("structureid", new PyLong(Structure));
        var gas = await route.GiveAsync(CompressedCytoserocin, Structure, quantity: 1);

        var outcome = await route.Authority.DecompressAsync(route.Call(), gas);

        Assert.Equal((0L, 0L), (outcome.OutputItemId, outcome.OutputQuantity));
        Assert.Null(await route.Items.GetAsync(gas));
    }

    [PgFact]
    public async Task Without_a_docked_structure_the_efficiency_is_zero()
    {
        await using var route = await Route.CreateAsync(PgUrl());
        await route.AnchorAsync();

        Assert.Equal((0.0, 0.0), await route.Authority.EfficiencyAsync(route.Call(method: "GetMyGasDecompressionEfficiency")));
    }

    [PgFact]
    public async Task A_corporation_hangar_item_in_its_office_at_the_structure_is_compressed_into_the_corporation_hangar_by_a_member_who_may_take()
    {
        await using var route = await Route.CreateAsync(PgUrl());
        await route.AnchorAsync();
        var ore = await route.GiveAsync(Veldspar, Structure, CorporationDivisionOne, 1000, owner: Corporation);
        route.Session.Set("structureid", new PyLong(Structure));
        route.Session.Set("corpid", new PyInt(Corporation));

        var outcome = await route.Authority.CompressAsync(route.Call(), ore);

        var output = await route.Items.GetAsync(outcome.OutputItemId);
        Assert.NotNull(output);
        Assert.Equal((Corporation, Structure, CorporationDivisionOne, CompressedVeldspar, 1000L),
            (output!.OwnerId, output.LocationId, output.Flag, output.TypeId, output.Quantity));
        Assert.Null(await route.Items.GetAsync(ore));
        Assert.Contains((Character, Corporation, Structure, CorporationDivisionOne, CorporationHangarOperation.Take), route.Corporate.Asked);
    }

    [PgFact]
    public async Task A_corporation_hangar_item_is_refused_without_take_access_or_without_an_office_and_changes_nothing()
    {
        await using var route = await Route.CreateAsync(PgUrl());
        await route.AnchorAsync();
        var ore = await route.GiveAsync(Veldspar, Structure, CorporationDivisionOne, 1000, owner: Corporation);
        route.Session.Set("structureid", new PyLong(Structure));
        route.Session.Set("corpid", new PyInt(Corporation));
        const string refusal = "That item is not in your hangar or your ship at this structure.";

        route.Corporate.Take = false;
        Assert.Equal(refusal, Notice(await Assert.ThrowsAsync<MachoWrappedException>(() => route.Authority.CompressAsync(route.Call(), ore))));
        route.Corporate.Take = true;
        route.Corporate.Office = false;
        Assert.Equal(refusal, Notice(await Assert.ThrowsAsync<MachoWrappedException>(() => route.Authority.CompressAsync(route.Call(), ore))));

        Assert.Equal(1000, (await route.Items.GetAsync(ore))!.Quantity);
    }

    [PgFact]
    public async Task Corporation_members_online_who_may_query_the_division_are_told_of_the_change_and_outsiders_are_not()
    {
        await using var route = await Route.CreateAsync(PgUrl());
        await route.AnchorAsync();
        var ore = await route.GiveAsync(Veldspar, Structure, CorporationDivisionOne, 1000, owner: Corporation);
        route.Session.Set("structureid", new PyLong(Structure));
        route.Session.Set("corpid", new PyInt(Corporation));
        var memberSink = new Sink();
        var member = new ClientSession(memberSink) { SessionId = 40 };
        member.Set("charid", new PyInt(41));
        member.Set("corpid", new PyInt(Corporation));
        route.Sessions.Enter(41, member);
        var outsiderSink = new Sink();
        var outsider = new ClientSession(outsiderSink) { SessionId = 42 };
        outsider.Set("charid", new PyInt(43));
        outsider.Set("corpid", new PyInt(Outsider));
        route.Sessions.Enter(43, outsider);

        await route.Authority.CompressAsync(route.Call(), ore);

        Assert.NotEmpty(ItemChangeNotices(memberSink));
        Assert.Empty(ItemChangeNotices(outsiderSink));
    }
}
