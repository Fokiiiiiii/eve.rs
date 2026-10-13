using Eve.Planets;

namespace Eve.Planets.Tests;

public class SimulationTests
{
    [Fact]
    public void An_ecu_output_without_decay_or_noise_is_the_base_times_the_bar_width()
    {
        // barWidth = 300 s / 900 = 1/3; 1000 * 1/3 = 333
        var output = PlanetRules.EcuOutput(Fixtures.Now + 300 * PlanetRules.Sec, 1000, 300 * PlanetRules.Sec, Fixtures.Now, 0, 0);
        Assert.Equal(333, output);
    }

    [Fact]
    public void An_ecu_output_falls_as_its_program_decays()
    {
        var cycle = 300 * PlanetRules.Sec;
        var early = PlanetRules.EcuOutput(Fixtures.Now + cycle, 1000, cycle, Fixtures.Now, 0.012, 0);
        var late = PlanetRules.EcuOutput(Fixtures.Now + 40 * cycle, 1000, cycle, Fixtures.Now, 0.012, 0);
        Assert.True(early > late, $"early {early} late {late}");
    }

    [Fact]
    public void A_minimal_program_runs_four_quarter_hour_cycles()
    {
        var result = ProgramMath.Create(TestCatalog.PlanetId, TestCatalog.AqueousLiquids, [new Head(0, 0.5, 1.0)], 0.01, 0.5, 9.2,
            new TestCatalog.ConstantField(1.0), []);
        Assert.Equal(4, result.NumCycles);
        Assert.Equal(900 * PlanetRules.Sec, result.CycleTime);
        Assert.Equal(9, result.MaxValue);
    }

    [Fact]
    public void Two_heads_on_one_spot_overlap_and_yield_no_more_than_one_head()
    {
        var one = ProgramMath.Create(TestCatalog.PlanetId, 1, [new Head(0, 0.5, 1.0)], 0.01, 0.5, 9.2, new TestCatalog.ConstantField(1.0), []);
        var same = ProgramMath.Create(TestCatalog.PlanetId, 1, [new Head(0, 0.5, 1.0), new Head(1, 0.5, 1.0)], 0.01, 0.5, 9.2, new TestCatalog.ConstantField(1.0), []);
        var apart = ProgramMath.Create(TestCatalog.PlanetId, 1, [new Head(0, 0.5, 1.0), new Head(1, 0.5, 2.0)], 0.01, 0.5, 9.2, new TestCatalog.ConstantField(1.0), []);
        Assert.Equal(one.MaxValue, same.MaxValue);
        Assert.Equal(18, apart.MaxValue);
    }

    [Theory]
    [InlineData(1.0, 0.25)]
    [InlineData(25.0, 0.5)]
    [InlineData(50.0, 1.0)]
    [InlineData(100.0, 2.0)]
    public void Cycle_hours_double_at_the_client_breakpoints(double length, double hours) =>
        Assert.Equal(hours, PlanetRules.CycleHours(length));

    [Fact]
    public void A_chain_moves_extracted_commodities_into_storage()
    {
        var colony = Fixtures.Chain();
        var pin = colony.Pins[3];
        Assert.Equal(9, pin.QtyPerCycle);
        colony.CreateRoute(100, [3, 2], TestCatalog.AqueousLiquids, 3);
        colony.RunTo(Fixtures.Now + 2 * PlanetRules.Hour);
        Assert.True(colony.Pins[2].Contents.GetValueOrDefault(TestCatalog.AqueousLiquids) > 0);
        Assert.Equal(2 * PlanetRules.Hour + Fixtures.Now, colony.CurrentSimTime);
    }

    [Fact]
    public void A_processor_consumes_its_inputs_and_produces_its_schematic_outputs()
    {
        var colony = Fixtures.Chain();
        colony.Pins[2].Contents[TestCatalog.AqueousLiquids] = 3000;
        colony.CreateRoute(101, [2, 4], TestCatalog.AqueousLiquids, 3000);
        colony.CreateRoute(102, [4, 2], TestCatalog.Water, 20);
        colony.RunTo(Fixtures.Now + 1 * PlanetRules.Hour);
        // the inputs were taken at the first run; the 20 units come out at the end of the first cycle (1800 s), and the second cycle ends after the hour
        Assert.Equal(0, colony.Pins[4].Contents.GetValueOrDefault(TestCatalog.AqueousLiquids));
        Assert.Equal(20, colony.Pins[2].Contents.GetValueOrDefault(TestCatalog.Water));
        Assert.Equal(0, colony.Pins[2].Contents.GetValueOrDefault(TestCatalog.AqueousLiquids));
    }
}
