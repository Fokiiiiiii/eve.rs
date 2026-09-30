using Eve.Data;
using Eve.Macho;
using Eve.Marshal;
using Eve.Sde;
using Eve.Services.Slash;
using Npgsql;

namespace Eve.Services.Dogma.Tests;

/// <summary><c>/deathtest</c>: what its arguments mean and what the pilot is told; the hulls are made by space (<see cref="ISpaceHulls"/>).</summary>
public class DeathTestCommandTests
{
    private sealed class Hulls(HullField? answer) : ISpaceHulls
    {
        public List<(int? Type, int Count)> Asked { get; } = [];

        public Task<HullField?> DeathTestAsync(ClientSession session, int? shipTypeId, int count, CancellationToken cancellationToken)
        {
            Asked.Add((shipTypeId, count));
            return Task.FromResult(answer);
        }
    }

    private static ClientSession InSpace()
    {
        var session = new ClientSession(new Sink()) { SessionId = 1, ClientId = 431065451, UserId = 34, NodeId = 65450 };
        session.Set("solarsystemid", new PyInt(30100032));
        return session;
    }

    [Theory]
    [InlineData("", "", null)]
    [InlineData("3", "", 3)]
    [InlineData("0", "", null)]
    [InlineData("Ibis", "Ibis", null)]
    [InlineData("Ibis 4", "Ibis", 4)]
    [InlineData("Ibis 1k", "Ibis", 1000)]
    [InlineData("  Rifter   2 ", "Rifter", 2)]
    [InlineData("Ibis 2.5", "Ibis", 2)]
    [InlineData("Ibis many", "Ibis many", null)]
    public void The_arguments_are_a_count_alone_or_a_name_and_a_trailing_amount(string argument, string name, int? count) =>
        Assert.Equal((name, count), DeathTestCommands.Parse(argument));

    [Fact]
    public void Names_are_compared_as_the_reference_compares_them() =>
        Assert.Equal("caldari frigate 3", ShipTypeNames.Normalize("  Caldari-Frigate_3! "));

    [PgFact]
    public async Task A_ship_is_found_by_name_by_a_part_of_it_or_by_its_type_id_and_what_is_not_one_ship_says_why()
    {
        await using var source = NpgsqlDataSource.Create(PgConnection.ToConnectionString(Rig.Url!));
        var names = new ShipTypeNames(new SdeReader(source));
        Assert.Equal(601, (await names.FindAsync("Ibis")).TypeId);
        Assert.Equal(601, (await names.FindAsync("  ibis ")).TypeId);
        Assert.Equal(601, (await names.FindAsync("601")).TypeId);
        Assert.Equal(596, (await names.FindAsync("impair")).TypeId);
        Assert.Equal("SHIP_NOT_FOUND", (await names.FindAsync("zzzzqq")).Error);
        Assert.Equal("SHIP_NAME_REQUIRED", (await names.FindAsync("  ")).Error);
        var ambiguous = await names.FindAsync("navy issue");
        Assert.Null(ambiguous.TypeId);
        Assert.Equal("AMBIGUOUS_SHIP_NAME", ambiguous.Error);
        Assert.InRange(ambiguous.Suggestions.Count, 2, 5);
        Assert.All(ambiguous.Suggestions, suggestion => Assert.Contains("(", suggestion));
    }

    [Fact]
    public async Task A_pilot_who_is_not_in_space_is_told_so_and_nothing_is_asked_of_space()
    {
        var hulls = new Hulls(null);
        var command = new DeathTestCommands(hulls, new ShipTypeNames(null!));
        var docked = new ClientSession(new Sink()) { SessionId = 2 };
        docked.Set("stationid", new PyInt(60015249));
        Assert.Equal("You must be in space before using /deathtest.", await command.RunAsync(docked, "", CancellationToken.None));
        Assert.Equal("You must be in space before using /deathtest.", await command.RunAsync(new ClientSession(new Sink()) { SessionId = 3 }, "", CancellationToken.None));
        Assert.Empty(hulls.Asked);
    }

    [Fact]
    public async Task What_the_pilot_is_told_is_what_the_reference_says_of_the_field_and_the_pilots_ship_is_the_default_type()
    {
        var hulls = new Hulls(new HullField("Ibis", 1, 20_000, 2.0));
        var command = new DeathTestCommands(hulls, new ShipTypeNames(null!));
        Assert.Equal("Spawned 1 Ibis hulls across 20 km. Detonation in 2.0s sim time.", await command.RunAsync(InSpace(), "1", CancellationToken.None));
        Assert.Equal((null, 1), hulls.Asked.Single());
        await command.RunAsync(InSpace(), "", CancellationToken.None);
        Assert.Equal((null, 6), hulls.Asked[1]);
    }

    [Fact]
    public async Task A_field_that_could_not_be_made_says_so()
    {
        var command = new DeathTestCommands(new Hulls(null), new ShipTypeNames(null!));
        Assert.Equal("Death-test hull spawning failed.", await command.RunAsync(InSpace(), "2", CancellationToken.None));
    }

    [PgFact]
    public async Task A_named_ship_that_is_not_found_or_not_one_is_told_and_a_found_one_is_asked_for_by_type()
    {
        await using var source = NpgsqlDataSource.Create(PgConnection.ToConnectionString(Rig.Url!));
        var hulls = new Hulls(new HullField("Impairor", 3, 20_000, 2.0));
        var command = new DeathTestCommands(hulls, new ShipTypeNames(new SdeReader(source)));
        Assert.StartsWith("Ship type not found: zzzzqq.", await command.RunAsync(InSpace(), "zzzzqq", CancellationToken.None));
        Assert.StartsWith("Ship type is ambiguous: navy issue.", await command.RunAsync(InSpace(), "navy issue", CancellationToken.None));
        Assert.Empty(hulls.Asked);
        Assert.Equal("Spawned 3 Impairor hulls across 20 km. Detonation in 2.0s sim time.", await command.RunAsync(InSpace(), "Impairor 3", CancellationToken.None));
        Assert.Equal((596, 3), hulls.Asked.Single());
    }
}
