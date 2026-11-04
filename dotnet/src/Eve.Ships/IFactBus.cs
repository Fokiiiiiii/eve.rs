using Npgsql;

namespace Eve.Ships;

/// <summary>
/// Where a granted cycle's facts go: published inside the transaction that makes the change, and woken once it is committed. The host implements it on the event
/// bus (<c>EventBusFacts</c> in Eve.Services.Space); the domain module does not reference the bus.
/// </summary>
public interface IFactBus
{
    Task PublishInTransactionAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string topic, object payload, string key, CancellationToken cancellationToken = default);

    void Wake();
}
