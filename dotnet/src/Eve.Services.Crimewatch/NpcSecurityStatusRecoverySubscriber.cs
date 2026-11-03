using System.Text.Json;
using Eve.Crimewatch;
using Eve.Events;
using Eve.Relations;
using Eve.Scheduler;
using Npgsql;

namespace Eve.Services.Crimewatch;

/// <summary>Authoritative NPC facts used to record a security-status recovery.</summary>
public sealed record NpcSecurityStatusProfile(decimal Bounty, long CorporationId, int FactionId, int ShipTypeId, bool EligibleForRecovery);

public sealed record NpcSecurityStatusRecoveryWindowDue(long CharacterId, long WindowEndMs);

/// <summary>Collects the highest-value NPC kill in each five-minute tick and settles it durably at the tick boundary.</summary>
public sealed class NpcSecurityStatusRecoverySubscriber
{
    public const string SubscriberName = "npc.security-status-recovery";
    public const string Topic = "npc.killed";
    public const string JobKind = "npc.security-status-recovery";
    public const long WindowLengthMs = 5 * 60 * 1000;
    public const string HistoryReason = "npc_bounty_recovery";

    private readonly PgCrimewatchStore _crimewatch;
    private readonly DurableScheduler _scheduler;
    private readonly EventBus _events;
    private readonly RelationStore _relations;
    private readonly Func<string, NpcSecurityStatusProfile?> _profileForId;
    private readonly Func<NpgsqlConnection, NpgsqlTransaction, long, CancellationToken, Task<double>> _fastTalkMultiplier;
    private readonly Func<double, decimal, double, double?> _nextStatus;

    public NpcSecurityStatusRecoverySubscriber(
        PgCrimewatchStore crimewatch,
        DurableScheduler scheduler,
        EventBus events,
        RelationStore relations,
        Func<string, NpcSecurityStatusProfile?> profileForId,
        Func<NpgsqlConnection, NpgsqlTransaction, long, CancellationToken, Task<double>> fastTalkMultiplier,
        Func<double, decimal, double, double?> nextStatus)
    {
        _crimewatch = crimewatch;
        _scheduler = scheduler;
        _events = events;
        _relations = relations;
        _profileForId = profileForId;
        _fastTalkMultiplier = fastTalkMultiplier;
        _nextStatus = nextStatus;
    }

    public void Register()
    {
        _events.Subscribe(SubscriberName, [Topic], HandleAsync);
        _scheduler.Register(JobKind, SettleAsync);
    }

    public async Task HandleAsync(EventContext context, CancellationToken cancellationToken = default)
    {
        using var document = JsonDocument.Parse(context.Event.PayloadJson);
        var payload = document.RootElement;
        var characterId = ReadInt64(payload, "characterId");
        var profileId = ReadString(payload, "profile");
        if (characterId <= 0 || string.IsNullOrWhiteSpace(profileId) || context.Event.OccurredMs <= 0)
        {
            return;
        }

        var profile = _profileForId(profileId);
        if (profile is not { Bounty: > 0, EligibleForRecovery: true } npc)
        {
            return;
        }

        var latestWindow = await _crimewatch.LatestNpcSecurityStatusRecoveryWindowAsync(
            context.Connection, context.Transaction, characterId, cancellationToken).ConfigureAwait(false);
        var windowEndMs = latestWindow is { } active && context.Event.OccurredMs <= active.WindowEndMs
            ? active.WindowEndMs
            : checked(Math.Max(context.Event.OccurredMs, latestWindow?.WindowEndMs ?? context.Event.OccurredMs) + WindowLengthMs);
        var npcTypeId = ReadInt64(payload, "typeId");
        if (npcTypeId <= 0)
        {
            npcTypeId = npc.ShipTypeId;
        }

        var window = new NpcSecurityStatusRecoveryWindow(
            characterId,
            windowEndMs,
            context.Event.Id,
            ReadInt64(payload, "solarSystemId"),
            ReadInt64(payload, "shipId"),
            npcTypeId,
            npc.CorporationId,
            npc.FactionId,
            profileId,
            npc.Bounty);
        await _crimewatch.RecordNpcSecurityStatusRecoveryWindowAsync(
            context.Connection, context.Transaction, window, cancellationToken).ConfigureAwait(false);
        var due = new NpcSecurityStatusRecoveryWindowDue(characterId, windowEndMs);
        await _scheduler.ScheduleAsync(context.Connection, context.Transaction,
            new JobSpec(JobKind, windowEndMs)
            {
                Key = WindowKey(due),
                Payload = due,
                OnDuplicate = OnDuplicate.Keep,
            }, cancellationToken).ConfigureAwait(false);
    }

