using Eve.Data;
using Eve.Host.Persistence;
using Eve.Inventory;
using Eve.Services.Core;
using Npgsql;
using Xunit;

namespace Eve.Host.Tests;

// Database tests for PersistenceContractReadModel.SearchContractsAsync. Each test seeds its own schema from the rows
// below, and every expected list is worked out by hand from those rows. The caller is character 42 in corporation 200
// and alliance 300. Lists are newest first (contract_id descending) unless a test sorts on another column.
public sealed class ContractSearchReadModelTests
{
    private const long Now = 134_000_000_000_000_000L;
    private const long Future = Now + 864_000_000_000L;
    private const long Expired = Now - 1;
    private const long CallerId = 42;
    private const long CallerCorporation = 200;
    private const long CallerAlliance = 300;

    [Fact]
    public async Task Item_type_category_group_and_name_filters_match_items_in_or_out_of_the_crate()
    {
        await using var fixture = await SearchFixture.CreateAsync();

        Assert.Equal(new long[] { 17, 14, 13, 10 }, Ids(await SearchAsync(fixture, Search() with { ItemTypeIds = [34] })));
        Assert.Equal(new long[] { 17, 14, 13, 12, 11, 10 }, Ids(await SearchAsync(fixture, Search() with { ItemTypeIds = [34, 36] })));
        Assert.Equal(new long[] { 11 }, Ids(await SearchAsync(fixture, Search() with { ItemTypeIds = [35] })));
        Assert.Empty(Ids(await SearchAsync(fixture, Search() with { ItemTypeIds = [999] })));
        Assert.Equal(new long[] { 17, 14, 13, 12, 11, 10 }, Ids(await SearchAsync(fixture, Search() with { ItemCategoryId = 6 })));
        Assert.Equal(new long[] { 11 }, Ids(await SearchAsync(fixture, Search() with { ItemCategoryId = 7 })));
        Assert.Equal(new long[] { 17, 14, 13, 12, 11, 10 }, Ids(await SearchAsync(fixture, Search() with { ItemGroupId = 300 })));
        Assert.Equal(new long[] { 11 }, Ids(await SearchAsync(fixture, Search() with { ItemGroupId = 301 })));
        Assert.Equal(new long[] { 17, 14, 13, 10 }, Ids(await SearchAsync(fixture, Search() with { ItemTypeName = "  rifter " })));
        Assert.Equal(new long[] { 12, 11 }, Ids(await SearchAsync(fixture, Search() with { ItemTypeName = "TRISTAN" })));
    }

    [Fact]
    public async Task Security_class_filter_follows_the_start_system_band()
    {
        await using var fixture = await SearchFixture.CreateAsync();

        Assert.Equal(new long[] { 21, 20, 17, 15, 14, 13, 10 }, Ids(await SearchAsync(fixture, Search() with { SecurityClasses = [2] })));
        Assert.Equal(new long[] { 11 }, Ids(await SearchAsync(fixture, Search() with { SecurityClasses = [1] })));
        Assert.Equal(new long[] { 12 }, Ids(await SearchAsync(fixture, Search() with { SecurityClasses = [0] })));
        Assert.Equal(new long[] { 12, 11 }, Ids(await SearchAsync(fixture, Search() with { SecurityClasses = [0, 1] })));
        Assert.Empty(Ids(await SearchAsync(fixture, Search() with { SecurityClasses = [3] })));
    }

    [Fact]
    public async Task Start_and_end_location_filters_accept_station_system_constellation_and_region_ids()
    {
        await using var fixture = await SearchFixture.CreateAsync();

        Assert.Equal(new long[] { 21, 20, 17, 15, 14, 13, 10 }, Ids(await SearchAsync(fixture, Search() with { LocationId = 6001 })));
        Assert.Equal(new long[] { 21, 20, 17, 15, 14, 13, 10 }, Ids(await SearchAsync(fixture, Search() with { LocationId = 30000001 })));
        Assert.Equal(new long[] { 21, 20, 17, 15, 14, 13, 12, 10 }, Ids(await SearchAsync(fixture, Search() with { LocationId = 20000001 })));
        Assert.Equal(new long[] { 21, 20, 17, 15, 14, 13, 12, 10 }, Ids(await SearchAsync(fixture, Search() with { LocationId = 10000001 })));
        Assert.Equal(new long[] { 11 }, Ids(await SearchAsync(fixture, Search() with { LocationId = 10000002 })));
        Assert.Equal(new long[] { 11 }, Ids(await SearchAsync(fixture, Search() with { EndLocationId = 6001 })));
        Assert.Equal(new long[] { 15 }, Ids(await SearchAsync(fixture, Search() with { EndLocationId = 10000002 })));
        Assert.Equal(new long[] { 15 }, Ids(await SearchAsync(fixture, Search() with { EndLocationId = 20000002 })));
        Assert.Empty(Ids(await SearchAsync(fixture, Search() with { EndLocationId = 30000003 })));
    }

