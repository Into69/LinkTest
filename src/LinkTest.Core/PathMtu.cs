using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace LinkTest.Core;

/// <summary>Result of a path-MTU test. Sizes are UDP payload bytes; path MTU adds the IP + UDP headers.</summary>
public sealed record MtuResult(int UpPayload, int DownPayload, int HeaderBytes, bool Ipv6, string? Error = null)
{
    public int UpPathMtu => UpPayload > 0 ? UpPayload + HeaderBytes : 0;
    public int DownPathMtu => DownPayload > 0 ? DownPayload + HeaderBytes : 0;
    /// <summary>The smaller of both directions - what a tunnel must fit into.</summary>
    public int PathMtu => UpPathMtu > 0 && DownPathMtu > 0 ? Math.Min(UpPathMtu, DownPathMtu) : Math.Max(UpPathMtu, DownPathMtu);
    public bool Ok => Error == null && PathMtu > 0;

    public string Summary => Error != null ? "MTU test: " + Error
        : $"Path MTU: up {Fmt(UpPathMtu, UpPayload)}, down {Fmt(DownPathMtu, DownPayload)}" +
          (PathMtu < 1500 && !Ipv6 ? " - smaller than the usual 1500 (PPPoE, tunnel or VPN on the path?)" : "");

    static string Fmt(int mtu, int payload) => mtu > 0 ? $"{mtu} bytes" + (payload >= LinkTest.Core.PathMtu.MaxProbePayload ? "+" : "") : "unknown";
}

/// <summary>Encapsulation overhead of common tunnel types, for MTU recommendations.</summary>
public sealed record VpnOverhead(string Name, int Ipv4Bytes, int Ipv6Bytes, string Note)
{
    public static readonly IReadOnlyList<VpnOverhead> All = new VpnOverhead[]
    {
        new("WireGuard", 60, 80, "IP + UDP 8 + WireGuard 32"),
        new("IPsec ESP AES-GCM (NAT-T)", 65, 85, "IP + UDP 8 + ESP 8 + IV 8 + ICV 16 + pad/trailer ≤5"),
        new("IPsec ESP AES-CBC/SHA1 (NAT-T)", 81, 101, "IP + UDP 8 + ESP 8 + IV 16 + ICV 12 + pad/trailer ≤17"),
        new("OpenVPN UDP (AES-GCM)", 52, 72, "IP + UDP 8 + opcode/peer 4 + packet-id 4 + tag 16"),
        new("GRE", 24, 44, "IP + GRE 4"),
    };

    public int Overhead(bool ipv6) => ipv6 ? Ipv6Bytes : Ipv4Bytes;
    public int TunnelMtu(int pathMtu, bool ipv6) => pathMtu - Overhead(ipv6);
    /// <summary>TCP MSS for IPv4 traffic inside the tunnel (inner IP 20 + TCP 20).</summary>
    public int TcpMss(int pathMtu, bool ipv6) => TunnelMtu(pathMtu, ipv6) - 40;
}

/// <summary>
/// Finds the largest datagram that crosses the path unfragmented in each direction, using don't-fragment
/// probes on a dedicated socket (binary search with retries, so random loss is not mistaken for "too big").
/// Needs a 1.6+ server.
/// </summary>
public static class PathMtu
{
    public const int MaxProbePayload = 8972;     // 9000-byte jumbo frame minus IPv4 + UDP

    public static async Task<MtuResult> MeasureAsync(IPEndPoint server, uint sessionId, double rttMs, CancellationToken ct)
    {
        bool v6 = server.AddressFamily == AddressFamily.InterNetworkV6;
        int header = v6 ? 48 : 28;
        int minPayload = (v6 ? 1280 : 576) - header;          // guaranteed by the IP standards
        using var sock = new Socket(server.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        Protocol.DisableUdpConnReset(sock);
        sock.Bind(new IPEndPoint(v6 ? IPAddress.IPv6Any : IPAddress.Any, 0));
        if (!Protocol.SetDontFragment(sock))
            return new MtuResult(0, 0, header, v6, "don't-fragment is not supported on this system");

        var pending = new ConcurrentDictionary<long, TaskCompletionSource<int>>();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var rx = Task.Run(() => ReceiveLoop(sock, sessionId, pending, cts.Token));
        long nextId = 0;
        int timeoutMs = (int)Math.Clamp(double.IsFinite(rttMs) ? rttMs * 3 + 150 : 400, 250, 2000);

        async Task<bool> Probe(bool up, int size)
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                long id = Interlocked.Increment(ref nextId);
                var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
                pending[id] = tcs;
                int len = up ? size : Protocol.HeaderSize;
                var buf = new byte[len];
                new PacketHeader
                {
                    Type = up ? PacketType.MtuProbe : PacketType.MtuRequest, SessionId = sessionId,
                    StreamId = (ushort)size, Seq = id, SendUs = Clock.NowUs, Length = len,
                }.Write(buf);
                try { await sock.SendToAsync(buf, SocketFlags.None, server, cts.Token); }
                catch (SocketException e) when (e.SocketErrorCode == SocketError.MessageSize)
                {
                    pending.TryRemove(id, out _);
                    return false;                                   // larger than the local interface MTU
                }
                catch (SocketException) { pending.TryRemove(id, out _); continue; }
                var done = await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs, cts.Token));
                pending.TryRemove(id, out _);
                if (done == tcs.Task && tcs.Task.Result == size) return true;
            }
            return false;
        }

        async Task<int> Search(bool up)
        {
            if (!await Probe(up, minPayload)) return -1;
            int lo = minPayload, hi = MaxProbePayload + 1;          // lo passes, hi fails
            int common = 1500 - header;
            if (common > lo && common < hi) { if (await Probe(up, common)) lo = common; else hi = common; }
            while (hi - lo > 1)
            {
                int mid = (lo + hi) / 2;
                if (await Probe(up, mid)) lo = mid; else hi = mid;
            }
            return lo;
        }

        try
        {
            var upTask = Search(up: true);
            var downTask = Search(up: false);
            int upP = await upTask, downP = await downTask;
            if (upP < 0 && downP < 0)
                return new MtuResult(0, 0, header, v6, "no replies - the server is older than 1.6, or UDP is blocked");
            return new MtuResult(Math.Max(0, upP), Math.Max(0, downP), header, v6);
        }
        finally
        {
            cts.Cancel();
            try { sock.Close(); } catch { }
            try { await rx; } catch { }
        }
    }

    static async Task ReceiveLoop(Socket sock, uint sessionId, ConcurrentDictionary<long, TaskCompletionSource<int>> pending, CancellationToken ct)
    {
        var buf = new byte[65536];
        EndPoint any = new IPEndPoint(sock.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, 0);
        while (!ct.IsCancellationRequested)
        {
            SocketReceiveFromResult r;
            try { r = await sock.ReceiveFromAsync(buf, SocketFlags.None, any, ct); }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) { if (ct.IsCancellationRequested) break; continue; }
            if (!PacketHeader.TryRead(buf.AsSpan(0, r.ReceivedBytes), out var h) || h.SessionId != sessionId) continue;
            int size = h.Type switch
            {
                PacketType.MtuAck => h.StreamId,                  // server reports how many bytes arrived
                PacketType.MtuDown => r.ReceivedBytes == h.Length ? h.Length : -1,
                _ => -1,
            };
            if (size > 0 && pending.TryRemove(h.Seq, out var tcs)) tcs.TrySetResult(size);
        }
    }
}
