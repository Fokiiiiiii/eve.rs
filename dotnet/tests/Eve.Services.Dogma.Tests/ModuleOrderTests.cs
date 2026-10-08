using Eve.Macho;
using Eve.Marshal;

namespace Eve.Services.Dogma.Tests;

/// <summary>The calls that start and stop a module's effect in space: what they pass on, what they answer and the messages of a refusal.</summary>
public class ModuleOrderTests
{
    private const long System = 30100032;
    private const long Module = 9988400002220, Target = 9988400002215;

    private static async Task<(Rig Rig, ClientSession Session, string Oid)> InSpaceAsync(
        SkillBoosterActivationHandler? activateBooster = null)
    {
        var rig = await Rig.CreateAsync(activateBooster);
        var character = await rig.NewCharacterAsync();
        var session = Rig.Session(character.CharacterId, station: null);
        session.Set("solarsystemid", new PyInt(System));
        return (rig, session, await rig.BindInSpaceAsync(session, System));
    }

    private static (string Name, Dictionary<string, PyValue> Values) UserError(MachoWrappedException error)
    {
        var arguments = ((PyTuple)((PyTuple)((PyObjectEx)error.Error.Payload).Header).Items[1]).Items;
        return (((PyStr)arguments[0]).Value, ((PyDict)arguments[1]).Entries.ToDictionary(e => ((PyStr)e.Key).Value, e => e.Value));
    }

    private static async Task<(string Name, Dictionary<string, PyValue> Values)> RefusedAsync(Rig rig, ClientSession session, string oid, ModuleResult refusal, bool starting = true)
    {
        rig.SpaceModules.Answer = refusal;
        var error = await Assert.ThrowsAsync<MachoWrappedException>(async () =>
        {
            _ = starting
                ? await rig.CallAsync(session, "Activate", oid, new PyLong(Module), new PyStr("moduleBonusAfterburner"), PyNone.Instance, new PyInt(1000))
                : await rig.CallAsync(session, "Deactivate", oid, new PyLong(Module), new PyStr("moduleBonusAfterburner"));
        });
        return UserError(error);
    }

    [PgFact]
    public async Task Starting_an_effect_passes_on_the_module_the_effect_the_target_and_the_cycles_and_answers_one()
    {
        var (rig, session, oid) = await InSpaceAsync();
        await using var _ = rig;
        Assert.Equal(new PyInt(1), await rig.CallAsync(session, "Activate", oid, new PyLong(Module), new PyStr("moduleBonusAfterburner"), PyNone.Instance, new PyInt(1000)));
        Assert.Equal(new PyInt(1), await rig.CallAsync(session, "Activate", oid, new PyLong(Module), new PyStr("targetAttack"), new PyLong(Target), new PyInt(5)));
        // no cycles asked for is one
        await rig.CallAsync(session, "Activate", oid, new PyLong(Module), new PyStr("miningLaser"), new PyLong(Target), PyNone.Instance);
        // and a number of cycles that is not one is one
        await rig.CallAsync(session, "Activate", oid, new PyLong(Module), new PyStr("miningLaser"), new PyLong(Target), new PyInt(0));
        Assert.Equal(
            [$"activate {Module} moduleBonusAfterburner 0 1000", $"activate {Module} targetAttack {Target} 5", $"activate {Module} miningLaser {Target} 1", $"activate {Module} miningLaser {Target} 1"],
            rig.SpaceModules.Asked);
    }

    [PgFact]
    public async Task Client_activate_routes_boosters_to_the_skills_callback_before_module_orders()
    {
        (long CharacterId, long ItemId, string EffectName, long? StationId, long? ShipId)? called = null;
        var (rig, session, oid) = await InSpaceAsync((characterId, itemId, effectName, stationId, shipId, _) =>
        {
            called = (characterId, itemId, effectName, stationId, shipId);
            return ValueTask.FromResult<bool?>(true);
        });
        await using var _ = rig;
        const long boosterItemId = 7000001;

        Assert.Equal(new PyInt(1), await rig.CallAsync(
            session, "Activate", oid, new PyLong(boosterItemId), new PyStr("boosterPrimary"), PyNone.Instance, new PyInt(1)));
        Assert.Equal((session.CharacterId!.Value, boosterItemId, "boosterPrimary", (long?)null, (long?)null), called);
        Assert.Empty(rig.SpaceModules.Asked);
    }

    [PgFact]
    public async Task Stopping_an_effect_passes_on_the_module_and_the_effect_and_answers_one()
    {
        var (rig, session, oid) = await InSpaceAsync();
        await using var _ = rig;
        Assert.Equal(new PyInt(1), await rig.CallAsync(session, "Deactivate", oid, new PyLong(Module), new PyStr("moduleBonusAfterburner")));
        Assert.Equal([$"deactivate {Module} moduleBonusAfterburner"], rig.SpaceModules.Asked);
    }

    [PgFact]
    public async Task A_call_with_no_bound_object_the_session_was_given_or_no_ship_in_space_is_a_denied_ship_change()
    {
        var (rig, session, oid) = await InSpaceAsync();
        await using var _ = rig;
        var unbound = await Assert.ThrowsAsync<MachoWrappedException>(async () => await rig.CallAsync(session, "Activate", "N=65450:99999", new PyLong(Module), new PyStr("x"), PyNone.Instance, new PyInt(1)));
        Assert.Equal("DeniedShipChanged", UserError(unbound).Name);
        rig.SpaceModules.Answer = null;
        var noShip = await Assert.ThrowsAsync<MachoWrappedException>(async () => await rig.CallAsync(session, "Deactivate", oid, new PyLong(Module), new PyStr("x")));
        Assert.Equal("DeniedShipChanged", UserError(noShip).Name);
        Assert.DoesNotContain(rig.SpaceModules.Asked, a => a.Contains("99999"));
    }