    [Fact]
    public async Task Price_reward_collateral_and_volume_ranges_are_inclusive()
    {
        await using var fixture = await SearchFixture.CreateAsync();

        Assert.Equal(new long[] { 20, 17, 10 }, Ids(await SearchAsync(fixture, Search() with { MinPrice = 800, MaxPrice = 1000 })));
        Assert.Equal(new long[] { 15, 13 }, Ids(await SearchAsync(fixture, Search() with { MinPrice = 0, MaxPrice = 0 })));
        Assert.Equal(new long[] { 15 }, Ids(await SearchAsync(fixture, Search() with { MinReward = 1 })));
        Assert.Equal(new long[] { 21, 20, 17, 14, 13, 12, 11, 10 }, Ids(await SearchAsync(fixture, Search() with { MaxReward = 0 })));
        Assert.Equal(new long[] { 15 }, Ids(await SearchAsync(fixture, Search() with { MinCollateral = 10000 })));
        Assert.Equal(new long[] { 21, 20, 17, 14, 13, 12, 11, 10 }, Ids(await SearchAsync(fixture, Search() with { MaxCollateral = 0 })));
        Assert.Equal(new long[] { 17, 11, 10 }, Ids(await SearchAsync(fixture, Search() with { MinVolume = 10 })));
        Assert.Equal(new long[] { 21, 20, 15, 14, 13, 12 }, Ids(await SearchAsync(fixture, Search() with { MaxVolume = 6 })));
        Assert.Equal(new long[] { 13, 12, 10 }, Ids(await SearchAsync(fixture, Search() with { MinVolume = 5, MaxVolume = 10 })));
    }

    [Fact]
    public async Task Exclude_trade_multiple_and_no_buyout_remove_only_their_own_rows()
    {
        await using var fixture = await SearchFixture.CreateAsync();

        Assert.Equal(new long[] { 21, 20, 17, 15, 13, 12, 11, 10 }, Ids(await SearchAsync(fixture, Search() with { ExcludeTrade = true })));
        Assert.Equal(new long[] { 21, 20, 17, 15, 14, 13, 12, 10 }, Ids(await SearchAsync(fixture, Search() with { ExcludeMultiple = true })));
        Assert.Equal(new long[] { 21, 20, 17, 15, 14, 12, 11, 10 }, Ids(await SearchAsync(fixture, Search() with { ExcludeNoBuyout = true })));
        Assert.Equal(new long[] { 21, 20, 17, 15, 12, 10 }, Ids(await SearchAsync(fixture, Search() with
        {
            ExcludeTrade = true,
            ExcludeMultiple = true,
            ExcludeNoBuyout = true,
        })));
    }

    [Fact]
    public async Task Issuer_and_description_filters_search_title_and_text_without_case()
    {
        await using var fixture = await SearchFixture.CreateAsync();

        Assert.Equal(new long[] { 13, 10 }, Ids(await SearchAsync(fixture, Search() with { IssuerId = 100 })));
        Assert.Empty(Ids(await SearchAsync(fixture, Search() with { IssuerId = 107 })));
        Assert.Equal(new long[] { 10 }, Ids(await SearchAsync(fixture, Search() with { Description = "FAST" })));
        Assert.Equal(new long[] { 13 }, Ids(await SearchAsync(fixture, Search() with { Description = "bid" })));
        Assert.Equal(new long[] { 13, 10 }, Ids(await SearchAsync(fixture, Search() with { Description = "rifter" })));
        Assert.Equal(new long[] { 12 }, Ids(await SearchAsync(fixture, Search() with { Description = "nullsec" })));
        Assert.Empty(Ids(await SearchAsync(fixture, Search() with { Description = "zzz" })));
    }

