using Eve.Events;
using Eve.Ships;
using Npgsql;

namespace Eve.Services.Space;

/// <summary>The event bus as the fact bus of the cycles: the facts are written in the grant's transaction, and the pump is woken after the commit.</summary>
public sealed class EventBusFacts(EventBus bus) : IFactBus
{
    public async Task PublishInTransactionAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string topic, object payload, string key, CancellationToken cancellationToken = default) =>
        await bus.PublishInTransactionAsync(connection, transaction, topic, payload, key, cancellationToken);

    public void Wake() => bus.Wake();
}
