using Eve.Data;
using Eve.Dogma;
using Eve.Services.Space.Effects;
using Eve.Sde;
using Eve.Space;
using Npgsql;

namespace Eve.Services.Space.Tests;

/// <summary>
/// The warp bubble immunity and the warp scramble status of the modules that set them (the superweapons brief's P2-10, audit D M-3). An interdiction nullifier sets the
/// ship's warp bubble immunity (attribute 1538, a PreAssign of its 3120), and the simulation is told it as <see cref="SimAction.WarpImmunity"/>, so a ship that runs it is
/// not held by a bubble. A warp core stabilizer's ModAdd of -2 on the scramble status (attribute 104) is told as <see cref="SimAction.ScrambleStatus"/>. The field generator
/// carries its bubble (<see cref="SimAction.WarpBubble"/>) and its penalties as the generator's own attributes say (signature +50 % on the Generator I).
/// </summary>
public class InterdictionNullifierTests
{
    private const int InterdictionNullifierI = 58919;
    private const int WarpCoreStabilizerI = 10998;
    private const int WarpDisruptFieldGeneratorI = 28654;

    [PgFact]
    public async Task A_ship_with_warpBubbleImmune_is_not_held_by_a_bubble()
    {
        await using var source = NpgsqlDataSource.Create(PgConnection.ToConnectionString(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        var sde = new SdeReader(source);
        var statics = await DogmaStatic.LoadAsync(sde);
        await statics.EnsureTypesAsync([InterdictionNullifierI]);
        var fixture = ModuleRuleTests.FittedModule(statics, await ModuleRuleTests.ShipTypeIdAsync(sde, statics), InterdictionNullifierI);

        var result = ModulePackages.Of(fixture.Snapshot, statics, fixture.ModuleId, "interdictionNullifierBonus", 0, 1);

        Assert.Null(result.Reason);
        Assert.IsType<SimAction.WarpImmunity>(result.Effect!.Action);
    }

    [PgFact]
    public async Task A_warp_core_stabilizer_takes_two_points_off_the_scramble_status_and_a_field_generator_is_a_bubble_with_its_penalties()
    {
        await using var source = NpgsqlDataSource.Create(PgConnection.ToConnectionString(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        var sde = new SdeReader(source);
        var statics = await DogmaStatic.LoadAsync(sde);
        await statics.EnsureTypesAsync([WarpCoreStabilizerI, WarpDisruptFieldGeneratorI]);
        var shipTypeId = await ModuleRuleTests.ShipTypeIdAsync(sde, statics);

        var stabilizer = ModuleRuleTests.FittedModule(statics, shipTypeId, WarpCoreStabilizerI);
        var stabilized = ModulePackages.Of(stabilizer.Snapshot, statics, stabilizer.ModuleId, "warpCoreStabilizerActive", 0, 1);
        Assert.Null(stabilized.Reason);
        Assert.Equal(new SimAction.ScrambleStatus(-2.0), stabilized.Effect!.Action);

        var generator = ModuleRuleTests.FittedModule(statics, shipTypeId, WarpDisruptFieldGeneratorI);
        var field = ModulePackages.Of(generator.Snapshot, statics, generator.ModuleId, "warpDisruptSphere", 0, 1);
        Assert.Null(field.Reason);
        Assert.Equal(new SimAction.WarpBubble(16_000.0), field.Effect!.Action);
        // the Generator I's own signature bonus is 50 %: the ship's signature with the field is one and a half times what it is without
        Assert.NotNull(field.Effect.On);
        Assert.NotNull(field.Effect.Off);
        Assert.Equal(field.Effect.Off!.SignatureRadius * 1.5, field.Effect.On!.SignatureRadius, 6);
        Assert.Equal(field.Effect.Off.Mass, field.Effect.On.Mass, 6);
        Assert.Equal(field.Effect.Off.MaxVelocity, field.Effect.On.MaxVelocity, 6);
    }
}
