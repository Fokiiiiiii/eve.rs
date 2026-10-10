using Eve.Ships;
using Eve.Space;

namespace Eve.Services.Space.Tests;

/// <summary>
/// A cynosural field the simulation lights is kept as a record (ADR 0012): the sweep takes it out of space when its time is up, and a restart puts it back for
/// what is left of its life.
/// </summary>
public class CynoFieldRecordHostTests
{
    private const int System = 30100032;
    private const long Field = 4_250_000_000_000_003;
    private const long Pilot = 1_790_646_377_000_015;

    private static SimDeployed Lit(double lifetimeMs) => new(
        System, Field, "Cynosural Field", 21_094, 2_020, [311_989_835_983.1308, -13_069_154_020.916_222, 156_646_308_895.761_87], 250, Pilot, 98_000_001, 0,
        9_988_400_001_943, lifetimeMs);

    private static async Task WaitForAsync(Func<Task<bool>> condition)
    {
        for (var i = 0; i < 200; i++)
        {
            if (await condition())
            {
                return;
            }
            await Task.Delay(25);
        }
        Assert.Fail("the record was not kept in time");
    }

    [PgFact]
    public async Task A_lit_field_is_kept_and_the_sweep_takes_it_out_of_space_when_its_time_is_up()
    {
        await using var rig = await Rig.CreateAsync();
        rig.Sim.Say(Lit(600_000));
        await WaitForAsync(async () => (await rig.Deployed.InSystemAsync(System)).Count == 1);

        var kept = (await rig.Deployed.InSystemAsync(System)).Single();
        Assert.Equal((Field, "cyno_field", "Cynosural Field", 21_094, 2_020), (kept.DeployableId, kept.Kind, kept.Name, kept.TypeId, kept.GroupId));
        Assert.Equal(Rig.ClockMs + 600_000, kept.ExpiresAtMs);
        Assert.Equal(new[] { 311_989_835_983.1308, -13_069_154_020.916_222, 156_646_308_895.761_87 }, kept.Position);

        // not yet due: the sweep leaves it in space
        await rig.Containers.SweepAsync();
        Assert.DoesNotContain(rig.Sim.Heard, message => message is SimRemoveDeployed);

        // due: taken out of the simulation, and forgotten
        rig.Now += 600_001;
        await rig.Containers.SweepAsync();
        Assert.Contains(rig.Sim.Heard, message => message is SimRemoveDeployed { DeployableId: Field });
        Assert.Empty(await rig.Deployed.InSystemAsync(System));
    }

    [PgFact]
    public async Task A_field_on_record_is_put_back_after_a_restart_for_what_is_left_of_its_life_and_an_expired_one_is_not()
    {
        await using var rig = await Rig.CreateAsync();
        var record = new DeployedRecord(Field, "cyno_field", System, "Cynosural Field", 21_094, 2_020, [1, 2, 3], 250, Pilot, 98_000_001, 0, 9_988_400_001_943, Rig.ClockMs + 120_000);
        await rig.Deployed.SaveAsync(record);
        await rig.Deployed.SaveAsync(record with { DeployableId = Field + 1, ExpiresAtMs = Rig.ClockMs - 1 });

        rig.Now += 30_000;
        await rig.Containers.RestoreDeployedAsync(System);

        var restored = rig.Sim.Heard.OfType<SimDeployed>().ToList();
        var placed = Assert.Single(restored);
        Assert.Equal((Field, 90_000.0), (placed.DeployableId, placed.LifetimeMs));
        Assert.Equal(new[] { 1.0, 2.0, 3.0 }, placed.Position);
    }
}
