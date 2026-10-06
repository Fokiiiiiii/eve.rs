using System.Text.Json;
using Eve.Crimewatch;

namespace Eve.Services.Crimewatch;

/// <summary>Values needed to atomically record one NPC security-status recovery.</summary>
/// <summary>Authoritative NPC profile facts needed to annotate the persisted security-status history.</summary>
public sealed record NpcSecurityStatusProfile(decimal Bounty, long CorporationId, int FactionId, int ShipTypeId);

public sealed record NpcSecurityStatusRecoveryChange(
    long CharacterId,
    double PreviousStatus,
    double NextStatus,
    long SourceEventId,
    long SolarSystemId,
    long NpcShipId,
    long NpcTypeId,
    long NpcCorporationId,
    int NpcFactionId,
    string ProfileId,
    decimal Bounty,
    double FastTalkMultiplier,
    double Modification)
{
    public int EventTypeID => SecurityStatusEventTypeIds.KillPirateNpc;
}

/// <summary>
/// Handles the payload of the durable <c>npc.killed</c> event without owning Host or PostgreSQL types.
/// Host binds the callbacks to EventContext.Connection/Transaction and Eve.Relations, and supplies
/// the EVE.js calculation from <c>Eve.Npc.NpcSecurityStatusRecovery.TryNext</c>.
/// </summary>
public sealed class NpcSecurityStatusRecoverySubscriber<TContext>
{
    public const string SubscriberName = "crimewatch.npc-security-status-recovery";
    public const string Topic = "npc.killed";
    public const string HistoryReason = "npc_bounty_recovery";

    private readonly Func<string, NpcSecurityStatusProfile?> _profileForId;
    private readonly Func<TContext, long, CancellationToken, Task<double>> _fastTalkMultiplier;
    private readonly Func<double, decimal, double, double?> _nextStatus;
    private readonly Func<TContext, long, CancellationToken, Task<double>> _statusOf;
    private readonly Func<TContext, NpcSecurityStatusRecoveryChange, CancellationToken, Task> _writeStatus;

    public NpcSecurityStatusRecoverySubscriber(
        Func<string, NpcSecurityStatusProfile?> profileForId,
        Func<TContext, long, CancellationToken, Task<double>> fastTalkMultiplier,
        Func<double, decimal, double, double?> nextStatus,
        Func<TContext, long, CancellationToken, Task<double>> statusOf,
        Func<TContext, NpcSecurityStatusRecoveryChange, CancellationToken, Task> writeStatus)
    {
        _profileForId = profileForId;
        _fastTalkMultiplier = fastTalkMultiplier;
        _nextStatus = nextStatus;
        _statusOf = statusOf;
        _writeStatus = writeStatus;
    }

    /// <summary>
    /// Processes one EventBus delivery. Register this method for <see cref="Topic"/> under
    /// <see cref="SubscriberName"/>. The status read and write callbacks must use the event's
    /// caller-owned transaction; EventBus commits that transaction with its subscriber cursor.
    /// </summary>
    public async Task HandleAsync(
        TContext context,
        long sourceEventId,
        string payloadJson,
        CancellationToken cancellationToken = default)
    {
        using var document = JsonDocument.Parse(payloadJson);
        var payload = document.RootElement;
        var characterId = ReadInt64(payload, "characterId");
        var profileId = ReadString(payload, "profile");
        if (characterId <= 0 || string.IsNullOrWhiteSpace(profileId))
        {
            return;
        }

        var profile = _profileForId(profileId);
        if (profile is not { Bounty: > 0 } npc)
        {
            return;
        }

        var fastTalk = await _fastTalkMultiplier(context, characterId, cancellationToken).ConfigureAwait(false);
        fastTalk = double.IsFinite(fastTalk) ? Math.Max(0, fastTalk) : 1.0;
        var previous = await _statusOf(context, characterId, cancellationToken);
        var next = _nextStatus(previous, npc.Bounty, fastTalk);
        if (next is not { } value)
        {
            return;
        }
        if (!double.IsFinite(value) || !double.IsFinite(previous) || previous >= 10)
        {
            throw new InvalidOperationException("NPC security-status recovery produced invalid history values");
        }

        // TryNext returns its unrounded next status; invert its exact formula to recover the multiplier.
        var modification = (value - previous) / (10 - previous);
        var npcTypeId = ReadInt64(payload, "typeId");
        if (npcTypeId <= 0)
        {
            npcTypeId = npc.ShipTypeId;
        }
        await _writeStatus(context, new NpcSecurityStatusRecoveryChange(
            characterId,
            previous,
            value,
            sourceEventId,
            ReadInt64(payload, "solarSystemId"),
            ReadInt64(payload, "shipId"),
            npcTypeId,
            npc.CorporationId,
            npc.FactionId,
            profileId,
            npc.Bounty,
            fastTalk,
            modification), cancellationToken);
    }

    private static long ReadInt64(JsonElement payload, string name) =>
        payload.TryGetProperty(name, out var value) && value.TryGetInt64(out var number) ? number : 0;

    private static string? ReadString(JsonElement payload, string name) =>
        payload.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
