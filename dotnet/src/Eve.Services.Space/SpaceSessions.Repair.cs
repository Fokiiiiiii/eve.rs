using Eve.Services;
using Eve.Ships;

namespace Eve.Services.Space;

/// <summary>
/// Nanite paste repair of a ship's modules in space: the start (<c>InitiateModuleRepair</c>) checks what the simulation and the pilot's state allow, the stop
/// (<c>StopModuleRepair</c>) takes the record out, and the rules and the paste are the ship's (<see cref="CharacterShips"/>). A repair is refused while a module
/// of the ship is overheated, and an overheat while a repair runs; a module under repair cannot run (the EVE University wiki, Overheating).
/// </summary>
public sealed partial class SpaceSessions
{
    public async Task<ModuleRepairStart?> StartRepairAsync(ClientSession session, IReadOnlyList<long> moduleIds, CancellationToken cancellationToken)
    {
        var id = (ulong)session.SessionId;
        if (!_pilots.TryGetValue(id, out var pilot) || session.CharacterId is not { } characterId || !IsInSpace(session))
        {
            return null;
        }
        var started = new List<long>();
        var missing = new List<long>();
        var failed = new List<long>();
        long inCargo = -1;
        long reserved = 0;
        foreach (var moduleId in moduleIds.Distinct())
        {
            if (pilot.Repairs.ContainsKey(moduleId))
            {
                started.Add(moduleId);
                continue;
            }
            // no paste while a module is overheated, and none for a module that runs
            if (!pilot.Overheated.IsEmpty || pilot.ActiveModules.ContainsKey(moduleId))
            {
                failed.Add(moduleId);
                continue;
            }
            if (await _ships.QuoteRepairAsync(characterId, moduleId, cancellationToken) is not { Incapacitated: false } quote)
            {
                failed.Add(moduleId);
                continue;
            }
            inCargo = inCargo < 0 ? await _ships.PasteInCargoAsync(characterId, pilot.ShipId, cancellationToken) : inCargo;
            // the paste the repairs asked for at the start is held back for them, so a second module is not promised the same paste
            if (inCargo - reserved < quote.PasteUnits)
            {
                missing.Add(moduleId);
                continue;
            }
            reserved += quote.PasteUnits;
            pilot.Repairs[moduleId] = new ModuleRepairRecord(moduleId, pilot.ShipId, _nowUnixMs(), quote.RateHpPerMinute, quote.DamageHp, quote.PasteUnits);
            started.Add(moduleId);
        }
        return new ModuleRepairStart(started, missing, failed);
    }

    public Task<ModuleRepairRecord?> EndRepairAsync(ClientSession session, long moduleId, CancellationToken cancellationToken) =>
        Task.FromResult(_pilots.TryGetValue((ulong)session.SessionId, out var pilot) && pilot.Repairs.TryRemove(moduleId, out var record) ? record : null);

    /// <summary>
    /// The pilot leaves the ship (a jump, a dock, a board, an eject or a log off): the repairs under way end with what they mended by now, and the paste for it
    /// is taken (the wiki: a repair cancelled by a jump keeps the part done and uses only its paste).
    /// </summary>
    private async Task CancelRepairsAsync(Pilot pilot, CancellationToken cancellationToken)
    {
        foreach (var repair in pilot.Repairs.Keys.ToList())
        {
            if (!pilot.Repairs.TryRemove(repair, out var record))
            {
                continue;
            }
            await _ships.FinishRepairAsync(pilot.CharacterId, new CharacterShips.RepairTerms(
                record.ModuleId, record.ShipId, record.StartedMs, record.RateHpPerMinute, record.DamageHp, record.PasteUnits), _nowUnixMs(), cancellationToken);
        }
    }
}