    [Fact]
    public async Task Availability_scopes_public_personal_corporation_and_alliance_rows()
    {
        await using var fixture = await SearchFixture.CreateAsync();

        Assert.Equal(new long[] { 21, 20, 17, 15, 14, 13, 12, 11, 10 }, Ids(await SearchAsync(fixture, Search())));
        Assert.Equal(new long[] { 15, 14, 13, 12, 11, 10 }, Ids(await SearchAsync(fixture, Search() with { Availability = 0 })));
        Assert.Equal(new long[] { 21 }, Ids(await SearchAsync(fixture, Search() with { Availability = 1 })));
        Assert.Equal(new long[] { 17 }, Ids(await SearchAsync(fixture, Search() with { Availability = 2 })));
        Assert.Equal(new long[] { 20 }, Ids(await SearchAsync(fixture, Search() with { Availability = 3 })));
    }

    [Fact]
    public async Task Availability_never_matches_public_rows_through_a_zero_alliance_id()
    {
        await using var fixture = await SearchFixture.CreateAsync();
        await fixture.SeedCorporationContractWithoutAssigneeAsync();
        // A pilot in no alliance has AllianceId 0, and public rows carry assignee 0: neither is an alliance contract.
        var noAlliance = Search() with { AllianceId = 0 };

        Assert.Empty(Ids(await SearchAsync(fixture, noAlliance with { Availability = 3 })));
        // Row 22 is a corporation row with no assignee, so it is not visible to a pilot in no alliance either.
        Assert.Equal(new long[] { 21, 17, 15, 14, 13, 12, 11, 10 }, Ids(await SearchAsync(fixture, noAlliance)));
    }

    [Fact]
    public async Task Contract_type_filter_matches_one_kind_or_the_item_exchange_and_auction_group()
    {
        await using var fixture = await SearchFixture.CreateAsync();

        Assert.Equal(new long[] { 21, 20, 17, 14, 12, 11, 10 }, Ids(await SearchAsync(fixture, Search() with { ContractType = 1 })));
        Assert.Equal(new long[] { 15 }, Ids(await SearchAsync(fixture, Search() with { ContractType = 3 })));
        Assert.Equal(new long[] { 21, 20, 17, 14, 13, 12, 11, 10 }, Ids(await SearchAsync(fixture, Search() with { ContractType = 10 })));
    }

    [Fact]
    public async Task Sorting_uses_each_column_and_direction_with_contract_id_as_the_tie_break()
    {
        await using var fixture = await SearchFixture.CreateAsync();

        Assert.Equal(new long[] { 21, 20, 17, 15, 14, 13, 12, 11, 10 }, Ids(await SearchAsync(fixture, Search())));
        Assert.Equal(new long[] { 15, 13, 14, 21, 12, 17, 20, 10, 11 }, Ids(await SearchAsync(fixture, Sorted(1, 0))));
        Assert.Equal(new long[] { 11, 10, 20, 17, 12, 21, 14, 15, 13 }, Ids(await SearchAsync(fixture, Sorted(1, 1))));
        Assert.Equal(new long[] { 15, 21, 20, 17, 14, 13, 12, 11, 10 }, Ids(await SearchAsync(fixture, Sorted(8, 1))));
        Assert.Equal(new long[] { 21, 20, 17, 14, 13, 12, 11, 10, 15 }, Ids(await SearchAsync(fixture, Sorted(9, 0))));
        Assert.Equal(new long[] { 17, 11, 10, 12, 13, 21, 20, 15, 14 }, Ids(await SearchAsync(fixture, Sorted(10, 1))));
        Assert.Equal(new long[] { 21, 20, 17, 14, 12, 11, 10, 13, 15 }, Ids(await SearchAsync(fixture, Sorted(7, 0))));
        Assert.Equal(new long[] { 12, 11, 21, 20, 17, 15, 14, 13, 10 }, Ids(await SearchAsync(fixture, Sorted(4, 1))));
        Assert.Equal(new long[] { 21, 20, 17, 15, 14, 13, 10, 11, 12 }, Ids(await SearchAsync(fixture, Sorted(3, 0))));
        Assert.Equal(new long[] { 15, 14, 13, 12, 11, 10, 21, 17, 20 }, Ids(await SearchAsync(fixture, Sorted(11, 0))));
    }

