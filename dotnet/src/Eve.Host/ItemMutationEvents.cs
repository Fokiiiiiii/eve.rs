using Eve.Events;
using Eve.Inventory.Mutation;
using Npgsql;

namespace Eve.Host;

/// <summary>The Event Bus as the mutation authority announces its mutations (<see cref="ItemMutationService.MutatedTopic"/>): the composition root's port.</summary>
public sealed class BusItemMutationEvents(EventBus bus) : IItemMutationEvents
{
    public async Task PublishMutatedAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, ItemMutationService.ItemMutated payload, string key, CancellationToken cancellationToken)
    {
        await bus.PublishAsync(connection, transaction, ItemMutationService.MutatedTopic, payload, key, cancellationToken);
    }

    public void Wake() => bus.Wake();
}
