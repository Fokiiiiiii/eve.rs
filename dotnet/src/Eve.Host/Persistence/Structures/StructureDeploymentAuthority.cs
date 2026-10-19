using System.Text.RegularExpressions;
using Eve.Marshal;
using Eve.Scheduler;
using Eve.Services;
using Eve.Structures;
using Eve.Host.Persistence.Structures;
using Npgsql;

namespace Eve.Host;

/// <summary>
/// The management calls of <c>structureDeployment.RenameStructure</c> and <c>structureVulnerability.UpdateReinforceTimesFromClient</c>. The gates are EveJS's
/// (<c>sovPlayerDeployment.js</c> <c>requireStructureManagementSession</c> and <c>renameStructureByID</c>; <c>structureVulnerabilityService.js</c>
/// <c>Handle_UpdateReinforceTimesFromClient</c>): every structure exists and is not gone, the pilot's corporation owns it, and the pilot is a Station Manager,
/// a Director or the CEO. A refusal is a named user error and changes nothing (all structures of one request are checked before any is changed).
/// The reinforcement time is the timeline's (<see cref="StructureStateMachine"/>): the new day and hour are pending for 30 days (wiki; DEVIATIONS).
/// </summary>
public sealed class StructureDeploymentAuthority
{
    private const long DirectorRole = 1, StationManagerRole = 2048;
    private const int MinimumNameLength = 3, MaximumNameLength = 256;

    private readonly NpgsqlDataSource _source;
    private readonly StructureStore _store;
    private readonly StructureTimers _timers;
    private readonly DurableScheduler _scheduler;
    private readonly Eve.Events.EventBus _events;

    public StructureDeploymentAuthority(NpgsqlDataSource source, StructureStore store, StructureTimers timers, DurableScheduler scheduler, Eve.Events.EventBus events)
    {
        _source = source;
        _store = store;
        _timers = timers;
        _scheduler = scheduler;
        _events = events;
    }

