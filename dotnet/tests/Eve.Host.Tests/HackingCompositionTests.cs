using System.Text.Json.Nodes;
using Eve.Data;
using Eve.GameStore;
using Eve.Identity;
using Eve.Macho;
using Eve.Marshal;
using Eve.Characters;
using Eve.Services;
using Eve.Sde;
using Eve.Universe;
using Eve.Hacking;
using Npgsql;

namespace Eve.Host.Tests;

/// <summary>
/// The hacking service through the real host composition (HostComposition.BuildAsync, the path eve-host runs), on scratch PostgreSQL schemas: the
/// service is registered under its client name, and a call from a character reaches that character's session.
/// </summary>
[Collection("host-composition")]
public sealed class HackingCompositionTests : IAsyncLifetime
{
    private readonly string _prefix = "hk" + Guid.NewGuid().ToString("N")[..10];
    private readonly Dictionary<string, string?> _saved = [];
    private readonly List<string> _schemas = [];

    /// <summary>The environment the host reads: every schema on its own scratch name, plus the no-simulation switch.</summary>
    private static readonly string[] SchemaVariables =
    [
        "EVE_IDENTITY_SCHEMA", "EVE_GLOBAL_CONFIG_SCHEMA", "EVE_CHARACTERS_SCHEMA", "EVE_INVENTORY_SCHEMA", "EVE_SKILLS_SCHEMA", "EVE_WALLET_SCHEMA",
        "EVE_FITTING_SCHEMA", "EVE_NPC_SCHEMA", "EVE_SPACE_SCHEMA", "EVE_GAMESTORE_SCHEMA", "EVE_ASSET_SAFETY_SCHEMA", "EVE_CORPORATIONS_SCHEMA",
        "EVE_MARKET_SCHEMA", "EVE_FLEET_SCHEMA", "EVE_INDUSTRY_SCHEMA", "EVE_SCHEDULER_SCHEMA", "EVE_EVENTS_SCHEMA", "EVE_RELATIONS_SCHEMA",
        "EVE_CONTENT_SCHEMA", "EVE_CRIMEWATCH_SCHEMA", "EVE_STRUCTURES_SCHEMA",
    ];

    private string Schema(string variable) => variable["EVE_".Length..^"_SCHEMA".Length].ToLowerInvariant() is var name ? $"{_prefix}_{name}" : "";

