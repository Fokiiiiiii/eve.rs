using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Eve.Host.ProjectDiscovery;

/// <summary>Original, code-generated geometry exercises for the five V24 COVID tutorial requests.</summary>
internal static class CovidTutorialCorpus
{
    internal sealed record Region(double Left, double Bottom, double Right, double Top)
    {
        public ScientificPoint[] Vertices =>
        [
            new(Left, Bottom), new(Right, Bottom), new(Right, Top), new(Left, Top),
        ];
    }

    private static readonly IReadOnlyDictionary<string, Region[]> Samples = new Dictionary<string, Region[]>(StringComparer.Ordinal)
    {
        ["ed134308-e080-49bc-be56-169a5f839c96"] = [new(.08, .10, .28, .26), new(.65, .72, .91, .93)],
        ["be362d8b-4ff4-448b-877f-23b8c2cb4fe0"] = [new(.05, .75, .20, .92), new(.42, .34, .56, .50), new(.82, .07, .95, .23)],
        ["eb4c6403-e878-43cf-a009-9c8c566ae90f"] = [new(.08, .10, .25, .28), new(.44, .76, .61, .94), new(.72, .38, .90, .58)],
        ["21cbef0c-684e-4a4c-b1fe-be6e24d894c2"] = [new(.04, .05, .15, .17), new(.33, .08, .46, .22), new(.68, .07, .82, .20), new(.19, .68, .31, .80), new(.80, .73, .93, .88)],
        ["ff9f9a21-954b-4c1c-9a54-16b21cf17670"] = [new(.03, .45, .14, .58), new(.38, .12, .55, .27), new(.62, .76, .78, .92), new(.88, .38, .97, .50)],
    };

    public const int Width = 512;
    public const int Height = 512;
    public const string ProjectId = "eve-rs-geometry-training";
    public const string LocalMode = "Eve.rs-authored geometry training";
    public static IReadOnlyList<string> TutorialCodes => Samples.Keys.ToArray();

    public static bool Contains(string code) => Samples.ContainsKey(code);