    /// <summary>
    /// <c>UpdateReinforceTimesFromClient</c>: each structure asks for the day and hour. The request equal to the structure's own clears its pending change; any other
    /// becomes the pending change, effective after 30 days (the timeline's deadline job applies it then).
    /// </summary>
    public async Task UpdateReinforceTimesAsync(ServiceCall call, IReadOnlyList<long> structureIds, int weekday, int hour, CancellationToken cancellationToken = default)
    {
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var structures = new List<(long StructureId, StructureRecord Record)>(structureIds.Count);
        foreach (var structureId in structureIds)
        {
            structures.Add((structureId, await ManagedAsync(call, connection, transaction, structureId, cancellationToken)));
        }
        var changed = new List<(long CorporationId, StructureReinforcementChangedStructure Structure, long ApplyAtMs)>();
        foreach (var (structureId, record) in structures)
        {
            var transition = await _timers.ApplyAsync(connection, transaction, structureId, new ReinforceTimeRequested(weekday, hour), cancellationToken);
            if (transition?.Refusal is { } refusal)
            {
                throw Refusal($"Failed to update reinforcement timing: {refusal}.");
            }
            if (transition is { Changed: true } && transition.Next.PendingEffectiveMs > 0)
            {
                changed.Add((record.OwnerCorporationId,
                    new StructureReinforcementChangedStructure(structureId, record.Name, record.TypeId),
                    transition.Next.PendingEffectiveMs));
            }
        }
        foreach (var corporation in changed.GroupBy(change => change.CorporationId))
        {
            var first = corporation.First();
            await _events.PublishInTransactionAsync(connection, transaction, ReinforcementChangedNotifications.Topic,
                new StructureReinforcementChanged(corporation.Key, weekday, hour, first.ApplyAtMs,
                    corporation.Select(change => change.Structure).ToArray()), cancellationToken: cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        _scheduler.Wake();
    }

    /// <summary>
    /// <c>Unanchor</c>: the structure's unanchoring starts and ends after 7 days (45 minutes for a Flex structure); the structure leaves the system then. Only a
    /// Director or the CEO may ask (EVE.js <c>requireStructureDecommissionSession</c>). A structure already unanchoring is answered as EVE.js does: nothing changes.
    /// </summary>
    public Task UnanchorAsync(ServiceCall call, long structureId, CancellationToken cancellationToken = default) =>
        ChangeUnanchorAsync(call, structureId, new UnanchorStarted(), cancellationToken);

    /// <summary>
    /// <c>CancelUnanchor</c>: a structure that is unanchoring stops doing so; one that is not is answered without a change (EVE.js
    /// <c>cancelStructureUnanchoring</c>). The same roles as <see cref="UnanchorAsync"/>.
    /// </summary>
    public Task CancelUnanchorAsync(ServiceCall call, long structureId, CancellationToken cancellationToken = default) =>
        ChangeUnanchorAsync(call, structureId, new UnanchorCancelled(), cancellationToken);

    private async Task ChangeUnanchorAsync(ServiceCall call, long structureId, StructureEvent change, CancellationToken cancellationToken)
    {
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var record = await LockedAsync(connection, transaction, structureId, cancellationToken);
        RequireOwner(call, record);
        var roles = call.Session.GetInt("corprole") ?? 0;
        var ceo = call.Session.Get("isCEO") is PyBool { Value: true };
        if (!ceo && (roles & DirectorRole) == 0)
        {
            throw MachoErrors.ThrowUserError("CrpAccessDenied", ("reason", new PyStr("Insufficient roles")));
        }
        var transition = await _timers.ApplyAsync(connection, transaction, structureId, change, cancellationToken);
        if (transition?.Refusal is { } refusal && !(refusal is "ALREADY_UNANCHORING" or "NOT_UNANCHORING"))
        {
            // the same words as EVE.js (structureState.startStructureUnanchoring: STRUCTURE_CANNOT_UNANCHOR_IN_STATE)
            throw Refusal($"Failed to unanchor structure: {(refusal == "UNANCHOR_NOT_ALLOWED" ? "STRUCTURE_CANNOT_UNANCHOR_IN_STATE" : refusal)}.");
        }
        await transaction.CommitAsync(cancellationToken);
        _scheduler.Wake();
    }

    /// <summary>
    /// <c>RenameStructure</c>: the name is trimmed, its line breaks and runs of spaces become one space, and it is cut to 256 characters; less than three
    /// characters is refused (EVE.js <c>normalizeStructureName</c>, <c>MIN_STRUCTURE_NAME_LENGTH</c>).
    /// </summary>
    public async Task RenameAsync(ServiceCall call, long structureId, string name, CancellationToken cancellationToken = default)
    {
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await ManagedAsync(call, connection, transaction, structureId, cancellationToken);
        var normalized = Normalize(name);
        if (normalized.Length < MinimumNameLength)
        {
            throw MachoErrors.ThrowUserError("CharNameTooShort");
        }
        await _store.SetNameAsync(connection, transaction, structureId, normalized, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>The structure, locked, when it exists and is not gone; otherwise the named refusal.</summary>
    private async Task<StructureRecord> LockedAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long structureId, CancellationToken cancellationToken)
    {
        if (await _store.GetAsync(connection, transaction, structureId, forUpdate: true, cancellationToken) is not { } record || record.Snapshot.DestroyedMs > 0)
        {
            throw MachoErrors.ThrowUserError("TargetingAttemptCancelled");
        }
        return record;
    }

    /// <summary>The structure, locked, when it exists, is not gone and the pilot may manage it; otherwise the named refusal.</summary>
    private async Task<StructureRecord> ManagedAsync(ServiceCall call, NpgsqlConnection connection, NpgsqlTransaction transaction, long structureId, CancellationToken cancellationToken)
    {
        var record = await LockedAsync(connection, transaction, structureId, cancellationToken);
        RequireManagement(call, record);
        return record;
    }

    /// <summary>The pilot's corporation owns the structure (a corporation the pilot has none in is refused too, as EVE.js does for a pilot with a corporation).</summary>
    private static void RequireOwner(ServiceCall call, StructureRecord record)
    {
        var corporationId = call.Session.GetInt("corporationID") ?? call.Session.GetInt("corpid") ?? 0;
        if (corporationId <= 0 || record.OwnerCorporationId <= 0 || corporationId != record.OwnerCorporationId)
        {
            throw MachoErrors.ThrowUserError("CrpAccessDenied", ("reason", new PyStr("Structure belongs to another corporation")));
        }
    }

    private static void RequireManagement(ServiceCall call, StructureRecord record)
    {
        RequireOwner(call, record);
        var roles = call.Session.GetInt("corprole") ?? 0;
        var ceo = call.Session.Get("isCEO") is PyBool { Value: true };
        if (!ceo && (roles & (StationManagerRole | DirectorRole)) == 0)
        {
            throw MachoErrors.ThrowUserError("CrpAccessDenied", ("reason", new PyStr("Insufficient roles")));
        }
    }

    /// <summary>EVE.js <c>normalizeStructureName</c>: trimmed, line breaks and runs of white space as one space, at most 256 characters.</summary>
    public static string Normalize(string name)
    {
        var text = Regex.Replace(Regex.Replace(name.Trim(), @"[\r\n]+", " "), @"\s+", " ").Trim();
        return text.Length > MaximumNameLength ? text[..MaximumNameLength].Trim() : text;
    }

    /// <summary>The refusal the client shows: a named notice.</summary>
    private static Exception Refusal(string message) => MachoErrors.ThrowUserError("CustomNotify", ("notify", new PyStr(message)));
}