    public async Task SettleAsync(JobContext context, CancellationToken cancellationToken = default)
    {
        var due = context.Payload<NpcSecurityStatusRecoveryWindowDue>();
        if (due is not { CharacterId: > 0, WindowEndMs: > 0 })
        {
            throw new InvalidOperationException("NPC security-status recovery job has an invalid window");
        }

        if (context.NowMs < due.WindowEndMs || await _events.HasUnprocessedAsync(
                context.Connection, context.Transaction, SubscriberName, Topic, due.WindowEndMs, cancellationToken).ConfigureAwait(false))
        {
            context.RunAgainAt(Math.Max(context.NowMs + 1000, due.WindowEndMs), due);
            return;
        }

        var window = await _crimewatch.NpcSecurityStatusRecoveryWindowAsync(
            context.Connection, context.Transaction, due.CharacterId, due.WindowEndMs, cancellationToken).ConfigureAwait(false);
        if (window is null)
        {
            return;
        }

        var previous = await _relations.ValueAsync(RelationKinds.SecurityStatus.Name,
            window.CharacterId, RelationStore.World, 0,
            context.Connection, context.Transaction, cancellationToken).ConfigureAwait(false);
        if (!double.IsFinite(previous) || previous < -10 || previous > 10)
        {
            throw new InvalidOperationException("NPC security-status recovery found invalid persisted status");
        }

        var fastTalk = await _fastTalkMultiplier(context.Connection, context.Transaction, window.CharacterId, cancellationToken).ConfigureAwait(false);
        fastTalk = double.IsFinite(fastTalk) ? Math.Max(0, fastTalk) : 1.0;
        var next = _nextStatus(previous, window.Bounty, fastTalk);
        if (next is { } value)
        {
            if (!double.IsFinite(value) || value <= previous || previous >= 10)
            {
                throw new InvalidOperationException("NPC security-status recovery produced invalid history values");
            }

            var modification = (value - previous) / (10 - previous);
            await _relations.SetAsync(context.Connection, context.Transaction,
                RelationKinds.SecurityStatus.Name, window.CharacterId, RelationStore.World, value,
                HistoryReason,
                new Dictionary<string, object?>
                {
                    ["eventTypeID"] = SecurityStatusEventTypeIds.KillPirateNpc,
                    ["otherOwnerID"] = window.NpcCorporationId,
                    ["otherTypeID"] = window.NpcTypeId,
                    ["locationID"] = window.SolarSystemId,
                    ["modification"] = modification,
                    ["sourceEventID"] = window.SourceEventId,
                    ["npcShipID"] = window.NpcShipId,
                    ["npcFactionID"] = window.NpcFactionId,
                    ["profileID"] = window.ProfileId,
                    ["bounty"] = window.Bounty,
                    ["fastTalkMultiplier"] = fastTalk,
                }, cancellationToken: cancellationToken).ConfigureAwait(false);
            await _events.PublishAsync(context.Connection, context.Transaction,
                "npc.security_status.recovered",
                new { characterId = window.CharacterId, status = value },
                $"character:{window.CharacterId}", cancellationToken).ConfigureAwait(false);
        }

        await _crimewatch.DeleteNpcSecurityStatusRecoveryWindowAsync(
            context.Connection, context.Transaction, window.CharacterId, window.WindowEndMs, cancellationToken).ConfigureAwait(false);
    }

    private static string WindowKey(NpcSecurityStatusRecoveryWindowDue due) => $"{due.CharacterId}:{due.WindowEndMs}";

    private static long ReadInt64(JsonElement payload, string name) =>
        payload.TryGetProperty(name, out var value) && value.TryGetInt64(out var number) ? number : 0;

    private static string? ReadString(JsonElement payload, string name) =>
        payload.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
