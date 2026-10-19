using System.Globalization;
using System.Text.Json.Nodes;
using Eve.Marshal;
using Eve.GameStore;
using Eve.Identity;
using Eve.Services;
using Eve.Services.Core;
using Eve.Services.Skills;

namespace Eve.Host;

/// <summary>
/// Multiple Character Training as the account's extra training queues: what userSvc reads, and the activation of the certificates the
/// account holds (each one is consumed, and adds 30 days to a queue, in one transaction).
/// </summary>
public sealed partial class PersistenceNewEdenStoreRuntime
{
    /// <summary>
    /// The certificate types: 34133 (Multiple Pilot Training Certificate, the soulbound redeem token) and 63188 (Multiple Pilot Training).

    /// </summary>
    private static readonly int[] TrainingCertificateTypeIds = [34133, 63188];

    /// <summary>Schedules the expiry of each slot granted (in the caller's transaction); see <see cref="Eve.Services.Skills.MultipleTrainingSlotExpiry"/>.</summary>
    public Func<Npgsql.NpgsqlConnection, Npgsql.NpgsqlTransaction, long, DateTimeOffset, CancellationToken, Task>? ScheduleSlotExpiry { get; set; }

    private async Task<(int Slot, DateTimeOffset EndsAt)> GrantTrainingSlotAsync(
        Npgsql.NpgsqlConnection connection, Npgsql.NpgsqlTransaction transaction, long accountId, int? requested, TimeSpan duration, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var granted = await accountDirectory.ExtendTrainingSlotAsync(connection, transaction, checked((uint)accountId), requested, duration, now, cancellationToken);
        if (ScheduleSlotExpiry is { } schedule)
            await schedule(connection, transaction, accountId, granted.EndsAt, cancellationToken);
        return granted;
    }

    public async ValueTask<IReadOnlyList<(int Slot, long EndsAt)>> GetActiveSlotsAsync(long authenticatedUserId, CancellationToken cancellationToken)
    {
        RequireAccount(authenticatedUserId);
        var now = DateTimeOffset.FromUnixTimeMilliseconds(nowUnixMs());
        var slots = await accountDirectory.TrainingSlotsAsync(checked((uint)authenticatedUserId), cancellationToken);
        return [.. slots
            .Where(slot => slot.Value > now)
            .OrderBy(slot => slot.Key)
            .Select(slot => (slot.Key, ToFileTime(slot.Value.ToUnixTimeMilliseconds())))];
    }

