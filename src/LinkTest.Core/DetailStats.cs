using System.Globalization;
using System.Text;

namespace LinkTest.Core;

/// <summary>
/// Detailed, cumulative receive statistics for the traffic one side got from its peer (one traffic class).
/// The server sends its copy to the client once a second over the control connection (1.8+).
/// </summary>
public sealed class RxDetail
{
    /// <summary>Upper edges (ms) of the one-way delay variation histogram; the last bucket is open-ended.</summary>
    public static readonly double[] DelayEdgesMs = { 0.1, 0.2, 0.5, 1, 2, 5, 10, 20, 50, 100, 200, 500, 1000, 2000 };
    /// <summary>Loss burst length buckets: 1, 2-3, 4-10, 11-100, over 100 consecutive packets.</summary>
    public static readonly string[] BurstLabels = { "1", "2-3", "4-10", "11-100", ">100" };

    public long Packets { get; set; }            // including duplicates
    public long Bytes { get; set; }
    public long Unique { get; set; }
    public long Lost { get; set; }
    public long OutOfOrder { get; set; }
    public long Duplicates { get; set; }
    /// <summary>Arrived too late to check for duplicates (more than 8192 packets behind).</summary>
    public long Late { get; set; }
    /// <summary>Largest distance (in packets) by which a packet arrived out of order.</summary>
    public long MaxReorder { get; set; }
    public long ReorderSum { get; set; }

    /// <summary>Runs of consecutive lost packets.</summary>
    public long LossEvents { get; set; }
    public long LongestLossBurst { get; set; }
    public long[] BurstHist { get; set; } = new long[5];

    public double JitterUs { get; set; }
    public double PeakJitterUs { get; set; }

    /// <summary>One-way delay above the recent minimum (the clock offset between hosts cancels out).</summary>
    public long[] DelayHist { get; set; } = new long[DelayEdgesMs.Length + 1];
    public double DelaySumUs { get; set; }
    public long DelayCount { get; set; }
    public double DelayMaxUs { get; set; }

    /// <summary>Longest time without any packet, over the whole test.</summary>
    public long MaxGapUs { get; set; }
    public long Stalls100ms { get; set; }
    public long Stalls1s { get; set; }

    public int MinSize { get; set; }
    public int MaxSize { get; set; }

    public static int BurstBucket(long n) => n <= 1 ? 0 : n <= 3 ? 1 : n <= 10 ? 2 : n <= 100 ? 3 : 4;

    public static int DelayBucket(double ms)
    {
        for (int i = 0; i < DelayEdgesMs.Length; i++) if (ms < DelayEdgesMs[i]) return i;
        return DelayEdgesMs.Length;
    }

    /// <summary>Adds another class's figures (sums, maxima; jitter is averaged by the caller).</summary>
    public void Merge(RxDetail o)
    {
        Packets += o.Packets; Bytes += o.Bytes; Unique += o.Unique; Lost += o.Lost;
        OutOfOrder += o.OutOfOrder; Duplicates += o.Duplicates; Late += o.Late;
        MaxReorder = Math.Max(MaxReorder, o.MaxReorder); ReorderSum += o.ReorderSum;
        LossEvents += o.LossEvents; LongestLossBurst = Math.Max(LongestLossBurst, o.LongestLossBurst);
        for (int i = 0; i < BurstHist.Length && i < o.BurstHist.Length; i++) BurstHist[i] += o.BurstHist[i];
        PeakJitterUs = Math.Max(PeakJitterUs, o.PeakJitterUs);
        for (int i = 0; i < DelayHist.Length && i < o.DelayHist.Length; i++) DelayHist[i] += o.DelayHist[i];
        DelaySumUs += o.DelaySumUs; DelayCount += o.DelayCount; DelayMaxUs = Math.Max(DelayMaxUs, o.DelayMaxUs);
        MaxGapUs = Math.Max(MaxGapUs, o.MaxGapUs); Stalls100ms += o.Stalls100ms; Stalls1s += o.Stalls1s;
        if (o.Packets > 0)
        {
            MinSize = MinSize == 0 ? o.MinSize : Math.Min(MinSize, o.MinSize);
            MaxSize = Math.Max(MaxSize, o.MaxSize);
        }
    }

    /// <summary>
    /// Upper bound (ms) of the delay variation percentile: the histogram bucket edge it falls under, or the
    /// maximum when that is lower. NaN when nothing was measured.
    /// </summary>
    public double DelayPercentileMs(double p)
    {
        long total = 0;
        foreach (var n in DelayHist) total += n;
        if (total == 0) return double.NaN;
        double target = total * p / 100, acc = 0, max = DelayMaxUs / 1000;
        for (int i = 0; i < DelayHist.Length; i++)
        {
            acc += DelayHist[i];
            if (acc >= target && DelayHist[i] > 0)
                return i < DelayEdgesMs.Length ? Math.Min(DelayEdgesMs[i], max) : max;
        }
        return max;
    }
}

