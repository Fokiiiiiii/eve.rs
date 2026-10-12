using System.Text.Json.Nodes;
using Eve.Agents;
using Eve.Data;
using Eve.Marshal;
using Eve.Services.Core;
using Npgsql;

namespace Eve.Services.Core.Tests;

/// <summary>The locator dialogue (buttons 15 to 17): the name, the offer with its fee, accept and reject, over the real locate store.</summary>
public class AgentLocatorDialogueTests
{
    private const long Pilot = 140000021;
    private const long Target = 140000099;
    private const long Agent = 3009400;
    private const long HomeSystem = 30000001;
    private const long SameConstellationSystem = 30000142;
    private const long OtherRegionSystem = 30002000;
    private static readonly DateTimeOffset Start = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);

    private sealed class FakeLocator(NpgsqlDataSource source, LocateStore locates, Func<DateTimeOffset> now) : IAgentLocatorPort
    {
        public LocateStore Locates { get; } = locates;

        public Dictionary<string, long> Names { get; } = new() { ["Ada Pilot"] = Target, ["Far Pilot"] = Target + 1 };

        public Dictionary<long, LocateTarget> Online { get; } = new()
        {
            [Target] = new LocateTarget(SameConstellationSystem, 0),
            [Target + 1] = new LocateTarget(OtherRegionSystem, 0),
        };

        public Dictionary<long, LocatePlace> Places { get; } = new()
        {
            [HomeSystem] = new LocatePlace(20000001, 10000001),
            [SameConstellationSystem] = new LocatePlace(20000001, 10000001),
            [OtherRegionSystem] = new LocatePlace(20000005, 10000002),
        };

        public bool CanPay { get; set; } = true;

        public List<(long Fee, JsonObject Result)> Delivered { get; } = [];

        public ValueTask<long?> FindCharacterAsync(string name, CancellationToken cancellationToken) =>
            ValueTask.FromResult(Names.TryGetValue(name, out var id) ? id : (long?)null);

        public ValueTask<LocateTarget?> TargetAsync(long characterId, CancellationToken cancellationToken) =>
            ValueTask.FromResult(Online.TryGetValue(characterId, out var location) ? location : (LocateTarget?)null);

        public ValueTask<LocatePlace?> PlaceAsync(long solarSystemId, CancellationToken cancellationToken) =>
            ValueTask.FromResult(Places.TryGetValue(solarSystemId, out var place) ? place : (LocatePlace?)null);

        // the same transaction shape as the host: the fee is charged and the located time is recorded together
        public async ValueTask<bool> DeliverAsync(long characterId, long agentId, long fee, JsonObject result, CancellationToken cancellationToken)
        {
            if (!CanPay) return false;
            await using var connection = await source.OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await Locates.MarkLocatedAsync(connection, transaction, characterId, agentId, now(), cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            Delivered.Add((fee, result));
            return true;
        }
    }

    private sealed class World : IAsyncDisposable
    {
        private readonly string _schema = "loc_" + Guid.NewGuid().ToString("N")[..12];
        private readonly NpgsqlDataSource _source;

        public DateTimeOffset Now { get; set; } = Start;
        public AgentMissionStore Missions { get; }
        public ResearchProjectStore Projects { get; }
        public LocateStore Locates { get; }
        public FakeLocator Port { get; }

        private World(NpgsqlDataSource source)
        {
            _source = source;
            Missions = new AgentMissionStore(source, _schema);
            Projects = new ResearchProjectStore(source, _schema);
            Locates = new LocateStore(source, _schema);
            Port = new FakeLocator(source, Locates, () => Now);
        }

        public static async Task<World> CreateAsync()
        {
            var source = NpgsqlDataSource.Create(PgConnection.ForTest(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
            var world = new World(source);
            await AgentMissionStore.MigrateAsync(source, world._schema);
            await ResearchProjectStore.MigrateAsync(source, world._schema);
            return world;
        }

        public AgentDialogueMachine Machine() => new(new AgentDialogueSources(
            Missions,
            (_, _, _, _, _) => ValueTask.FromResult(new AgentStandings(0, 10, 10)),
            (_, _, _) => ValueTask.FromResult(5),
            _ => 500_003,
            (_, _, _) => ValueTask.FromResult<AgentMissionContent?>(null),
            () => Now,
            null,
            null,
            Port));

        public async ValueTask DisposeAsync()
        {
            await using (var drop = _source.CreateCommand($"DROP SCHEMA IF EXISTS {_schema} CASCADE"))
                await drop.ExecuteNonQueryAsync();
            await _source.DisposeAsync();
        }
    }

    private static AgentMissionAgentData LocatorAgent(long level) => new(
        AgentId: Agent, AgentTypeId: 2, DivisionId: 18, Level: level, StationId: 60000001,
        CorporationId: 1000001, IsLocatorAgent: true, IsInSpace: false, CareerId: null, ImportantMission: false, MissionKind: "locator",
        MissionTypeLabel: "locator", SpecialityId: null, StationTypeId: null, BloodlineId: null, SolarSystemId: HomeSystem,
        DungeonId: null, SpawnPointId: null, TypeId: null);

    private static PyValue Button(long id) => new PyInt(id);

    private static string Says(AgentDialogueReply reply) => ((PyStr)((PyTuple)reply.AgentSays).Items[0]).Value;

    [AgentsPgFact]
    public async Task Locate_asks_for_the_name_offers_the_fee_and_accept_sends_the_result_and_starts_the_cooldown()
    {
        await using var world = await World.CreateAsync();
        var machine = world.Machine();

        var asked = await machine.StepAsync(Pilot, LocatorAgent(1), Button(AgentDialogueButton.LocateCharacter), false, default, 60000001);
        Assert.Equal("RemoteNamePopup", asked.Prompt!.Method);
        var offer = Assert.IsType<AgentDialogueStep>(await asked.Prompt.Then(new PyStr("Ada Pilot"))).Reply!;
        Assert.Contains("5,000 ISK", Says(offer));
        Assert.Equal(new (long, int)[] { (AgentDialogueButton.LocateAccept, AgentDialogueButton.LocateAccept), (AgentDialogueButton.LocateReject, AgentDialogueButton.LocateReject) },
            offer.Actions.ToArray());

        await machine.StepAsync(Pilot, LocatorAgent(1), Button(AgentDialogueButton.LocateAccept), false, default, 60000001);

        var (fee, result) = Assert.Single(world.Port.Delivered);
        Assert.Equal(5_000L, fee);
        Assert.Equal(Target, result["characterID"]!.GetValue<long>());
        Assert.Equal(SameConstellationSystem, ((JsonObject)result["targetLocation"]!)["5"]!.GetValue<long>());
        Assert.Equal(HomeSystem, ((JsonObject)result["agentLocation"]!)["5"]!.GetValue<long>());
        var record = await world.Locates.ReadAsync(Pilot, Agent);
        Assert.Null(record!.TargetCharacterId);
        Assert.Equal(world.Now, record.LocatedAt);

        var during = await machine.StepAsync(Pilot, LocatorAgent(1), Button(AgentDialogueButton.LocateCharacter), false, default, 60000001);
        Assert.Null(during.Prompt);
        Assert.Empty(during.Reply!.Actions);
    }

    [AgentsPgFact]
    public async Task Reject_clears_the_open_request_and_offers_the_locate_again()
    {
        await using var world = await World.CreateAsync();
        var machine = world.Machine();
        var asked = await machine.StepAsync(Pilot, LocatorAgent(1), Button(AgentDialogueButton.LocateCharacter), false, default, 60000001);
        await asked.Prompt!.Then(new PyStr("Ada Pilot"));

        var rejected = await machine.StepAsync(Pilot, LocatorAgent(1), Button(AgentDialogueButton.LocateReject), false, default, 60000001);

        Assert.Null((await world.Locates.ReadAsync(Pilot, Agent))!.TargetCharacterId);
        Assert.Empty(world.Port.Delivered);
        Assert.Contains((AgentDialogueButton.LocateCharacter, AgentDialogueButton.LocateCharacter), rejected.Reply!.Actions);
    }

    [AgentsPgFact]
    public async Task Unknown_names_and_pilots_out_of_reach_offer_no_fee()
    {
        await using var world = await World.CreateAsync();
        var machine = world.Machine();

        var unknown = await machine.StepAsync(Pilot, LocatorAgent(1), Button(AgentDialogueButton.LocateCharacter), false, default, 60000001);
        var notFound = Assert.IsType<AgentDialogueStep>(await unknown.Prompt!.Then(new PyStr("Nobody"))).Reply!;
        Assert.Contains("could be found", Says(notFound));
        Assert.Null(await world.Locates.ReadAsync(Pilot, Agent));

        // a level one locator cannot reach another region (the table's "cannot")
        var far = await machine.StepAsync(Pilot, LocatorAgent(1), Button(AgentDialogueButton.LocateCharacter), false, default, 60000001);
        var unavailable = Assert.IsType<AgentDialogueStep>(await far.Prompt!.Then(new PyStr("Far Pilot"))).Reply!;
        Assert.Contains("cannot find", Says(unavailable));
        Assert.Null(await world.Locates.ReadAsync(Pilot, Agent));

        // a level three locator can: the other region is 100,000 ISK
        var reachable = await machine.StepAsync(Pilot, LocatorAgent(3), Button(AgentDialogueButton.LocateCharacter), false, default, 60000001);
        var offer = Assert.IsType<AgentDialogueStep>(await reachable.Prompt!.Then(new PyStr("Far Pilot"))).Reply!;
        Assert.Contains("100,000 ISK", Says(offer));
    }

    [AgentsPgFact]
    public async Task A_pilot_who_cannot_pay_is_charged_nothing_and_the_request_stays_open_without_a_cooldown()
    {
        await using var world = await World.CreateAsync();
        world.Port.CanPay = false;
        var machine = world.Machine();
        var asked = await machine.StepAsync(Pilot, LocatorAgent(1), Button(AgentDialogueButton.LocateCharacter), false, default, 60000001);
        await asked.Prompt!.Then(new PyStr("Ada Pilot"));

        var refused = await machine.StepAsync(Pilot, LocatorAgent(1), Button(AgentDialogueButton.LocateAccept), false, default, 60000001);

        Assert.Contains("cannot pay", Says(refused.Reply!));
        Assert.Empty(world.Port.Delivered);
        var record = await world.Locates.ReadAsync(Pilot, Agent);
        Assert.Equal(Target, record!.TargetCharacterId);
        Assert.Null(record.LocatedAt);
    }
}
