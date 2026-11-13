using System.Buffers.Binary;
using System.Net.Sockets;

namespace LinkTest.Core;

/// <summary>Reads kernel TCP statistics via SIO_TCP_INFO (Windows 10 1703+).</summary>
public static class TcpInfo
{
    const int SIO_TCP_INFO = unchecked((int)0xD8000027);

    public readonly record struct Info(long BytesRetrans, int TimeoutEpisodes, int RttUs);

    public static bool TryGet(Socket s, out Info info)
    {
        info = default;
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            var outBuf = new byte[88];  // sizeof(TCP_INFO_v0)
            int n = s.IOControl(SIO_TCP_INFO, BitConverter.GetBytes(0), outBuf);
            if (n < 84) return false;
            info = new Info(
                BytesRetrans: BinaryPrimitives.ReadUInt32LittleEndian(outBuf.AsSpan(68)),
                TimeoutEpisodes: (int)BinaryPrimitives.ReadUInt32LittleEndian(outBuf.AsSpan(80)),
                RttUs: (int)BinaryPrimitives.ReadUInt32LittleEndian(outBuf.AsSpan(20)));
            return true;
        }
        catch { return false; }
    }

    public static (long retransBytes, int timeouts) Sum(IEnumerable<Socket> sockets)
    {
        long r = 0; int t = 0;
        foreach (var s in sockets)
            if (TryGet(s, out var i)) { r += i.BytesRetrans; t += i.TimeoutEpisodes; }
        return (r, t);
    }
}
