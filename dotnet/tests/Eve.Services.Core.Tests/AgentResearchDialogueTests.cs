using Eve.Agents;
using Eve.Data;
using Eve.Marshal;
using Eve.Services.Core;
using Npgsql;

namespace Eve.Services.Core.Tests;

/// <summary>The R&amp;D dialogue (buttons 12 to 14): the field choice, the cancel question, the datacore quantity, over the real research store.</summary>
public class AgentResearchDialogueTests
{
    private const long Pilot = 140000021;
    private const long Agent = 3009318;
    private const long Corporation = 1000001;
    private const long Faction = 500003;
    private const long Station = 60000001;
    private const int Quantum = 11455;
    private const int Rocket = 11449;
    private static readonly DateTimeOffset Start = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);

    private sealed class FakePort(ResearchProjectStore projects) : IAgentResearchPort
    {
        public IReadOnlyList<AgentResearchField> Fields { get; } =
        [
            new AgentResearchField(Quantum, 20414, "Datacore - Quantum Physics"),
            new AgentResearchField(Rocket, 20415, "Datacore - Rocket Science"),
        ];

        public ResearchProjectStore Projects { get; } = projects;

        public List<(long Skill, long Quantity, long Station)> Bought { get; } = [];

        public ValueTask<AgentDatacorePurchase> BuyDatacoresAsync(long characterId, long agentId, long skillTypeId, long quantity, long stationId, CancellationToken cancellationToken)
        {
            Bought.Add((skillTypeId, quantity, stationId));
            return ValueTask.FromResult(new AgentDatacorePurchase(AgentDatacoreRefusal.None, quantity));
        }
    }

    private sealed class World : IAsyncDisposable
    {
        private readonly string _schema = "rsd_" + Guid.NewGuid().ToString("N")[..12];
        private readonly NpgsqlDataSource _source;

        public DateTimeOffset Now { get; set; } = Start;
        public Dictionary<int, int> Levels { get; } = new() { [ResearchRules.ScienceSkillTypeId] = 5, [ResearchRules.NegotiationSkillTypeId] = 5, [Quantum] = 4, [Rocket] = 0 };
        public AgentMissionStore Missions { get; }
        public ResearchProjectStore Projects { get; }
        public FakePort Port { get; }

        private World(NpgsqlDataSource source)
        {
            _source = source;
            Missions = new AgentMissionStore(source, _schema);
            Projects = new ResearchProjectStore(source, _schema);
            Port = new FakePort(Projects);
        }

        public static async Task<World> CreateAsync()
        {
            var source = NpgsqlDataSource.Create(PgConnection.ToConnectionString(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
            var world = new World(source);
            await AgentMissionStore.MigrateAsync(source, world._schema);
            await ResearchProjectStore.MigrateAsync(source, world._schema);
            return world;
        }

        public AgentDialogueMachine Machine() => new(new AgentDialogueSources(
            Missions,
            (_, _, _, _, _) => ValueTask.FromResult(new AgentStandings(0, 10, 10)),
            (_, skill, _) => ValueTask.FromResult(Levels.GetValueOrDefault(skill)),
            _ => Faction,
            (_, _, _) => ValueTask.FromResult<AgentMissionContent?>(null),
            () => Now,
            null,
            Port));

        public async ValueTask DisposeAsync()
        {
            await using (var drop = _source.CreateCommand($"DROP SCHEMA IF EXISTS {_schema} CASCADE"))
                await drop.ExecuteNonQueryAsync();
            await _source.DisposeAsync();
        }
    }

    private static AgentMissionAgentData ResearchAgent(long level = 1) => new(
        AgentId: Agent, AgentTypeId: AgentAccessRule.ResearchAgentType, DivisionId: 18, Level: level, StationId: Station,
        CorporationId: Corporation, IsLocatorAgent: false, IsInSpace: false, CareerId: null, ImportantMission: false, MissionKind: "research",
        MissionTypeLabel: "research", SpecialityId: null, StationTypeId: null, BloodlineId: null, SolarSystemId: 30000001,
        DungeonId: null, SpawnPointId: null, TypeId: null);

    private static PyValue Button(long id) => new PyInt(id);

    private static PyValue Choice(long index) => new PyTuple([PyBool.True, new PyStr($"radioboxOption{index}Selected")]);

    private static PyValue Field(AgentDialogueStep step, string key) =>
        step.Prompt!.Kwargs.Entries.Single(entry => entry.Key is PyStr name && name.Value == key).Value;

    [AgentsPgFact]
    public async Task Start_offers_the_qualifying_fields_and_the_answer_starts_a_project_at_the_formula_rate()
    {
        await using var world = await World.CreateAsync();
        var machine = world.Machine();

        var asked = await machine.StepAsync(Pilot, ResearchAgent(), Button(AgentDialogueToken.StartResearch), false, default);
        Assert.Equal("SingleChoiceBox", asked.Prompt!.Method);
        Assert.Single(Assert.IsType<PyList>(asked.Prompt.Args[2]).Items);
        var started = Assert.IsType<AgentDialogueStep>(await asked.Prompt.Then(Choice(1))).Reply!;

        var project = Assert.IsType<ResearchProject>(await world.Projects.ReadAsync(Pilot, Agent));
        Assert.Equal(Quantum, project.SkillTypeId);
        // Negotiation V, effective standing 10 (no Connections), specialty IV, agent level I: (1 + 55/100) * 5^2.
        Assert.Equal(38.75, project.PointsPerDay, 9);
        Assert.Equal(new (long, int)[] { (AgentDialogueToken.BuyDatacores, AgentDialogueButton.BuyDatacores), (AgentDialogueToken.CancelResearch, AgentDialogueButton.CancelResearch) },
            started.Actions.ToArray());
    }

    [AgentsPgFact]
    public async Task Start_is_refused_by_name_without_the_science_skill_and_asks_nothing()
    {
        await using var world = await World.CreateAsync();
        world.Levels[ResearchRules.ScienceSkillTypeId] = 4;

        var step = await world.Machine().StepAsync(Pilot, ResearchAgent(), Button(AgentDialogueToken.StartResearch), false, default);

        Assert.Null(step.Prompt);
        Assert.Empty(step.Reply!.Actions);
        Assert.Empty(await world.Projects.ReadAllAsync(Pilot));
    }

    [AgentsPgFact]
    public async Task Cancel_asks_first_and_a_yes_deletes_the_project_and_a_no_keeps_it()
    {
        await using var world = await World.CreateAsync();
        var machine = world.Machine();
        var pick = await machine.StepAsync(Pilot, ResearchAgent(), Button(AgentDialogueToken.StartResearch), false, default);
        await pick.Prompt!.Then(Choice(1));

        var asked = await machine.StepAsync(Pilot, ResearchAgent(), Button(AgentDialogueToken.CancelResearch), false, default);
        Assert.Equal("YesNo", asked.Prompt!.Method);
        Assert.Equal(3, asked.Prompt.Args.Count);
        await asked.Prompt.Then(PyBool.False);
        Assert.NotNull(await world.Projects.ReadAsync(Pilot, Agent));

        var again = await machine.StepAsync(Pilot, ResearchAgent(), Button(AgentDialogueToken.CancelResearch), false, default);
        await again.Prompt!.Then(PyBool.True);
        Assert.Null(await world.Projects.ReadAsync(Pilot, Agent));
    }

    [AgentsPgFact]
    public async Task Datacores_ask_for_the_affordable_quantity_then_debit_the_points_and_grant_the_datacores()
    {
        await using var world = await World.CreateAsync();
        await world.Projects.StartAsync(new ResearchProject(Pilot, Agent, Quantum, Start.AddDays(-10), 100.0, 0), 1);
        world.Now = Start;
        var machine = world.Machine();

        var asked = await machine.StepAsync(Pilot, ResearchAgent(), Button(AgentDialogueToken.BuyDatacores), false, default, Station);
        Assert.Equal("GetQuantity", asked.Prompt!.Method);
        Assert.Equal(10L, ((PyInt)Field(asked, "maxvalue")).Value);
        var bought = Assert.IsType<AgentDialogueStep>(await asked.Prompt.Then(new PyInt(3))).Reply!;

        Assert.Equal((Quantum, 3L, Station), Assert.Single(world.Port.Bought));
        Assert.Equal(new (long, int)[] { (AgentDialogueToken.BuyDatacores, AgentDialogueButton.BuyDatacores), (AgentDialogueToken.CancelResearch, AgentDialogueButton.CancelResearch) },
            bought.Actions.ToArray());
    }

    [AgentsPgFact]
    public async Task Datacores_are_refused_by_name_when_the_points_do_not_cover_one()
    {
        await using var world = await World.CreateAsync();
        await world.Projects.StartAsync(new ResearchProject(Pilot, Agent, Quantum, Start, 1.0, 0), 1);

        var step = await world.Machine().StepAsync(Pilot, ResearchAgent(), Button(AgentDialogueToken.BuyDatacores), false, default, Station);

        Assert.Null(step.Prompt);
        Assert.Empty(world.Port.Bought);
    }
}
