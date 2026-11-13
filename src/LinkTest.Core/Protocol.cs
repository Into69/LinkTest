using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace LinkTest.Core;

public static class Protocol
{
    public const ushort Magic = 0x544C;          // "LT"
    public const byte Version = 1;
    public const int HeaderSize = 48;
    public const int MaxPacket = 65000;
    public const int DefaultPort = 7447;

    /// <summary>First byte written on every TCP connection so the server knows what it is.</summary>
    public const byte ConnControl = (byte)'C';
    public const byte ConnStream = (byte)'S';

    /// <summary>How often each sender embeds a stats report in its packets.</summary>
    public const long ReportIntervalUs = 100_000;
    /// <summary>Maximum silence on a data stream; a small heartbeat is sent if nothing else went out.</summary>
    public const long HeartbeatIntervalUs = 50_000;

    public static IPAddress Normalize(IPAddress a) => a.IsIPv4MappedToIPv6 ? a.MapToIPv4() : a;

    /// <summary>Stop Windows from failing UDP receives with WSAECONNRESET after an ICMP port-unreachable.</summary>
    public static void DisableUdpConnReset(Socket s)
    {
        if (!OperatingSystem.IsWindows()) return;
        const int SIO_UDP_CONNRESET = unchecked((int)0x9800000C);
        try { s.IOControl(SIO_UDP_CONNRESET, new byte[] { 0, 0, 0, 0 }, null); } catch { }
    }

    /// <summary>
    /// Sets the don't-fragment bit (IPv4) / IPV6_DONTFRAG so oversized datagrams are dropped (or rejected
    /// locally with MessageSize) instead of being fragmented. Best effort; returns false if unsupported.
    /// </summary>
    public static bool SetDontFragment(Socket s)
    {
        bool ok = false;
        Span<byte> one = stackalloc byte[4];
        BitConverter.TryWriteBytes(one, 1);
        Span<byte> pmtuDo = stackalloc byte[4];
        BitConverter.TryWriteBytes(pmtuDo, 2);   // IP_PMTUDISC_DO
        bool v6 = s.AddressFamily == AddressFamily.InterNetworkV6;
        try
        {
            if (OperatingSystem.IsWindows())
            {
                // IP_DONTFRAGMENT = 14 (also applies to IPv4-mapped traffic on dual-mode sockets), IPV6_DONTFRAG = 14
                if (!v6 || s.DualMode) { s.SetRawSocketOption(0, 14, one); ok = true; }
                if (v6) { s.SetRawSocketOption(41, 14, one); ok = true; }
            }
            else
            {
                // Linux / Android: IP_MTU_DISCOVER = 10 (value IP_PMTUDISC_DO), IPV6_DONTFRAG = 62
                if (!v6 || s.DualMode) { s.SetRawSocketOption(0, 10, pmtuDo); ok = true; }
                if (v6) { s.SetRawSocketOption(41, 62, one); ok = true; }
            }
        }
        catch { }
        return ok;
    }

    public static void TuneUdp(Socket s)
    {
        try { s.ReceiveBufferSize = 8 * 1024 * 1024; } catch { }
        try { s.SendBufferSize = 4 * 1024 * 1024; } catch { }
        DisableUdpConnReset(s);
    }

    public static void SendAll(Socket s, byte[] buf, int len)
    {
        int off = 0;
        while (off < len)
        {
            int n = s.Send(buf, off, len - off, SocketFlags.None);
            if (n <= 0) throw new SocketException((int)SocketError.ConnectionReset);
            off += n;
        }
    }

    public static bool ReceiveExact(Socket s, byte[] buf, int off, int count)
    {
        while (count > 0)
        {
            int n = s.Receive(buf, off, count, SocketFlags.None);
            if (n <= 0) return false;
            off += n; count -= n;
        }
        return true;
    }

    public static async Task<bool> ReceiveExactAsync(Socket s, byte[] buf, CancellationToken ct)
    {
        int off = 0;
        while (off < buf.Length)
        {
            int n = await s.ReceiveAsync(buf.AsMemory(off), SocketFlags.None, ct);
            if (n <= 0) return false;
            off += n;
        }
        return true;
    }
}

public enum PacketType : byte
{
    Data = 1,
    ProbeReq = 2,
    ProbeResp = 3,
    /// <summary>Path-MTU test, upstream: client sends a don't-fragment datagram of the probed size (1.6+).</summary>
    MtuProbe = 4,
    /// <summary>Server's reply to <see cref="MtuProbe"/>: header only, StreamId = size received.</summary>
    MtuAck = 5,
    /// <summary>Path-MTU test, downstream: client asks for a datagram of StreamId bytes.</summary>
    MtuRequest = 6,
    /// <summary>Server's don't-fragment datagram of the requested size.</summary>
    MtuDown = 7,
}

[Flags]
public enum PacketFlags : ushort { None = 0, HasReport = 1, Heartbeat = 2 }

/// <summary>
/// 48-byte little-endian header shared by data and tunnel-probe packets.
/// For probes, StreamId = tunnel index and Flags = probe kind (0 = keepalive, n = handshake step n).
/// </summary>
public struct PacketHeader
{
    public PacketType Type;
    public uint SessionId;
    public ushort StreamId;
    public ushort Flags;
    public long Seq;
    public long SendUs;      // sender's clock
    public long EchoUs;      // SendUs of the newest packet the sender has received from its peer
    public long EchoHoldUs;  // how long the sender held that before sending this one
    public int Length;       // total packet length including header

    public readonly void Write(Span<byte> b)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(b, Protocol.Magic);
        b[2] = Protocol.Version;
        b[3] = (byte)Type;
        BinaryPrimitives.WriteUInt32LittleEndian(b[4..], SessionId);
        BinaryPrimitives.WriteUInt16LittleEndian(b[8..], StreamId);
        BinaryPrimitives.WriteUInt16LittleEndian(b[10..], Flags);
        BinaryPrimitives.WriteInt64LittleEndian(b[12..], Seq);
        BinaryPrimitives.WriteInt64LittleEndian(b[20..], SendUs);
        BinaryPrimitives.WriteInt64LittleEndian(b[28..], EchoUs);
        BinaryPrimitives.WriteInt64LittleEndian(b[36..], EchoHoldUs);
        BinaryPrimitives.WriteInt32LittleEndian(b[44..], Length);
    }

    public static bool TryRead(ReadOnlySpan<byte> b, out PacketHeader h)
    {
        h = default;
        if (b.Length < Protocol.HeaderSize) return false;
        if (BinaryPrimitives.ReadUInt16LittleEndian(b) != Protocol.Magic || b[2] != Protocol.Version) return false;
        h.Type = (PacketType)b[3];
        h.SessionId = BinaryPrimitives.ReadUInt32LittleEndian(b[4..]);
        h.StreamId = BinaryPrimitives.ReadUInt16LittleEndian(b[8..]);
        h.Flags = BinaryPrimitives.ReadUInt16LittleEndian(b[10..]);
        h.Seq = BinaryPrimitives.ReadInt64LittleEndian(b[12..]);
        h.SendUs = BinaryPrimitives.ReadInt64LittleEndian(b[20..]);
        h.EchoUs = BinaryPrimitives.ReadInt64LittleEndian(b[28..]);
        h.EchoHoldUs = BinaryPrimitives.ReadInt64LittleEndian(b[36..]);
        h.Length = BinaryPrimitives.ReadInt32LittleEndian(b[44..]);
        return h.Length >= Protocol.HeaderSize && h.Length <= Protocol.MaxPacket;
    }
}
