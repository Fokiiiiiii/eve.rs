using Eve.Macho;
using Eve.Marshal;
using Eve.Ships;

namespace Eve.Services.Dogma;

/// <summary>
/// The nanite paste repair of fitted modules (<c>InitiateModuleRepair</c>, <c>InitiateModuleRepairMany</c>, <c>StopModuleRepair</c>). The space layer keeps the
/// repairs under way and checks what a start may do (<see cref="ISpaceModules.StartRepairAsync"/>); the ship's rules, the paste and the damage it mends are
/// <see cref="CharacterShips"/>'s, and what changed is told to the client as the ammunition is (<see cref="TellAmmo"/>).
/// </summary>
public sealed partial class DogmaIMService
{
    /// <summary><c>InitiateModuleRepair(itemID)</c>: starts a module's repair; true when it started. Short of paste is a user error (the reference's words).</summary>
    [Method("InitiateModuleRepair")]
    public async ValueTask<PyValue> InitiateModuleRepair(ServiceCall call)
    {
        var start = await StartRepairAsync(call, [IdOf(call.Arg(0)) ?? 0]);
        if (start is null)
        {
            return PyBool.False;
        }
        if (start.MissingPaste.Count > 0)
        {
            throw MachoErrors.ThrowUserError("NotEnoughRepairMaterialToFinishAllRepairs");
        }
        return start.Started.Count > 0 ? PyBool.True : PyBool.False;
    }

    /// <summary><c>InitiateModuleRepairMany(itemIDs)</c>: the three lists the client reads: the modules started, short of paste, and refused.</summary>
    [Method("InitiateModuleRepairMany")]
    public async ValueTask<PyValue> InitiateModuleRepairMany(ServiceCall call)
    {
        var start = await StartRepairAsync(call, [.. IdsOf(call.Arg(0))]);
        if (start is null)
        {
            return new PyList([new PyList([]), new PyList([]), new PyList([])]);
        }
        return new PyList([
            new PyList([.. start.Started.Select(id => new PyLong(id))]),
            new PyList([.. start.MissingPaste.Select(id => new PyLong(id))]),
            new PyList([.. start.Failed.Select(id => new PyLong(id))]),
        ]);
    }

    /// <summary><c>StopModuleRepair(itemID)</c>: ends a module's repair; what it mended is taken off the module and the paste it used out of the hold.</summary>
    [Method("StopModuleRepair")]
    public async ValueTask<PyValue> StopModuleRepair(ServiceCall call)
    {
        if (BoundContextOf(call) is null || call.Session.CharacterId is not { } character || modules is null
            || IdOf(call.Arg(0)) is not { } moduleId
            || await modules.EndRepairAsync(call.Session, moduleId, call.Cancellation) is not { } repair)
        {
            return PyBool.False;
        }
        var terms = new CharacterShips.RepairTerms(repair.ModuleId, repair.ShipId, repair.StartedMs, repair.RateHpPerMinute, repair.DamageHp, repair.PasteUnits);
        var outcome = await ships.FinishRepairAsync(character, terms, nowUnixMs(), call.Cancellation);
        TellAmmo(call.Session, character, outcome);
        await modules.RefreshShipAsync(call.Session, call.Cancellation);
        return PyBool.True;
    }

    /// <summary>The repair starts for the modules asked for; null when the call is not from a bound object of the session (nothing is told).</summary>
    private async Task<ModuleRepairStart?> StartRepairAsync(ServiceCall call, IReadOnlyList<long> moduleIds)
    {
        if (BoundContextOf(call) is null || modules is null)
        {
            return null;
        }
        return await modules.StartRepairAsync(call.Session, moduleIds.Where(id => id > 0).ToList(), call.Cancellation);
    }
}
