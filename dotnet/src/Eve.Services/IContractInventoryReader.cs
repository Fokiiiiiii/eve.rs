namespace Eve.Services;

public interface IContractInventoryReader
{
    ValueTask<IReadOnlyList<ItemRow>> GetContractDockableItemsAsync(
        ClientSession session, long stationId, bool forCorporation, CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<ItemRow>> GetContractContainerItemsAsync(
        ClientSession session, long stationId, long containerId, bool forCorporation, int flagId,
        CancellationToken cancellationToken);
}
