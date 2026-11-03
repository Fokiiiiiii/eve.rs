namespace Eve.Host.ProjectDiscovery;

// Research reproduction of published F1.R and get_F1_scores.R (FRDR DOI 10.20383/103.01043).
// This peer agreement score is not the game's live accuracy, gold, or reward policy.
public readonly record struct ScientificPoint(double X, double Y);
public sealed record ScientificGateSet(double MinX, double MinY, double MaxX, double MaxY, IReadOnlyList<IReadOnlyList<ScientificPoint>> Polygons);

public static class ScientificReplayScorer
{
    public static int[] Label(IReadOnlyList<ScientificPoint> points, ScientificGateSet gates)
    {
        ArgumentNullException.ThrowIfNull(points); ArgumentNullException.ThrowIfNull(gates);
        if (!(gates.MaxX > gates.MinX) || !(gates.MaxY > gates.MinY) || gates.Polygons.Count == 0) throw new ArgumentException("Invalid gate bounds or empty gates.", nameof(gates));
        var labels = new int[points.Count];
        for (int gate = 0; gate < gates.Polygons.Count; gate++)
        {
            var source = gates.Polygons[gate];
            if (source.Count < 3) throw new ArgumentException("A polygon must have at least three vertices.", nameof(gates));
            var polygon = source.Select(p => new ScientificPoint(p.X * (gates.MaxX - gates.MinX) + gates.MinX, p.Y * (gates.MaxY - gates.MinY) + gates.MinY)).ToArray();
            for (int i = 0; i < points.Count; i++) if (Contains(polygon, points[i])) labels[i] = gate + 1;
        }
        return labels;
    }
    private static bool Contains(IReadOnlyList<ScientificPoint> polygon, ScientificPoint p)
    {
        bool inside = false;
        for (int i = 0; i < polygon.Count; i++)
        {
            var a = polygon[i]; var b = polygon[(i + 1) % polygon.Count];
            double cross = (p.X - a.X) * (b.Y - a.Y) - (p.Y - a.Y) * (b.X - a.X);
            if (Math.Abs(cross) < 1e-9 && p.X >= Math.Min(a.X, b.X) && p.X <= Math.Max(a.X, b.X) && p.Y >= Math.Min(a.Y, b.Y) && p.Y <= Math.Max(a.Y, b.Y)) return true;
            if ((a.Y > p.Y) != (b.Y > p.Y) && p.X < (b.X - a.X) * (p.Y - a.Y) / (b.Y - a.Y) + a.X) inside = !inside;
        }
        return inside;
    }
    public static double Compare(int[] predicted, int[] reference, int gateCount)
    {
        ArgumentNullException.ThrowIfNull(predicted); ArgumentNullException.ThrowIfNull(reference);
        if (predicted.Length != reference.Length || predicted.Length == 0 || gateCount <= 0) throw new ArgumentException("Matching nonempty labels and positive gate count are required.");
        var classes = predicted.Distinct().ToArray(); double sum = 0;
        for (int gate = 1; gate <= gateCount; gate++)
        {
            int referenceCount = reference.Count(x => x == gate); double best = 0;
            foreach (int label in classes)
            {
                int count = 0, truePositive = 0;
                for (int i = 0; i < predicted.Length; i++) if (predicted[i] == label) { count++; if (reference[i] == gate) truePositive++; }
                if (count == predicted.Length || referenceCount == 0 || count == 0 || truePositive == 0) continue;
                // The IEEE operation order follows MLmetrics::F1_Score; 2*TP/(pred+ref) changes ties.
                double precision = (double)truePositive / count, recall = (double)truePositive / referenceCount;
                double score = 2 * (precision * recall) / (precision + recall);
                best = Math.Max(best, Math.Round(score, 3, MidpointRounding.ToEven));
            }
            sum += best;
        }
        return sum / gateCount;
    }
    // F1.R compares each pair once in cohort order and mirrors it; matching is directional.
    public static double[] LeaveOneOut(IReadOnlyList<int[]> labels, int gateCount)
    {
        if (labels.Count < 2) throw new ArgumentException("At least two cohort members are required.", nameof(labels));
        var sums = new double[labels.Count];
        for (int i = 0; i < labels.Count; i++) for (int j = i + 1; j < labels.Count; j++)
        { double score = Compare(labels[i], labels[j], gateCount); sums[i] += score; sums[j] += score; }
        return sums.Select(s => s / (labels.Count - 1)).ToArray();
    }
    // A new answer is compared to every fixed cohort member. No member is implicitly excluded.
    public static double ScoreNew(int[] answer, IReadOnlyList<int[]> cohort, int gateCount)
    {
        if (cohort.Count == 0) throw new ArgumentException("A reference cohort is required.", nameof(cohort));
        return cohort.Average(reference => Compare(answer, reference, gateCount));
    }
}
