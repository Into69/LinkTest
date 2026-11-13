namespace LinkTest.Core;

/// <summary>
/// What a type of traffic needs from the network to work well. NaN = no limit. Values follow common guidance
/// (ITU-T G.114 / Y.1541, Cisco QoS baseline, RFC 4594): real-time media ≤ 1% loss, ≤ 30 ms jitter,
/// ≤ 150 ms one-way delay (300 ms round trip).
/// </summary>
public sealed record TrafficRequirement(
    string Kind, string Why, double MaxLossPct, double MaxJitterMs, double MaxRttMs, double MinDeliveredPct,
    bool Informational = false)
{
    public static TrafficRequirement For(TrafficClass c, bool simulate)
    {
        bool udp = c.Transport == TransportKind.Udp;
        if (simulate || c.Dscp != 0)
        {
            switch (c.Dscp)
            {
                case 46: return Voice;
                case 32 or 34 or 36 or 38 or 40: return Video;
                case 48 or 56: return new("Network control", "Routing and keepalive traffic: losses can take links or tunnels down.", 0.5, double.NaN, 500, 95);
                case 16: return new("Management", "Monitoring and management sessions.", 1, double.NaN, 500, 90);
                case 24: return new("Call signalling", "Call setup: delay or loss shows as slow or failed call setup.", 1, double.NaN, 400, 95);
                case 26 or 28 or 30: return new("Streaming video", "Buffered video tolerates delay, but needs its bit rate.", 2, double.NaN, double.NaN, 90);
                case 18 or 20 or 22: return new("Transactional data", "Interactive business apps: response time matters.", 1, double.NaN, 300, 95);
                case 10 or 12 or 14: return new("Bulk data", "File transfers and backups: only throughput matters.", 2, double.NaN, double.NaN, 90);
                case 8 or 1: return new("Scavenger", "Lowest priority by design: expected to give way under load.", double.NaN, double.NaN, double.NaN, double.NaN, Informational: true);
                case 0 when simulate: return new("Best effort", "Default traffic (web, email): should keep most of its rate.", 2, double.NaN, double.NaN, 90);
            }
        }
        if (c.Profile == TrafficProfile.Voip) return Voice;
        if (udp && c.Profile == TrafficProfile.Video) return Video;
        return udp
            ? new("Real-time UDP", "Treated as real-time media (VoIP / video / gaming tunnels).", 1, 30, 300, 95)
            : new("TCP application", "Typical TCP application traffic.", 2, double.NaN, 500, 90);
    }

    static readonly TrafficRequirement Voice = new("Voice", "Calls: loss and jitter cause choppy audio; delay causes talk-over.", 1, 30, 300, 95);
    static readonly TrafficRequirement Video = new("Interactive video", "Video calls: loss causes freezes and artefacts.", 1, 30, 300, 95);

    public string LimitsText
    {
        get
        {
            if (Informational) return "none (informational)";
            var p = new List<string>();
            if (double.IsFinite(MaxLossPct)) p.Add($"loss ≤ {MaxLossPct:0.#}%");
            if (double.IsFinite(MaxJitterMs)) p.Add($"jitter ≤ {MaxJitterMs:0} ms");
            if (double.IsFinite(MaxRttMs)) p.Add($"RTT ≤ {MaxRttMs:0} ms");
            if (double.IsFinite(MinDeliveredPct)) p.Add($"≥ {MinDeliveredPct:0}% of its rate");
            return string.Join(", ", p);
        }
    }
}

public enum TrafficVerdict { Good, Marginal, Poor, Info, NoData }

/// <summary>How well one traffic class was carried during a test.</summary>
public sealed class ClassAssessment
{
    public required string Name { get; init; }
    public required TrafficClass Def { get; init; }
    public required TrafficRequirement Req { get; init; }
    public ClassSummary? Summary { get; init; }
    public double TargetUpMbps { get; init; }
    public double TargetDownMbps { get; init; }
    public double DeliveredUpPct { get; init; } = double.NaN;
    public double DeliveredDownPct { get; init; } = double.NaN;
    public double JitterMs { get; init; } = double.NaN;
    /// <summary>TCP: retransmitted share of the data in each direction (%).</summary>
    public double RetransUpPct { get; init; } = double.NaN;
    public double RetransDownPct { get; init; } = double.NaN;

