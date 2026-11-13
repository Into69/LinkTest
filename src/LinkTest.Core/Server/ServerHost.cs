using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using LinkTest.Core;

namespace LinkTest.Server;

/// <summary>The LinkTest server: accepts control/stream connections and UDP traffic, and runs sessions.</summary>
public sealed class ServerHost
{
    readonly ServerOptions _opts;
    readonly ConcurrentDictionary<uint, ServerSession> _sessions = new();
    Socket _listener = null!;
    Socket _udp = null!;
    Socket? _udpDf;          // separate don't-fragment socket for downstream path-MTU probes
    volatile bool _stopping;
    static readonly object ConsoleLock = new();

    public ServerHost(ServerOptions opts) => _opts = opts;

    System.Security.Cryptography.X509Certificates.X509Certificate2? _cert;
    /// <summary>SHA-256 fingerprint of the server certificate (clients show it so users can compare).</summary>
    public string? CertificateFingerprint { get; private set; }

    public ServerOptions Options => _opts;
    public int SessionCount => _sessions.Count;
    public Socket UdpSocket => _udp;
    /// <summary>Print per-session statistics to the log every 5 s.</summary>
    public bool PeriodicStatus { get; set; } = true;

    /// <summary>Raised for every log line (already timestamped).</summary>
    public static event Action<string>? LogMessage;
    /// <summary>Also write log lines to the console (console server).</summary>
    public static bool ConsoleOutput { get; set; } = true;

    public static void Log(string msg)
    {
        string line = $"{DateTime.Now:HH:mm:ss} {msg}";
        if (ConsoleOutput) lock (ConsoleLock) Console.WriteLine(line);
        LogMessage?.Invoke(line);
    }

    public IReadOnlyList<SessionSnapshot> GetSessions() => _sessions.Values.Select(s => s.Snapshot()).OrderBy(s => s.Started).ToList();

    public async Task RunAsync(CancellationToken ct)
    {
        (_listener, _udp) = CreateSockets();
        try
        {
            _cert = Tls.LoadOrCreateServerCertificate(_opts.CertificatePath ?? Tls.DefaultServerCertPath);
            CertificateFingerprint = Tls.Fingerprint(_cert);
        }
        catch (Exception e) { Log("TLS unavailable (clients will connect unencrypted): " + e.Message); }
        _udpDf = CreateDfSocket();
        Log($"LinkTest server {Updater.CurrentVersion} listening on TCP+UDP port {_opts.Port}" +
            (_opts.Key != null ? " (key required)" : "") + $", cap {_opts.MaxMbps} Mbps/direction, max {_opts.MaxSessions} sessions");
        foreach (var a in LocalAddresses()) Log($"  local address: {a}");
        if (CertificateFingerprint != null)
            Log($"  TLS certificate SHA-256: {CertificateFingerprint}" + (_opts.RequireTls ? " (encryption required)" : ""));

        var udpThread = new Thread(UdpLoop) { IsBackground = true, Name = "udp-rx", Priority = ThreadPriority.AboveNormal };
        udpThread.Start();
        _ = StatusLoopAsync(ct);

        try
        {
            while (!ct.IsCancellationRequested)
            {
                Socket c;
                try { c = await _listener.AcceptAsync(ct); }
                catch (OperationCanceledException) { break; }
                catch (SocketException) { continue; }
                _ = HandleConnectionAsync(c, ct);
            }
        }
        finally
        {
            _stopping = true;
            Log("Shutting down…");
            foreach (var s in _sessions.Values) s.Dispose();
            _listener.Dispose();
            _udp.Dispose();
            _udpDf?.Dispose();
        }
    }

    Socket? CreateDfSocket()
    {
        try
        {
            var s = new Socket(_udp.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
            if (s.AddressFamily == AddressFamily.InterNetworkV6) s.DualMode = true;
            var local = (IPEndPoint)_udp.LocalEndPoint!;
            s.Bind(new IPEndPoint(local.Address, 0));
            Protocol.DisableUdpConnReset(s);
            if (Protocol.SetDontFragment(s)) return s;
            s.Dispose();
        }
        catch (Exception e) { Log("Path-MTU test unavailable: " + e.Message); }
        return null;
    }

    (Socket tcp, Socket udp) CreateSockets()
    {
        var bind = _opts.Bind;
        if (bind == null)
        {
            try { return Create(IPAddress.IPv6Any, dual: true); }
            catch (SocketException) { bind = IPAddress.Any; }   // IPv6 disabled
        }
        return Create(bind, dual: false);

        (Socket, Socket) Create(IPAddress addr, bool dual)
        {
            var tcp = new Socket(addr.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            var udp = new Socket(addr.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
            try
            {
                if (dual) { tcp.DualMode = true; udp.DualMode = true; }
                tcp.Bind(new IPEndPoint(addr, _opts.Port));
                tcp.Listen(128);
                Protocol.TuneUdp(udp);
                udp.Bind(new IPEndPoint(addr, _opts.Port));
                return (tcp, udp);
            }
            catch { tcp.Dispose(); udp.Dispose(); throw; }
        }
    }

    static IEnumerable<string> LocalAddresses()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses.Select(u => (n, u.Address)))
                .Where(x => !x.Address.IsIPv6LinkLocal)
                .Select(x => $"{x.Address} ({x.n.Name})")
                .ToList();
        }
        catch { return Array.Empty<string>(); }
    }