    [Fact]
    public async Task StartNum_pages_the_hand_computed_result_set_and_keeps_the_full_count()
    {
        await using var fixture = await SearchFixture.CreateAsync();

        var second = await SearchAsync(fixture, Search() with { StartNum = 2 });
        Assert.Equal(9, second.NumFound);
        Assert.Equal(new long[] { 17, 15, 14, 13, 12, 11, 10 }, Ids(second));

        var last = await SearchAsync(fixture, Search() with { StartNum = 9 });
        Assert.Equal(9, last.NumFound);
        Assert.Empty(Ids(last));

        var beyond = await SearchAsync(fixture, Search() with { StartNum = 50 });
        Assert.Equal(9, beyond.NumFound);
        Assert.Empty(Ids(beyond));
    }

    [Fact]
    public async Task Search_returns_at_most_100_rows_per_page_and_reports_the_full_count()
    {
        await using var fixture = await SearchFixture.CreateAsync();
        await fixture.SeedBulkContractsAsync();
        var bulk = Search() with { IssuerId = 500, SortBy = 1, SortDirection = 0, HasExplicitSortBy = true };

        var first = await SearchAsync(fixture, bulk);
        Assert.Equal(105, first.NumFound);
        Assert.Equal(100, first.Contracts.Count);
        Assert.Equal(1000L, first.Contracts[0].Contract.ContractId);
        Assert.Equal(1099L, first.Contracts[99].Contract.ContractId);

        var tail = await SearchAsync(fixture, bulk with { StartNum = 100 });
        Assert.Equal(105, tail.NumFound);
        Assert.Equal(new long[] { 1100, 1101, 1102, 1103, 1104 }, Ids(tail));

        var past = await SearchAsync(fixture, bulk with { StartNum = 101 });
        Assert.Equal(new long[] { 1101, 1102, 1103, 1104 }, Ids(past));
    }

    private static ContractSearchQuery Search() => new(
        CharacterId: CallerId,
        CorporationId: CallerCorporation,
        AllianceId: CallerAlliance,
        Availability: null,
        ContractType: 0,
        LocationId: 0,
        EndLocationId: 0,
        IssuerId: 0,
        MinPrice: null,
        MaxPrice: null,
        MinReward: null,
        MaxReward: null,
        MinCollateral: null,
        MaxCollateral: null,
        MinVolume: null,
        MaxVolume: null,
        ExcludeTrade: false,
        ExcludeMultiple: false,
        ExcludeNoBuyout: false,
        Description: null,
        ItemTypeIds: [],
        ItemCategoryId: 0,
        ItemGroupId: 0,
        ItemTypeName: null,
        SecurityClasses: [],
        SearchHint: 0,
        SortBy: 0,
        SortDirection: null,
        HasExplicitSortBy: false,
        StartNum: 0);

    private static ContractSearchQuery Sorted(int sortBy, int sortDirection) =>
        Search() with { SortBy = sortBy, SortDirection = sortDirection, HasExplicitSortBy = true };

    private static async Task<ContractSearchResult> SearchAsync(SearchFixture fixture, ContractSearchQuery query) =>
        await fixture.Store.SearchContractsAsync(query, Now, CancellationToken.None);

    private static long[] Ids(ContractSearchResult result)
    {
        var ids = new System.Collections.Generic.List<long>();
        foreach (var entry in result.Contracts)
            ids.Add(entry.Contract.ContractId);
        return ids.ToArray();
    }

