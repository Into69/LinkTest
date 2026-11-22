using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using LinkTest.Core;
using LinkTest.Server;

namespace LinkTest.Web;

/// <summary>
/// Lets browser SNMP tests reach the server on its browser SNMP ports (UDP 1161 / 1162: browsers refuse WebRTC to
/// ports below 1024, so 161 / 162 can't be used), where an SNMP agent answers too.
/// The SNMP test's WebRTC connections are bound to a loopback port; WebRTC datagrams (STUN / DTLS) arriving on the
/// SNMP ports are relayed there, one loopback socket per browser address, and the answers go back out from the
/// SNMP port. SNMP itself (first byte 0x30) stays with the agent.
/// </summary>
sealed class SnmpRtcBridge : IDisposable
{
    sealed class Relay
    {
        public required Socket Socket;
        public required int PortIndex;
        public required IPEndPoint Remote;
        public long LastUs = Clock.NowUs;
    }

    const int MaxRelays = 256;
    readonly SnmpServer _snmp;
    readonly ConcurrentDictionary<(int, IPEndPoint), Relay> _relays = new();
    readonly IPEndPoint _target;
    readonly CancellationTokenSource _cts = new();

    /// <summary>Loopback UDP port the SNMP test's WebRTC connections listen on.</summary>
    public int LoopbackPort { get; }
    public SnmpServer Snmp => _snmp;

    public SnmpRtcBridge(SnmpServer snmp)
    {
        _snmp = snmp;
        using (var probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
        {
            probe.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            LoopbackPort = ((IPEndPoint)probe.LocalEndPoint!).Port;
        }
        _target = new IPEndPoint(IPAddress.Loopback, LoopbackPort);
        snmp.OtherPacket += OnOther;
        _ = CleanupLoopAsync();
    }

    // RFC 7983: 0-3 STUN, 20-63 DTLS (0x30 is SNMP's SEQUENCE and handled by the agent).
    static bool IsStun(ReadOnlySpan<byte> d) => d.Length >= 20 && d[0] <= 1 && d[4] == 0x21 && d[5] == 0x12 && d[6] == 0xA4 && d[7] == 0x42;
    static bool IsDtls(ReadOnlySpan<byte> d) => d.Length >= 13 && d[0] is >= 20 and <= 63;

    void OnOther(int portIndex, ReadOnlyMemory<byte> data, IPEndPoint from)
    {
        var d = data.Span;
        var key = (portIndex, from);
        if (!_relays.TryGetValue(key, out var r))
        {
            if (!IsStun(d) || _relays.Count >= MaxRelays) return;      // connections start with a STUN check
            var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            Protocol.DisableUdpConnReset(s);
            s.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            r = new Relay { Socket = s, PortIndex = portIndex, Remote = from };
            if (!_relays.TryAdd(key, r)) { s.Dispose(); r = _relays[key]; }
            else new Thread(() => ReturnLoop(r)) { IsBackground = true, Name = "snmp-rtc-relay" }.Start();
        }
        else if (!IsStun(d) && !IsDtls(d)) return;
        r.LastUs = Clock.NowUs;
        try { r.Socket.SendTo(d, SocketFlags.None, _target); } catch { }
    }

    void ReturnLoop(Relay r)
    {
        var buf = new byte[65536];
        EndPoint from = new IPEndPoint(IPAddress.Any, 0);
        while (!_cts.IsCancellationRequested)
        {
            int n;
            try { n = r.Socket.ReceiveFrom(buf, ref from); }
            catch (SocketException) { continue; }
            catch (ObjectDisposedException) { return; }
            if (!from.Equals(_target)) continue;
            r.LastUs = Clock.NowUs;
            _snmp.Send(r.PortIndex, buf.AsSpan(0, n), r.Remote);
        }
    }

    async Task CleanupLoopAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(30), _cts.Token);
                long now = Clock.NowUs;
                foreach (var (k, r) in _relays)
                    if (now - r.LastUs > 90_000_000 && _relays.TryRemove(k, out _))
                        try { r.Socket.Dispose(); } catch { }
            }
        }
        catch (OperationCanceledException) { }
    }

    public void Dispose()
    {
        _snmp.OtherPacket -= OnOther;
        _cts.Cancel();
        foreach (var r in _relays.Values) try { r.Socket.Dispose(); } catch { }
        _relays.Clear();
    }
}