    /// <summary>Per second: true = within limits, false = not, null = not evaluated (baseline, no data).</summary>
    public List<bool?> Seconds { get; } = new();
    public int SecondsEvaluated => Seconds.Count(s => s != null);
    public int SecondsOk => Seconds.Count(s => s == true);
    public double WithinPct => SecondsEvaluated > 0 ? 100.0 * SecondsOk / SecondsEvaluated : double.NaN;
    public int LongestBadSec { get; set; }
    /// <summary>Seconds over which loss was averaged for this class (longer for low-rate traffic).</summary>
    public int LossWindowSec { get; set; }
    public int LossSeconds { get; set; }
    public int JitterSeconds { get; set; }
    public int RttSeconds { get; set; }
    public int RateSeconds { get; set; }
    public double WorstLossPct { get; set; } = double.NaN;
    public double WorstJitterMs { get; set; } = double.NaN;
    public double WorstRttMs { get; set; } = double.NaN;

    public TrafficVerdict Verdict { get; set; }
    public List<string> Reasons { get; } = new();

    public bool IsUdp => Def.Transport == TransportKind.Udp;
    /// <summary>Loss for UDP, retransmissions for TCP (worst direction).</summary>
    public double LossOrRetransPct => IsUdp
        ? MaxF(Summary?.UpLossPct ?? double.NaN, Summary?.DownLossPct ?? double.NaN)
        : MaxF(RetransUpPct, RetransDownPct);

    internal static double MaxF(double a, double b) => double.IsFinite(a) ? (double.IsFinite(b) ? Math.Max(a, b) : a) : b;
}