    async Task HandleConnectionAsync(Socket c, CancellationToken ct)
    {
        var remote = (IPEndPoint)c.RemoteEndPoint!;
        try
        {
            c.NoDelay = true;
            var first = new byte[1];
            using (var t = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                t.CancelAfter(TimeSpan.FromSeconds(10));
                if (!await Protocol.ReceiveExactAsync(c, first, t.Token)) { c.Dispose(); return; }
            }

            if (first[0] == Protocol.ConnControl || first[0] == Protocol.ConnControlTls)
            {
                Stream? stream = null;
                if (first[0] == Protocol.ConnControlTls)
                {
                    if (_cert == null) { c.Dispose(); return; }
                    using var t = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    t.CancelAfter(TimeSpan.FromSeconds(10));
                    stream = await Tls.ServerHandshakeAsync(new NetworkStream(c, ownsSocket: true), _cert, t.Token);
                }
                else if (_opts.RequireTls)
                {
                    using var ch = new ControlChannel(c);
                    ch.TrySend(new ControlMessage { Type = "error", Message = "This server requires an encrypted connection: update the client to 1.9 or newer." });
                    Log($"Rejected {remote}: unencrypted client");
                    return;
                }
                if (_sessions.Count >= _opts.MaxSessions)
                {
                    using var ch = new ControlChannel(c, stream);
                    ch.TrySend(new ControlMessage { Type = "error", Message = "Server is busy (too many sessions)" });
                    Log($"Rejected {remote}: too many sessions");
                    return;
                }
                using var session = new ServerSession(this, c, remote, stream);
                await session.RunAsync(ct);
            }
            else if (first[0] == Protocol.ConnStream)
            {
                var hdr = new byte[6];
                using (var t = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    t.CancelAfter(TimeSpan.FromSeconds(10));
                    if (!await Protocol.ReceiveExactAsync(c, hdr, t.Token)) { c.Dispose(); return; }
                }
                uint id = BinaryPrimitives.ReadUInt32LittleEndian(hdr);
                int idx = BinaryPrimitives.ReadUInt16LittleEndian(hdr.AsSpan(4));
                if (_sessions.TryGetValue(id, out var s) && s.ClientIp.Equals(Protocol.Normalize(remote.Address)) && s.AttachStream(c, idx))
                {
                    c.Send(new byte[] { 1 });
                }
                else
                {
                    try { c.Send(new byte[] { 0 }); } catch { }
                    c.Dispose();
                }
            }
            else
            {
                c.Dispose();
            }
        }
        catch (Exception e) when (e is OperationCanceledException or SocketException or IOException or System.Security.Authentication.AuthenticationException)
        {
            c.Dispose();
        }
        catch (Exception e)
        {
            Log($"Connection from {remote} failed: {e.Message}");
            c.Dispose();
        }
    }

    public uint Register(ServerSession s)
    {
        Span<byte> b = stackalloc byte[4];
        while (true)
        {
            RandomNumberGenerator.Fill(b);
            uint id = BinaryPrimitives.ReadUInt32LittleEndian(b);
            if (id != 0 && _sessions.TryAdd(id, s)) return id;
        }
    }

    public void Unregister(ServerSession s) => _sessions.TryRemove(new KeyValuePair<uint, ServerSession>(s.Id, s));

    public void SendUdp(byte[] buf, int len, EndPoint ep)
    {
        try { _udp.SendTo(buf, 0, len, SocketFlags.None, ep); }
        catch (SocketException) { }
        catch (ObjectDisposedException) { }
    }

    void UdpLoop()
    {
        var buf = new byte[65536];
        EndPoint any = new IPEndPoint(_udp.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, 0);
        while (!_stopping)
        {
            EndPoint from = any;
            int n;
            try { n = _udp.ReceiveFrom(buf, ref from); }
            catch (SocketException) { continue; }
            catch (ObjectDisposedException) { break; }
            long now = Clock.NowUs;

            if (!PacketHeader.TryRead(buf.AsSpan(0, n), out var h)) continue;
            if (!_sessions.TryGetValue(h.SessionId, out var s)) continue;
            var ep = (IPEndPoint)from;
            // Only talk to the address that owns the control connection (prevents reflection to third parties).
            if (!Protocol.Normalize(ep.Address).Equals(s.ClientIp)) continue;

            switch (h.Type)
            {
                case PacketType.Data:
                    s.OnUdpData(buf.AsSpan(0, n), h, ep, now);
                    break;
                case PacketType.ProbeReq:
                    buf[3] = (byte)PacketType.ProbeResp;
                    SendUdp(buf, n, ep);
                    break;
                case PacketType.MtuProbe:
                {
                    // Header-only acknowledgement that reports how many bytes arrived.
                    var ack = h;
                    ack.Type = PacketType.MtuAck;
                    ack.StreamId = (ushort)Math.Min(n, ushort.MaxValue);
                    ack.Length = Protocol.HeaderSize;
                    ack.Write(buf);
                    SendUdp(buf, Protocol.HeaderSize, ep);
                    break;
                }
                case PacketType.MtuRequest:
                {
                    if (_udpDf == null) break;
                    int size = Math.Clamp((int)h.StreamId, Protocol.HeaderSize, PathMtu.MaxProbePayload);
                    var down = new byte[size];
                    var dh = h;
                    dh.Type = PacketType.MtuDown;
                    dh.Length = size;
                    dh.Write(down);
                    try { _udpDf.SendTo(down, 0, size, SocketFlags.None, ep); }
                    catch (SocketException) { }      // bigger than this server's interface MTU: the probe fails, as it should
                    break;
                }
            }
        }
    }

    async Task StatusLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(5000, ct);
                if (PeriodicStatus) foreach (var s in _sessions.Values) s.PrintStatus();
            }
        }
        catch (OperationCanceledException) { }
    }
}