    private sealed class SearchFixture : IAsyncDisposable
    {
        private const string ContractRows = """
            INSERT INTO "@@SCHEMA@@".contract_records
                (contract_id, issuer_id, issuer_corporation_id, is_corporation_issued, acceptor_id, assignee_id,
                 contract_type, status, availability, start_station_id, end_station_id, date_accepted_filetime,
                 date_expired_filetime, num_days, reward_escrow, created_at_filetime, price_isk, reward_isk,
                 collateral_isk, title, description)
            VALUES
                (10, 100, 0, false, 0, 0, 1, 0, 0, 6001, 0, 0, @future, 0, 0, @now, 1000, 0, 0, 'Rifter bundle', 'Fast ship'),
                (11, 101, 0, false, 0, 0, 1, 0, 0, 6002, 6001, 0, @future, 0, 0, @now, 5000, 0, 0, 'Other haul', 'Low sec haul'),
                (12, 102, 0, false, 0, 0, 1, 0, 0, 6003, 0, 0, @future, 0, 0, @now, 200, 0, 0, 'Null goods', 'nullsec'),
                (13, 100, 0, false, 0, 0, 2, 0, 0, 6001, 0, 0, @future, 0, 0, @now, 0, 0, 0, 'Auction rifter', 'bid only'),
                (14, 103, 0, false, 0, 0, 1, 0, 0, 6001, 0, 0, @future, 0, 0, @now, 10, 0, 0, 'Trade', 'trade item'),
                (15, 104, 0, false, 0, 0, 3, 0, 0, 6001, 6002, 0, @future, 0, 0, @now, 0, 7000, 20000, 'Courier run', 'package'),
                (16, 107, 0, false, 0, 999, 1, 0, 1, 6001, 0, 0, @future, 0, 0, @now, 40, 0, 0, 'Private', 'other char'),
                (17, 105, 0, false, 0, 200, 1, 0, 2, 6001, 0, 0, @future, 0, 0, @now, 800, 0, 0, 'Corp stock', 'for corp'),
                (18, 100, 0, false, 0, 0, 1, 0, 0, 6001, 0, 0, @expired, 0, 0, @now, 50, 0, 0, 'Old', 'expired'),
                (19, 100, 0, false, 0, 0, 1, 1, 0, 6001, 0, 0, @future, 0, 0, @now, 60, 0, 0, 'Done', 'completed'),
                (20, 106, 0, false, 0, 300, 1, 0, 3, 6001, 0, 0, @future, 0, 0, @now, 900, 0, 0, 'Alliance thing', 'alliance'),
                (21, 108, 0, false, 0, 42, 1, 0, 1, 6001, 0, 0, @future, 0, 0, @now, 70, 0, 0, 'Mine', 'personal');

            INSERT INTO "@@SCHEMA@@".contract_items (contract_id, item_id, type_id, quantity, in_crate, is_package)
            VALUES (10, 7001, 34, 2, true, false),
                   (11, 7002, 35, 1, true, false),
                   (11, 7003, 36, 1, true, false),
                   (12, 7004, 36, 3, true, false),
                   (13, 7005, 34, 1, true, false),
                   (14, 7006, 34, 1, false, false),
                   (17, 7007, 34, 4, true, false);
            """;

        private const string SdeRows = """
            CREATE SCHEMA "@@SDE@@";
            CREATE TABLE "@@SDE@@".npcStations (ord integer NOT NULL, key bigint PRIMARY KEY, doc jsonb NOT NULL);
            CREATE TABLE "@@SDE@@".mapSolarSystems (ord integer NOT NULL, key bigint PRIMARY KEY, doc jsonb NOT NULL);
            CREATE TABLE "@@SDE@@".types (ord integer NOT NULL, key bigint PRIMARY KEY, doc jsonb NOT NULL);
            CREATE TABLE "@@SDE@@".groups (ord integer NOT NULL, key bigint PRIMARY KEY, doc jsonb NOT NULL);
            INSERT INTO "@@SDE@@".npcStations (ord, key, doc)
            VALUES (0, 6001, '{"solarSystemID":30000001}'::jsonb),
                   (1, 6002, '{"solarSystemID":30000002}'::jsonb),
                   (2, 6003, '{"solarSystemID":30000003}'::jsonb);
            INSERT INTO "@@SDE@@".mapSolarSystems (ord, key, doc)
            VALUES (0, 30000001, '{"constellationID":20000001,"regionID":10000001,"securityStatus":0.8,"name":{"en":"Alpha"}}'::jsonb),
                   (1, 30000002, '{"constellationID":20000002,"regionID":10000002,"securityStatus":0.2,"name":{"en":"Beta"}}'::jsonb),
                   (2, 30000003, '{"constellationID":20000001,"regionID":10000001,"securityStatus":-0.3,"name":{"en":"Gamma"}}'::jsonb);
            INSERT INTO "@@SDE@@".types (ord, key, doc)
            VALUES (0, 34, '{"groupID":300,"volume":5,"name":{"en":"Rifter"}}'::jsonb),
                   (1, 35, '{"groupID":301,"volume":10,"name":{"en":"Other"}}'::jsonb),
                   (2, 36, '{"groupID":300,"volume":2,"name":{"en":"Tristan"}}'::jsonb);
            INSERT INTO "@@SDE@@".groups (ord, key, doc)
            VALUES (0, 300, '{"categoryID":6}'::jsonb),
                   (1, 301, '{"categoryID":7}'::jsonb);
            """;

