using System.Globalization;
using System.Text;

namespace LinkTest.Core;

/// <summary>One sampling interval (normally 1 s) as seen by the client. NaN = not available.</summary>
public sealed class StatSample
{
    public DateTime Time { get; init; }
    public double ElapsedSec { get; init; }
    public double IntervalSec { get; init; }

    // Upload (client → server), measured by the server and reported in-band.
    public double TxMbps { get; init; }                 // what the client actually sent
    public double UpMbps { get; init; } = double.NaN;
    public double UpLossPct { get; init; } = double.NaN;
    public double UpJitterMs { get; init; } = double.NaN;
    public double UpMaxGapMs { get; init; } = double.NaN;
    public long UpLostTotal { get; init; }
    public long UpOutOfOrderTotal { get; init; }
    public double UpLossTotalPct { get; init; } = double.NaN;
    public bool UpReportStale { get; init; }
    public double UpReportAgeSec { get; init; } = double.NaN;

    // Download (server → client), measured locally.
    public double ServerTxMbps { get; init; } = double.NaN;
    public double DownMbps { get; init; }
    public double DownLossPct { get; init; }
    public double DownJitterMs { get; init; }
    public double DownMaxGapMs { get; init; }
    public long DownLostTotal { get; init; }
    public long DownOutOfOrderTotal { get; init; }
    public double DownLossTotalPct { get; init; }

    // Round trip.
    public double RttMs { get; init; } = double.NaN;
    public double RttMinMs { get; init; } = double.NaN;
    public double RttMaxMs { get; init; } = double.NaN;

    // TCP kernel stats (bytes retransmitted during this interval).
    public long UpRetransBytes { get; init; }
    public long DownRetransBytes { get; init; }
    public int UpTimeoutsTotal { get; init; }
    public int DownTimeoutsTotal { get; init; }

    // Mixed mode: throughput per transport (NaN otherwise). Quality figures above come from the UDP traffic.
    public bool Mixed { get; init; }
    public double UpMbpsUdp { get; init; } = double.NaN;
    public double UpMbpsTcp { get; init; } = double.NaN;
    public double DownMbpsUdp { get; init; } = double.NaN;
    public double DownMbpsTcp { get; init; } = double.NaN;
    /// <summary>Mixed mode: RTT measured on the TCP streams (includes TCP queueing).</summary>
    public double RttTcpMs { get; init; } = double.NaN;

    /// <summary>Per traffic class figures (one entry per class, in class order).</summary>
    public IReadOnlyList<ClassStat> Classes { get; init; } = Array.Empty<ClassStat>();

    /// <summary>Estimated VoIP MOS (ITU-T G.107 simplified) from RTT, worst jitter and worst loss.</summary>
    public double Mos { get; init; } = double.NaN;

    public static double EstimateMos(double rttMs, double jitterMs, double lossPct)
    {
        if (!double.IsFinite(rttMs)) return double.NaN;
        if (!double.IsFinite(jitterMs)) jitterMs = 0;
        if (!double.IsFinite(lossPct)) lossPct = 0;
        double eff = rttMs / 2 + 2 * jitterMs + 10;
        double r = 93.2 - (eff < 160 ? eff / 40 : (eff - 120) / 10) - 2.5 * lossPct;
        r = Math.Clamp(r, 0, 100);
        return Math.Clamp(1 + 0.035 * r + 7e-6 * r * (r - 60) * (100 - r), 1, 4.5);
    }
}

/// <summary>One traffic class within a sample. NaN = not available this interval.</summary>
public sealed record ClassStat(
    string Name, TransportKind Transport, int Dscp, double SharePct,
    double UpMbps, double UpLossPct, double UpJitterMs,
    double DownMbps, double DownLossPct, double DownJitterMs,
    double RttMs, long UpRetransBytes, long DownRetransBytes,
    long UpLostTotal, long DownLostTotal);

public sealed class CsvLogger : IDisposable
{
    readonly StreamWriter _w;
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public CsvLogger(string path)
    {
        Path = path;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!);
        _w = new StreamWriter(path, false, new UTF8Encoding(false)) { AutoFlush = true };
        _w.WriteLine("time,elapsed_s,tx_mbps,up_mbps,up_loss_pct,up_jitter_ms,up_max_gap_ms,up_lost_total,up_report_stale," +
                     "down_mbps,down_loss_pct,down_jitter_ms,down_max_gap_ms,down_lost_total," +
                     "rtt_ms,rtt_min_ms,rtt_max_ms,up_retrans_bytes,down_retrans_bytes,mos," +
                     "up_udp_mbps,up_tcp_mbps,down_udp_mbps,down_tcp_mbps,rtt_tcp_ms,tunnels");
    }

    public string Path { get; }

    public void Write(StatSample s, string tunnelStates)
    {
        static string F(double v) => double.IsFinite(v) ? v.ToString("0.###", Inv) : "";
        _w.WriteLine(string.Join(",",
            s.Time.ToString("yyyy-MM-dd HH:mm:ss.fff", Inv), F(s.ElapsedSec), F(s.TxMbps),
            F(s.UpMbps), F(s.UpLossPct), F(s.UpJitterMs), F(s.UpMaxGapMs), s.UpLostTotal, s.UpReportStale ? 1 : 0,
            F(s.DownMbps), F(s.DownLossPct), F(s.DownJitterMs), F(s.DownMaxGapMs), s.DownLostTotal,
            F(s.RttMs), F(s.RttMinMs), F(s.RttMaxMs), s.UpRetransBytes, s.DownRetransBytes, F(s.Mos),
            F(s.UpMbpsUdp), F(s.UpMbpsTcp), F(s.DownMbpsUdp), F(s.DownMbpsTcp), F(s.RttTcpMs),
            "\"" + tunnelStates.Replace("\"", "'") + "\""));
    }

    public void Dispose() => _w.Dispose();
}