/// <summary>Kernel TCP figures for one side's sending sockets (summed / averaged over the streams). Null = not available.</summary>
public sealed class TcpDetail
{
    public int Sockets { get; set; }
    public double? RttUs { get; set; }
    public double? MinRttUs { get; set; }
    public long? CwndBytes { get; set; }
    public long? InFlightBytes { get; set; }
    public long? BytesSent { get; set; }
    public long? BytesRetrans { get; set; }
    public long? FastRetrans { get; set; }
    public long? Timeouts { get; set; }
    public long? DupAcksIn { get; set; }
    public long? ReorderEvents { get; set; }
    public int? Mss { get; set; }
}

/// <summary>What one side reports about one traffic class: what it received, and how its own sending went.</summary>
public sealed class ClassDetail
{
    public long TimeUs { get; set; }
    public RxDetail Rx { get; set; } = new();
    public long TxPackets { get; set; }
    public long TxBytes { get; set; }
    public long SenderLagEvents { get; set; }
    public TcpDetail? Tcp { get; set; }
}

/// <summary>One column of the statistics table: one transport in one direction, all its classes combined.</summary>
public sealed class DirectionDetail
{
    public TransportKind Transport { get; set; }
    public bool Upstream { get; set; }
    public string Title => $"{(Transport == TransportKind.Tcp ? "TCP" : "UDP")} {(Upstream ? "↑ upload" : "↓ download")}";
    /// <summary>Who measured the receive side ("server" for upload, "client" for download).</summary>
    public string MeasuredBy => Upstream ? "server" : "client";
    public bool Available { get; set; } = true;
    public double DurationSec { get; set; }

    public long TxPackets { get; set; }
    public long TxBytes { get; set; }
    public long SenderLagEvents { get; set; }
    public RxDetail Rx { get; set; } = new();
    public TcpDetail? Tcp { get; set; }

    public double LossPct => Rx.Unique + Rx.Lost > 0 ? 100.0 * Rx.Lost / (Rx.Unique + Rx.Lost) : double.NaN;
    public double AvgMbps => DurationSec > 0 ? Rx.Bytes * 8 / DurationSec / 1e6 : double.NaN;
    public double AvgPps => DurationSec > 0 ? Rx.Packets / DurationSec : double.NaN;
}

/// <summary>Detailed statistics for a test: up to four columns (UDP/TCP × upload/download).</summary>
public sealed class LinkDetail
{
    public List<DirectionDetail> Columns { get; set; } = new();
    /// <summary>False when the server is older than 1.8 (upload columns then lack the extended figures).</summary>
    public bool ServerDetail { get; set; }

    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Combines per-class reports from both sides into the columns.</summary>
    /// <param name="classes">(transport, upload report from the server or null, client's local report, download report sender part from the server or null).</param>
    public static LinkDetail Build(IEnumerable<(TransportKind transport, ClassDetail? server, ClassDetail local)> classes, double durationSec, bool serverDetail)
    {
        var list = classes.ToList();
        var d = new LinkDetail { ServerDetail = serverDetail };
        foreach (var transport in new[] { TransportKind.Udp, TransportKind.Tcp })
        {
            var group = list.Where(x => x.transport == transport).ToList();
            if (group.Count == 0) continue;
            foreach (bool up in new[] { true, false })
            {
                var col = new DirectionDetail { Transport = transport, Upstream = up, DurationSec = durationSec, Available = !up || serverDetail };
                double jit = 0; int jn = 0;
                var tcps = new List<TcpDetail>();
                foreach (var (_, server, local) in group)
                {
                    // Upload: the server received, the client sent. Download: the client received, the server sent.
                    var rx = up ? server?.Rx : local.Rx;
                    var tx = up ? local : server;
                    if (rx != null)
                    {
                        col.Rx.Merge(rx);
                        if (rx.Packets > 0) { jit += rx.JitterUs; jn++; }
                    }
                    if (tx != null)
                    {
                        col.TxPackets += tx.TxPackets; col.TxBytes += tx.TxBytes; col.SenderLagEvents += tx.SenderLagEvents;
                        if (tx.Tcp != null) tcps.Add(tx.Tcp);
                    }
                }
                col.Rx.JitterUs = jn > 0 ? jit / jn : 0;
                if (transport == TransportKind.Tcp && tcps.Count > 0) col.Tcp = TcpInfo.Combine(tcps);
                d.Columns.Add(col);
            }
        }
        return d;
    }