    public static JsonElement Task(long actor, string code, long sequence, Uri asset, bool includeSolution = false)
    {
        if (!Samples.TryGetValue(code, out var regions) || sequence <= 0)
            throw new ArgumentException("Unknown Project Discovery tutorial sample.", nameof(code));
        string id = $"eve-rs-pd-tutorial-v1-{actor}-{sequence}";
        var task = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = id,
            ["taskID"] = id,
            ["taskId"] = id,
            ["projectID"] = ProjectId,
            ["isTrainingSet"] = true,
            ["isTutorial"] = true,
            ["localMode"] = LocalMode,
            ["assets"] = new { url = asset.AbsoluteUri, dimensions = new[] { Width, Height } },
            ["url"] = asset.AbsoluteUri,
            ["votes"] = new { highScore = 0.0, avgScore = 0.0 },
            ["classificationCount"] = 0,
        };
        if (includeSolution)
            task["solution"] = new
            {
                clusters = regions.Select(region => new
                {
                    polygons = new[] { new { vertices = region.Vertices.Select(point => new[] { point.X, point.Y }).ToArray(), densityLevel = 1 } },
                }).ToArray(),
            };
        return JsonSerializer.SerializeToElement(task);
    }

    public static byte[] RenderPng(string code, long sequence)
    {
        if (!Samples.TryGetValue(code, out var regions) || sequence <= 0)
            throw new ArgumentException("Unknown Project Discovery tutorial sample.", nameof(code));
        var pixels = new byte[Width * Height];
        Array.Fill(pixels, (byte)12);
        uint seed = Seed(code, sequence);
        foreach (var region in regions)
        {
            double centerX = (region.Left + region.Right) / 2;
            double centerY = (region.Bottom + region.Top) / 2;
            double halfWidth = (region.Right - region.Left) / 2;
            double halfHeight = (region.Top - region.Bottom) / 2;
            for (var index = 0; index < 900; index++)
            {
                // The sum of four uniforms gives each cluster a dense centre and a soft edge.
                double x = centerX + (Next(ref seed) + Next(ref seed) + Next(ref seed) + Next(ref seed) - 2) * halfWidth;
                double y = centerY + (Next(ref seed) + Next(ref seed) + Next(ref seed) + Next(ref seed) - 2) * halfHeight;
                int pixelX = Math.Clamp((int)(x * Width), 0, Width - 1);
                int pixelY = Height - 1 - Math.Clamp((int)(y * Height), 0, Height - 1);
                int offset = pixelY * Width + pixelX;
                pixels[offset] = (byte)Math.Min(byte.MaxValue, pixels[offset] + 82);
            }
        }

        using var raw = new MemoryStream(Height * (Width + 1));
        for (var y = 0; y < Height; y++)
        {
            raw.WriteByte(0);
            raw.Write(pixels, y * Width, Width);
        }
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
            zlib.Write(raw.ToArray());
        using var png = new MemoryStream();
        png.Write([137, 80, 78, 71, 13, 10, 26, 10]);
        Span<byte> header = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, Width);
        BinaryPrimitives.WriteInt32BigEndian(header[4..], Height);
        header[8] = 8;
        header[9] = 0;
        WriteChunk(png, "IHDR"u8, header);
        WriteChunk(png, "IDAT"u8, compressed.ToArray());
        WriteChunk(png, "IEND"u8, ReadOnlySpan<byte>.Empty);
        return png.ToArray();
    }

    public static bool TryScore(string code, JsonElement answer, out double score)
    {
        score = 0;
        if (!Samples.TryGetValue(code, out var regions) || answer.ValueKind != JsonValueKind.Object ||
            !answer.TryGetProperty("skip", out var skip) || skip.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            return false;
        if (skip.GetBoolean())
            return !answer.TryGetProperty("polygons", out var skippedPolygons) ||
                skippedPolygons.ValueKind == JsonValueKind.Array && skippedPolygons.GetArrayLength() == 0;
        if (!answer.TryGetProperty("polygons", out var polygonsJson) || polygonsJson.ValueKind != JsonValueKind.Array ||
            polygonsJson.GetArrayLength() is 0 or > 8)
            return false;

        var actual = new List<ScientificPoint[]>();
        foreach (var polygon in polygonsJson.EnumerateArray())
        {
            if (polygon.ValueKind != JsonValueKind.Object || !polygon.TryGetProperty("vertices", out var vertices) ||
                vertices.ValueKind != JsonValueKind.Array || vertices.GetArrayLength() is < 3 or > 64)
                return false;
            var points = new List<ScientificPoint>();
            foreach (var vertex in vertices.EnumerateArray())
            {
                if (vertex.ValueKind != JsonValueKind.Array || vertex.GetArrayLength() != 2 ||
                    !vertex[0].TryGetDouble(out double x) || !vertex[1].TryGetDouble(out double y) ||
                    !double.IsFinite(x) || !double.IsFinite(y) || x is < 0 or > 1 || y is < 0 or > 1)
                    return false;
                points.Add(new(x, y));
            }
            actual.Add(points.ToArray());
        }

        var referenceMasks = regions.Select(region => Mask(region.Vertices)).ToArray();
        var answerMasks = actual.Select(Mask).ToArray();
        var weights = referenceMasks.Select(reference => answerMasks.Select(answer => IntersectionOverUnion(reference, answer)).ToArray()).ToArray();
        var states = new Dictionary<int, double> { [0] = 0 };
        for (var expected = 0; expected < weights.Length; expected++)
        {
            var next = new Dictionary<int, double>(states);
            foreach (var (mask, value) in states)
            for (var candidate = 0; candidate < answerMasks.Length; candidate++)
            {
                int bit = 1 << candidate;
                if ((mask & bit) != 0) continue;
                int newMask = mask | bit;
                double newValue = value + weights[expected][candidate];
                if (!next.TryGetValue(newMask, out double previous) || newValue > previous)
                    next[newMask] = newValue;
            }
            states = next;
        }
        double total = states.Values.Max() / Math.Max(referenceMasks.Length, answerMasks.Length);
        if (referenceMasks.Length != answerMasks.Length) total = Math.Min(0.49, total);
        score = Math.Round(total, 4, MidpointRounding.AwayFromZero);
        return true;
    }

    private static bool[] Mask(IReadOnlyList<ScientificPoint> polygon)
    {
        const int side = 64;
        var mask = new bool[side * side];
        for (var y = 0; y < side; y++)
        for (var x = 0; x < side; x++)
            mask[y * side + x] = Contains(polygon, new((x + 0.5) / side, (y + 0.5) / side));
        return mask;
    }

    private static double IntersectionOverUnion(bool[] left, bool[] right)
    {
        int intersection = 0, union = 0;
        for (var index = 0; index < left.Length; index++)
        {
            if (left[index] && right[index]) intersection++;
            if (left[index] || right[index]) union++;
        }
        return union == 0 ? 0 : (double)intersection / union;
    }

    private static bool Contains(IReadOnlyList<ScientificPoint> polygon, ScientificPoint point)
    {
        bool inside = false;
        for (var index = 0; index < polygon.Count; index++)
        {
            var a = polygon[index];
            var b = polygon[(index + 1) % polygon.Count];
            if ((a.Y > point.Y) != (b.Y > point.Y) && point.X < (b.X - a.X) * (point.Y - a.Y) / (b.Y - a.Y) + a.X)
                inside = !inside;
        }
        return inside;
    }

    private static uint Seed(string code, long sequence)
    {
        byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"eve-rs-project-discovery:{code}:{sequence}"));
        uint seed = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        return seed == 0 ? 0x9e3779b9u : seed;
    }

    private static double Next(ref uint state)
    {
        state ^= state << 13;
        state ^= state >> 17;
        state ^= state << 5;
        return state / (double)uint.MaxValue;
    }

    private static void WriteChunk(Stream stream, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        stream.Write(length);
        stream.Write(type);
        stream.Write(data);
        uint crc = 0xffffffff;
        foreach (byte value in type) crc = CrcByte(crc, value);
        foreach (byte value in data) crc = CrcByte(crc, value);
        Span<byte> checksum = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(checksum, crc ^ 0xffffffff);
        stream.Write(checksum);
    }

    private static uint CrcByte(uint crc, byte value)
    {
        crc ^= value;
        for (var bit = 0; bit < 8; bit++)
            crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0u : 0xedb88320u);
        return crc;
    }
}
