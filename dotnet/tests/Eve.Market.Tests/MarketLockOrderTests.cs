using System.Text.RegularExpressions;

namespace Eve.Market.Tests;

/// <summary>
/// The documented lock order of the trading engine (M8; MarketTrading class summary): character and corporation rows, then the book's
/// advisory lock, then the order rows, then the wallets, then the items. This is a source-order guard: it checks that in each entry point
/// the lock acquisitions appear in that order. The concurrency tests exercise the same paths at run time.
/// </summary>
public class MarketLockOrderTests
{
    // the acquisitions of one entry point, in the documented order (a later rank must not appear before an earlier one)
    private static readonly (string Rank, string Pattern)[] Order =
    [
        ("character", @"characters\.LockAndGetAsync\("),
        ("corporation", @"corporations\.MarketAuthorityAsync\("),
        ("book", @":book:\{"),
        ("orders", @"(MatchAsync\(c,tx|GetOrder\(c,tx,[^;]*true)"),
        ("wallets", @"wallets\.LockBalancesAsync\("),
        ("items", @"items\.LockAndGetAsync\("),
    ];

    [Theory]
    [InlineData("PlaceAsync")]
    [InlineData("PlaceBuyOrdersAsync")]
    [InlineData("ModifyAsync")]
    [InlineData("CloseAsync")]
    public void Each_entry_point_takes_its_locks_in_the_documented_order(string method)
    {
        var source = SourceText();
        var body = MethodBody(source, method);
        if (method is "PlaceAsync" or "PlaceBuyOrdersAsync")
        {
            Assert.Contains("PlaceWithinTransactionAsync", body);
            body = MethodBody(source, "PlaceWithinTransactionAsync");
        }
        var previous = -1;
        var previousRank = "";
        foreach (var (rank, pattern) in Order)
        {
            var matches = Regex.Matches(body, pattern);
            if (matches.Count == 0) continue;
            var position = matches[0].Index;
            // the item locks of a fill come after the wallets, in the fill loop; the first occurrence decides the rank
            Assert.True(position > previous, $"{method}: {rank} is locked before {previousRank}");
            previous = position;
            previousRank = rank;
        }
        Assert.True(previous >= 0, $"{method}: no lock found, the pattern list is stale");
    }

    private static string SourceText()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "dotnet", "src", "Eve.Market", "MarketTrading.cs")))
            dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!, "dotnet", "src", "Eve.Market", "MarketTrading.cs"));
    }

    /// <summary>The text of one method: from its signature to the next member at the same indentation.</summary>
    private static string MethodBody(string source, string method)
    {
        var start = Regex.Match(source, @"\n\s*(public|private|internal)[^\n(]*\b" + method + @"\(");
        Assert.True(start.Success, $"{method} not found");
        var declarationEnd = start.Index + start.Length;
        var end = Regex.Match(source.Substring(declarationEnd), @"\n    (public|private|internal|/// |//)");
        return end.Success ? source.Substring(start.Index, declarationEnd + end.Index - start.Index) : source.Substring(start.Index);
    }
}
