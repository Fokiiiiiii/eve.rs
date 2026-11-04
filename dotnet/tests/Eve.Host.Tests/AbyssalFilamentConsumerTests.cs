using Eve.Data;
using Eve.Inventory;
using Eve.Marshal;
using Eve.Services;
using Eve.Sde;
using Npgsql;
using Xunit;

namespace Eve.Host.Tests;

public sealed class AbyssalFilamentConsumerTests
{
    private const long Character = 41;
    private const long OtherCharacter = 42;
    private const long Ship = 51;
    private const long Corporation = 61;
    private const int CalmExoticFilament = 47761;
    private const int Merlin = 603;

    [PgFact]
    public async Task Consuming_a_stack_uses_the_callers_transaction_and_removes_only_the_required_units()
    {
        var url = Environment.GetEnvironmentVariable("EVE_PG_URL")
            ?? throw new InvalidOperationException("EVE_PG_URL is not set");
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(url));
        var schema = "abyssal_consume_" + Guid.NewGuid().ToString("N");
        await PgItemStore.MigrateAsync(source, schema);
        var items = new PgItemStore(source, schema);
        var types = new ItemTypeCatalog(new SdeReader(source));
        Assert.Equal(1979, (await types.GetAsync(CalmExoticFilament))!.GroupId);
        var consumer = new AbyssalFilamentConsumer(items, types);

        long filamentId;
        await using (var connection = await source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            filamentId = await items.CreateAsync(connection, transaction,
                new NewItem(CalmExoticFilament, Character, Ship, ItemFlags.Cargo, Quantity: 5));
            await transaction.CommitAsync();
        }

        await using (var connection = await source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await consumer.ConsumeAsync(connection, transaction,
                new AbyssalFilamentConsumptionRequest(filamentId, Character, Corporation, Ship, 3));
            await transaction.RollbackAsync();
        }
        Assert.Equal(5, (await items.GetAsync(filamentId))!.Quantity);

        await using (var connection = await source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await consumer.ConsumeAsync(connection, transaction,
                new AbyssalFilamentConsumptionRequest(filamentId, Character, Corporation, Ship, 3));
            await transaction.CommitAsync();
        }
        Assert.Equal(2, (await items.GetAsync(filamentId))!.Quantity);

        await using (var connection = await source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await consumer.ConsumeAsync(connection, transaction,
                new AbyssalFilamentConsumptionRequest(filamentId, Character, Corporation, Ship, 2));
            await transaction.CommitAsync();
        }
        Assert.Null(await items.GetAsync(filamentId));
    }

    [PgFact]
    public async Task A_valid_filament_in_the_wrong_custody_or_a_non_filament_type_is_not_consumed()
    {
        var url = Environment.GetEnvironmentVariable("EVE_PG_URL")
            ?? throw new InvalidOperationException("EVE_PG_URL is not set");
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(url));
        var schema = "abyssal_custody_" + Guid.NewGuid().ToString("N");
        await PgItemStore.MigrateAsync(source, schema);
        var items = new PgItemStore(source, schema);
        var consumer = new AbyssalFilamentConsumer(items, new ItemTypeCatalog(new SdeReader(source)));

        long wrongOwnerId;
        long wrongTypeId;
        await using (var connection = await source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            wrongOwnerId = await items.CreateAsync(connection, transaction,
                new NewItem(CalmExoticFilament, OtherCharacter, Ship, ItemFlags.Cargo, Quantity: 1));
            wrongTypeId = await items.CreateAsync(connection, transaction,
                new NewItem(Merlin, Character, Ship, ItemFlags.Cargo, Quantity: 1));
            await transaction.CommitAsync();
        }

        await using (var connection = await source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            var ownerError = await Assert.ThrowsAsync<MachoWrappedException>(() => consumer.ConsumeAsync(connection, transaction,
                new AbyssalFilamentConsumptionRequest(wrongOwnerId, Character, Corporation, Ship, 1)));
            Assert.Equal(AbyssErrorPayload(4), ownerError.Error.Payload);
            await transaction.RollbackAsync();
        }
        await using (var connection = await source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            var typeError = await Assert.ThrowsAsync<MachoWrappedException>(() => consumer.ConsumeAsync(connection, transaction,
                new AbyssalFilamentConsumptionRequest(wrongTypeId, Character, Corporation, Ship, 1)));
            Assert.Equal(AbyssErrorPayload(3), typeError.Error.Payload);
            await transaction.RollbackAsync();
        }

        Assert.Equal(1, (await items.GetAsync(wrongOwnerId))!.Quantity);
        Assert.Equal(1, (await items.GetAsync(wrongTypeId))!.Quantity);
    }

    private static PyValue AbyssErrorPayload(int code) =>
        MachoErrors.ThrowUserError("AbyssError", ("errors",
            new PyList([new PyTuple([new PyInt(code), new PyTuple([])])]))).Error.Payload;
}