    public async ValueTask<IReadOnlyList<(int Slot, long EndsAt)>> ActivateCertificatesAsync(
        long authenticatedUserId, PyValue tokens, int? trainingId, CancellationToken cancellationToken)
    {
        RequireAccount(authenticatedUserId);
        var requested = TokenPairs(tokens);
        if (requested.Length == 0)
            return await GetActiveSlotsAsync(authenticatedUserId, cancellationToken);
        var accountId = checked((uint)authenticatedUserId);
        var accountKey = authenticatedUserId.ToString(CultureInfo.InvariantCulture);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var nowUnix = nowUnixMs();
            var now = DateTimeOffset.FromUnixTimeMilliseconds(nowUnix);
            var state = await PersistenceReadHelpers.ReadOptionalTableAsync(store, RedeemTable, cancellationToken);
            var accounts = state[RedeemRow] as JsonObject ?? new JsonObject();
            var accountTokens = accounts[accountKey] as JsonObject ?? new JsonObject();

            // A token is consumed once: a repeated request finds it gone and adds nothing. Tokens that are not certificates are left alone.
            var consumedKeys = new List<string>();
            var units = 0L;
            foreach (var pair in requested.Distinct())
            {
                var key = pair.TokenId.ToString(CultureInfo.InvariantCulture);
                if (accountTokens[key] is not JsonObject token)
                    continue; // already consumed, or not this account's: nothing to activate
                if (pair.MassTokenId > 0 && Integer(token["massTokenID"]) != pair.MassTokenId)
                    continue;
                if (String(token["status"]) is not ("" or "pending"))
                    continue;
                var soulbound = token["soulbound"] is JsonValue flag && flag.TryGetValue<bool>(out var isSoulbound) && isSoulbound;
                if (!soulbound || Array.IndexOf(TrainingCertificateTypeIds, checked((int)Integer(token["typeID"]))) < 0)
                    continue;
                consumedKeys.Add(key);
                units += Math.Max(1, Integer(token["quantity"], 1));
            }
            if (units == 0)
                return await GetActiveSlotsAsync(authenticatedUserId, cancellationToken);

            var endsAt = await accountDirectory.SubscriptionEndsAtAsync(accountId, cancellationToken)
                ?? throw new InvalidOperationException($"Account {accountId} does not exist.");
            if (endsAt <= now)
                throw new InvalidOperationException("Multiple Character Training can only be activated on an Omega clone.");

            await using (var connection = await source.OpenConnectionAsync(cancellationToken))
            await using (var transaction = await connection.BeginTransactionAsync(cancellationToken))
            {
                for (var unit = 0L; unit < units; unit++)
                    await GrantTrainingSlotAsync(
                        connection, transaction, accountId, trainingId, TimeSpan.FromDays(MultipleTrainingRules.CertificateDays), now, cancellationToken);
                foreach (var key in consumedKeys)
                    accountTokens.Remove(key);
                accounts[accountKey] = accountTokens;
                await store.ApplyChangesAsync(
                    connection, transaction, RedeemTable,
                    [new RowUpsert(RedeemRow, accounts.ToJsonString())], [], ownerFence, cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            }
        }
        finally
        {
            _gate.Release();
        }

        foreach (var session in sessions.OfUser(authenticatedUserId))
            SkillNotifications.MultipleCharactersTrainingUpdated(session);
        return await GetActiveSlotsAsync(authenticatedUserId, cancellationToken);
    }

    public async ValueTask<IReadOnlyList<(int Slot, long EndsAt)>> ActivateItemAsync(
        long authenticatedUserId, long itemId, int? trainingId, CancellationToken cancellationToken)
    {
        RequireAccount(authenticatedUserId);
        var accountId = checked((uint)authenticatedUserId);
        var item = await items.GetAsync(itemId, cancellationToken)
            ?? throw new InvalidOperationException("That item does not exist.");
        var owner = await characters.GetAsync(item.OwnerId, cancellationToken);
        if (owner is null || owner.AccountId != checked((int)authenticatedUserId))
            throw new InvalidOperationException("That item is not held by a character of this account.");
        if (Array.IndexOf(TrainingCertificateTypeIds, item.TypeId) < 0)
            throw new InvalidOperationException("That item is not a Multiple Character Training certificate.");

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var now = DateTimeOffset.FromUnixTimeMilliseconds(nowUnixMs());
            var endsAt = await accountDirectory.SubscriptionEndsAtAsync(accountId, cancellationToken)
                ?? throw new InvalidOperationException($"Account {accountId} does not exist.");
            if (endsAt <= now)
                throw new InvalidOperationException("Multiple Character Training can only be activated on an Omega clone.");

            await using var connection = await source.OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            var current = await items.LockAndGetAsync(connection, transaction, itemId, cancellationToken);
            if (current is null || current.OwnerId != owner.Id || current.TypeId != item.TypeId || current.Quantity < 1)
                throw new InvalidOperationException("That item is no longer there.");
            // One certificate is used from the stack; the rest of the stack stays in place.
            if (current.Quantity > 1)
                await items.SetQuantityAsync(connection, transaction, itemId, current.Quantity - 1, cancellationToken);
            else
                await items.DeleteAsync(connection, transaction, itemId, cancellationToken);
            await GrantTrainingSlotAsync(
                connection, transaction, accountId, trainingId, TimeSpan.FromDays(MultipleTrainingRules.CertificateDays), now, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }

        foreach (var session in sessions.OfUser(authenticatedUserId))
            SkillNotifications.MultipleCharactersTrainingUpdated(session);
        return await GetActiveSlotsAsync(authenticatedUserId, cancellationToken);
    }
}