    public Task InitializeAsync()
    {
        foreach (var variable in SchemaVariables)
        {
            _saved[variable] = Environment.GetEnvironmentVariable(variable);
            var schema = Schema(variable);
            _schemas.Add(schema);
            Environment.SetEnvironmentVariable(variable, schema);
        }
        // no simulation, and no market seed from ESI (as StructureCompositionTests)
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

    private sealed class Sink : IPacketSink
    {
        public List<PyValue> Packets { get; } = [];
        public void Send(PyValue packet) => Packets.Add(packet);
    }

    private static HostSettings Settings() => new(
        "", "disabled", new ClusterOptions { NewCharacterExperienceEnabled = false, NewCharacterExperienceMode = "disabled" },
        new AccountOptions { AutoCreate = true }, StrictRpc: true);

    [PgFact]
    public async Task The_hacking_service_is_registered_and_a_character_without_an_attempt_is_answered_on_its_session()
    {
        var sink = new Sink();
        var session = new ClientSession(sink) { SessionId = 501 };
        session.Set("charid", new PyInt(99_001));

        var host = await HostComposition.BuildAsync(Settings());
        var hacking = host.Registry.Lookup("hackingMgr") ?? throw new InvalidOperationException("hackingMgr is not registered");
        Assert.True(hacking.HasMethod("StartNewGameInstance"));
        Assert.True(hacking.HasMethod("ClickedOnTile"));
        Assert.True(hacking.HasMethod("QuitHackingAttempt"));

        var before = sink.Packets.Count;
        var result = await hacking.CallAsync(new ServiceCall
        {
            Session = session,
            Method = "ClickedOnTile",
            Args = [new PyTuple([new PyInt(0), new PyInt(3)])],
            CallId = new PyInt(1),
        });

        Assert.IsType<PyNone>(result);
        // the acknowledgement of a character with no open attempt is one notification to the session
        Assert.Equal(before + 1, sink.Packets.Count);
        await host.ShutdownAsync();
    }

    [PgFact]
    public async Task Qa_practice_board_is_QA_role_gated_and_starts_over_the_registered_service()
    {
        var host = await HostComposition.BuildAsync(Settings());
        var hacking = host.Registry.Lookup("hackingMgr") ?? throw new InvalidOperationException("hackingMgr is not registered");
        Assert.True(hacking.HasMethod("StartNewGameInstance"));

        static ServiceCall PracticeCall(ClientSession session, long callId = 1) => new()
        {
            Session = session,
            Method = "StartNewGameInstance",
            Args = [PyNone.Instance, new PyInt(HackingWire.GameTypeHacking), new PyInt(22_329), new PyInt(2),
                new PyInt(40), new PyInt(20), new PyInt(3)],
            CallId = new PyInt(callId),
        };

        var deniedSink = new Sink();
        var denied = new ClientSession(deniedSink) { SessionId = 502, CharacterId = 99_002 };
        denied.Set("role", new PyLong(0));
        denied.Set("accountRole", new PyLong(GameMasterRoles.Qa));
        var error = await Assert.ThrowsAsync<MachoWrappedException>(async () => await hacking.CallAsync(PracticeCall(denied)));
        Assert.Equal("Activate an analyzer to hack a container. Standalone practice boards require the QA role.", error.Error.UserErrorMessage);
        Assert.Empty(deniedSink.Packets);

        var sink = new Sink();
        var qa = new ClientSession(sink) { SessionId = 503, CharacterId = 99_003 };
        qa.Set("role", new PyLong(GameMasterRoles.Qa));
        Assert.IsType<PyNone>(await hacking.CallAsync(PracticeCall(qa)));

        var notification = Assert.IsType<PyObject>(Assert.Single(sink.Packets));
        var packet = Assert.IsType<PyTuple>(notification.Args).Items;
        Assert.Equal(MachoMessage.Notification, ((PyInt)packet[0]).Value);
        var address = Assert.IsType<PyTuple>(Assert.IsType<PyObject>(packet[2]).Args).Items;
        Assert.Equal("OnHackingTurnComplete", JsSemantics.StringValue(address[1]));
        var body = Assert.IsType<PyBuffer>(Assert.IsType<PyTuple>(Assert.IsType<PyTuple>(packet[4]).Items[0]).Items[1]);
        var inner = Assert.IsType<PyTuple>(MarshalCodec.Decode(body.Value.ToArray())).Items;
        var callArgs = Assert.IsType<PyTuple>(Assert.IsType<PyTuple>(inner[1]).Items[1]).Items;
        var events = Assert.IsType<PyTuple>(callArgs[0]).Items;
        var tiles = events.Where(item => Number(Field(item, "eventID")) == HackingWire.EventTileCreated).ToList();
        Assert.InRange(tiles.Count, 39, 64);
        Assert.All(tiles, item =>
        {
            var coord = Assert.IsType<PyTuple>(Field(Field(item, "eventData"), "coord")).Items;
            Assert.InRange(Number(coord[0]), 0, 7);
            Assert.InRange(Number(coord[1]), 0, 7);
        });
        var start = events.Single(item => Number(Field(item, "eventID")) == HackingWire.EventGameStart);
        Assert.IsType<PyDict>(Field(start, "eventData"));
        Assert.Same(PyNone.Instance, Field(Field(start, "eventData"), "targetID"));
        await host.ShutdownAsync();
    }

    private static PyValue Field(PyValue keyVal, string key)
    {
        var entries = keyVal switch
        {
            PyDict dict => dict.Entries,
            PyObject { Args: PyDict dict } => dict.Entries,
            _ => throw new Xunit.Sdk.XunitException($"Expected a hacking event mapping, got {keyVal.GetType().Name}"),
        };
        return entries.Single(entry => JsSemantics.StringValue(entry.Key) == key).Value;
    }

    private static long Number(PyValue value) => value switch
    {
        PyInt integer => integer.Value,
        PyLong integer => integer.Value,
        _ => throw new InvalidOperationException(value.ToString()),
    };
}
