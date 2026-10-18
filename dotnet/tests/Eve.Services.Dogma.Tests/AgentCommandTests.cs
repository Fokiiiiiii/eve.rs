using Eve.Services.Slash;

namespace Eve.Services.Dogma.Tests;

/// <summary>
/// The agent commands (<c>/agent</c>) without a database: a fake host answers each change, and the parsing, the usage checks and the words of the answers are the
/// commands' own.
/// </summary>
public sealed class AgentCommandTests
{
    private const long Agent = 3008416;
    private static readonly GmAgentMission Courier = new(Agent, "Offered", 5001, "Courier to Jita");

    private sealed class FakeAgentControl : IAgentControl
    {
        public List<GmAgentMission> Held { get; } = [];

        /// <summary>What a change is answered with; the default is a change of the courier mission.</summary>
        public GmAgentAnswer Answer { get; set; } = new(Courier, null);

        public int ResetAllCount { get; set; }

        public List<string> Calls { get; } = [];

        public Task<IReadOnlyList<GmAgentMission>> HeldAsync(long characterId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<GmAgentMission>>(Held);

        public Task<GmAgentAnswer> OfferAsync(long characterId, long agentId, CancellationToken cancellationToken) => Record("offer", agentId);

        public Task<GmAgentAnswer> AcceptAsync(long characterId, long agentId, CancellationToken cancellationToken) => Record("accept", agentId);

        public Task<GmAgentAnswer> QuitAsync(long characterId, long agentId, CancellationToken cancellationToken) => Record("quit", agentId);

        public Task<GmAgentAnswer> CompleteAsync(long characterId, long agentId, CancellationToken cancellationToken) => Record("complete", agentId);

        public Task<GmAgentAnswer> ResetAsync(long characterId, long agentId, CancellationToken cancellationToken) => Record("reset", agentId);

        public Task<int> ResetAllAsync(long characterId, CancellationToken cancellationToken)
        {
            Calls.Add("reset-all");
            return Task.FromResult(ResetAllCount);
        }

        private Task<GmAgentAnswer> Record(string action, long agentId)
        {
            Calls.Add($"{action}:{agentId}");
            return Task.FromResult(Answer);
        }
    }

    private static Task<string> RunAsync(FakeAgentControl fake, string argument, ClientSession? session = null) =>
        new AgentCommands(fake).RunAsync(session ?? Rig.Session(1001), argument, default);

    [Fact]
    public async Task Without_a_verb_or_with_an_unknown_one_the_usage_is_given_and_nothing_is_changed()
    {
        var fake = new FakeAgentControl();

        Assert.Equal(AgentCommands.Usage, await RunAsync(fake, "   "));
        Assert.Equal(AgentCommands.Usage, await RunAsync(fake, "fly 3008416"));
        Assert.Equal(AgentCommands.Usage, await RunAsync(fake, "offers 3008416"));
        Assert.Empty(fake.Calls);
    }

    [Theory]
    [InlineData("accept")]
    [InlineData("offer")]
    [InlineData("quit")]
    [InlineData("complete")]
    [InlineData("reset")]
    public async Task An_agent_must_be_named_by_a_positive_number(string verb)
    {
        var fake = new FakeAgentControl();

        Assert.Equal(AgentCommands.Usage, await RunAsync(fake, $"{verb} abc"));
        Assert.Equal(AgentCommands.Usage, await RunAsync(fake, $"{verb} 0"));
        Assert.Equal(AgentCommands.Usage, await RunAsync(fake, $"{verb} -3"));
        Assert.Equal(AgentCommands.Usage, await RunAsync(fake, verb));
        Assert.Empty(fake.Calls);
    }

    [Fact]
    public async Task A_character_is_needed_for_every_agent_command()
    {
        var fake = new FakeAgentControl();

        Assert.Equal("Select a character before using /agent.", await RunAsync(fake, "offers", Rig.Session(null)));
        Assert.Empty(fake.Calls);
    }

    [Fact]
    public async Task Offers_lists_the_held_missions_and_says_when_there_are_none()
    {
        var fake = new FakeAgentControl();
        Assert.Equal("No offered or accepted agent mission for character 1001.", await RunAsync(fake, "offers"));

        fake.Held.Add(Courier);
        fake.Held.Add(new GmAgentMission(3008417, "Accepted", 5002, "Kill the pirates"));
        Assert.Equal(
            "Agent 3008416: Offered mission 5001 (Courier to Jita).\nAgent 3008417: Accepted mission 5002 (Kill the pirates).",
            await RunAsync(fake, "offers"));
    }

    [Fact]
    public async Task Each_change_is_sent_to_the_host_with_its_agent_and_says_what_it_did()
    {
        var fake = new FakeAgentControl();

        Assert.Equal("Offered mission 5001 (Courier to Jita) from agent 3008416 to character 1001.", await RunAsync(fake, "offer 3008416"));
        Assert.Equal("Accepted mission 5001 from agent 3008416 for character 1001.", await RunAsync(fake, "accept 3008416"));
        Assert.Equal("Quit mission 5001 with agent 3008416 for character 1001.", await RunAsync(fake, "quit 3008416"));
        Assert.Equal("Completed mission 5001 with agent 3008416 for character 1001.", await RunAsync(fake, "complete 3008416"));
        Assert.Equal("Reset mission 5001 with agent 3008416 for character 1001.", await RunAsync(fake, "reset 3008416"));
        Assert.Equal(["offer:3008416", "accept:3008416", "quit:3008416", "complete:3008416", "reset:3008416"], fake.Calls);
    }

    [Fact]
    public async Task A_refusal_of_the_host_is_given_as_it_is_and_nothing_else_is_said()
    {
        var fake = new FakeAgentControl { Answer = new GmAgentAnswer(null, "there is no accepted mission to quit") };

        Assert.Equal("there is no accepted mission to quit", await RunAsync(fake, "quit 3008416"));
    }

    [Fact]
    public async Task A_change_that_changed_nothing_says_so()
    {
        var fake = new FakeAgentControl { Answer = new GmAgentAnswer(null, null) };

        Assert.Equal("Nothing was changed.", await RunAsync(fake, "accept 3008416"));
    }

    [Theory]
    [InlineData(0, "No agent mission to reset for character 1001.")]
    [InlineData(1, "Reset 1 agent mission for character 1001.")]
    [InlineData(2, "Reset 2 agent missions for character 1001.")]
    public async Task Reset_all_resets_every_mission_the_character_holds(int reset, string expected)
    {
        var fake = new FakeAgentControl { ResetAllCount = reset };

        Assert.Equal(expected, await RunAsync(fake, "reset all"));
        Assert.Equal(["reset-all"], fake.Calls);
    }
}
