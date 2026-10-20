using Eve.Services.Space.Effects;
using Eve.Space;

namespace Eve.Services.Space.Tests;

public class RemoteResistanceTests
{
    private static double NoModuleAttribute(int _) => 0;

    [Fact]
    public void Each_remote_is_weakened_by_the_resistance_attribute_that_names_it()
    {
        Assert.Equal(2115, RemoteResistance.AttributeOf(new SimRemote.Web(-50), NoModuleAttribute));
        Assert.Equal(2115, RemoteResistance.AttributeOf(new SimRemote.Tackle(1, -50), NoModuleAttribute));
        Assert.Equal(2114, RemoteResistance.AttributeOf(new SimRemote.Paint(20), NoModuleAttribute));
        Assert.Equal(2112, RemoteResistance.AttributeOf(new SimRemote.Damp(-30, -30), NoModuleAttribute));
        Assert.Equal(2113, RemoteResistance.AttributeOf(new SimRemote.Disrupt(-20, -20, -20), NoModuleAttribute));
        Assert.Equal(2113, RemoteResistance.AttributeOf(new SimRemote.GuidanceDisrupt(-10, 0, 0, 0), NoModuleAttribute));
        Assert.Equal(2045, RemoteResistance.AttributeOf(new SimRemote.Neutralize(100, 0), NoModuleAttribute));
        Assert.Equal(2045, RemoteResistance.AttributeOf(new SimRemote.Drain(100, 0), NoModuleAttribute));
        // a scramble and a jam keep their own rules
        Assert.Equal(0, RemoteResistance.AttributeOf(new SimRemote.Scramble(1, false), NoModuleAttribute));
        Assert.Equal(0, RemoteResistance.AttributeOf(new SimRemote.Jam(1, 1, 1, 1), NoModuleAttribute));
    }

    [Fact]
    public void A_remote_assistance_names_its_resistance_in_the_module_and_otherwise_is_the_remote_repair_impedance()
    {
        Assert.Equal(2116, RemoteResistance.AttributeOf(new SimRemote.Repair(SimLayer.Armor, 50), NoModuleAttribute));
        Assert.Equal(2116, RemoteResistance.AttributeOf(new SimRemote.Transfer(100), _ => 0.0));
        Assert.Equal(2135, RemoteResistance.AttributeOf(new SimRemote.Repair(SimLayer.Armor, 50), id => id == 2138 ? 2135.0 : 0.0));
    }

    [Fact]
    public void The_multiplier_is_the_attribute_clamped_to_zero_and_one_and_one_when_the_target_does_not_have_it()
    {
        var target = new Dictionary<int, double> { [2115] = 0.4, [2114] = 1.5, [2112] = -0.2 };
        Assert.Equal(0.4, RemoteResistance.MultiplierOf(2115, target));
        Assert.Equal(1, RemoteResistance.MultiplierOf(2114, target));
        Assert.Equal(0, RemoteResistance.MultiplierOf(2112, target));
        Assert.Equal(1, RemoteResistance.MultiplierOf(2113, target));
        Assert.Equal(1, RemoteResistance.MultiplierOf(0, target));
        Assert.Equal(1, RemoteResistance.MultiplierOf(2115, new Dictionary<int, double> { [2115] = double.NaN }));
    }

    [Fact]
    public void A_weakened_remote_keeps_its_shape_and_scales_its_strength_and_a_full_multiplier_changes_nothing()
    {
        var web = new SimRemote.Web(-60);
        Assert.Equal(new SimRemote.Web(-30), RemoteResistance.Weakened(web, 0.5));
        Assert.Same(web, RemoteResistance.Weakened(web, 1));

        var damp = new SimRemote.Damp(-30, -20, 10, 0, 0, 0);
        Assert.Equal(new SimRemote.Damp(-15, -10, 5, 0, 0, 0), RemoteResistance.Weakened(damp, 0.5));

        Assert.Equal(new SimRemote.Repair(SimLayer.Shield, 25), RemoteResistance.Weakened(new SimRemote.Repair(SimLayer.Shield, 50), 0.5));
        Assert.Equal(new SimRemote.Tackle(1, -25), RemoteResistance.Weakened(new SimRemote.Tackle(1, -50), 0.5));

        // a scramble is not weakened by any resistance
        var scramble = new SimRemote.Scramble(1, true);
        Assert.Same(scramble, RemoteResistance.Weakened(scramble, 0));
    }
}
