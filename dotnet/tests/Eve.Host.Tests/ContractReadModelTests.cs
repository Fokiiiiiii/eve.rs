using Eve.Data;
using Eve.Host.Persistence;
using Npgsql;
using Xunit;

namespace Eve.Host.Tests;

public sealed class ContractReadModelTests
{
    [Fact]
    public async Task Login_and_escrow_reads_use_persisted_contract_rows_and_owner_scopes()
    {
        var connectionString = Environment.GetEnvironmentVariable("EVE_PG_URL")
            ?? throw new InvalidOperationException("EVE_PG_URL must name the assigned PostgreSQL lane");
        var schema = $"contract_test_{Guid.NewGuid():N}";
        await using var source = NpgsqlDataSource.Create(PgConnection.ToConnectionString(connectionString));
        await PersistenceContractReadModel.MigrateAsync(source, schema);
        try
        {
            var now = 134_000_000_000_000_000L;
            await SeedAsync(source, schema, now);
            var store = new PersistenceContractReadModel(source, schema);

            var login = await store.GetLoginInfoAsync(100, 200, 300, now, CancellationToken.None);
            Assert.Equal(new[] { new Eve.Services.Core.ContractNeedsAttentionRow(3, 1), new Eve.Services.Core.ContractNeedsAttentionRow(4, 0), new Eve.Services.Core.ContractNeedsAttentionRow(5, 0) }, login.NeedsAttention);
            Assert.Equal(new[] { new Eve.Services.Core.ContractInProgressRow(2, 3001, 3002, now + 4 * 864_000_000_000L) }, login.InProgress);
            Assert.Equal(new[] { new Eve.Services.Core.ContractAssignedToMeRow(1, 100), new Eve.Services.Core.ContractAssignedToMeRow(7, 107) }, login.AssignedToMe);

            Assert.Equal(new Eve.Services.Core.ContractEscrowSummary(22m, 3),
                await store.GetMyContractEscrowAsync(100, 200, CancellationToken.None));
            Assert.Equal(new Eve.Services.Core.ContractEscrowSummary(27m, 1),
                await store.GetMyContractEscrowAsync(101, 200, CancellationToken.None));
        }
        finally
        {
            await using var cleanup = source.CreateCommand($"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE");
            await cleanup.ExecuteNonQueryAsync();
        }
    }

    private static async Task SeedAsync(NpgsqlDataSource source, string schema, long now)
    {
        await using var connection = await source.OpenConnectionAsync();
        await using var command = new NpgsqlCommand($"""
            INSERT INTO "{schema}".contract_records
                (contract_id, issuer_id, issuer_corporation_id, is_corporation_issued, acceptor_id, assignee_id,
                 contract_type, status, start_station_id, end_station_id, date_accepted_filetime,
                 date_expired_filetime, num_days, reward_escrow, created_at_filetime)
            VALUES
                (1, 100, 200, false, 0, 100, 1, 0, 0, 0, 0, $1, 0, 10, $2),
                (2, 101, 200, false, 100, 0, 3, 1, 3001, 3002, $2 - 864000000000, $1, 5, 20, $2),
                (3, 101, 200, true, 0, 0, 1, 7, 0, 0, 0, $1, 0, 7, $2),
                (4, 102, 200, false, 100, 0, 1, 6, 0, 0, 0, $1, 0, 2, $2),
                (5, 100, 200, false, 0, 0, 1, 0, 0, 0, 0, $3, 0, 5, $2),
                (6, 106, 200, false, 0, 400, 2, 0, 0, 0, 0, $1, 0, 0, $2),
                (7, 107, 200, false, 0, 300, 2, 0, 0, 0, 0, $1, 0, 0, $2)
            """, connection);
        command.Parameters.AddWithValue(now + 10_000_000L);
        command.Parameters.AddWithValue(now);
        command.Parameters.AddWithValue(now - 1L);
        await command.ExecuteNonQueryAsync();

        await using var items = new NpgsqlCommand($"""
            INSERT INTO "{schema}".contract_items (contract_id, item_id, type_id, quantity, in_crate, is_package)
            VALUES (1, 9001, 34, 1, true, false), (1, 9002, 35, 5, false, true), (3, 9003, 36, 1, true, false)
            """, connection);
        await items.ExecuteNonQueryAsync();
    }
}
