using System.Text.Json.Nodes;
using Eve.Agents;
using Eve.Characters;
using Eve.Events;
using Eve.Services;
using Eve.Services.Core;
using Eve.Universe;
using Eve.Wallet;
using Npgsql;

namespace Eve.Host;

/// <summary>
/// The located result as an event: the transactional outbox row of the fee. The subscriber writes the notification (type 69).
/// </summary>
public sealed record LocateDelivered(long CharacterId, long AgentId, JsonObject Data)
{
    public const string Topic = "agents.locate_delivered";
}

/// <summary>
/// The locator agents' server side (AG-4 / A7): the pilot by name, the pilot's place from the session that is online, the place of a
/// system from the SDE, and the delivery of a located result. The fee, the cooldown and the result event are one transaction: the result
/// is an outbox row, so a located pilot is always told once the fee is taken (the subscriber retries until the notification is written).
/// </summary>
internal sealed class PersistenceAgentLocator(
    NpgsqlDataSource source,
    LocateStore locates,
    PgCharacterStore characters,
    CharacterSessions sessions,
    SolarSystemCatalog systems,
    WalletTransactions wallets,
    EventBus events,
    Func<long> nowMs) : IAgentLocatorPort
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
            await events.PublishAsync(connection, transaction, LocateDelivered.Topic, new LocateDelivered(characterId, agentId, result),
                characterId.ToString(System.Globalization.CultureInfo.InvariantCulture), cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        Announce(settlement);
        return true;
    }

    private void Announce(WalletSettlement settlement)
    {
        foreach (var entry in settlement.Entries)
            wallets.AnnounceCommitted(new WalletBalanceChange(entry.OwnerId, entry.AccountKey, entry.Balance));
    }
}
