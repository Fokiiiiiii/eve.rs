using System.Diagnostics;
using System.Text;
using Npgsql;

namespace Eve.Market.Tests;

/// <summary>
/// Opt-in benchmark: runs only with EVE_PG_URL and EVE_MARKET_BENCH=1 (a skipped test is not a pass). It is not part of the default
/// run and CI does not set the variable. EVE_MARKET_BENCH_OUT, when set, is the file the numbers are written to.
/// </summary>
public sealed class BenchFactAttribute : FactAttribute
{
    public BenchFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("EVE_PG_URL") is null) Skip = "EVE_PG_URL is not set";
        else if (Environment.GetEnvironmentVariable("EVE_MARKET_BENCH") != "1") Skip = "opt-in benchmark: set EVE_MARKET_BENCH=1";
    }
}

public sealed class OrderBookBenchmarkTests
{
    /// <summary>The resting orders on one type in one region (M7: N=10000).</summary>
    private const int RestingOrders = 10000;
    /// <summary>The placements measured; each buys one unit from the cheapest ask, so the book keeps its size.</summary>
    private const int Placements = 20;
    /// <summary>The bound the design asks for: the rows one candidate fetch may lock (MarketTrading.MatchBatchSize).</summary>
    private const int CandidateBound = 50;
    /// <summary>A generous per-placement budget (median); the rows bound is the deterministic assertion.</summary>
    private const double MedianBudgetMs = 1000;

    [BenchFact]
    public async Task Placement_on_a_10000_order_book_locks_a_bounded_number_of_rows()
    {
        await using var r = await TradeRig.Create();
        var buyer = await r.Actor("Bench Buyer",1_000_000m);
        await SeedBookAsync(r);
        var latencies = new List<double>();
        var locked = new List<long>();
        for (var i = 0; i < Placements; i++)
        {
            var before = r.Trading.CandidateRowsLocked;
            var clock = Stopwatch.StartNew();
            var receipt = await r.Trading.PlaceAsync(r.Buy(buyer,200m,1),$"bench-buy-{i}");
            clock.Stop();
            Assert.Equal(1,receipt.FilledQuantity);
            latencies.Add(clock.Elapsed.TotalMilliseconds);
            locked.Add(r.Trading.CandidateRowsLocked - before);
        }
        var bookClock = Stopwatch.StartNew();
        var book = await r.Book.BookAsync(r.Station.RegionId,34,r.Now);
        bookClock.Stop();

        var report = new StringBuilder();
        report.AppendLine($"orders={RestingOrders} placements={Placements} candidate_bound={CandidateBound}");
        report.AppendLine($"placement_ms p50={Percentile(latencies,0.5):F1} p95={Percentile(latencies,0.95):F1} max={latencies.Max():F1}");
        report.AppendLine($"rows_locked_per_call min={locked.Min()} p50={Percentile(locked,0.5):F0} max={locked.Max()}");
        report.AppendLine($"book_read ms={bookClock.Elapsed.TotalMilliseconds:F1} asks={book.Asks.Count} bids={book.Bids.Count}");
        Emit(report.ToString());

        Assert.All(locked,n => Assert.True(n <= CandidateBound,$"a placement locked {n} order rows"));
        Assert.True(Percentile(latencies,0.5) <= MedianBudgetMs,$"median placement {Percentile(latencies,0.5):F1} ms");
    }

    private static async Task SeedBookAsync(TradeRig r)
    {
        var location = MarketLocation.From(r.Station);
        await using var command = r.Source.CreateCommand($"""
            INSERT INTO {r.MarketSchema}.orders(id,type_id,bid,price,vol_remaining,vol_entered,min_volume,range_value,station_id,solar_system_id,
                constellation_id,region_id,issued_ms,duration_days,is_npc,seed_key)
            SELECT nextval('{r.MarketSchema}.order_ids'),34,false,(10+i*0.01)::numeric(20,2),10,10,1,32767,$1,$2,$3,$4,1800000000000,3650,true,'bench:'||i
            FROM generate_series(0,{RestingOrders - 1}) AS i
            """);
        command.Parameters.Add(new NpgsqlParameter { Value = location.StationId });
        command.Parameters.Add(new NpgsqlParameter { Value = location.SolarSystemId });
        command.Parameters.Add(new NpgsqlParameter { Value = location.ConstellationId });
        command.Parameters.Add(new NpgsqlParameter { Value = location.RegionId });
        await command.ExecuteNonQueryAsync();
    }

    private static double Percentile(IEnumerable<double> values, double p)
    {
        var sorted = values.OrderBy(v => v).ToArray();
        return sorted[(int)Math.Ceiling(p * sorted.Length) - 1];
    }

    private static double Percentile(IEnumerable<long> values, double p) => Percentile(values.Select(v => (double)v), p);

    private static void Emit(string text)
    {
        var path = Environment.GetEnvironmentVariable("EVE_MARKET_BENCH_OUT");
        if (path is null) Console.WriteLine(text);
        else File.AppendAllText(path, text);
    }
}
