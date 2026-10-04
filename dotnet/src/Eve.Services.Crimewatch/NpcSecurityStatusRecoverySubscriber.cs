using System.Text.Json;

namespace Eve.Services.Crimewatch;

/// <summary>Values needed to atomically record one NPC security-status recovery.</summary>
public sealed record NpcSecurityStatusRecoveryChange(
    long CharacterId,
    double PreviousStatus,
    double NextStatus,
    long SourceEventId,
    long NpcShipId,
    long NpcTypeId,
    string ProfileId,
    decimal Bounty,
    double FastTalkMultiplier);

/// <summary>
/// Handles the payload of the durable <c>npc.killed</c> event without owning Host or PostgreSQL types.
/// Host binds the callbacks to EventContext.Connection/Transaction and Eve.Relations, and supplies
/// the EVE.js calculation from <c>Eve.Npc.NpcSecurityStatusRecovery.TryNext</c>.
/// </summary>
public sealed class NpcSecurityStatusRecoverySubscriber<TContext>
{
    public const string SubscriberName = "crimewatch.npc-security-status-recovery";
    public const string Topic = "npc.killed";
    public const string HistoryReason = "npc-kill-security-status-recovery";

    private readonly Func<string, decimal?> _bountyForProfile;
    private readonly Func<TContext, long, CancellationToken, Task<double>> _fastTalkMultiplier;
    private readonly Func<double, decimal, double, double?> _nextStatus;
    private readonly Func<TContext, long, CancellationToken, Task<double>> _statusOf;
    private readonly Func<TContext, NpcSecurityStatusRecoveryChange, CancellationToken, Task> _writeStatus;

    public NpcSecurityStatusRecoverySubscriber(
        Func<string, decimal?> bountyForProfile,
        Func<TContext, long, CancellationToken, Task<double>> fastTalkMultiplier,
        Func<double, decimal, double, double?> nextStatus,
        Func<TContext, long, CancellationToken, Task<double>> statusOf,
        Func<TContext, NpcSecurityStatusRecoveryChange, CancellationToken, Task> writeStatus)
    {
        _bountyForProfile = bountyForProfile;
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

        var bounty = _bountyForProfile(profileId);
        if (bounty is not { } amount || amount <= 0)
        {
            return;
        }

        var fastTalk = await _fastTalkMultiplier(context, characterId, cancellationToken).ConfigureAwait(false);
        fastTalk = double.IsFinite(fastTalk) ? Math.Max(0, fastTalk) : 1.0;
        var previous = await _statusOf(context, characterId, cancellationToken);
        var next = _nextStatus(previous, amount, fastTalk);
        if (next is not { } value)
        {
            return;
        }
        if (!double.IsFinite(value))
        {
            throw new InvalidOperationException("NPC security-status recovery produced a non-finite value");
        }

        await _writeStatus(context, new NpcSecurityStatusRecoveryChange(
            characterId,
            previous,
            value,
            sourceEventId,
            ReadInt64(payload, "shipId"),
            ReadInt64(payload, "typeId"),
            profileId,
            amount,
            fastTalk), cancellationToken);
    }

    private static long ReadInt64(JsonElement payload, string name) =>
        payload.TryGetProperty(name, out var value) && value.TryGetInt64(out var number) ? number : 0;

    private static string? ReadString(JsonElement payload, string name) =>
        payload.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