    [PgFact]
    public async Task An_effect_that_is_running_is_the_error_that_names_the_module_by_its_type()
    {
        var (rig, session, oid) = await InSpaceAsync();
        await using var _ = rig;
        // as recorded: the 1MN Civilian Afterburner
        var (name, values) = await RefusedAsync(rig, session, oid, ModuleResult.Refusal("MODULE_ALREADY_ACTIVE", 21857, "1MN Civilian Afterburner"));
        Assert.Equal(("EffectAlreadyActive2", new PyTuple([new PyInt(4), new PyInt(21857)])), (name, values["modulename"]));
    }

    [PgFact]
    public async Task What_the_reference_says_of_an_effect_that_cannot_be_started_is_what_the_client_shows()
    {
        var (rig, session, oid) = await InSpaceAsync();
        await using var _ = rig;
        async Task<string> Said(string reason, string module = "1MN Civilian Afterburner")
        {
            var (name, values) = await RefusedAsync(rig, session, oid, ModuleResult.Refusal(reason, 21857, module));
            Assert.Equal("CustomNotify", name);
            return ((PyStr)values["notify"]).Value;
        }
        Assert.Equal("You need an active target to activate that module.", await Said("TARGET_REQUIRED"));
        Assert.Equal("1MN Civilian Afterburner is offline.", await Said("MODULE_NOT_ONLINE"));
        Assert.Equal("You do not have enough capacitor to activate that module.", await Said("NOT_ENOUGH_CAPACITOR"));
        Assert.Equal("1MN Civilian Afterburner cannot be activated.", await Said("UNSUPPORTED_EFFECT"));
        // an effect whose body is pending is shown as the same refusal, not as the generic failure
        Assert.Equal("1MN Civilian Afterburner cannot be activated.", await Said("EFFECT_NOT_IMPLEMENTED"));
        // a launcher whose group is not run yet names its reason; the client shows the same refusal
        // a cloak the ship cannot take is the client's own UserError, as the client raises it itself (FakeCloaking.PreStartChecks)
        Assert.Equal("CantCloakTooShipUnsuitable", (await RefusedAsync(rig, session, oid, ModuleResult.Refusal("CLOAK_SHIP_UNSUITABLE", 21857, "1MN Civilian Afterburner"))).Name);
        Assert.Equal("CantCloak", (await RefusedAsync(rig, session, oid, ModuleResult.Refusal("CLOAK_DISALLOWED", 21857, "1MN Civilian Afterburner"))).Name);
        Assert.Equal("1MN Civilian Afterburner cannot be activated.", await Said("DEFENDER_LAUNCHER_NOT_IMPLEMENTED"));
        Assert.Equal("1MN Civilian Afterburner cannot be activated.", await Said("BOMB_LAUNCHER_NOT_IMPLEMENTED"));
        // a firing effect refused by name (F.O.F. missiles, MissileEffect.RefusedFiringEffects) is shown as the same refusal, not as a raw reason
        Assert.Equal("1MN Civilian Afterburner cannot be activated.", await Said("FOF_MISSILE_NOT_IMPLEMENTED"));
        // the reference's wording (moduleOperation.js): the microwarpdrive is held by a scrambler, and a module too damaged to start says so
        Assert.Equal("That module cannot be activated while you are warp scrambled.", await Said("MICROWARPDRIVE_BLOCKED"));
        Assert.Equal("1MN Civilian Afterburner is too damaged to activate.", await Said("MODULE_INCAPACITATED"));
        // a module that burns fuel the ship does not have is refused with the reference's wording; a fuel refusal the host gives for another reason is refused as a whole
        Assert.Equal("You do not have enough fuel to activate that module.", await Said("NO_FUEL"));
        Assert.Equal("1MN Civilian Afterburner cannot be activated.", await Said("FUEL_REFUSED"));
        Assert.Equal("Failed to activate 1MN Civilian Afterburner: SafetyActivated.", await Said("SafetyActivated"));
        Assert.Equal("Failed to activate module: BAD_EFFECT.", await Said("BAD_EFFECT", ""));
        foreach (var reason in new[] { "NOT_IN_SPACE", "NO_SHIP_IN_SPACE", "MODULE_NOT_FOUND" })
        {
            Assert.Equal("DeniedShipChanged", (await RefusedAsync(rig, session, oid, ModuleResult.Refusal(reason))).Name);
        }
    }

    [PgFact]
    public async Task Stopping_what_is_not_running_is_a_message_that_names_the_module()
    {
        var (rig, session, oid) = await InSpaceAsync();
        await using var _ = rig;
        var (name, values) = await RefusedAsync(rig, session, oid, ModuleResult.Refusal("MODULE_NOT_ACTIVE", 21857, "1MN Civilian Afterburner"), starting: false);
        Assert.Equal(("CustomNotify", "1MN Civilian Afterburner is not active."), (name, ((PyStr)values["notify"]).Value));
        var (other, otherValues) = await RefusedAsync(rig, session, oid, ModuleResult.Refusal("SOMETHING", 21857, "1MN Civilian Afterburner"), starting: false);
        Assert.Equal(("CustomNotify", "Failed to deactivate 1MN Civilian Afterburner."), (other, ((PyStr)otherValues["notify"]).Value));
        Assert.Equal("DeniedShipChanged", (await RefusedAsync(rig, session, oid, ModuleResult.Refusal("MODULE_NOT_FOUND"), starting: false)).Name);
    }
}
