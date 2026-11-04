using Eve.Marshal;
using Eve.Services.Slash;

namespace Eve.Services.Dogma.Tests;

/// <summary>
/// The faction warfare commands (<c>/fw</c>) without a database: a fake host records the changes, and the usage checks, the corporation and the system the caller is in,
/// and the words of the answers are the commands' own.
/// </summary>
public sealed class FwCommandTests
{
    private const long Corporation = 98000001;
    private const long Amarr = 500003;

    private sealed class FakeFw : IFwControl
    {
        public GmFwAnswer Answer { get; set; } = new(null, "Amarr Empire");

        public List<string> Calls { get; } = [];

        public Task<GmFwAnswer> EnlistAsync(long corporationId, string factionToken, CancellationToken cancellationToken)
        {
            Calls.Add($"enlist:{corporationId}:{factionToken}");
            return Task.FromResult(Answer);
        }

        public Task<GmFwAnswer> LeaveAsync(long corporationId, CancellationToken cancellationToken)
        {
            Calls.Add($"leave:{corporationId}");
            return Task.FromResult(Answer);
        }

        public Task<GmFwAnswer> AddVictoryPointsAsync(long solarSystemId, long points, CancellationToken cancellationToken)
        {
            Calls.Add($"vp:{solarSystemId}:{points}");
            return Task.FromResult(Answer);
        }
    }

    /// <summary>A caller in a corporation, in a system.</summary>
    private static ClientSession InCorporation(long system = 30000142)
    {
        var session = Rig.Session(1001);
        session.Set("corpid", new PyLong(Corporation));
        session.Set("solarsystemid2", new PyInt(system));
        return session;
    }

    private static Task<string> RunAsync(FakeFw fake, string argument, ClientSession? session = null) =>
        new FwCommands(fake).RunAsync(session ?? InCorporation(), argument, default);

    [Theory]
    [InlineData("")]
    [InlineData("join amarr")]
    [InlineData("enlist")]
    [InlineData("leave now")]
    [InlineData("vp")]
    [InlineData("vp 0")]
    [InlineData("vp -4")]
    [InlineData("vp many")]
    public async Task Malformed_arguments_give_the_usage_and_change_nothing(string argument)
    {
        var fake = new FakeFw();

        Assert.Equal(FwCommands.Usage, await RunAsync(fake, argument));
        Assert.Empty(fake.Calls);
    }

    [Fact]
    public async Task A_character_with_no_corporation_cannot_enlist_or_leave()
    {
        var fake = new FakeFw();
        var session = Rig.Session(1001);

        Assert.Equal("Character has no corporation to enlist.", await RunAsync(fake, "enlist Amarr Empire", session));
        Assert.Equal("Character has no corporation to leave a faction.", await RunAsync(fake, "leave", session));
        Assert.Empty(fake.Calls);
    }

    [Fact]
    public async Task Enlist_takes_the_faction_name_with_its_spaces_and_says_it_is_done()
    {
        var fake = new FakeFw();

        Assert.Equal($"Corporation {Corporation} is in Amarr Empire now.", await RunAsync(fake, "enlist Amarr Empire"));
        Assert.Equal([$"enlist:{Corporation}:Amarr Empire"], fake.Calls);
    }

    [Fact]
    public async Task Enlist_gives_the_refusal_of_the_host_as_it_is()
    {
        var fake = new FakeFw { Answer = new GmFwAnswer("GuristasPirates is not an occupier faction; a corporation enlists in an occupier militia.") };

        Assert.Equal(fake.Answer.Refusal, await RunAsync(fake, $"enlist {Amarr}"));
    }

    [Fact]
    public async Task Leave_takes_the_corporation_out_and_gives_the_refusal_as_it_is()
    {
        var fake = new FakeFw();
        Assert.Equal($"Corporation {Corporation} left its faction now.", await RunAsync(fake, "leave"));

        fake.Answer = new GmFwAnswer($"Corporation {Corporation} is in no faction to leave.");
        Assert.Equal(fake.Answer.Refusal, await RunAsync(fake, "leave"));
        Assert.Equal([$"leave:{Corporation}", $"leave:{Corporation}"], fake.Calls);
    }

    [Fact]
    public async Task Vp_gives_the_points_to_the_system_the_caller_is_in_and_names_the_total()
    {
        var fake = new FakeFw { Answer = new GmFwAnswer(null, Points: 40) };

        Assert.Equal("Added 25 victory points to system 30000142; its attacker points are 40 now.", await RunAsync(fake, "vp 25"));
        Assert.Equal(["vp:30000142:25"], fake.Calls);
    }

    [Fact]
    public async Task Vp_needs_a_solar_system_and_gives_the_refusal_of_an_unoccupied_one()
    {
        var fake = new FakeFw();
        var nowhere = Rig.Session(1001);
        nowhere.Set("corpid", new PyLong(Corporation));
        Assert.Equal("Character is in no solar system.", await RunAsync(fake, "vp 25", nowhere));

        fake.Answer = new GmFwAnswer("System 30000142 is not occupied, so it takes no victory points.");
        Assert.Equal(fake.Answer.Refusal, await RunAsync(fake, "vp 25"));
    }
}
