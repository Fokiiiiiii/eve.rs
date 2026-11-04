namespace Eve.Dogma.Tests;

public class EffectRegistryTests
{
    private sealed class Handler(string[] names, string label, bool handles = true, bool implemented = true) : IEffectHandler<string, string>
    {
        public IReadOnlyCollection<string> EffectNames => names;

        public bool Handles(EffectContext<string> context) => handles;

        public bool Implemented => implemented;

        public string Build(EffectContext<string> context) => label;
    }

    private static EffectContext<string> Context(string effectName) =>
        new("host", null!, null!, 1, new TypeDef(1, 1, 1, new Dictionary<int, double>(), []), new EffectDef(1, effectName, EffectDef.Target, []), 0, 1);

    [Fact]
    public void A_handler_is_found_by_the_effect_name_without_regard_to_case_or_padding()
    {
        var registry = new EffectRegistry<string, string>().Register(new Handler(["armorRepair"], "repair"));
        Assert.Equal("repair", registry.Find(Context("ARMORREPAIR"))!.Build(Context("armorRepair")));
        Assert.Equal("repair", registry.Find(Context(" armorRepair "))!.Build(Context("armorRepair")));
        Assert.Null(registry.Find(Context("shieldBoosting")));
    }

    [Fact]
    public void Several_handlers_may_share_a_name_and_the_first_that_handles_the_item_serves_it()
    {
        var registry = new EffectRegistry<string, string>()
            .Register(new Handler(["targetAttack"], "turret", handles: false))
            .Register(new Handler(["targetAttack"], "drone"));
        Assert.Equal("drone", registry.Find(Context("targetAttack"))!.Build(Context("targetAttack")));
        var none = new EffectRegistry<string, string>().Register(new Handler(["targetAttack"], "turret", handles: false));
        Assert.Null(none.Find(Context("targetAttack")));
    }

    [Fact]
    public void A_handler_must_name_an_effect()
    {
        Assert.Throws<ArgumentException>(() => new EffectRegistry<string, string>().Register(new Handler([], "nothing")));
    }

    [Fact]
    public void The_effects_of_the_static_data_without_a_handler_are_listed_for_the_categories_asked()
    {
        var statics = new DogmaStatic([], [
            new EffectDef(1, "zeta", EffectDef.Target, []),
            new EffectDef(2, "alpha", EffectDef.Activation, []),
            new EffectDef(3, "armorRepair", EffectDef.Activation, []),
            new EffectDef(4, "passiveThing", EffectDef.Passive, []),
        ], []);
        var registry = new EffectRegistry<string, string>().Register(new Handler(["armorRepair"], "repair"));
        Assert.Equal(["alpha", "zeta"], registry.Uncovered(statics).Select(e => e.Name));
        Assert.Equal(["passiveThing"], registry.Uncovered(statics, EffectDef.Passive).Select(e => e.Name));
    }

    private static DogmaStatic Statics() => new([], [
        new EffectDef(1, "armorRepair", EffectDef.Activation, []),
        new EffectDef(2, "doHacking", EffectDef.Target, []),
        new EffectDef(3, "shieldBoosting", EffectDef.Activation, []),
    ], []);

    [Fact]
    public void A_pending_handler_is_registered_and_refuses_but_does_not_cover_its_effect()
    {
        var pending = new EffectRegistry<string, string>().Register(new Handler(["doHacking"], "refusal", implemented: false));
        Assert.Equal(["doHacking"], pending.Names);
        Assert.Empty(pending.ImplementedNames);
        Assert.NotNull(pending.Find(Context("doHacking")));
        Assert.Equal(["armorRepair", "doHacking", "shieldBoosting"], pending.Uncovered(Statics()).Select(e => e.Name));
    }

    [Fact]
    public void An_implemented_handler_covers_its_effect_alone_and_beside_a_pending_one_in_either_order()
    {
        var implemented = new EffectRegistry<string, string>().Register(new Handler(["doHacking"], "hack"));
        Assert.Equal(["doHacking"], implemented.ImplementedNames);
        Assert.Equal(["armorRepair", "shieldBoosting"], implemented.Uncovered(Statics()).Select(e => e.Name));

        var pendingFirst = new EffectRegistry<string, string>()
            .Register(new Handler(["doHacking"], "refusal", implemented: false))
            .Register(new Handler(["doHacking"], "hack"));
        Assert.Equal(["armorRepair", "shieldBoosting"], pendingFirst.Uncovered(Statics()).Select(e => e.Name));

        var implementedFirst = new EffectRegistry<string, string>()
            .Register(new Handler(["doHacking"], "hack"))
            .Register(new Handler(["doHacking"], "refusal", implemented: false));
        Assert.Equal(["armorRepair", "shieldBoosting"], implementedFirst.Uncovered(Statics()).Select(e => e.Name));
        Assert.Equal(["doHacking"], implementedFirst.ImplementedNames);
        Assert.Equal(["doHacking"], implementedFirst.Names);
    }
}
