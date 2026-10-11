using System.Text.Json.Nodes;
using Eve.Agents;
using Eve.Characters;
using Eve.Services;
using Eve.Services.Core;
using Eve.Universe;
using Eve.Wallet;
using Npgsql;

namespace Eve.Host;

/// <summary>
/// The locator agents' server side (AG-4 / A7): the pilot by name, the pilot's place from the session that is online, the place of a
/// system from the SDE, and the delivery of a located result. The fee and the located time are one transaction, and the result is sent
/// after the commit. A result that cannot be written refunds the fee, so a pilot pays only for a result it gets.
/// </summary>
internal sealed class PersistenceAgentLocator(
    NpgsqlDataSource source,
    LocateStore locates,
    PgCharacterStore characters,
    CharacterSessions sessions,
    SolarSystemCatalog systems,
    WalletTransactions wallets,
    Func<long> nowMs,
    Func<long, long, JsonObject, string, CancellationToken, Task> sendResult) : IAgentLocatorPort
{
    /// <summary>The journal reference type of the locate fee (eve-logic appConst.py refAgentLocationServices).</summary>
    public const int LocateFeeRefType = 27;

    public LocateStore Locates => locates;

    public async ValueTask<long?> FindCharacterAsync(string name, CancellationToken cancellationToken) =>
        await characters.FindIdByNameAsync(name, cancellationToken);

    public ValueTask<LocateTarget?> TargetAsync(long characterId, CancellationToken cancellationToken)
    {
        var session = sessions.Of(characterId);
        var system = session?.GetInt("solarsystemid2") ?? session?.GetInt("solarsystemid");
        return ValueTask.FromResult<LocateTarget?>(system is > 0 ? new LocateTarget(system.Value, session!.DockedLocationId ?? 0) : null);
    }

    public ValueTask<LocatePlace?> PlaceAsync(long solarSystemId, CancellationToken cancellationToken) =>
        ValueTask.FromResult<LocatePlace?>(systems.Get((int)solarSystemId) is { } system ? new LocatePlace(system.ConstellationId, system.RegionId) : null);

    public async ValueTask<bool> DeliverAsync(long characterId, long agentId, long fee, JsonObject result, CancellationToken cancellationToken)
    {
        var now = nowMs();
        var reference = $"locate:{characterId}:{agentId}:{now}";
        WalletSettlement settlement;
        await using (var connection = await source.OpenConnectionAsync(cancellationToken))
        await using (var transaction = await connection.BeginTransactionAsync(cancellationToken))
        {
            try
            {
                settlement = await wallets.AdjustAsync(connection, transaction, new WalletAccount(characterId), -fee, 0,
                    LocateFeeRefType, "Locate fee", now, reference, reference, cancellationToken);
            }
            catch (Exception exception) when (exception is InsufficientFundsException or WalletNotFoundException)
            {
                return false;
            }
            await locates.MarkLocatedAsync(connection, transaction, characterId, agentId, DateTimeOffset.FromUnixTimeMilliseconds(now), cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        Announce(settlement);

        try
        {
            await sendResult(characterId, agentId, result, reference, cancellationToken);
        }
        catch
        {
            await RefundAsync(characterId, fee, now, reference, cancellationToken);
            throw;
        }
        return true;
    }

    private async Task RefundAsync(long characterId, long fee, long now, string reference, CancellationToken cancellationToken)
    {
        var refund = $"{reference}:refund";
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var settlement = await wallets.AdjustAsync(connection, transaction, new WalletAccount(characterId), fee, 0,
            LocateFeeRefType, "Locate refund", now, refund, refund, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        Announce(settlement);
    }

    private void Announce(WalletSettlement settlement)
    {
        foreach (var entry in settlement.Entries)
            wallets.AnnounceCommitted(new WalletBalanceChange(entry.OwnerId, entry.AccountKey, entry.Balance));
    }
}
