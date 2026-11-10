using System.Globalization;
using Eve.Events;
using Eve.Marshal;
using Eve.Scheduler;
using Eve.Services;
using Eve.Services.Core;
using Eve.Services.Slash;
using Eve.Structures;
using Npgsql;

namespace Eve.Host;

/// <summary>The persistent backend for the V24 structure menu and moon scheduling GM actions.</summary>
public sealed class StructureGmAuthority(
    NpgsqlDataSource source,
    StructureStore structures,
    StructureTimers timers,
    MoonExtractions extractions,
    DurableScheduler scheduler,
    EventBus events)
{
    public async Task<string> ExecuteAsync(StructureGmRequest request, CancellationToken cancellationToken)
    {
        if (request.Command == "advance")
            return await AdvanceExtractionAsync(request, cancellationToken);

        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        StructureTransition? transition;
        switch (request.Command)
        {
            case "state":
                if (!int.TryParse(request.Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var state))
                    return "Usage: /structure state <structureID> <stateID>";
                transition = await timers.AdminSetStateAsync(connection, transaction, request.StructureId, state, cancellationToken);
                break;
            case "timer":
                transition = await timers.AdminSetStateTimerAsync(connection, transaction, request.StructureId, request.DurationMs, cancellationToken);
                break;
            case "deploytimer":
                transition = await timers.AdminSetDeployTimerAsync(connection, transaction, request.StructureId, request.DurationMs, cancellationToken);
                break;
            case "unanchor" when request.Value == "start":
                transition = await timers.ApplyAsync(connection, transaction, request.StructureId, new UnanchorStarted(), cancellationToken);
                break;
            case "unanchor" when request.Value == "cancel":
                transition = await timers.ApplyAsync(connection, transaction, request.StructureId, new UnanchorCancelled(), cancellationToken);
                break;
            case "unanchor" when request.Value == "timer":
                transition = await timers.AdminSetUnanchorTimerAsync(connection, transaction, request.StructureId, request.DurationMs, cancellationToken);
                break;
            case "abandontimer":
                transition = await timers.AdminSetAbandonTimerAsync(connection, transaction, request.StructureId, request.DurationMs, cancellationToken);
                break;
            default:
                return $"Unknown /structure subcommand '{request.Command}'. {StructureGmCommands.StructureUsage}";
        }

        if (transition is null)
            return $"Structure not found: {request.StructureId}.";
        if (transition.Refusal is { } refusal)
            return $"Failed to set structure {request.Command}: {refusal}.";

        await transaction.CommitAsync(cancellationToken);
        scheduler.Wake();
        events.Wake();
        return StructureMessage(request, transition.Next);
    }

    private async Task<string> AdvanceExtractionAsync(StructureGmRequest request, CancellationToken cancellationToken)
    {
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var structure = await structures.GetAsync(connection, transaction, request.StructureId, true, cancellationToken);
        if (structure is null || structure.Snapshot.DestroyedMs > 0)
            return $"Structure not found: {request.StructureId}.";
        var result = await extractions.AdminAdvanceAsync(connection, transaction, request.StructureId, request.Value,
            cancellationToken, request.ActorCharacterId);
        if (result.Outcome == MoonExtractions.AdvanceOutcome.NoActiveExtraction)
            return $"Failed to advance the extraction of {request.StructureId}: NO_ACTIVE_EXTRACTION. {StructureGmCommands.ExtractionUsage}";
        if (result.Outcome == MoonExtractions.AdvanceOutcome.UnknownPhase)
            return $"Failed to advance the extraction of {request.StructureId}: UNKNOWN_PHASE. {StructureGmCommands.ExtractionUsage}";

        await transaction.CommitAsync(cancellationToken);
        scheduler.Wake();
        events.Wake();
        var extraction = result.Extraction!;
        var seconds = Math.Round(-result.ShiftMs / 1000d, MidpointRounding.AwayFromZero);
        var available = DateTimeOffset.FromUnixTimeMilliseconds(extraction.ChunkAvailableMs).ToString("O", CultureInfo.InvariantCulture);
        var decay = DateTimeOffset.FromUnixTimeMilliseconds(extraction.NaturalDecayMs).ToString("O", CultureInfo.InvariantCulture);
        return $"GM advanced the extraction of {request.StructureId} to {request.Value} (times moved {seconds:0} s earlier; chunk ready {available}, auto fracture {decay}).";
    }

    private static string StructureMessage(StructureGmRequest request, StructureSnapshot snapshot)
    {
        var seconds = request.DurationMs / 1000d;
        var formattedSeconds = seconds.ToString("0.###", CultureInfo.InvariantCulture);
        return request.Command switch
        {
            "state" => $"GM set structure state={StateName(snapshot.State)} for {request.StructureId}.",
            "timer" => $"GM set structure state timer to {formattedSeconds}s for {request.StructureId}.",
            "deploytimer" => $"GM set deploy timer to {formattedSeconds}s for {request.StructureId}.",
            "unanchor" when request.Value == "start" => $"GM started unanchoring for {request.StructureId}.",
            "unanchor" when request.Value == "cancel" => $"GM cancelled unanchoring for {request.StructureId}.",
            "unanchor" => $"GM set unanchoring timer to {formattedSeconds}s for {request.StructureId}.",
            "abandontimer" => $"GM set abandon timer to {formattedSeconds}s for {request.StructureId}.",
            _ => "Command completed.",
        };
    }

    private static string StateName(int state) => state switch
    {
        StructureState.Unknown => "unknown",
        StructureState.Unanchored => "unanchored",
        StructureState.Anchoring => "anchoring",
        StructureState.OnlineDeprecated => "online_deprecated",
        StructureState.FittingInvulnerable => "fitting_invulnerable",
        StructureState.OnliningVulnerable => "onlining_vulnerable",
        StructureState.ShieldVulnerable => "shield_vulnerable",
        StructureState.ArmorReinforce => "armor_reinforce",
        StructureState.ArmorVulnerable => "armor_vulnerable",
        StructureState.HullReinforce => "hull_reinforce",
        StructureState.HullVulnerable => "hull_vulnerable",
        StructureState.AnchorVulnerable => "anchor_vulnerable",
        StructureState.DeployVulnerable => "deploy_vulnerable",
        StructureState.FobInvulnerable => "fob_invulnerable",
        _ => state.ToString(CultureInfo.InvariantCulture),
    };
}

