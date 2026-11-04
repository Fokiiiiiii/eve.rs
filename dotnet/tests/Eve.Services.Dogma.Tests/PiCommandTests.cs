using Eve.Marshal;
using Eve.Services.Slash;

namespace Eve.Services.Dogma.Tests;

/// <summary>
/// The planet helpers (<c>/pi</c>) without a database: a fake host lists the caller's planets and records the two changes, and the usage checks and the words of the
/// answers are the commands' own.
/// </summary>
public sealed class PiCommandTests
{
    private const long Owner = 1001;

    private sealed class FakePlanets : IPiControl
    {
        public List<GmPlanet> Planets { get; } = [];

        public GmPiAnswer Answer { get; set; } = new(null, 40001);

        public List<string> Calls { get; } = [];

        public Task<IReadOnlyList<GmPlanet>> PlanetsAsync(long characterId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<GmPlanet>>(Planets);

        public Task<GmPiAnswer> CommodityAsync(long characterId, long pinId, int typeId, long quantity, CancellationToken cancellationToken)
        {
            Calls.Add($"commodity:{pinId}:{typeId}:{quantity}");
            return Task.FromResult(Answer);
        }

        public Task<GmPiAnswer> CommandCenterAsync(long characterId, long pinId, CancellationToken cancellationToken)
        {
            Calls.Add($"command:{pinId}");
            return Task.FromResult(Answer);
        }
    }

    private static Task<string> RunAsync(FakePlanets fake, string argument, ClientSession? session = null) =>
        new PiCommands(fake).RunAsync(session ?? Rig.Session(Owner), argument, default);

    [Theory]
    [InlineData("")]
    [InlineData("launch")]
    [InlineData("list all")]
    [InlineData("commodity 7 2393")]
    [InlineData("commodity 7 2393 many")]
    [InlineData("commodity 0 2393 5")]
    [InlineData("commodity 7 0 5")]
    [InlineData("commodity 7 2393 -5")]
    [InlineData("command")]
    [InlineData("command 0")]
    public async Task Malformed_arguments_give_the_usage_and_change_nothing(string argument)
    {
        var fake = new FakePlanets();

        Assert.Equal(PiCommands.Usage, await RunAsync(fake, argument));
        Assert.Empty(fake.Calls);
    }

    [Fact]
    public async Task A_character_is_needed_for_the_planet_helpers()
    {
        var fake = new FakePlanets();

        Assert.Equal("Select a character before using /pi.", await RunAsync(fake, "list", Rig.Session(null)));
        Assert.Empty(fake.Calls);
    }

    [Fact]
    public async Task List_names_each_planet_with_its_pins_and_says_when_there_is_none()
    {
        var fake = new FakePlanets();
        Assert.Equal($"No planet for character {Owner}.", await RunAsync(fake, "list"));

        fake.Planets.Add(new GmPlanet(40001, 2016, [new GmPlanetPin(900001, 2254), new GmPlanetPin(900002, 2344)]));
        fake.Planets.Add(new GmPlanet(40002, 2014, []));
        Assert.Equal(
            "Planet 40001 (type 2016): pins 900001:2254, 900002:2344.\nPlanet 40002 (type 2014): pins none.",
            await RunAsync(fake, "list"));
        Assert.Empty(fake.Calls);
    }

    [Fact]
    public async Task Commodity_adds_to_the_named_pin_and_names_the_planet()
    {
        var fake = new FakePlanets();

        Assert.Equal("Added 5 of type 2393 to pin 900001 on planet 40001.", await RunAsync(fake, "commodity 900001 2393 5"));
        Assert.Equal(["commodity:900001:2393:5"], fake.Calls);
    }

    [Fact]
    public async Task Command_converts_the_named_command_center_and_names_the_planet()
    {
        var fake = new FakePlanets();

        Assert.Equal("Converted the command center pin 900000 on planet 40001.", await RunAsync(fake, "command 900000"));
        Assert.Equal(["command:900000"], fake.Calls);
    }

    [Fact]
    public async Task A_refusal_of_the_host_is_given_as_it_is()
    {
        var fake = new FakePlanets { Answer = new GmPiAnswer("No pin 900009 is on a planet of character 1001.") };

        Assert.Equal(fake.Answer.Refusal, await RunAsync(fake, "commodity 900009 2393 5"));
        Assert.Equal(fake.Answer.Refusal, await RunAsync(fake, "command 900009"));
    }
}