/// <summary>Judges every traffic class of a finished test against what that kind of traffic needs.</summary>
public static class TrafficAssessment
{
    public static List<ClassAssessment> Evaluate(TestRecord r)
    {
        var defs = TrafficClasses.Resolve(r.Config);
        var list = new List<ClassAssessment>();
        var t = r.Samples.T;
        for (int i = 0; i < defs.Count; i++)
        {
            var def = defs[i];
            var req = TrafficRequirement.For(def, r.Config.DscpSimulate);
            var sum = i < r.Classes.Count ? r.Classes[i] : null;
            var ser = i < r.ClassSeries.Count ? r.ClassSeries[i] : null;
            double tUp = r.Config.UpMbps * def.SharePct / 100, tDown = r.Config.DownMbps * def.SharePct / 100;
            bool udp = def.Transport == TransportKind.Udp;

            // Whole-test averages from the series (after the idle baseline), falling back to the saved summary.
            double Avg(List<double>? v)
            {
                if (v == null) return double.NaN;
                double s = 0; int n = 0;
                for (int k = 0; k < v.Count && k < t.Count; k++)
                    if (t[k] > r.JudgeAfterSec + 1 && double.IsFinite(v[k])) { s += v[k]; n++; }   // skip the warm-up and the ramp-up second
                return n > 0 ? s / n : double.NaN;
            }
            double upAvg = ser != null ? Avg(ser.Up) : sum?.UpMbps ?? double.NaN;
            double dnAvg = ser != null ? Avg(ser.Down) : sum?.DownMbps ?? double.NaN;
            double retrUp = double.NaN, retrDn = double.NaN;
            if (!udp && ser != null)
            {
                double ru = Avg(ser.RetransUpKB), rd = Avg(ser.RetransDownKB);
                if (upAvg > 0 && double.IsFinite(ru)) retrUp = 100 * ru * 8 / 1000 / upAvg;
                if (dnAvg > 0 && double.IsFinite(rd)) retrDn = 100 * rd * 8 / 1000 / dnAvg;
            }
            var a = new ClassAssessment
            {
                Name = sum?.Name ?? def.Name, Def = def, Req = req, Summary = sum,
                TargetUpMbps = tUp, TargetDownMbps = tDown,
                DeliveredUpPct = tUp > 0 && double.IsFinite(upAvg) ? Math.Min(100, 100 * upAvg / tUp) : double.NaN,
                DeliveredDownPct = tDown > 0 && double.IsFinite(dnAvg) ? Math.Min(100, 100 * dnAvg / tDown) : double.NaN,
                JitterMs = ser != null ? ClassAssessment.MaxF(Avg(ser.JitUp), Avg(ser.JitDown)) : sum?.JitterDownMs ?? double.NaN,
                RetransUpPct = retrUp, RetransDownPct = retrDn,
            };

            // Per-second compliance. Rate is only judged per second for steady profiles (bursty ones vary by design).
            bool steady = def.Profile is TrafficProfile.Constant or TrafficProfile.Voip or TrafficProfile.Imix;
            int bad = 0;
            // Loss (or TCP retransmission) per second, then judged over a sliding window: on low-rate traffic a
            // single lost packet is already a few percent of one second, which says little about the service.
            // Window long enough to hold ~2000 packets, so a loss rate near the limit isn't judged on a handful of packets.
            int max = def.PacketSize > 0 ? def.PacketSize : Math.Max(64, r.Config.PacketSize);
            int size = def.Profile switch   // average packet size of the profile
            {
                TrafficProfile.Voip => TrafficShaper.VoipPayload,
                TrafficProfile.Imix => Math.Min(max, 354),
                TrafficProfile.Random => (64 + max) / 2,
                _ => max,
            };
            double pps = Math.Max(tUp, tDown) * 1e6 / 8 / size;
            int window = pps > 0 ? (int)Math.Clamp(Math.Ceiling(MinWindowPackets / pps), LossWindowSec, MaxLossWindowSec) : LossWindowSec;
            a.LossWindowSec = window;
            // Each direction separately, weighted by the traffic carried that second (≈ lost / sent packets).
            var lossUp = new List<double>();
            var lossDn = new List<double>();
            if (ser != null)
                for (int k = 0; k < ser.Up.Count; k++)
                {
                    if (udp) { lossUp.Add(ser.LossUp[k]); lossDn.Add(ser.LossDown[k]); }
                    else
                    {
                        lossUp.Add(ser.Up[k] > 0 ? 100 * ser.RetransUpKB[k] * 8 / 1000 / ser.Up[k] : double.NaN);
                        lossDn.Add(ser.Down[k] > 0 ? 100 * ser.RetransDownKB[k] * 8 / 1000 / ser.Down[k] : double.NaN);
                    }
                }
            if (ser != null)
                for (int k = 0; k < t.Count && k < ser.Up.Count; k++)
                {
                    if (t[k] <= r.JudgeAfterSec + 1 || req.Informational) { a.Seconds.Add(null); continue; }
                    bool ok = true, any = false;
                    double loss = ClassAssessment.MaxF(Window(lossUp, ser.Up, k, window, t, r.JudgeAfterSec + 1),
                                                       Window(lossDn, ser.Down, k, window, t, r.JudgeAfterSec + 1));
                    if (double.IsFinite(loss))
                    {
                        any = true;
                        a.WorstLossPct = ClassAssessment.MaxF(a.WorstLossPct, loss);
                        if (double.IsFinite(req.MaxLossPct) && loss > req.MaxLossPct) { ok = false; a.LossSeconds++; }
                    }
                    if (udp)
                    {
                        double jit = ClassAssessment.MaxF(ser.JitUp[k], ser.JitDown[k]);
                        if (double.IsFinite(jit))
                        {
                            any = true;
                            a.WorstJitterMs = ClassAssessment.MaxF(a.WorstJitterMs, jit);
                            if (double.IsFinite(req.MaxJitterMs) && jit > req.MaxJitterMs) { ok = false; a.JitterSeconds++; }
                        }
                    }
                    double rtt = ser.Rtt[k];
                    if (double.IsFinite(rtt))
                    {
                        any = true;
                        a.WorstRttMs = ClassAssessment.MaxF(a.WorstRttMs, rtt);
                        if (double.IsFinite(req.MaxRttMs) && rtt > req.MaxRttMs) { ok = false; a.RttSeconds++; }
                    }
                    if (steady)
                    {
                        bool low = (tUp > 0 && double.IsFinite(ser.Up[k]) && ser.Up[k] < tUp * 0.5)
                                || (tDown > 0 && double.IsFinite(ser.Down[k]) && ser.Down[k] < tDown * 0.5);
                        if (double.IsFinite(ser.Up[k]) || double.IsFinite(ser.Down[k])) any = true;
                        if (low) { ok = false; a.RateSeconds++; }
                    }
                    a.Seconds.Add(any ? ok : null);
                    if (any && !ok) { bad++; a.LongestBadSec = Math.Max(a.LongestBadSec, bad); }
                    else if (any) bad = 0;
                }

            Judge(a);
            list.Add(a);
        }
        return list;
    }

    /// <summary>Shortest and longest window (s) over which loss is averaged when judging each second.</summary>
    public const int LossWindowSec = 5, MaxLossWindowSec = 30;
    const double MinWindowPackets = 2000;

