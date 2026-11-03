using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using Eve.Host.ProjectDiscovery;

namespace Eve.Host.Tests;

public sealed class ScientificReplayScorerTests
{
    [Fact]
    public void PublishedCovidCohortReproducesAllThirtyPeerF1Values()
    {
        var root = FindFixtures(); var points = new List<ScientificPoint>();
        using (var file = File.OpenRead(Path.Combine(root, "covid-analysis50.csv.gz")))
        using (var gzip = new GZipStream(file, CompressionMode.Decompress))
        using (var reader = new StreamReader(gzip))
        {
            Assert.Equal("\"GATA3\",\"CD4\"", reader.ReadLine());
            string? line;
            while ((line = reader.ReadLine()) is not null)
            { var cells = line.Split(','); points.Add(new(double.Parse(cells[0], CultureInfo.InvariantCulture), double.Parse(cells[1], CultureInfo.InvariantCulture))); }
        }
        Assert.Equal(62452, points.Count);
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "covid-analysis50-gates.json")));
        var labels = new List<int[]>(); var expected = new List<double>();
        foreach (var record in doc.RootElement.EnumerateArray())
        {
            var bounds = record.GetProperty("minMax").EnumerateArray().Select(n => n.GetDouble()).ToArray();
            var polygons = record.GetProperty("polygons").EnumerateArray().Select(poly => (IReadOnlyList<ScientificPoint>)poly.EnumerateArray().Select(p => new ScientificPoint(p[0].GetDouble(), p[1].GetDouble())).ToArray()).ToArray();
            Assert.Equal(2, polygons.Length);
            labels.Add(ScientificReplayScorer.Label(points, new(bounds[0], bounds[1], bounds[2], bounds[3], polygons)));
            expected.Add(record.GetProperty("f1").GetDouble());
        }
        Assert.Equal(30, labels.Count);
        var actual = ScientificReplayScorer.LeaveOneOut(labels, 2);
        for (int i = 0; i < actual.Length; i++) Assert.InRange(Math.Abs(actual[i] - expected[i]), 0, 1e-10);
        // New answers include every cohort member, including an identical member if present.
        Assert.InRange(Math.Abs(ScientificReplayScorer.ScoreNew(labels[0], labels, 2) - (expected[0] * 29 + 1) / 30), 0, 1e-10);
    }
    [Fact]
    public void BoundaryIncludedAndLastOverlappingGateWins()
    {
        IReadOnlyList<ScientificPoint>[] polygons =
        [
            new ScientificPoint[] { new(0,0), new(1,0), new(1,1), new(0,1) },
            new ScientificPoint[] { new(.5,.5), new(1,.5), new(1,1), new(.5,1) }
        ];
        var labels = ScientificReplayScorer.Label([new(0,0), new(.5,.5), new(.25,.25), new(2,2)], new(0,0,1,1,polygons));
        Assert.Equal(new[] { 1, 2, 1, 0 }, labels);
    }
    private static string FindFixtures()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "dotnet", "tests", "Eve.Host.Tests", "fixtures", "project-discovery");
            if (Directory.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Project Discovery research fixtures were not found.");
    }
}
