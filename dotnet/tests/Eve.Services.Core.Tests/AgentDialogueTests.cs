using System.Text;
using System.Text.Json.Nodes;
using Eve.Agents;
using Eve.Data;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Npgsql;

namespace Eve.Services.Core.Tests;

/// <summary>DoAction on the bound agent: the greeting, the standing refusal, request, accept, decline, quit and the refusals by name (A4).</summary>
public class AgentDialogueTests
{
    private const long Pilot = 140000021;
    private const long Agent = 3008416;
    private const long Corporation = 1000001;
    private const long Faction = 500003;
    private static readonly DateTimeOffset Start = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);
    private static readonly AgentMissionContent Courier = new(5001, 9100, "", "courier", false);

    private sealed class Sink : IPacketSink
    {
        public List<PyValue> Packets { get; } = [];
        public void Send(PyValue packet) => Packets.Add(packet);
    }

    private sealed class World : IAsyncDisposable
    {
        private readonly string _schema = "dlg_" + Guid.NewGuid().ToString("N")[..12];
        private readonly NpgsqlDataSource _source;
        public DateTimeOffset Now { get; set; } = Start;
        public AgentStandings Standings { get; set; } = new(0, 0, 0);
        public AgentMissionContent? Pool { get; set; }
        public AgentMissionStore Store { get; }

        private World(NpgsqlDataSource source)
        {
            _source = source;
            Store = new AgentMissionStore(source, _schema);
        }

        public static async Task<World> CreateAsync()
        {
            var source = NpgsqlDataSource.Create(PgConnection.ToConnectionString(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
            return new World(source);
        }

        public async Task MigrateAsync()
        {
            await AgentMissionStore.MigrateAsync(_source, _schema);
        }

        public AgentDialogueSources Sources() => new(
            Store,
            (_, _, _, _, _) => ValueTask.FromResult(Standings),
            (_, _, _) => ValueTask.FromResult(0),
            _ => Faction,
            (_, _, _) => ValueTask.FromResult(Pool),
            () => Now);

        public async ValueTask DisposeAsync()
        {
            await using (var drop = _source.CreateCommand($"DROP SCHEMA IF EXISTS {_schema} CASCADE"))
            {
                await drop.ExecuteNonQueryAsync();
            }
            await _source.DisposeAsync();
        }
    }

    private static AgentMissionAgentData Basic(long level = 1, long type = 2) => new(
        AgentId: Agent, AgentTypeId: type, DivisionId: 22, Level: level, StationId: 60000001, CorporationId: Corporation,
        IsLocatorAgent: false, IsInSpace: false, CareerId: null, ImportantMission: false, MissionKind: "courier",
        MissionTypeLabel: "courier", SpecialityId: null, StationTypeId: null, BloodlineId: null, SolarSystemId: 30000001,
        DungeonId: null, SpawnPointId: null, TypeId: null);

    private static PyValue Button(long id) => new PyInt(id);

    /// <summary>The user error's text: the payload is marshalled, and the message is in it.</summary>
    private static string Info(Exception exception) => exception is MachoWrappedException wrapped
        ? Encoding.UTF8.GetString(MarshalCodec.Encode(wrapped.Error.Payload))
        : exception.ToString();

    /// <summary>The agent's line of a reply: (message, contentID) inside the (agentSays, actions) pair.</summary>
    private static PyTuple SaysOf(AgentDialogueReply reply) =>
        Assert.IsType<PyTuple>(Assert.IsType<PyTuple>(reply.ToWire()).Items[0]).Items[0] is PyTuple says ? says : throw new InvalidOperationException("no agent line");

    private static (long ActionId, int ButtonType)[] Pairs(params (long ActionId, int ButtonType)[] pairs) => pairs;

    private static AgentDialogueMachine Machine(World world) => new(world.Sources());

    [AgentsPgFact]
    public async Task The_first_contact_greets_and_asks_for_a_mission_only_when_one_can_be_offered()
    {
        await using var world = await World.CreateAsync();
        await world.MigrateAsync();
        var machine = Machine(world);

        var none = await machine.DoActionAsync(Pilot, Basic(), PyNone.Instance, false, default);
        world.Pool = Courier;
        var offered = await machine.DoActionAsync(Pilot, Basic(), PyNone.Instance, false, default);

        Assert.Empty(none.Actions);
        Assert.Equal(Pairs((AgentDialogueToken.Request, AgentDialogueButton.RequestMission)), offered.Actions.ToArray());
        var parts = Assert.IsType<PyTuple>(Assert.IsType<PyTuple>(offered.ToWire()).Items[0]);
        var says = Assert.IsType<PyTuple>(parts.Items[0]);
        var message = Assert.IsType<PyTuple>(says.Items[0]);
        Assert.Equal(AgentDialogueMachine.GreetingLabel, ((PyStr)message.Items[0]).Value);
        Assert.Null(await world.Store.ReadAsync(Pilot, Agent));
    }

    [AgentsPgFact]
    public async Task A_request_offers_the_mission_then_accept_holds_it_and_quit_waits_for_the_station()
    {
        await using var world = await World.CreateAsync();
        await world.MigrateAsync();
        world.Pool = Courier;
        var machine = Machine(world);

        var requested = await machine.DoActionAsync(Pilot, Basic(), Button(AgentDialogueToken.Request), false, default);
        Assert.Equal(Pairs((AgentDialogueToken.Accept, AgentDialogueButton.Accept), (AgentDialogueToken.Decline, AgentDialogueButton.Decline)), requested.Actions.ToArray());
        Assert.Equal(AgentMissionState.Offered, (await world.Store.ReadAsync(Pilot, Agent))!.State);

        var accepted = await machine.DoActionAsync(Pilot, Basic(), Button(AgentDialogueToken.Accept), false, default);
        Assert.Empty(accepted.Actions);
        Assert.Equal(AgentMissionState.Accepted, (await world.Store.ReadAsync(Pilot, Agent))!.State);

        var away = await machine.DoActionAsync(Pilot, Basic(), Button(AgentDialogueToken.Quit), false, default);
        Assert.Empty(away.Actions);
        Assert.Equal(AgentMissionState.Accepted, (await world.Store.ReadAsync(Pilot, Agent))!.State);

        var shown = await machine.DoActionAsync(Pilot, Basic(), PyNone.Instance, true, default);
        Assert.Equal(Pairs((AgentDialogueToken.Quit, AgentDialogueButton.Quit)), shown.Actions.ToArray());
        // Quit asks the client first (YesNo); a yes quits the mission.
        var asked = await machine.StepAsync(Pilot, Basic(), Button(AgentDialogueToken.Quit), true, default);
        Assert.Equal("YesNo", asked.Prompt!.Method);
        Assert.Equal(AgentMissionState.Accepted, (await world.Store.ReadAsync(Pilot, Agent))!.State);
        var quit = Assert.IsType<AgentDialogueStep>(await asked.Prompt.Then(PyBool.True)).Reply!;
        Assert.Equal(AgentMissionState.Quit, (await world.Store.ReadAsync(Pilot, Agent))!.State);
        var quitInfo = quit.LastActionInfo;
        Assert.Equal(PyBool.True, quitInfo.Entries.Single(entry => entry.Key is PyStr { Value: "missionQuit" }).Value);
    }

    [AgentsPgFact]
    public async Task A_no_to_the_quit_confirmation_keeps_the_mission()
    {
        await using var world = await World.CreateAsync();
        await world.MigrateAsync();
        world.Pool = Courier;
        var machine = Machine(world);
        await machine.DoActionAsync(Pilot, Basic(), Button(AgentDialogueToken.Request), false, default);
        await machine.DoActionAsync(Pilot, Basic(), Button(AgentDialogueToken.Accept), false, default);

        var asked = await machine.StepAsync(Pilot, Basic(), Button(AgentDialogueToken.Quit), true, default);
        var kept = Assert.IsType<AgentDialogueStep>(await asked.Prompt!.Then(PyBool.False)).Reply!;

        Assert.Equal(AgentMissionState.Accepted, (await world.Store.ReadAsync(Pilot, Agent))!.State);
        Assert.Equal(Pairs((AgentDialogueToken.Quit, AgentDialogueButton.Quit)), kept.Actions.ToArray());
    }

    [AgentsPgFact]
    public async Task A_decline_holds_the_agent_back_for_four_hours_and_the_request_is_not_offered_again()
    {
        await using var world = await World.CreateAsync();
        await world.MigrateAsync();
        world.Pool = Courier;
        var machine = Machine(world);
        await machine.DoActionAsync(Pilot, Basic(), Button(AgentDialogueToken.Request), false, default);

        // Decline asks the client first (YesNo); the yes declines, and the four-hour replay starts from there.
        var asked = await machine.StepAsync(Pilot, Basic(), Button(AgentDialogueToken.Decline), false, default);
        Assert.Equal("YesNo", asked.Prompt!.Method);
        var declined = Assert.IsType<AgentDialogueStep>(await asked.Prompt.Then(PyBool.True)).Reply!;
        world.Now = Start + TimeSpan.FromHours(1);
        var during = await machine.DoActionAsync(Pilot, Basic(), Button(AgentDialogueToken.Request), false, default);
        world.Now = Start + AgentMissionStore.DeclineReplayDelay;
        var after = await machine.DoActionAsync(Pilot, Basic(), Button(AgentDialogueToken.Request), false, default);

        Assert.Empty(declined.Actions);
        Assert.Empty(during.Actions);
        Assert.Equal(AgentMissionState.Offered, (await world.Store.ReadAsync(Pilot, Agent))!.State);
        Assert.Equal(Pairs((AgentDialogueToken.Accept, AgentDialogueButton.Accept), (AgentDialogueToken.Decline, AgentDialogueButton.Decline)), after.Actions.ToArray());
    }

    [AgentsPgFact]
    public async Task A_standing_below_the_level_is_refused_in_the_reference_text_and_a_research_agent_in_its_label()
    {
        await using var world = await World.CreateAsync();
        await world.MigrateAsync();
        world.Pool = Courier;
        world.Standings = new AgentStandings(0, 0, 0);
        var machine = Machine(world);

        var basic = await machine.DoActionAsync(Pilot, Basic(level: 2), PyNone.Instance, false, default);
        var research = await machine.DoActionAsync(Pilot, Basic(level: 2, type: AgentAccessRule.ResearchAgentType), PyNone.Instance, false, default);

        Assert.Empty(basic.Actions);
        Assert.Empty(research.Actions);
        var basicMessage = Assert.IsType<PyStr>(SaysOf(basic).Items[0]);
        Assert.Equal(AgentDialogueMachine.TooLowStandingsText, basicMessage.Value);
        var researchMessage = Assert.IsType<PyTuple>(SaysOf(research).Items[0]);
        Assert.Equal(AgentDialogueMachine.ResearchTooLowLabel, ((PyStr)researchMessage.Items[0]).Value);
    }

    [AgentsPgFact]
    public async Task Research_and_locator_buttons_are_refused_by_name_and_change_nothing()
    {
        await using var world = await World.CreateAsync();
        await world.MigrateAsync();
        world.Pool = Courier;
        var machine = Machine(world);

        foreach (var button in new long[] { AgentDialogueButton.StartResearch, AgentDialogueButton.LocateCharacter })
        {
            var refused = await Assert.ThrowsAnyAsync<Exception>(() => machine.DoActionAsync(Pilot, Basic(), Button(button), false, default).AsTask());
            Assert.Contains("not available on this server yet", Info(refused));
        }
        Assert.Null(await world.Store.ReadAsync(Pilot, Agent));
    }

    [AgentsPgFact]
    public async Task DoAction_on_a_bound_agent_answers_through_the_service_and_is_refused_on_an_unknown_agent()
    {
        await using var world = await World.CreateAsync();
        await world.MigrateAsync();
        world.Pool = Courier;
        var service = new AgentMissionService(
            (_, _) => ValueTask.FromResult<JsonObject?>(null),
            (_, _, _) => ValueTask.FromResult<IReadOnlyDictionary<long, JsonObject>>(new Dictionary<long, JsonObject>()),
            (_, _) => ValueTask.FromResult<JsonArray?>(null),
            (_, _, _) => true,
            new AgentMissionRpcCallbacks(ReadAgents: _ => ValueTask.FromResult<IReadOnlyList<AgentMissionAgentData>>([Basic()])),
            world.Sources());
        var sink = new Sink();
        var session = new ClientSession(sink) { UserId = 7, CharacterId = Pilot };
        session.Set("charid", new PyInt(Pilot));

        var bound = await service.CallAsync(new ServiceCall
        {
            Method = "MachoBindObject",
            Args = [new PyInt(Agent), new PyTuple([new PyStr("DoAction"), new PyTuple([Button(AgentDialogueToken.Request)]), PyNone.Instance])],
            Session = session, CallId = new PyInt(1), Kwargs = PyNone.Instance,
        });
        var reply = Assert.IsType<PyTuple>(Assert.IsType<PyTuple>(bound).Items[1]);

        Assert.Equal(2, reply.Items.Count);
        Assert.Equal(AgentMissionState.Offered, (await world.Store.ReadAsync(Pilot, Agent))!.State);
        var unknown = await Assert.ThrowsAnyAsync<Exception>(() => service.CallAsync(new ServiceCall
        {
            Method = "MachoBindObject", Args = [new PyInt(3008999), PyNone.Instance], Session = session, CallId = new PyInt(2), Kwargs = PyNone.Instance,
        }).AsTask());
        Assert.Contains("That agent is not known", Info(unknown));
    }
}