/// <summary>Fans a committed GM extraction phase change out in the wire shape the V24 scheduling window consumes.</summary>
public static class MoonExtractionGmNotifications
{
    public static void Subscribe(EventBus events, CharacterSessions sessions)
    {
        events.Subscribe("moon.gm_advance_notice", [MoonExtractions.AdvancedTopic], (context, _) =>
        {
            var change = context.Event.Payload<MoonExtractionAdvanced>()!;
            var extraction = change.Extraction;
            var payload = MoonExtractionsRpcService.ExtractionPayload(MoonExtractionStore.ToLegacy(extraction));
            var recipients = new HashSet<ClientSession>();
            if (change.ActorCharacterId > 0 && sessions.Of(change.ActorCharacterId) is { } actor)
                recipients.Add(actor);
            foreach (var session in sessions.InStructure(extraction.StructureId).Where(session => session.GetInt("shipid") == extraction.StructureId))
                recipients.Add(session);
            foreach (var session in sessions.OfCorporation(extraction.OwnerCorporationId).Where(session => ((session.GetInt("corprole") ?? 0) & 2048) != 0))
                recipients.Add(session);
            foreach (var session in recipients)
                session.SendNotification("OnMoonExtractionUpdate", "clientID", payload);
            return Task.CompletedTask;
        });
    }
}
