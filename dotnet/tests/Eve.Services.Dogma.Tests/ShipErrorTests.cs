using Eve.Dogma;
using Eve.Fitting;
using Eve.Marshal;
using Eve.Ships;
using Eve.Services.Ships;

namespace Eve.Services.Dogma.Tests;

/// <summary>What the client shows when a ship refuses: the error names and the words are the reference's.</summary>
public class ShipErrorTests
{
    private const int Skill = 3300, Module = 5001, Ship = 11019;

    private static readonly DogmaStatic Statics = new(
        [], [],
        [
            new TypeDef(Skill, 255, 16, new Dictionary<int, double>(), [], "Gunnery"),
            new TypeDef(Module, 53, 7, new Dictionary<int, double>(), [], "Widget"),
        ],
        groupNames: new Dictionary<int, string> { [53] = "Energy Weapon" });

    private static (string Name, Dictionary<string, PyValue> Values) Say(ShipRefused refused)
    {
        var error = ShipErrors.Of(refused, Statics).Error;
        var arguments = ((PyTuple)((PyTuple)((PyObjectEx)error.Payload).Header).Items[1]).Items;
        var values = ((PyDict)arguments[1]).Entries.ToDictionary(e => ((PyStr)e.Key).Value, e => e.Value);
        return (((PyStr)arguments[0]).Value, values);
    }

    private static string Notified(ShipRefused refused)
    {
        var (name, values) = Say(refused);
        Assert.Equal("CustomNotify", name);
        return ((PyStr)values["notify"]).Value;
    }

    private static ShipRefused Cannot(FitError error, params (string Key, double Value)[] data) =>
        new(ShipRefusal.CannotFit, FitFailure.Of(error, data), Module, Ship);

    [Fact]
    public void A_module_that_is_not_the_ships_is_a_denied_change_and_a_missing_slot_a_failed_fit()
    {
        Assert.Equal("DeniedShipChanged", Say(new ShipRefused(ShipRefusal.NotFitted)).Name);
        var (name, values) = Say(new ShipRefused(ShipRefusal.NoSuitableSlot, null, Module, Ship));
        Assert.Equal("ModuleFitFailed", name);
        Assert.Equal((new PyInt(Module), new PyStr("No suitable slot available")), (values["moduleName"], values["reason"]));
    }

    [Fact]
    public void Onlining_what_the_ship_cannot_carry_returns_the_client_user_error_and_all_capacity_values()
    {
        foreach (var (reason, fitError, name, require, remaining, total) in new[]
        {
            (ShipRefusal.NotEnoughCpu, FitError.NotEnoughCpu, "NotEnoughCpu", 12d, 8d, 10d),
            (ShipRefusal.NotEnoughPower, FitError.NotEnoughPower, "NotEnoughPower", 20d, 4d, 15d),
        })
        {
            var refusal = new ShipRefused(reason, FitFailure.Of(fitError,
                ("require", require), ("remaining", remaining), ("total", total)), Module, Ship);
            var (actual, values) = Say(refusal);
            Assert.Equal(name, actual);
            Assert.Equal(Py.Tuple(new PyInt(4), new PyInt(Module)), values["moduleName"]);
            Assert.Equal(new PyInt(Module), values["moduleType"]);
            Assert.Equal(Py.Number(require), values["require"]);
            Assert.Equal(Py.Number(remaining), values["remaining"]);
            Assert.Equal(Py.Number(total), values["total"]);
        }
    }

    [Fact]
    public void A_missing_skill_uses_the_clients_prerequisite_error_and_all_required_arguments()
    {
        var failure = FitFailure.Of(FitError.SkillRequired, ("skillTypeID", Skill), ("requiredLevel", 3), ("currentLevel", 1));
        var (name, values) = Say(new ShipRefused(ShipRefusal.CannotFit, failure, Module, Ship));
        Assert.Equal("FittingHasSkillPrerequisites", name);
        Assert.Equal(new PyStr("Gunnery : Level 3"), values["requiredSkills"]);
        Assert.Equal(new PyInt(Module), values["item"]);
        Assert.Equal(new PyInt(1), values["skillCount"]);
    }

    [Fact]
    public void Hard_points_and_slots_have_their_own_words()
    {
        Assert.Equal("This ship has no turret hardpoints.", Notified(Cannot(FitError.NoTurretHardpoints, ("turretHardpoints", 0))));
        Assert.Equal("All turret hardpoints on this ship are already in use.", Notified(Cannot(FitError.NoTurretHardpoints, ("turretHardpoints", 2), ("usedTurrets", 2))));
        Assert.Equal("This ship has no launcher hardpoints.", Notified(Cannot(FitError.NoLauncherHardpoints, ("launcherHardpoints", 0))));
        Assert.Equal("All launcher hardpoints on this ship are already in use.", Notified(Cannot(FitError.NoLauncherHardpoints, ("launcherHardpoints", 1), ("usedLaunchers", 1))));
        Assert.Equal("That fitting slot is already occupied.", Notified(Cannot(FitError.SlotOccupied)));
    }

    [Fact]
    public void Too_many_of_a_group_or_a_type_say_how_many_are_allowed_and_fitted()
    {
        var (group, groupValues) = Say(Cannot(FitError.MaxGroupFitted, ("maxGroupFitted", 1), ("existingGroupCount", 1)));
        Assert.Equal("CantFitTooManyByGroup", group);
        Assert.Equal(
            (new PyInt(Ship), new PyInt(Module), new PyStr("Energy Weapon"), new PyInt(1), new PyInt(1)),
            (groupValues["ship"], groupValues["module"], groupValues["groupName"], groupValues["noOfModules"], groupValues["noOfModulesFitted"]));
        var (type, typeValues) = Say(Cannot(FitError.MaxTypeFitted, ("maxTypeFitted", 2), ("existingTypeCount", 2)));
        Assert.Equal("CantFitTooManyByType", type);
        Assert.Equal((new PyInt(2), new PyInt(2)), (typeValues["noOfModules"], typeValues["noOfModulesFitted"]));
    }

    [Theory]
    [InlineData(FitError.InvalidShipGroup, "")]
    [InlineData(FitError.InvalidShipType, "")]
    [InlineData(FitError.RigSizeMismatch, "")]
    [InlineData(FitError.InsufficientCalibration, "")]
    [InlineData(FitError.InvalidSlot, "No suitable slot available")]
    [InlineData(FitError.TypeNotFittable, "No suitable slot available")]
    public void The_other_refusals_are_a_failed_fit_with_the_reason_the_reference_gives(FitError error, string reason)
    {
        var (name, values) = Say(Cannot(error));
        Assert.Equal("ModuleFitFailed", name);
        Assert.Equal((new PyInt(Module), new PyStr(reason)), (values["moduleName"], values["reason"]));
    }
}
