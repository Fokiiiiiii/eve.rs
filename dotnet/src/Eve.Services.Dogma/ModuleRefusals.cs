using Eve.Macho;
using Eve.Marshal;

namespace Eve.Services.Dogma;

/// <summary>
/// The refusals of a module order, as the reference shows them: a simulation refusal (its reason) becomes the user error or the notice the client shows. One table for the
/// activation, deactivation and overload orders (<see cref="DogmaIMService"/> asks it; the wording is the reference's, <c>moduleOperation.js</c>
/// <c>_throwModuleActivationUserError</c>).
/// </summary>
public static class ModuleRefusals
{
    /// <summary>The client's UserError kind for a type (eveexceptions/const.py: UE_TYPEID = 4).</summary>
    private const int TypeReference = 4;

    /// <summary>The error the reference raises for a module order the simulation refused (its wording).</summary>
    public static MachoWrappedException Of(ModuleResult refusal, bool starting)
    {
        static MachoWrappedException Notify(string text) => MachoErrors.ThrowUserError("CustomNotify", ("notify", new PyStr(text)));
        var name = string.IsNullOrEmpty(refusal.ModuleName) ? "module" : refusal.ModuleName;
        if (!starting)
        {
            return refusal.Reason switch
            {
                "NOT_IN_SPACE" or "NO_SHIP_IN_SPACE" or "MODULE_NOT_FOUND" => MachoErrors.ThrowUserError("DeniedShipChanged"),
                "MODULE_NOT_ACTIVE" => Notify($"{name} is not active."),
                "MODULE_INCAPACITATED" => Notify($"{name} is too damaged to overload."),
                "MODULE_NOT_OVERLOADABLE" => Notify($"{name} cannot be overloaded."),
                "THERMODYNAMICS_SKILL_REQUIRED" => MachoErrors.ThrowUserError("DontHaveThermoDynamicsSkill", ("skillLevel", new PyInt(1))),
                _ => Notify($"Failed to deactivate {name}."),
            };
        }
        return refusal.Reason switch
        {
            "NOT_IN_SPACE" or "NO_SHIP_IN_SPACE" or "MODULE_NOT_FOUND" => MachoErrors.ThrowUserError("DeniedShipChanged"),
            "MODULE_ALREADY_ACTIVE" => MachoErrors.ThrowUserError("EffectAlreadyActive2", ("modulename", new PyTuple([new PyInt(TypeReference), new PyInt(refusal.ModuleType)]))),
            "TARGET_REQUIRED" => Notify("You need an active target to activate that module."),
            "TARGET_NOT_LOCKED" => Notify("That target is not locked."),
            "TARGET_NOT_FOUND" => MachoErrors.ThrowUserError("DeniedActivateTargetNotPresent"),
            // a mining laser's target out of reach is TargetTooFar with the distance, the target and the module (moduleOperation.js: _buildModuleTargetTooFarUserErrorValues)
            "TARGET_OUT_OF_RANGE" when string.Equals(refusal.EffectName, "miningLaser", StringComparison.OrdinalIgnoreCase) => MachoErrors.ThrowUserError(
                "TargetTooFar",
                ("distance", Py.Number(Math.Max(0, refusal.DesiredRange))),
                ("targetname", refusal.TargetType > 0 ? new PyTuple([new PyInt(TypeReference), new PyInt(refusal.TargetType)]) : new PyStr("That target")),
                ("module", new PyInt(refusal.ModuleType))),
            "TARGET_OUT_OF_RANGE" => MachoErrors.ThrowUserError(
                "TargetNotWithinRangeGeneric",
                ("desiredRange", Py.Number(Math.Max(0, refusal.DesiredRange))),
                ("targetName", refusal.TargetType > 0 ? new PyTuple([new PyInt(TypeReference), new PyInt(refusal.TargetType)]) : new PyStr("That target")),
                ("shipType", refusal.ShipType > 0 ? new PyTuple([new PyInt(TypeReference), new PyInt(refusal.ShipType)]) : new PyInt(0)),
                ("actualDistance", Py.Number(Math.Max(0, refusal.ActualDistance)))),
            "NO_AMMO" => MachoErrors.ThrowUserError("NoCharges", ("launcher", new PyTuple([new PyInt(TypeReference), new PyInt(refusal.ModuleType)]))),
            "MODULE_NOT_ONLINE" => Notify($"{name} is offline."),
            // the reference's wording for a module that cannot start (moduleOperation.js MODULE_INCAPACITATED: "is too damaged to activate.")
            "MODULE_INCAPACITATED" => Notify($"{name} is too damaged to activate."),
            "MODULE_NOT_OVERLOADABLE" => Notify($"{name} cannot be overloaded."),
            "THERMODYNAMICS_SKILL_REQUIRED" => MachoErrors.ThrowUserError("DontHaveThermoDynamicsSkill", ("skillLevel", new PyInt(1))),
            "NOT_ENOUGH_CAPACITOR" => Notify("You do not have enough capacitor to activate that module."),
            // the reference's wording for a microwarpdrive held by a warp scrambler (moduleOperation.js MICROWARPDRIVE_BLOCKED)
            "MICROWARPDRIVE_BLOCKED" => Notify("That module cannot be activated while you are warp scrambled."),
            // a running module blocks this one (activationBlocked 1349; the client's own UserError, fittingDogmaLocation: ModuleIsBlocked, moduleName the type)
            "MODULE_BLOCKED" => MachoErrors.ThrowUserError("ModuleIsBlocked", ("moduleName", new PyTuple([new PyInt(TypeReference), new PyInt(refusal.ModuleType)]))),
            // a compressor needs an industrial core that runs (activationRequiresActiveIndustrialCore 3265): the reference's words (EveJS moduleOperation.js)
            "ACTIVE_INDUSTRIAL_CORE_REQUIRED" => Notify("An active industrial core is required to activate that module."),
            // a module that cannot be deactivated early runs (disallowEarlyDeactivation 906): the reference's words for a cloak refused by it (EveJS moduleOperation.js)
            "CLOAK_MODULE_ACTIVE" => Notify("You cannot activate a cloaking device while a module that cannot be deactivated early is still running."),
            // a module that waits after it stopped (its reactivation delay, 669). shortcut: the client's ModuleReactivationDelayed2 wants the time since the stop and the
            // module id, which the refusal does not carry yet; until then the pilot is told in words of ours
            "MODULE_REACTIVATING" => Notify($"{name} cannot be reactivated yet."),
            "CLOAK_WARP_BLOCKED" => Notify("You cannot activate that cloaking device while warping."),
            // disallowActivateOnWarp (ModuleRestrictions.CheckWarp): the wording is ours; the static data's rule is the source, not a client UserError
            "MODULE_WARP_BLOCKED" => Notify($"{name} cannot be activated while your ship is warping."),
            // the client's own UserErrors (FakeCloaking.PreStartChecks): the ship cannot cloak, or disallows a cloak
            "CLOAK_SHIP_UNSUITABLE" => MachoErrors.ThrowUserError("CantCloakTooShipUnsuitable"),
            "CLOAK_DISALLOWED" => MachoErrors.ThrowUserError("CantCloak"),
            "CLOAK_TARGET_LOCKED" => Notify("Your ship is already target locked and cannot activate a cloaking device."),
            "CLOAK_PROXIMITY_BLOCKED" => Notify("You are too close to another object to activate your cloaking device."),
            "MODULE_CLOAKED" => Notify("You cannot activate modules while cloaked."),
            // a disruptive lance needs an active siege module on the ship (EveJS ACTIVE_SIEGE_MODULE_REQUIRED, moduleActivationRequiresActiveSiegeModule)
            "ACTIVE_SIEGE_MODULE_REQUIRED" => Notify($"{name} requires an active siege module."),
            // an effect with no body yet (PendingModuleEffect) or a module that burns a resource not taken yet (DogmaModifierEffect) is shown as one this module cannot run,
            // the same as an effect with no handler
            // the module burns fuel and the ship has not got the fuel of its cycle (the host's answer to the fuel of the first cycle; moduleOperation.js, shipModuleFuel.js NO_FUEL)
            "NO_FUEL" => Notify("You do not have enough fuel to activate that module."),
            // the host refused the fuel for a reason that is not the lack of it: the module cannot be run
            "FUEL_REFUSED" => Notify($"{name} cannot be activated."),
            "UNSUPPORTED_EFFECT" or "EFFECT_NOT_IMPLEMENTED" => Notify($"{name} cannot be activated."),
            // a module of a group of which one runs at a time (the reactive armor hardeners) while another of its group runs
            "MAX_GROUP_ACTIVE" => Notify($"{name} cannot run while another module of its group is active."),
            // a launcher or firing effect whose kind is not run yet names its reason (MissileEffect.RefusedLauncherGroups, RefusedFiringEffects, e.g. F.O.F.):
            // shown as the same refusal
            _ when refusal.Reason?.EndsWith("_NOT_IMPLEMENTED", StringComparison.Ordinal) == true => Notify($"{name} cannot be activated."),
            _ => Notify($"Failed to activate {name}: {refusal.Reason ?? "unknown error"}."),
        };
    }
}
