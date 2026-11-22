using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using LinkTest.Core;
using LinkTest.Core.Snmp;

namespace LinkTest.Server;

/// <summary>
/// The server side of the SNMP test: a minimal SNMPv2c agent on UDP 161 and notification receiver on UDP 162.
/// It only answers messages with the community of a running test (each test gets a random one), answers with about
/// as many bytes as it received and limits each test's rate, so it can't be used to reflect or amplify traffic. Datagrams that
/// aren't SNMP (WebRTC's STUN / DTLS from browser tests on the same ports) go to <see cref="OtherPacket"/>.
/// </summary>
public sealed class SnmpServer : IDisposable
{
    sealed class Registration
    {
        public required string Tag;
        public long Polls, Informs, Answered;
        public double Tokens = 50;
        public long LastRefillUs = Clock.NowUs;
    }

    readonly ConcurrentDictionary<string, Registration> _tests = new();
    readonly Socket?[] _socks = new Socket?[2];
    readonly CancellationTokenSource _cts = new();
    readonly long _startUs = Clock.NowUs;
    long _lastUnknownLogUs;

    /// <summary>UDP ports: [0] = agent (161), [1] = notifications (162); 0 where the port couldn't be opened.</summary>
    public int[] Ports { get; } = new int[2];
    public int PollPort => Ports[0];
    public int TrapPort => Ports[1];
    public bool Available => PollPort != 0 || TrapPort != 0;

    /// <summary>A datagram that isn't SNMP: (port index, data, sender). Called on the receive thread.</summary>
    public event Action<int, ReadOnlyMemory<byte>, IPEndPoint>? OtherPacket;

