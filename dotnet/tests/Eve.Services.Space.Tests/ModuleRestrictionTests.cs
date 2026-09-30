using Eve.Inventory;
using Eve.Macho;
using Eve.Marshal;
using Eve.Space;
using Eve.Universe;

namespace Eve.Services.Space.Tests;

/// <summary>Where a module may be used, as <c>dogma/moduleRestrictions.py</c> of the client has it.</summary>
public class ModuleRestrictionTests
{
    private static Func<int, double> Attributes(params (int Id, double Value)[] values) => id => values.FirstOrDefault(v => v.Id == id).Value;

    private static readonly (int, double) NoEmpire = (ModuleRestrictions.AttributeDisallowInEmpireSpace, 1);
    private static readonly (int, double) NoHighSec = (ModuleRestrictions.AttributeDisallowInHighSec, 1);

    [Fact]
    public void A_module_that_is_not_allowed_in_empire_space_cannot_be_used_in_low_high_or_safe_security_but_can_be_in_null_security()
    {
        var module = Attributes(NoEmpire);
        Assert.Null(ModuleRestrictions.Check(SecurityClass.ZeroSec, module));
        Assert.Equal("CantInEmpireSpace", ModuleRestrictions.Check(SecurityClass.LowSec, module));
        Assert.Equal("CantInEmpireSpace", ModuleRestrictions.Check(SecurityClass.HighSec, module));
        Assert.Equal("CantInEmpireSpace", ModuleRestrictions.Check(SecurityClass.SafeSec, module));
    }

    [Fact]
    public void A_module_that_is_not_allowed_in_high_security_is_allowed_in_low_and_null_security()
    {
        var module = Attributes(NoHighSec);
        Assert.Null(ModuleRestrictions.Check(SecurityClass.ZeroSec, module));
        Assert.Null(ModuleRestrictions.Check(SecurityClass.LowSec, module));
        Assert.Equal("CantInHighSecSpace", ModuleRestrictions.Check(SecurityClass.HighSec, module));
        Assert.Equal("CantInHighSecSpace", ModuleRestrictions.Check(SecurityClass.SafeSec, module));
    }

    [Fact]
    public void The_empire_rule_is_asked_first_and_a_module_with_neither_is_used_anywhere()
    {
        Assert.Equal("CantInEmpireSpace", ModuleRestrictions.Check(SecurityClass.HighSec, Attributes(NoEmpire, NoHighSec)));
        foreach (var securityClass in Enum.GetValues<SecurityClass>())
        {
            Assert.Null(ModuleRestrictions.Check(securityClass, Attributes()));
        }
    }

    [Fact]
    public void A_fully_corrupted_system_lets_through_the_modules_whose_type_says_so_and_only_those()
    {
        Assert.Null(ModuleRestrictions.Check(SecurityClass.LowSec, Attributes(NoEmpire, (ModuleRestrictions.AttributeAllowInFullyCorruptedLowSec, 1)), fullyCorrupted: true));
        Assert.Equal("CantInEmpireSpace", ModuleRestrictions.Check(SecurityClass.LowSec, Attributes(NoEmpire, (ModuleRestrictions.AttributeAllowInFullyCorruptedLowSec, 1))));
        Assert.Equal("CantInEmpireSpace", ModuleRestrictions.Check(SecurityClass.LowSec, Attributes(NoEmpire), fullyCorrupted: true));
        // the exception of high security is another attribute than that of low security
        Assert.Equal("CantInEmpireSpace", ModuleRestrictions.Check(SecurityClass.HighSec, Attributes(NoEmpire, (ModuleRestrictions.AttributeAllowInFullyCorruptedLowSec, 1)), fullyCorrupted: true));
        var allowed = (ModuleRestrictions.AttributeAllowInFullyCorruptedHighSec, 1.0);
        Assert.Null(ModuleRestrictions.Check(SecurityClass.HighSec, Attributes(NoEmpire, NoHighSec, allowed), fullyCorrupted: true));
        Assert.Equal("CantInHighSecSpace", ModuleRestrictions.Check(SecurityClass.HighSec, Attributes(NoHighSec), fullyCorrupted: true));
    }

    /// <summary>A module of a type the game does not let be used in empire space is refused where the pilot is (a system of the highest security), and the simulation is not asked.</summary>
    [PgFact]
    public async Task A_module_that_may_not_be_used_in_empire_space_is_refused_by_name_and_the_simulation_is_not_asked()
    {
        var rig = await Rig.CreateAsync();
        await using var _ = rig;
        var character = await rig.NewCharacterAsync();
        // a Warp Disruption Field Generator II in a high slot
        await using (var connection = await rig.Source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await rig.Items.CreateAsync(connection, transaction, new NewItem(4248, character.CharacterId, character.ShipId, ItemFlags.HiSlot0 + 5, 1, true));
            await transaction.CommitAsync();
        }
        var session = await rig.SessionAsync(character);
        var oid = await rig.BindShipAsync(session);
        await rig.ShipCallAsync(session, "Undock", oid, new PyLong(character.ShipId), PyBool.False);
        var bubble = (await rig.Items.InLocationAsync(character.ShipId, character.CharacterId)).Single(i => i.TypeId == 4248).Id;
        Assert.True(Security.IsHighSecOrHigher(Security.ClassFromLevel(1.0)), "the system of the test is of the highest security");

        var error = await Assert.ThrowsAsync<MachoWrappedException>(() => rig.Space.ActivateAsync(session, bubble, "warpDisruptSphere", 0, 1, CancellationToken.None));
        var arguments = ((PyTuple)((PyObjectEx)error.Error.Payload).Header).Items;
        Assert.Equal("CantInEmpireSpace", ((PyStr)((PyTuple)arguments[1]).Items[0]).Value);
        Assert.Empty(rig.Sim.Heard.OfType<SimActivate>());
    }
}
