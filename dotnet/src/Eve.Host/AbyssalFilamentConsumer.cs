using Eve.Inventory;
using Eve.Marshal;
using Eve.Services;
using Npgsql;

namespace Eve.Host;

public sealed record AbyssalFilamentConsumptionRequest(
    long ItemId,
    long CharacterId,
    long? CorporationId,
    long ShipId,
    int RequiredQuantity);

/// <summary>Consumes the catalog-validated filament from the active ship's cargo in the caller's transaction.</summary>
public sealed class AbyssalFilamentConsumer(PgItemStore items, ItemTypeCatalog itemTypes)
{
    public const int FilamentGroupId = 1979;

    public async Task<Item> ValidateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        AbyssalFilamentConsumptionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.ItemId <= 0 || request.CharacterId <= 0 || request.ShipId <= 0 || request.RequiredQuantity <= 0)
            throw new ArgumentOutOfRangeException(nameof(request));

        var item = await items.LockAndGetAsync(connection, transaction, request.ItemId, cancellationToken);
        if (item is null)
            throw AbyssalErrorAdapter.ClientError(7); // KEY_NOT_FOUND

        var itemType = await itemTypes.GetAsync(item.TypeId, cancellationToken);
        if (itemType?.GroupId != FilamentGroupId)
            throw AbyssalErrorAdapter.ClientError(3); // INVALID_KEY_TYPE

        if (item.OwnerId != request.CharacterId &&
            (request.CorporationId is not { } corporationId || item.OwnerId != corporationId))
            throw AbyssalErrorAdapter.ClientError(4);

        if (item.LocationId != request.ShipId || item.Flag != ItemFlags.Cargo)
            throw AbyssalErrorAdapter.ClientError(6, new PyInt(item.TypeId)); // ITEM_NOT_REACHABLE

        if (item.Quantity < request.RequiredQuantity)
            throw AbyssalErrorAdapter.ClientError(31, new PyInt(request.RequiredQuantity)); // INVALID_KEY_AMOUNT

        return item;
    }

    public async Task<Item> ConsumeAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        AbyssalFilamentConsumptionRequest request,
        CancellationToken cancellationToken = default)
    {
        var item = await ValidateAsync(connection, transaction, request, cancellationToken);
        if (item.Quantity == request.RequiredQuantity)
            await items.DeleteAsync(connection, transaction, item.Id, cancellationToken);
        else
            await items.SetQuantityAsync(
                connection,
                transaction,
                item.Id,
                item.Quantity - request.RequiredQuantity,
                cancellationToken);

        return item;
    }
}
