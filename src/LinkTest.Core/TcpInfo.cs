using System.Buffers.Binary;
using System.Net.Sockets;

namespace LinkTest.Core;

/// <summary>
/// Kernel TCP statistics: SIO_TCP_INFO on Windows 10 1703+, getsockopt(TCP_INFO) on Linux / Android.
/// </summary>
public static class TcpInfo
{
    const int SIO_TCP_INFO = unchecked((int)0xD8000027);

    public readonly record struct Info(long BytesRetrans, int TimeoutEpisodes, int RttUs);

    public static bool TryGet(Socket s, out Info info)
    {
        info = default;
        try
        {
            if (OperatingSystem.IsWindows()) return TryGetWindows(s, out info);
            if (OperatingSystem.IsLinux() || OperatingSystem.IsAndroid()) return TryGetLinux(s, out info);
        }
        catch { }
        return false;
    }

    static bool TryGetWindows(Socket s, out Info info)
    {
        info = default;
        var outBuf = new byte[88];  // sizeof(TCP_INFO_v0)
        int n = s.IOControl(SIO_TCP_INFO, BitConverter.GetBytes(0), outBuf);
        if (n < 84) return false;
        info = new Info(
            BytesRetrans: BinaryPrimitives.ReadUInt32LittleEndian(outBuf.AsSpan(68)),
            TimeoutEpisodes: (int)BinaryPrimitives.ReadUInt32LittleEndian(outBuf.AsSpan(80)),
            RttUs: (int)BinaryPrimitives.ReadUInt32LittleEndian(outBuf.AsSpan(20)));
        return true;
    }

    /// <summary>struct tcp_info (linux/tcp.h). Fields are native-endian; all supported Android ABIs are little-endian.</summary>
    static bool TryGetLinux(Socket s, out Info info)
    {
        info = default;
        var buf = new byte[232];
        int n = s.GetRawSocketOption(6 /* IPPROTO_TCP */, 11 /* TCP_INFO */, buf);
        if (n < 104) return false;
        var b = buf.AsSpan();
        int mss = (int)BinaryPrimitives.ReadUInt32LittleEndian(b[16..]);           // tcpi_snd_mss
        long totalRetrans = BinaryPrimitives.ReadUInt32LittleEndian(b[100..]);     // tcpi_total_retrans (segments)
        long bytesRetrans = n >= 216
            ? (long)BinaryPrimitives.ReadUInt64LittleEndian(b[208..])              // tcpi_bytes_retrans (kernel 4.19+)
            : totalRetrans * mss;
        info = new Info(
            BytesRetrans: bytesRetrans,
            TimeoutEpisodes: b[4],                                                   // tcpi_backoff (current RTO backoff)
            RttUs: (int)BinaryPrimitives.ReadUInt32LittleEndian(b[68..]));          // tcpi_rtt
        return true;
    }

    public static (long retransBytes, int timeouts) Sum(IEnumerable<Socket> sockets)
    {
        long r = 0; int t = 0;
        foreach (var s in sockets)
            if (TryGet(s, out var i)) { r += i.BytesRetrans; t += i.TimeoutEpisodes; }
        return (r, t);
    }

    /// <summary>Full kernel figures for a set of sending sockets (null when none could be read).</summary>
    public static TcpDetail? Detail(IEnumerable<Socket> sockets)
    {
        var list = new List<TcpDetail>();
        foreach (var s in sockets)
        {
            try
            {
                var d = OperatingSystem.IsWindows() ? DetailWindows(s)
                      : OperatingSystem.IsLinux() || OperatingSystem.IsAndroid() ? DetailLinux(s) : null;
                if (d != null) list.Add(d);
            }
            catch { }
        }
        return list.Count == 0 ? null : Combine(list);
    }

    /// <summary>Sums counters and windows; averages RTT; takes the lowest min-RTT and MSS.</summary>
    public static TcpDetail Combine(IReadOnlyList<TcpDetail> list)
    {
        static long? Sum(IEnumerable<long?> v) { long t = 0; bool any = false; foreach (var x in v) if (x is long y) { t += y; any = true; } return any ? t : null; }
        var rtts = list.Where(x => x.RttUs != null).Select(x => x.RttUs!.Value).ToList();
        var mins = list.Where(x => x.MinRttUs is > 0).Select(x => x.MinRttUs!.Value).ToList();
        var mss = list.Where(x => x.Mss is > 0).Select(x => x.Mss!.Value).ToList();
        return new TcpDetail
        {
            Sockets = list.Sum(x => x.Sockets),
            RttUs = rtts.Count > 0 ? rtts.Average() : null,
            MinRttUs = mins.Count > 0 ? mins.Min() : null,
            CwndBytes = Sum(list.Select(x => x.CwndBytes)),
            InFlightBytes = Sum(list.Select(x => x.InFlightBytes)),
            BytesSent = Sum(list.Select(x => x.BytesSent)),
            BytesRetrans = Sum(list.Select(x => x.BytesRetrans)),
            FastRetrans = Sum(list.Select(x => x.FastRetrans)),
            Timeouts = Sum(list.Select(x => x.Timeouts)),
            DupAcksIn = Sum(list.Select(x => x.DupAcksIn)),
            ReorderEvents = Sum(list.Select(x => x.ReorderEvents)),
            Mss = mss.Count > 0 ? mss.Min() : null,
        };
    }

    /// <summary>TCP_INFO_v0 (Windows 10 1703+).</summary>
    static TcpDetail? DetailWindows(Socket s)
    {
        var b = new byte[88];
        int n = s.IOControl(SIO_TCP_INFO, BitConverter.GetBytes(0), b);
        if (n < 85) return null;
        uint U(int o) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(o));
        uint minRtt = U(24);
        return new TcpDetail
        {
            Sockets = 1,
            Mss = (int)U(4),
            RttUs = U(20),
            MinRttUs = minRtt is 0 or uint.MaxValue ? null : minRtt,
            InFlightBytes = U(28),
            CwndBytes = U(32),
            BytesSent = (long)BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(48)),
            ReorderEvents = null,                 // Windows reports reordered bytes, not events
            BytesRetrans = U(68),
            FastRetrans = U(72),
            DupAcksIn = U(76),
            Timeouts = U(80),
        };
    }

    /// <summary>struct tcp_info (linux/tcp.h).</summary>
    static TcpDetail? DetailLinux(Socket s)
    {
        var buf = new byte[232];
        int n = s.GetRawSocketOption(6, 11, buf);
        if (n < 104) return null;
        var b = buf.AsSpan();
        uint U(int o) => BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(o));
        int mss = (int)U(16);
        long totalRetrans = U(100);
        return new TcpDetail
        {
            Sockets = 1,
            Mss = mss,
            RttUs = U(68),
            MinRttUs = n >= 152 && U(148) is > 0 and < uint.MaxValue ? U(148) : null,
            CwndBytes = (long)U(80) * mss,                                        // tcpi_snd_cwnd is in segments
            InFlightBytes = (long)U(24) * mss,                                    // tcpi_unacked segments
            BytesSent = n >= 208 ? (long)BinaryPrimitives.ReadUInt64LittleEndian(b[200..]) : null,
            BytesRetrans = n >= 216 ? (long)BinaryPrimitives.ReadUInt64LittleEndian(b[208..]) : totalRetrans * mss,
            FastRetrans = null,
            Timeouts = b[4],                                                       // current RTO backoff
            DupAcksIn = null,
            ReorderEvents = n >= 224 ? U(220) : null,                              // tcpi_reord_seen (kernel 4.19+)
        };
    }
}