        private readonly NpgsqlDataSource _source;
        private readonly string _schema;
        private readonly string _sdeSchema;
        private readonly string _inventorySchema;

        private SearchFixture(NpgsqlDataSource source, string schema, string sdeSchema, string inventorySchema)
        {
            _source = source;
            _schema = schema;
            _sdeSchema = sdeSchema;
            _inventorySchema = inventorySchema;
            Store = new PersistenceContractReadModel(source, schema, sdeSchema, inventorySchema);
        }

        public PersistenceContractReadModel Store { get; }

        public static async Task<SearchFixture> CreateAsync()
        {
            var connectionString = Environment.GetEnvironmentVariable("EVE_PG_URL")
                ?? throw new InvalidOperationException("EVE_PG_URL must name the assigned PostgreSQL lane");
            var suffix = Guid.NewGuid().ToString("N");
            var source = NpgsqlDataSource.Create(PgConnection.ToConnectionString(connectionString));
            var fixture = new SearchFixture(source, $"contract_search_{suffix}", $"sde_contract_search_{suffix}",
                $"inventory_contract_search_{suffix}");
            try
            {
                await PersistenceContractReadModel.MigrateAsync(source, fixture._schema);
                await PgItemStore.MigrateAsync(source, fixture._inventorySchema);
                await fixture.ExecuteAsync(SdeRows, Array.Empty<(string, long)>());
                await fixture.ExecuteAsync(ContractRows,
                    [("now", Now), ("future", Future), ("expired", Expired)]);
                return fixture;
            }
            catch
            {
                await fixture.DisposeAsync();
                throw;
            }
        }

        public async Task SeedBulkContractsAsync()
        {
            await ExecuteAsync("""
                INSERT INTO "@@SCHEMA@@".contract_records
                    (contract_id, issuer_id, issuer_corporation_id, is_corporation_issued, acceptor_id, assignee_id,
                     contract_type, status, availability, start_station_id, end_station_id, date_accepted_filetime,
                     date_expired_filetime, num_days, reward_escrow, created_at_filetime, price_isk, reward_isk,
                     collateral_isk, title, description)
                SELECT 1000 + g, 500, 0, false, 0, 0, 1, 0, 0, 6001, 0, 0, @future, 0, 0, @now, g + 1, 0, 0, 'Bulk', 'bulk'
                FROM generate_series(0, 104) AS g;
                """, [("now", Now), ("future", Future)]);
        }

        public async Task SeedCorporationContractWithoutAssigneeAsync()
        {
            await ExecuteAsync("""
                INSERT INTO "@@SCHEMA@@".contract_records
                    (contract_id, issuer_id, issuer_corporation_id, is_corporation_issued, acceptor_id, assignee_id,
                     contract_type, status, availability, start_station_id, end_station_id, date_accepted_filetime,
                     date_expired_filetime, num_days, reward_escrow, created_at_filetime, price_isk, reward_isk,
                     collateral_isk, title, description)
                VALUES (22, 109, 200, true, 0, 0, 1, 0, 2, 6001, 0, 0, @future, 0, 0, @now, 30, 0, 0, 'Corp open', 'no assignee');
                """, [("now", Now), ("future", Future)]);
        }

        private async Task ExecuteAsync(string sql, (string Name, long Value)[] parameters)
        {
            await using var command = _source.CreateCommand(Sql(sql));
            foreach (var (name, value) in parameters)
                command.Parameters.AddWithValue(name, value);
            await command.ExecuteNonQueryAsync();
        }

        private string Sql(string template) => template
            .Replace("@@SCHEMA@@", _schema, StringComparison.Ordinal)
            .Replace("@@SDE@@", _sdeSchema, StringComparison.Ordinal);

        public async ValueTask DisposeAsync()
        {
            try
            {
                await using var cleanup = _source.CreateCommand(
                    $"DROP SCHEMA IF EXISTS \"{_schema}\" CASCADE; DROP SCHEMA IF EXISTS \"{_sdeSchema}\" CASCADE; " +
                    $"DROP SCHEMA IF EXISTS \"{_inventorySchema}\" CASCADE");
                await cleanup.ExecuteNonQueryAsync();
            }
            finally
            {
                await _source.DisposeAsync();
            }
        }
    }
}
