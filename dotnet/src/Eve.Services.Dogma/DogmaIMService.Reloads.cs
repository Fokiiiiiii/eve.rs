using System.Collections.Concurrent;
using Eve.Scheduler;
using Eve.Fitting;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services.Ships;
using Eve.Ships;

namespace Eve.Services.Dogma;

/// <summary>
/// Ammunition in space. A pilot who loads a gun waits for the reload time of the module (the reference: <c>_queuePendingModuleReload</c> and
/// <c>_completePendingModuleReload</c> of <c>dogmaService.js</c>, the recording <c>oracle/fixtures/scenarios/reload.json</c>): the client is told at once that the module will be
/// ready at a moment (its <c>nextActivationTime</c>) and that a charge is being loaded; when the time has run the rounds move from the hold into the module and the client is told what
/// changed, last that the module is ready. An unload takes no time. The rounds of a gun that runs are in the simulation (<see cref="ISpaceRounds"/>); the inventory follows what it says.
/// A reload that is waiting when the host stops is not done (nothing moved yet: the rounds are in the hold).
/// </summary>
public sealed partial class DogmaIMService
{
    /// <summary>The reloads that wait, by module: a module that is asked to load while one waits goes on waiting for it.</summary>
    private readonly ConcurrentDictionary<long, ReloadPlan> _reloads = new();

    private static PyValue NextActivation(long characterId, long moduleId, long fileTime, long next, long previous) => Py.Tuple(
        new PyStr("OnModuleAttributeChange"), InventoryWire.Id(characterId), InventoryWire.Id(moduleId), new PyInt(AmmoRules.NextActivationTimeAttribute), new PyLong(fileTime),
        new PyLong(next), new PyLong(previous), new PyLong(fileTime));

    /// <summary>Whatever a notification to a session that is gone cannot do is nothing: what was done to the character's items stands.</summary>
    private static void Tell(Action tell)
    {
        try
        {
            tell();
        }
        catch (Exception error) when (error is ObjectDisposedException or InvalidOperationException or IOException)
        {
        }
    }

    private async Task LoadInSpaceAsync(
        ClientSession session, long characterId, long shipId, IReadOnlyList<long> moduleIds, IReadOnlyList<long> chargeItemIds, long ammoLocationId, CancellationToken cancellationToken)
    {
        foreach (var plan in await ships.PlanReloadsAsync(characterId, shipId, moduleIds, chargeItemIds, ammoLocationId, cancellationToken))
        {
            if (plan.ReloadMs <= 0)
            {
                await ReloadAsync(session, characterId, plan, null, CancellationToken.None);
            }
            else
            {
                StartReload(session, characterId, plan);
            }
        }
    }

    /// <summary>The client is told the module is reloading until a moment and that a charge is being loaded; the rounds move when the time has run.</summary>
    private void StartReload(ClientSession session, long characterId, ReloadPlan plan)
    {
        if (!_reloads.TryAdd(plan.ModuleId, plan))
        {
            return; // it is waiting for one already
        }
        var now = Py.FileTimeFromUnixMs(nowUnixMs());
        var readyAt = Py.FileTimeFromUnixMs(nowUnixMs() + plan.ReloadMs);
        Tell(() =>
        {
            session.SendNotification("OnModuleAttributeChanges", "clientID", new PyList([NextActivation(characterId, plan.ModuleId, now, readyAt, 0)]));
            session.SendNotification("OnChargeBeingLoadedToModule", "charid", new PyList([InventoryWire.Id(plan.ModuleId)]), new PyInt(plan.ChargeTypeId), new PyInt(plan.ReloadMs));
        });
        // the wait starts as the client is told of it, not when a thread is free to start it (LocalTimers makes the wait in Set)
        _timers.Set($"dogma.reload:{plan.ModuleId}", TimeSpan.FromMilliseconds(plan.ReloadMs), async () =>
        {
            try
            {
                _reloads.TryRemove(plan.ModuleId, out _);
                await ReloadAsync(session, characterId, plan, readyAt, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                _reloads.TryRemove(plan.ModuleId, out _);
                log?.Invoke($"the reload of module {plan.ModuleId} could not be done: {error.Message}");
            }
        });
    }

    /// <summary>
    /// The rounds move from the hold into the module and the client is told what changed, last that the module is ready (<paramref name="readyAt"/>: the moment it was told).
    /// A gun that runs is given what the simulation adds to the rounds it has; one the hold could not give all of gives the rest back.
    /// </summary>
    private async Task ReloadAsync(ClientSession session, long characterId, ReloadPlan plan, long? readyAt, CancellationToken cancellationToken)
    {
        ShipOutcome outcome = new NothingHappened();
        try
        {
            if (await ships.ReloadRoomAsync(characterId, plan, cancellationToken) is { } room)
            {
                outcome = rounds is null
                    ? await ships.CompleteReloadAsync(characterId, plan, GunRounds.Idle, cancellationToken)
                    : await rounds.AdjustAsync(
                        session, plan.ModuleId, plan.ChargeTypeId, new RoundsChange.Add(room.Capacity, room.Available),
                        gun => ships.CompleteReloadAsync(characterId, plan, gun, cancellationToken), cancellationToken);
            }
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            log?.Invoke($"the reload of module {plan.ModuleId} could not be done: {error.Message}");
        }
        finally
        {
            Tell(() => TellAmmo(session, characterId, outcome));
            if (outcome is Reloaded { Shortfall: > 0 } lacking)
            {
                rounds?.GiveBack(session, plan.ModuleId, lacking.Shortfall);
            }
            if (readyAt is { } ready)
            {
                Tell(() => session.SendNotification(
                    "OnModuleAttributeChanges", "clientID", new PyList([NextActivation(characterId, plan.ModuleId, Py.FileTimeFromUnixMs(nowUnixMs()), 0, ready)])));
            }
        }
    }

    /// <summary>What the modules hold goes to the hold at once; a gun that runs gives what the simulation took out of its rounds.</summary>
    private async Task UnloadInSpaceAsync(
        ClientSession session, long characterId, long shipId, IReadOnlyList<long> moduleIds, long location, int? flag, long? quantity, CancellationToken cancellationToken)
    {
        foreach (var moduleId in moduleIds.Distinct())
        {
            ShipOutcome outcome;
            try
            {
                outcome = await rounds!.AdjustAsync(
                    session, moduleId, 0, new RoundsChange.Remove(quantity ?? 0),
                    gun => ships.UnloadAmmoInSpaceAsync(characterId, shipId, moduleId, location, flag, quantity, gun, cancellationToken), cancellationToken);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                log?.Invoke($"module {moduleId} could not be unloaded: {error.Message}");
                continue;
            }
            TellAmmo(session, characterId, outcome);
        }
    }
}
