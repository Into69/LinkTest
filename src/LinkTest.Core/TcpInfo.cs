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
}