    public SnmpServer(IPAddress? bind, int pollPort, int trapPort)
    {
        int[] want = { pollPort, trapPort };
        for (int i = 0; i < 2; i++)
        {
            if (want[i] <= 0) continue;
            try
            {
                _socks[i] = Bind(bind, want[i]);
                Ports[i] = want[i];
            }
            catch (SocketException e)
            {
                ServerHost.Log($"SNMP test: can't open UDP {want[i]} ({(e.SocketErrorCode == SocketError.AddressAlreadyInUse
                    ? (i == 1 ? "in use - the Windows SNMP Trap service or another SNMP manager is running" : "in use - the Windows SNMP service or another SNMP agent is running")
                    : e.SocketErrorCode == SocketError.AccessDenied ? "needs administrator / root rights" : e.Message)})");
            }
        }
        for (int i = 0; i < 2; i++)
            if (_socks[i] != null)
            {
                int idx = i;
                new Thread(() => ReceiveLoop(idx)) { IsBackground = true, Name = $"snmp-{Ports[idx]}" }.Start();
            }
    }

    static Socket Bind(IPAddress? bind, int port)
    {
        if (bind == null)
        {
            try
            {
                var s6 = new Socket(AddressFamily.InterNetworkV6, SocketType.Dgram, ProtocolType.Udp) { DualMode = true };
                try { Protocol.DisableUdpConnReset(s6); s6.Bind(new IPEndPoint(IPAddress.IPv6Any, port)); return s6; }
                catch (SocketException e) when (e.SocketErrorCode is SocketError.AddressFamilyNotSupported or SocketError.ProtocolNotSupported) { s6.Dispose(); }
                catch { s6.Dispose(); throw; }
            }
            catch (SocketException e) when (e.SocketErrorCode is SocketError.AddressFamilyNotSupported or SocketError.ProtocolNotSupported) { }
            bind = IPAddress.Any;
        }
        var s = new Socket(bind.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        try { Protocol.DisableUdpConnReset(s); s.Bind(new IPEndPoint(bind, port)); return s; }
        catch { s.Dispose(); throw; }
    }

    /// <summary>Registers a test; returns the community string its SNMP messages must carry.</summary>
    public string Register(string tag)
    {
        string community = "linktest-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant();
        _tests[community] = new Registration { Tag = tag };
        return community;
    }

    /// <summary>Ends a test's registration; returns (polls, informs, answered) it received.</summary>
    public (long polls, long informs, long answered) Unregister(string community) =>
        _tests.TryRemove(community, out var r) ? (r.Polls, r.Informs, r.Answered) : (0, 0, 0);

    public void Send(int portIndex, ReadOnlySpan<byte> data, EndPoint to)
    {
        try { _socks[portIndex]?.SendTo(data, SocketFlags.None, to); } catch { }
    }

    void ReceiveLoop(int idx)
    {
        var s = _socks[idx]!;
        var buf = new byte[65536];
        EndPoint any = new IPEndPoint(s.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, 0);
        while (!_cts.IsCancellationRequested)
        {
            int n;
            EndPoint from = any;
            try { n = s.ReceiveFrom(buf, ref from); }
            catch (SocketException) { continue; }
            catch (ObjectDisposedException) { return; }
            if (n == 0) continue;
            var ep = (IPEndPoint)from;
            try
            {
                if (buf[0] == 0x30) HandleSnmp(idx, buf.AsSpan(0, n), ep);
                else OtherPacket?.Invoke(idx, buf.AsMemory(0, n), ep);
            }
            catch (Exception e) { ServerHost.Log("SNMP test: " + e.Message); }
        }
    }

    void HandleSnmp(int idx, ReadOnlySpan<byte> pkt, IPEndPoint ep)
    {
        if (!SnmpCodec.TryParse(pkt, out var m) || m == null) return;
        if (!_tests.TryGetValue(m.Community, out var reg))
        {
            long now = Clock.NowUs;
            if (now - Interlocked.Read(ref _lastUnknownLogUs) > 60_000_000)
            {
                Interlocked.Exchange(ref _lastUnknownLogUs, now);
                ServerHost.Log($"SNMP test: ignored {SnmpCodec.PduName(m.PduType)} on UDP {Ports[idx]} from {Protocol.Normalize(ep.Address)} (not a running test's community)");
            }
            return;
        }
        // Rate limit per test: 50 datagrams burst, 20 per second (the test sends ~1.2 per second, more with retries).
        lock (reg)
        {
            long now = Clock.NowUs;
            reg.Tokens = Math.Min(50, reg.Tokens + (now - reg.LastRefillUs) / 1e6 * 20);
            reg.LastRefillUs = now;
            if (reg.Tokens < 1) return;
            reg.Tokens--;
        }
        byte[]? reply = m.PduType switch
        {
            SnmpCodec.GetRequest or SnmpCodec.GetNextRequest => AnswerGet(m),
            SnmpCodec.InformRequest => SnmpCodec.Build(m.Community, SnmpCodec.Response, m.RequestId, m.VarBinds),
            _ => null,                                     // traps and anything else: received, no answer
        };
        if (m.PduType is SnmpCodec.GetRequest or SnmpCodec.GetNextRequest) Interlocked.Increment(ref reg.Polls);
        else if (m.PduType is SnmpCodec.InformRequest or SnmpCodec.TrapV2) Interlocked.Increment(ref reg.Informs);
        // The values make an answer a little longer than the request's NULLs; never much more than was sent.
        if (reply == null || reply.Length > pkt.Length + 64) return;
        Send(idx, reply, ep);
        Interlocked.Increment(ref reg.Answered);
    }

    byte[] AnswerGet(SnmpMessage m)
    {
        uint ticks = (uint)((Clock.NowUs - _startUs) / 10_000);
        var vbs = m.VarBinds.Select(v => v.Oid switch
        {
            SnmpCodec.SysUpTime => SnmpVarBind.TimeTicks(v.Oid, ticks),
            SnmpCodec.SysName => SnmpVarBind.Text(v.Oid, "lt"),
            SnmpCodec.IfInOctets1 or SnmpCodec.IfOutOctets1 => SnmpVarBind.Counter32(v.Oid, ticks),
            _ => SnmpVarBind.NoSuchObject(v.Oid),
        }).ToList();
        return SnmpCodec.Build(m.Community, SnmpCodec.Response, m.RequestId, vbs);
    }

    public void Dispose()
    {
        _cts.Cancel();
        foreach (var s in _socks) try { s?.Dispose(); } catch { }
    }
}