    /// <summary>The table as rows (metric name, value per column, optional hint), for UIs and reports.</summary>
    public List<(string section, string name, string[] values, string hint)> Rows()
    {
        var rows = new List<(string, string, string[], string)>();
        var cols = Columns;
        void Row(string section, string name, Func<DirectionDetail, string> f, string hint = "", bool tcpOnly = false, bool udpOnly = false)
        {
            if (tcpOnly && !cols.Any(c => c.Transport == TransportKind.Tcp)) return;
            if (udpOnly && !cols.Any(c => c.Transport == TransportKind.Udp)) return;
            rows.Add((section, name, cols.Select(c =>
                !c.Available ? "n/a" :
                tcpOnly && c.Transport != TransportKind.Tcp ? "" :
                udpOnly && c.Transport != TransportKind.Udp ? "" : f(c)).ToArray(), hint));
        }
        static string N(double v, string fmt = "0.##") => double.IsFinite(v) ? v.ToString(fmt, Inv) : "–";
        static string I(long v) => v.ToString("#,0", Inv);
        static string Ms(double us) => double.IsFinite(us) ? (us / 1000).ToString(us >= 10_000 ? "0" : "0.##", Inv) + " ms" : "–";
        static string Pct(long part, long whole) => whole > 0 ? $"{I(part)} ({100.0 * part / whole:0.###}%)" : I(part);
        static string Bytes(double b) => !double.IsFinite(b) ? "–" : b >= 1e9 ? $"{b / 1e9:0.##} GB" : b >= 1e6 ? $"{b / 1e6:0.##} MB" : b >= 1e3 ? $"{b / 1e3:0.#} kB" : $"{b:0} B";
        static string Opt(long? v) => v is long x ? I(x) : "–";
        static string Pk(double n, string fmt = "0.#") => $"{n.ToString(fmt, Inv)} packet{(n == 1 ? "" : "s")}";
        static string Le(double ms) => !double.IsFinite(ms) ? "–" : "≤ " + ms.ToString(ms >= 10 ? "0" : "0.##", Inv);

        const string T = "Traffic", L = "Loss", O = "Order", D = "Timing", K = "TCP (sender's kernel)";
        Row(T, "Measured by", c => c.MeasuredBy, "The receiving side counts what arrived; the sending side supplies its own counters.");
        Row(T, "Average throughput", c => N(c.AvgMbps) + " Mbps");
        Row(T, "Packets sent", c => I(c.TxPackets));
        Row(T, "Packets received", c => I(c.Rx.Packets));
        Row(T, "Data received", c => Bytes(c.Rx.Bytes));
        Row(T, "Average packet rate", c => N(c.AvgPps, "0") + " pps");
        Row(T, "Packet size min / avg / max", c => c.Rx.Packets > 0 ? $"{c.Rx.MinSize} / {c.Rx.Bytes / Math.Max(1, c.Rx.Packets)} / {c.Rx.MaxSize} B" : "–",
            "Application-level message sizes (TCP streams them inside segments).");
        Row(T, "Sender fell behind", c => I(c.SenderLagEvents), "Times the sender was more than 200 ms behind schedule (CPU or socket buffer could not keep up).");

        Row(L, "Lost packets", c => c.Rx.Unique + c.Rx.Lost > 0 ? $"{I(c.Rx.Lost)} ({N(c.LossPct, "0.###")}%)" : "–", "Sequence numbers that never arrived.", udpOnly: true);
        Row(L, "Loss events", c => I(c.Rx.LossEvents), "Separate runs of consecutive lost packets.", udpOnly: true);
        Row(L, "Longest loss burst", c => c.Rx.LossEvents > 0 ? Pk(c.Rx.LongestLossBurst, "#,0") : "–", udpOnly: true);
        Row(L, "Average loss burst", c => c.Rx.LossEvents > 0 ? Pk(Math.Round((double)c.Rx.Lost / c.Rx.LossEvents, 1)) : "–",
            "1 = scattered random loss; long bursts point to outages, buffer overflows or radio fades.", udpOnly: true);
        Row(L, "Burst lengths 1 / 2-3 / 4-10 / 11-100 / >100", c => string.Join(" / ", c.Rx.BurstHist.Select(I)), udpOnly: true);

        Row(O, "Out of order", c => Pct(c.Rx.OutOfOrder, c.Rx.Unique), "Arrived after a later packet of the same stream.", udpOnly: true);
        Row(O, "Reorder distance avg / max", c => c.Rx.OutOfOrder > 0 ? $"{N((double)c.Rx.ReorderSum / c.Rx.OutOfOrder, "0.#")} / {Pk(c.Rx.MaxReorder, "#,0")}" : "–",
            "How many newer packets had already arrived when a reordered one came in.", udpOnly: true);
        Row(O, "Duplicates", c => Pct(c.Rx.Duplicates, c.Rx.Packets), udpOnly: true);
        Row(O, "Very late (> 8192 behind)", c => I(c.Rx.Late), udpOnly: true);

        Row(D, "Jitter avg / peak", c => c.Rx.Packets > 1 ? $"{Ms(c.Rx.JitterUs)} / {Ms(c.Rx.PeakJitterUs)}" : "–", "RFC 3550 inter-arrival jitter.");
        Row(D, "Delay variation avg", c => c.Rx.DelayCount > 0 ? Ms(c.Rx.DelaySumUs / c.Rx.DelayCount) : "–",
            "One-way delay above its recent minimum: queueing and buffering on the path (TCP adds its own send-buffer wait).");
        Row(D, "Delay variation p50 / p95 / p99", c => c.Rx.DelayCount > 0
            ? $"{Le(c.Rx.DelayPercentileMs(50))} / {Le(c.Rx.DelayPercentileMs(95))} / {Le(c.Rx.DelayPercentileMs(99))} ms" : "–",
            "Half, 95% and 99% of packets were delayed by at most this much above the minimum (from a histogram, so shown as upper bounds)."); 
        Row(D, "Delay variation max", c => c.Rx.DelayCount > 0 ? Ms(c.Rx.DelayMaxUs) : "–");
        Row(D, "Longest silence", c => c.Rx.Packets > 0 ? Ms(c.Rx.MaxGapUs) : "–", "Longest time without any packet (heartbeats arrive every 50 ms).");
        Row(D, "Stalls ≥ 100 ms / ≥ 1 s", c => $"{I(c.Rx.Stalls100ms)} / {I(c.Rx.Stalls1s)}");

        Row(K, "Streams", c => c.Tcp != null ? c.Tcp.Sockets.ToString(Inv) : "–", tcpOnly: true);
        Row(K, "RTT / min RTT", c => c.Tcp != null ? $"{(c.Tcp.RttUs is double r ? Ms(r) : "–")} / {(c.Tcp.MinRttUs is double m ? Ms(m) : "–")}" : "–",
            "Kernel's smoothed round-trip time, including data queued in the network.", tcpOnly: true);
        Row(K, "Congestion window", c => c.Tcp?.CwndBytes is long w ? Bytes(w) : "–", tcpOnly: true);
        Row(K, "Bytes in flight", c => c.Tcp?.InFlightBytes is long f ? Bytes(f) : "–", tcpOnly: true);
        Row(K, "Retransmitted", c => c.Tcp?.BytesRetrans is long r
            ? Bytes(r) + (c.Tcp.BytesSent is long s && s > 0 ? $" ({100.0 * r / s:0.###}%)" : "") : "–",
            "TCP's own loss recovery: what UDP would have lost.", tcpOnly: true);
        Row(K, "Fast retransmits", c => Opt(c.Tcp?.FastRetrans), tcpOnly: true);
        Row(K, "Retransmission timeouts", c => Opt(c.Tcp?.Timeouts), "Recovery by timeout instead of fast retransmit: a stall of at least one RTO (≥ 200 ms).", tcpOnly: true);
        Row(K, "Duplicate ACKs received", c => Opt(c.Tcp?.DupAcksIn), tcpOnly: true);
        Row(K, "Reordering seen", c => Opt(c.Tcp?.ReorderEvents), tcpOnly: true);
        Row(K, "MSS", c => c.Tcp?.Mss is int m ? $"{m} B" : "–", "Maximum segment size: the path MTU minus IP and TCP headers.", tcpOnly: true);
        return rows;
    }

    /// <summary>Plain-text table (CLI, logs).</summary>
    public string Format()
    {
        var rows = Rows();
        if (rows.Count == 0) return "";
        int nameW = Math.Max(10, rows.Max(r => r.name.Length));
        int[] w = Columns.Select((c, i) => Math.Max(c.Title.Length, rows.Max(r => r.values[i].Length))).ToArray();
        var sb = new StringBuilder();
        sb.Append("".PadRight(nameW));
        for (int i = 0; i < Columns.Count; i++) sb.Append("  ").Append(Columns[i].Title.PadLeft(w[i]));
        sb.AppendLine();
        string? section = null;
        foreach (var (sec, name, values, _) in rows)
        {
            if (sec != section) { sb.AppendLine($"-- {sec}"); section = sec; }
            sb.Append(name.PadRight(nameW));
            for (int i = 0; i < values.Length; i++) sb.Append("  ").Append(values[i].PadLeft(w[i]));
            sb.AppendLine();
        }
        if (!ServerDetail) sb.AppendLine("(upload detail needs a 1.8+ server)");
        return sb.ToString();
    }
}
