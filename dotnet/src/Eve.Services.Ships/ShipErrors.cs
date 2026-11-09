using System.Globalization;
using Eve.Dogma;
using Eve.Fitting;
using Eve.Marshal;
using Eve.Ships;

namespace Eve.Services.Ships;

/// <summary>What the client is told when a ship refuses: the user error it shows (the messages and error names are the reference's).</summary>
public static class ShipErrors
{
    /// <summary>The client's UserError kinds for a type and a group (eveexceptions/const.py).</summary>
    private const int UeTypeId = 4;
    private const int UeGroupId = 7;

    /// <summary>The error that answers a refusal (the static data names the skills and groups a message speaks of).</summary>
    public static MachoWrappedException Of(ShipRefused refused, DogmaStatic statics)
    {
        string nameOfType(int type) => statics.Type(type)?.Name ?? "";
        string nameOfGroup(int moduleType) => statics.GroupName(statics.Type(moduleType)?.GroupId ?? 0);
        static MachoWrappedException Notify(string text) => MachoErrors.ThrowUserError("CustomNotify", ("notify", new PyStr(text)));
        MachoWrappedException OnlineError(string name)
        {
            double Data(string key) => refused.Fit?.Data.GetValueOrDefault(key) ?? 0;
            return MachoErrors.ThrowUserError(
                name,
                ("moduleName", Py.Tuple(new PyInt(UeTypeId), new PyInt(refused.ModuleTypeId))),
                ("moduleType", new PyInt(refused.ModuleTypeId)),
                ("require", Py.Number(Data("require"))),
                ("remaining", Py.Number(Data("remaining"))),
                ("total", Py.Number(Data("total"))));
        }

        switch (refused.Reason)
        {
            case ShipRefusal.NotFitted:
                return MachoErrors.ThrowUserError("DeniedShipChanged");
            case ShipRefusal.NotEnoughCpu:
                return OnlineError("NotEnoughCpu");
            case ShipRefusal.NotEnoughPower:
                return OnlineError("NotEnoughPower");
            // the client's UserError (locationDogmaItem.py CheckCanOnlineItem): the type and the group are tuples of the client's reference kinds (eveexceptions/const.py:

            case ShipRefusal.MaxGroupOnline:
                return MachoErrors.ThrowUserError(
                    "CannotOnlineReachedMaxGroupOnline",
                    ("type", new PyTuple([new PyInt(UeTypeId), new PyInt(refused.ModuleTypeId)])),
                    ("group", new PyTuple([new PyInt(UeGroupId), new PyInt(refused.GroupId)])),
                    ("maxGroupOnline", new PyInt(refused.MaxGroupOnline)));
            case ShipRefusal.NotEnoughCargoSpace:
                return MachoErrors.ThrowUserError("NotEnoughCargoSpace", ("available", Py.Number(refused.Available)), ("volume", Py.Number(refused.Volume)));
            case ShipRefusal.NotEnoughDroneBaySpace:
                return MachoErrors.ThrowUserError("NotEnoughDroneBaySpace", ("type", new PyInt(refused.ModuleTypeId)), ("free", Py.Number(refused.Available)), ("required", Py.Number(refused.Volume)));
            case ShipRefusal.CargoWouldOverflow:
                return Notify(
                    $"That cannot be removed while the cargo hold is this full. The hold carries {refused.Used.ToString("0.00", CultureInfo.InvariantCulture)} m3 and the ship would be left with room for {refused.CapacityAfter.ToString("0.00", CultureInfo.InvariantCulture)} m3. Move some cargo out first.");
            case ShipRefusal.NotAllowedInHold:
                return MachoErrors.ThrowUserError("CustomNotify", ("notify", new PyStr("That item cannot be placed in that ship hold.")));
            case ShipRefusal.ContainerNesting:
                return Notify("A cargo container cannot be placed inside another cargo container.");
            case ShipRefusal.CannotMoveChargesBetweenModules:
                return MachoErrors.ThrowUserError("CantMoveChargesBetweenModules");
            case ShipRefusal.NotADrone:
                return Notify("Only drones can be placed in the Drone Bay.");
            case ShipRefusal.NoSpaceForThat:
                return MachoErrors.ThrowUserError("NoSpaceForThat", ("type", new PyInt(refused.ModuleTypeId)), ("free", Py.Number(refused.Available)), ("required", Py.Number(refused.Volume)));
            case ShipRefusal.NoSuitableSlot:
                return MachoErrors.ThrowUserError("ModuleFitFailed", ("moduleName", new PyInt(refused.ModuleTypeId)), ("reason", new PyStr("No suitable slot available")));
            case ShipRefusal.SkillRequired:
            case ShipRefusal.CannotFit when refused.Fit is { Error: FitError.SkillRequired }:
                {
                    IReadOnlyList<(int SkillTypeId, int RequiredLevel)> requirements = refused.Fit?.MissingSkills ?? [];
                    if (requirements.Count == 0 && refused.Fit is { } skillFailure
                        && skillFailure.Data.TryGetValue("skillTypeID", out var skill)
                        && skill > 0)
                    {
                        requirements = [((int)skill, (int)skillFailure.Data.GetValueOrDefault("requiredLevel"))];
                    }
                    var names = requirements.Select(requirement =>
                    {
                        var skillName = nameOfType(requirement.SkillTypeId);
                        if (string.IsNullOrEmpty(skillName))
                            skillName = requirement.SkillTypeId.ToString(CultureInfo.InvariantCulture);
                        return $"{skillName} : Level {requirement.RequiredLevel.ToString(CultureInfo.InvariantCulture)}";
                    }).ToArray();
                    return MachoErrors.ThrowUserError(
                        "FittingHasSkillPrerequisites",
                        ("requiredSkills", new PyStr(string.Join(", ", names))),
                        ("item", new PyInt(refused.ModuleTypeId)),
                        ("skillCount", new PyInt(names.Length)));
                }
        }

        var fit = refused.Fit!;
        double Data(string key) => fit.Data.GetValueOrDefault(key);
        switch (fit.Error)
        {
            case FitError.NoTurretHardpoints:
                return Notify(Data("turretHardpoints") > 0 && Data("usedTurrets") >= Data("turretHardpoints")
                    ? "All turret hardpoints on this ship are already in use."
                    : "This ship has no turret hardpoints.");
            case FitError.NoLauncherHardpoints:
                return Notify(Data("launcherHardpoints") > 0 && Data("usedLaunchers") >= Data("launcherHardpoints")
                    ? "All launcher hardpoints on this ship are already in use."
                    : "This ship has no launcher hardpoints.");
            case FitError.SlotOccupied:
                return Notify("That fitting slot is already occupied.");
            case FitError.MaxGroupFitted:
                return MachoErrors.ThrowUserError(
                    "CantFitTooManyByGroup",
                    ("ship", new PyInt(refused.ShipTypeId)), ("module", new PyInt(refused.ModuleTypeId)),
                    ("groupName", new PyStr(nameOfGroup(refused.ModuleTypeId))), ("noOfModules", Py.Number(Data("maxGroupFitted"))), ("noOfModulesFitted", Py.Number(Data("existingGroupCount"))));
            case FitError.MaxTypeFitted:
                return MachoErrors.ThrowUserError(
                    "CantFitTooManyByType",
                    ("ship", new PyInt(refused.ShipTypeId)), ("module", new PyInt(refused.ModuleTypeId)),
                    ("noOfModules", Py.Number(Data("maxTypeFitted"))), ("noOfModulesFitted", Py.Number(Data("existingTypeCount"))));
            case FitError.InvalidShipType or FitError.InvalidShipGroup or FitError.RigSizeMismatch or FitError.InsufficientCalibration:
                return MachoErrors.ThrowUserError("ModuleFitFailed", ("moduleName", new PyInt(refused.ModuleTypeId)), ("reason", new PyStr("")));
            default:
                // the slot is not one the ship has, the kind is wrong, the thing is no module: the reference has no message and neither do we
                return MachoErrors.ThrowUserError("ModuleFitFailed", ("moduleName", new PyInt(refused.ModuleTypeId)), ("reason", new PyStr("No suitable slot available")));
        }
    }
}
