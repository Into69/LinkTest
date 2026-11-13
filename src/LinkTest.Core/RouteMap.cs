using System.Globalization;

namespace LinkTest.Core;

/// <summary>
/// The route of a test that moved (location reporting on), coloured by link quality each second, laid out in a box
/// for the HTML and PDF reports. Drawn from the recorded data only - no map tiles - so reports stay self-contained
/// and the location is not sent to a map service.
/// </summary>
public sealed class RouteMap
{
    public sealed record Run(StatLevel Level, List<(double x, double y)> Points);

    public List<Run> Runs { get; } = new();
    public (double x, double y) Start { get; private set; }
    public (double x, double y) End { get; private set; }
    public double Width { get; private set; }
    public double Height { get; private set; }
    /// <summary>Scale bar: length in metres and in drawing units.</summary>
    public double ScaleMetres { get; private set; }
    public double ScaleLength { get; private set; }
    public double DistanceM { get; private set; }
    /// <summary>Share of the distance travelled at each quality level (Good / Fair / Poor / Info = no data).</summary>
    public Dictionary<StatLevel, double> SharePct { get; } = new();

    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Quality of one second: poor = loss over 1%, RTT over 150 ms or a gap of 1 s; fair = loss over 0.1%, RTT over 50 ms, jitter over 30 ms or a gap over 250 ms.</summary>
    public static StatLevel SecondLevel(SampleSeries s, int k)
    {
        static double M(double a, double b) => double.IsFinite(a) ? (double.IsFinite(b) ? Math.Max(a, b) : a) : b;
        double loss = M(At(s.LossUp, k), At(s.LossDown, k)), rtt = At(s.Rtt, k), jit = M(At(s.JitUp, k), At(s.JitDown, k)), gap = M(At(s.GapUp, k), At(s.GapDown, k));
        if (!double.IsFinite(loss) && !double.IsFinite(rtt)) return StatLevel.Info;
        if (loss > 1 || rtt > 150 || gap >= 1000) return StatLevel.Poor;
        if (loss > 0.1 || rtt > 50 || jit > 30 || gap > 250) return StatLevel.Fair;
        return StatLevel.Good;
    }

    static double At(List<double> v, int k) => k < v.Count ? v[k] : double.NaN;

    /// <summary>Null when the test has no positions or did not move beyond the location accuracy.</summary>
    public static RouteMap? Build(TestRecord r, double width, double height, double pad = 16)
    {
        var s = r.Samples;
        if (s.Lat == null || s.Lon == null) return null;
        var pts = new List<(int k, double lat, double lon)>();
        for (int k = 0; k < s.Lat.Count && k < s.Lon.Count && k < s.T.Count; k++)
            if (double.IsFinite(s.Lat[k]) && double.IsFinite(s.Lon[k])) pts.Add((k, s.Lat[k], s.Lon[k]));
        if (pts.Count < 2) return null;

        double lat0 = pts.Average(p => p.lat), lon0 = pts.Average(p => p.lon);
        double kx = 111_320 * Math.Cos(lat0 * Math.PI / 180), ky = 110_540;
        var m = pts.Select(p => (p.k, x: (p.lon - lon0) * kx, y: -(p.lat - lat0) * ky)).ToList();
        double minX = m.Min(p => p.x), maxX = m.Max(p => p.x), minY = m.Min(p => p.y), maxY = m.Max(p => p.y);
        double spanM = Math.Max(maxX - minX, maxY - minY);
        double noise = Math.Max(50, r.Locations.Where(f => double.IsFinite(f.AccuracyM)).Select(f => f.AccuracyM).DefaultIfEmpty(0).Max());
        if (spanM <= noise) return null;

        double scale = Math.Min((width - 2 * pad) / Math.Max(1, maxX - minX), (height - 2 * pad) / Math.Max(1, maxY - minY));
        double ox = pad + ((width - 2 * pad) - (maxX - minX) * scale) / 2, oy = pad + ((height - 2 * pad) - (maxY - minY) * scale) / 2;
        (double, double) P((int k, double x, double y) p) => (ox + (p.x - minX) * scale, oy + (p.y - minY) * scale);

        var map = new RouteMap { Width = width, Height = height };
        var dist = new Dictionary<StatLevel, double>();
        Run? cur = null;
        for (int i = 1; i < m.Count; i++)
        {
            var lvl = SecondLevel(s, m[i].k);
            double d = Math.Sqrt(Math.Pow(m[i].x - m[i - 1].x, 2) + Math.Pow(m[i].y - m[i - 1].y, 2));
            map.DistanceM += d;
            dist[lvl] = dist.GetValueOrDefault(lvl) + d;
            if (cur == null || cur.Level != lvl)
            {
                cur = new Run(lvl, new List<(double, double)> { P(m[i - 1]) });
                map.Runs.Add(cur);
            }
            cur.Points.Add(P(m[i]));
        }
        foreach (var (k, v) in dist) map.SharePct[k] = map.DistanceM > 0 ? 100 * v / map.DistanceM : 0;
        map.Start = P(m[0]);
        map.End = P(m[^1]);

        // Scale bar of a round length near a quarter of the width.
        double target = (width / 4) / scale, e = Math.Pow(10, Math.Floor(Math.Log10(target)));
        double nice = target / e >= 5 ? 5 * e : target / e >= 2 ? 2 * e : e;
        map.ScaleMetres = nice;
        map.ScaleLength = nice * scale;
        return map;
    }

    public string ScaleText => ScaleMetres >= 1000 ? (ScaleMetres / 1000).ToString("0.#", Inv) + " km" : ScaleMetres.ToString("0", Inv) + " m";

    public string Summary
    {
        get
        {
            string D(double mtr) => mtr >= 1000 ? (mtr / 1000).ToString("0.0", Inv) + " km" : mtr.ToString("0", Inv) + " m";
            var parts = new List<string>();
            foreach (var (lvl, name) in new[] { (StatLevel.Good, "good"), (StatLevel.Fair, "fair"), (StatLevel.Poor, "poor"), (StatLevel.Info, "no data") })
                if (SharePct.TryGetValue(lvl, out var p) && p >= 0.5) parts.Add($"{name} {p.ToString("0", Inv)}%");
            return $"{D(DistanceM)} travelled: " + string.Join(", ", parts) + " of the route.";
        }
    }
}
