using System.Globalization;

namespace LinkTest.Core;

/// <summary>Side-by-side figures of two tests (IPv4 vs IPv6, or any two saved results) with which one did better.</summary>
public sealed class Comparison
{
    public sealed record Line(string Name, string A, string B, int Better, string Diff);   // Better: -1 = A, 1 = B, 0 = alike / n.a.

    public TestRecord RecA { get; }
    public TestRecord RecB { get; }
    public string LabelA { get; }
    public string LabelB { get; }
    public List<Line> Lines { get; } = new();
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public Comparison(TestRecord a, TestRecord b, string? labelA = null, string? labelB = null)
    {
        RecA = a; RecB = b;
        LabelA = labelA ?? a.Family ?? "Test A";
        LabelB = labelB ?? b.Family ?? "Test B";
        if (LabelA == LabelB) { LabelA += $" ({a.StartedAt:HH:mm})"; LabelB += $" ({b.StartedAt:HH:mm})"; }

        Num("Upload (avg)", a.UpMbpsAvg, b.UpMbpsAvg, "Mbps", higherBetter: true, rel: 0.05);
        Num("Download (avg)", a.DownMbpsAvg, b.DownMbpsAvg, "Mbps", higherBetter: true, rel: 0.05);
        Num("Packet loss up", a.UpLossPct, b.UpLossPct, "%", higherBetter: false, abs: 0.1);
        Num("Packet loss down", a.DownLossPct, b.DownLossPct, "%", higherBetter: false, abs: 0.1);
        Num("RTT (avg)", a.RttAvgMs, b.RttAvgMs, "ms", higherBetter: false, abs: 2, rel: 0.1);
        Num("RTT (min)", a.RttMinMs, b.RttMinMs, "ms", higherBetter: false, abs: 2, rel: 0.1);
        Num("RTT (max)", a.RttMaxMs, b.RttMaxMs, "ms", higherBetter: false, abs: 10, rel: 0.2);
        Num("Jitter up", a.JitterUpMs, b.JitterUpMs, "ms", higherBetter: false, abs: 1, rel: 0.2);
        Num("Jitter down", a.JitterDownMs, b.JitterDownMs, "ms", higherBetter: false, abs: 1, rel: 0.2);
        Num("Longest gap", a.MaxGapMs, b.MaxGapMs, "ms", higherBetter: false, abs: 50, rel: 0.25);
        Num("VoIP MOS", a.MosAvg, b.MosAvg, "", higherBetter: true, abs: 0.1, fmt: "0.00");
        Grade("Bufferbloat", a.BloatGrade, b.BloatGrade);
        if (a.Mtu is { Ok: true } || b.Mtu is { Ok: true })
            Num("Path MTU", a.Mtu?.PathMtu ?? double.NaN, b.Mtu?.PathMtu ?? double.NaN, "B", higherBetter: true, abs: 1, fmt: "0");
        if (double.IsFinite(a.CapacityDownMbps) || double.IsFinite(b.CapacityDownMbps))
        {
            Num("Capacity up", a.CapacityUpMbps, b.CapacityUpMbps, "Mbps", higherBetter: true, rel: 0.05);
            Num("Capacity down", a.CapacityDownMbps, b.CapacityDownMbps, "Mbps", higherBetter: true, rel: 0.05);
        }
        if (a.Tunnels.Count > 0 || b.Tunnels.Count > 0)
            Num("VPN tunnel drops", a.Tunnels.Sum(t => t.Drops), b.Tunnels.Sum(t => t.Drops), "", higherBetter: false, abs: 0.5, fmt: "0");
        var ma = TrafficAssessment.Evaluate(a); var mb = TrafficAssessment.Evaluate(b);
        int ga = ma.Count(x => x.Verdict == TrafficVerdict.Good), gb = mb.Count(x => x.Verdict == TrafficVerdict.Good);
        int ja = ma.Count(x => x.Verdict is TrafficVerdict.Good or TrafficVerdict.Marginal or TrafficVerdict.Poor);
        int jb = mb.Count(x => x.Verdict is TrafficVerdict.Good or TrafficVerdict.Marginal or TrafficVerdict.Poor);
        Lines.Add(new Line("Traffic types maintained", $"{ga} of {ja}", $"{gb} of {jb}", ga == gb ? 0 : ga > gb ? -1 : 1, ga == gb ? "" : $"{Math.Abs(ga - gb)} more"));
    }

    void Num(string name, double a, double b, string unit, bool higherBetter, double abs = 0, double rel = 0, string fmt = "0.##")
    {
        string F(double v) => double.IsFinite(v) ? v.ToString(fmt, Inv) + (unit.Length > 0 ? " " + unit : "") : "–";
        int better = 0; string diff = "";
        if (double.IsFinite(a) && double.IsFinite(b))
        {
            double d = b - a;
            bool meaningful = Math.Abs(d) > abs && Math.Abs(d) > rel * Math.Max(Math.Abs(a), Math.Abs(b));
            if (meaningful) better = (d > 0) == higherBetter ? 1 : -1;
            diff = (d >= 0 ? "+" : "−") + Math.Abs(d).ToString(fmt, Inv) + (unit.Length > 0 ? " " + unit : "");
        }
        Lines.Add(new Line(name, F(a), F(b), better, diff));
    }

    void Grade(string name, string? a, string? b)
    {
        static int Rank(string? g) => g switch { "A+" => 0, "A" => 1, "B" => 2, "C" => 3, "D" => 4, "F" => 5, _ => -1 };
        int ra = Rank(a), rb = Rank(b);
        Lines.Add(new Line(name, a ?? "–", b ?? "–", ra < 0 || rb < 0 || ra == rb ? 0 : rb < ra ? 1 : -1, ""));
    }

    /// <summary>One-sentence verdict.</summary>
    public string Headline
    {
        get
        {
            var aBetter = Lines.Where(l => l.Better < 0).Select(l => l.Name.ToLowerInvariant()).ToList();
            var bBetter = Lines.Where(l => l.Better > 0).Select(l => l.Name.ToLowerInvariant()).ToList();
            if (aBetter.Count == 0 && bBetter.Count == 0) return $"{LabelA} and {LabelB} performed alike.";
            string Part(string label, List<string> what) => $"{label} was better on {string.Join(", ", what.Take(5))}{(what.Count > 5 ? " and more" : "")}";
            if (bBetter.Count == 0) return Part(LabelA, aBetter) + ".";
            if (aBetter.Count == 0) return Part(LabelB, bBetter) + ".";
            return Part(LabelA, aBetter) + "; " + Part(LabelB, bBetter) + ".";
        }
    }

    public string SuggestedFileName(string ext) =>
        $"LinkTest_compare_{RecA.StartedAt:yyyyMMdd_HHmmss}_{LabelA.Split(' ')[0]}_vs_{LabelB.Split(' ')[0]}.{ext}";
}