    /// <summary>Loss over the window ending at second <paramref name="k"/>, each second weighted by its rate.</summary>
    static double Window(List<double> loss, List<double> rate, int k, int window, List<double> t, double after)
    {
        double s = 0, w = 0;
        for (int i = Math.Max(0, k - window + 1); i <= k; i++)
        {
            if (t[i] <= after || !double.IsFinite(loss[i])) continue;
            double weight = double.IsFinite(rate[i]) && rate[i] > 0 ? rate[i] : 0;
            s += loss[i] * weight; w += weight;
        }
        return w > 0 ? s / w : double.NaN;
    }

    static void Judge(ClassAssessment a)
    {
        var q = a.Req;
        if (q.Informational) { a.Verdict = TrafficVerdict.Info; a.Reasons.Add(q.Why); return; }
        if (a.Summary == null && a.SecondsEvaluated == 0) { a.Verdict = TrafficVerdict.NoData; return; }

        int poor = 0, marginal = 0;
        double loss = a.LossOrRetransPct;
        string lossWord = a.IsUdp ? "loss" : "retransmissions";
        if (double.IsFinite(q.MaxLossPct) && double.IsFinite(loss) && loss > q.MaxLossPct)
        {
            if (loss > 2 * q.MaxLossPct) poor++; else marginal++;
            a.Reasons.Add($"Average {lossWord} {loss:0.##}% (limit {q.MaxLossPct:0.#}%).");
        }
        if (double.IsFinite(q.MaxJitterMs) && double.IsFinite(a.JitterMs) && a.JitterMs > q.MaxJitterMs)
        {
            marginal++;
            a.Reasons.Add($"Average jitter {a.JitterMs:0.#} ms (limit {q.MaxJitterMs:0} ms).");
        }
        double rtt = a.Summary?.RttAvgMs ?? double.NaN;
        if (double.IsFinite(q.MaxRttMs) && double.IsFinite(rtt) && rtt > q.MaxRttMs)
        {
            if (rtt > 1.5 * q.MaxRttMs) poor++; else marginal++;
            a.Reasons.Add($"Average RTT {rtt:0} ms (limit {q.MaxRttMs:0} ms).");
        }
        foreach (var (pct, dir) in new[] { (a.DeliveredUpPct, "upload"), (a.DeliveredDownPct, "download") })
            if (double.IsFinite(q.MinDeliveredPct) && double.IsFinite(pct) && pct < q.MinDeliveredPct)
            {
                if (pct < q.MinDeliveredPct - 15) poor++; else marginal++;
                a.Reasons.Add($"Only {pct:0}% of its {dir} rate was delivered (needs {q.MinDeliveredPct:0}%).");
            }
        double within = a.WithinPct;
        if (double.IsFinite(within) && within < 99)
        {
            if (within < 90) poor++; else marginal++;
            var why = new List<string>();
            if (a.LossSeconds > 0) why.Add($"{lossWord} in {a.LossSeconds} s");
            if (a.JitterSeconds > 0) why.Add($"jitter in {a.JitterSeconds} s");
            if (a.RttSeconds > 0) why.Add($"RTT in {a.RttSeconds} s");
            if (a.RateSeconds > 0) why.Add($"rate below half in {a.RateSeconds} s");
            a.Reasons.Add($"Within limits {within:0.#}% of the time; over limits: {string.Join(", ", why)}" +
                          (a.LongestBadSec > 1 ? $" (longest run {a.LongestBadSec} s)." : "."));
        }
        a.Verdict = poor > 0 ? TrafficVerdict.Poor : marginal > 0 ? TrafficVerdict.Marginal : TrafficVerdict.Good;
    }

    /// <summary>One-sentence overall result.</summary>
    public static string Headline(IReadOnlyList<ClassAssessment> list)
    {
        var judged = list.Where(a => a.Verdict is TrafficVerdict.Good or TrafficVerdict.Marginal or TrafficVerdict.Poor).ToList();
        if (judged.Count == 0) return "Not enough data to judge the traffic.";
        int good = judged.Count(a => a.Verdict == TrafficVerdict.Good), marg = judged.Count(a => a.Verdict == TrafficVerdict.Marginal),
            poor = judged.Count(a => a.Verdict == TrafficVerdict.Poor);
        string kinds = judged.Count == 1 ? "the traffic" : $"{good} of {judged.Count} traffic types";
        if (good == judged.Count) return $"The link maintained {(judged.Count == 1 ? "the traffic" : $"all {judged.Count} traffic types")} within their requirements.";
        return $"The link maintained {kinds} within requirements" +
               (marg > 0 ? $"; {marg} {(marg == 1 ? "was" : "were")} marginal" : "") +
               (poor > 0 ? $"; {poor} {(poor == 1 ? "was" : "were")} poorly served" : "") + ".";
    }
}
