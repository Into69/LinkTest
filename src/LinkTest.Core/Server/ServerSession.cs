using System.Net;
using System.Net.Sockets;
using LinkTest.Core;

namespace LinkTest.Server;

/// <summary>Live counters of a session, for status displays.</summary>
public sealed record SessionSnapshot(
    uint Id, string Client, string? ClientVersion, string Mode, DateTime Started, bool Running,
    long RxBytes, long TxBytes, long Lost, long Expected, double RttMs, int Classes,
    IpInfo? ClientInfo);

/// <summary>Server side of one client test. Each traffic class has its own engine and sockets.</summary>
public sealed class ServerSession : IDisposable
{
    sealed class ClassRt
    {
        public required int Index;
        public required TrafficClass Def;
        public required LinkEngine Eng;
        public int TcpOrdinal = -1;
        public volatile IPEndPoint? UdpEp;
        public readonly List<Socket> Tcp = new();
        public readonly HashSet<int> StreamNos = new();
        public (StatsReport rx, long tx) Prev;
        public bool IsUdp => Def.Transport == TransportKind.Udp;
    }

    readonly ServerHost _host;
    readonly ControlChannel _ctrl;
    readonly IPEndPoint _remote;
    readonly object _lock = new();
    readonly List<ClassRt> _classes = new();
    readonly CancellationTokenSource _cts = new();
    readonly CancellationTokenSource _senderCts = new();
    readonly List<Thread> _senders = new();
    TestConfig _cfg = new();
    QosFlow? _qos;
    bool _started, _disposed;
    DateTime _startedAt = DateTime.Now;
    string? _clientVersion;
    bool _sendDetail;          // client 1.8+ understands the "stats" message
    volatile IpInfo? _clientInfo;
    int _dscpWarned;

    void DscpWarn(string? err)
    {
        if (Interlocked.Exchange(ref _dscpWarned, 1) == 0) ServerHost.Log($"{Tag} DSCP not applied: {err}");
    }
    long _prevUs;

    public ServerSession(ServerHost host, Socket control, IPEndPoint remote)
    {
        _host = host;
        _remote = remote;
        ClientIp = Protocol.Normalize(remote.Address);
        control.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
        _ctrl = new ControlChannel(control);
    }

    public uint Id { get; private set; }
    public IPAddress ClientIp { get; }
    string Tag => $"[{Id:X8} {ClientIp}]";

    public async Task RunAsync(CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
        var token = linked.Token;
        try
        {
            ControlMessage? hello;
            using (var t = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                t.CancelAfter(TimeSpan.FromSeconds(10));
                hello = await _ctrl.ReceiveAsync(t.Token);
            }
            if (hello?.Type != "hello") return;
            if (hello.Version != Protocol.Version)
            {
                _ctrl.TrySend(new ControlMessage { Type = "error", Message = $"Protocol version mismatch (server {Protocol.Version}, client {hello.Version})" });
                return;
            }
            if (_host.Options.Key != null && hello.Key != _host.Options.Key)
            {
                _ctrl.TrySend(new ControlMessage { Type = "error", Message = "Invalid key" });
                ServerHost.Log($"Rejected {_remote}: invalid key");
                return;
            }
            _clientVersion = hello.AppVersion;

            var cfg = hello.Config ?? new TestConfig();
            cfg.Normalize();
            // Clients before 1.6 do not know about the idle baseline (their configs default to it).
            if (!Updater.TryParseVersion(hello.AppVersion ?? "0", out var cv)) cv = new Version(0, 0);
            if (cv < new Version(1, 6, 0)) cfg.BaselineSec = 0;
            _sendDetail = cv >= new Version(1, 8, 0);
            string? note = null;
            double cap = _host.Options.MaxMbps;
            if (cfg.UpMbps > cap || cfg.DownMbps > cap)
            {
                cfg.UpMbps = Math.Min(cfg.UpMbps, cap);
                cfg.DownMbps = Math.Min(cfg.DownMbps, cap);
                note = $"rates capped to {cap} Mbps by server policy";
            }
            _cfg = cfg;

            Id = _host.Register(this);
            var defs = TrafficClasses.Resolve(cfg);
            int ord = 0;
            for (int i = 0; i < defs.Count; i++)
            {
                var c = new ClassRt { Index = i, Def = defs[i], Eng = new LinkEngine(Id) };
                if (!c.IsUdp)
                {
                    c.TcpOrdinal = ord++;
                    c.Eng.TcpInfoProvider = () => { lock (_lock) return TcpInfo.Sum(c.Tcp.ToArray()); };
                    c.Eng.TcpDetailProvider = () => { Socket[] socks; lock (_lock) socks = c.Tcp.ToArray(); return TcpInfo.Detail(socks); };
                }
                _classes.Add(c);
            }

            _ctrl.Send(new ControlMessage
            {
                Type = "welcome", Version = Protocol.Version, AppVersion = Updater.CurrentVersion.ToString(),
                SessionId = Id, UdpPort = _host.Options.Port, Config = cfg, Message = note,
                ClientAddress = $"{(ClientIp.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{ClientIp}]" : ClientIp.ToString())}:{_remote.Port}",
            });
            _ = LookupClientAsync(token);
            ServerHost.Log($"{Tag} session opened{(_clientVersion != null ? $" (client {_clientVersion})" : "")}: {cfg}");

            while (!token.IsCancellationRequested)
            {
                var m = await _ctrl.ReceiveAsync(token);
                if (m == null) break;
                switch (m.Type)
                {
                    case "start":
                        Start();
                        if (_sendDetail) _ = DetailLoopAsync(token);
                        break;
                    case "stop":
                        StopSenders();
                        long now = Clock.NowUs;
                        var reports = _classes.Select(c => c.Eng.BuildReport(now)).ToList();
                        _ctrl.Send(new ControlMessage
                        {
                            Type = "final",
                            ClassReports = reports,
                            Report = reports[0],                                   // for 1.2 clients
                            TcpReport = reports.Count == 2 ? reports[1] : null,
                            Details = _sendDetail ? _classes.Select(c => c.Eng.BuildDetail(now, final: true)).ToList() : null,
                        });
                        break;
                    case "tprobe":
                        _ctrl.TrySend(new ControlMessage { Type = "tprobe_r", Tunnel = m.Tunnel, Seq = m.Seq, Kind = m.Kind, Time = m.Time });
                        break;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception e) when (e is IOException or SocketException) { }
        catch (Exception e) { ServerHost.Log($"{Tag} error: {e.Message}"); }
        finally
        {
            if (Id != 0)
            {
                PrintSummary();
                _host.Unregister(this);
            }
        }
    }

    /// <summary>Sends the detailed per-class statistics to the client once a second while the test runs.</summary>
    async Task DetailLoopAsync(CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            while (await timer.WaitForNextTickAsync(ct) && !_senderCts.IsCancellationRequested)
            {
                long now = Clock.NowUs;
                var details = _classes.Select(c => c.Eng.BuildDetail(now)).ToList();
                if (!_ctrl.TrySend(new ControlMessage { Type = "stats", Details = details })) break;
            }
        }
        catch (OperationCanceledException) { }
    }

    async Task LookupClientAsync(CancellationToken ct)
    {
        try
        {
            var info = await IpLookup.LookupAsync(ClientIp, !_host.Options.NoIpLookup, ct);
            _clientInfo = info;
            ServerHost.Log($"{Tag} client: {info.Summary}");
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { ServerHost.Log($"{Tag} client lookup failed: {e.Message}"); }
    }

    void Start()
    {
        lock (_lock)
        {
            if (_started) return;
            _started = true;
            _startedAt = DateTime.Now;
            _prevUs = Clock.NowUs;
            if (_classes.Any(c => c.Def.Dscp > 0))
            {
                _qos = QosFlow.TryCreate(out var err);
                if (_qos == null) DscpWarn(err);
            }

            foreach (var c in _classes)
            {
                if (c.IsUdp)
                {
                    var k = c;
                    var spec = TrafficClasses.Spec(_cfg, k.Def, _cfg.DownMbps);
                    StartSender($"udp-tx-{k.Index}", k.Eng, k.Index, spec, (b, n) =>
                    {
                        var ep = k.UdpEp;
                        if (ep == null) return false;   // wait until the client's first datagram tells us where to send
                        _host.SendUdp(b, n, ep);
                        return true;
                    });
                }
                else
                {
                    for (int i = 0; i < c.Tcp.Count; i++) StartStream(c, c.Tcp[i], i);
                }
            }
        }
    }

    /// <summary>Called by the host when a TCP data stream connects for this session.</summary>
    public bool AttachStream(Socket s, int idx)
    {
        lock (_lock)
        {
            if (_disposed) return false;
            var (ord, n) = TrafficClasses.DecodeStream(idx);
            var c = _classes.FirstOrDefault(x => x.TcpOrdinal == ord);
            if (c == null || n >= c.Def.Streams || !c.StreamNos.Add(n)) return false;
            s.NoDelay = true;
            c.Tcp.Add(s);
            if (_started) StartStream(c, s, c.Tcp.Count - 1);
            return true;
        }
    }

    void StartStream(ClassRt c, Socket s, int i)
    {
        var spec = TrafficClasses.Spec(_cfg, c.Def, _cfg.DownMbps, c.Def.Streams);
        if (_qos != null && c.Def.Dscp > 0 && !_qos.TryAdd(s, null, c.Def.Dscp, out var err)) DscpWarn(err);
        StartSender($"tcp-tx-{c.Index}-{i}", c.Eng, i, spec, (b, n) => { Protocol.SendAll(s, b, n); return true; });
        var eng = c.Eng;
        var rx = new Thread(() =>
        {
            var buf = new byte[Protocol.MaxPacket];
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    if (!Protocol.ReceiveExact(s, buf, 0, Protocol.HeaderSize)) break;
                    if (!PacketHeader.TryRead(buf, out var h) || h.SessionId != Id) break;
                    if (!Protocol.ReceiveExact(s, buf, Protocol.HeaderSize, h.Length - Protocol.HeaderSize)) break;
                    eng.HandlePacket(buf.AsSpan(0, h.Length), h, Clock.NowUs);
                }
            }
            catch { }
        })
        { IsBackground = true, Name = $"tcp-rx-{c.Index}-{i}" };
        rx.Start();
    }

    void StartSender(string name, LinkEngine eng, int streamId, SenderSpec spec, Func<byte[], int, bool> send)
    {
        var token = _senderCts.Token;
        var t = new Thread(() =>
        {
            try { eng.RunSender(streamId, spec, send, token); }
            catch (Exception e) when (!token.IsCancellationRequested && !_disposed)
            {
                ServerHost.Log($"{Tag} {name} stopped: {e.Message}");
            }
            catch { }
        })
        { IsBackground = true, Name = name, Priority = ThreadPriority.AboveNormal };
        _senders.Add(t);
        t.Start();
    }

    void StopSenders()
    {
        _senderCts.Cancel();
        Thread[] ts;
        lock (_lock) ts = _senders.ToArray();
        foreach (var t in ts) t.Join(2000);
    }

    /// <summary>UDP data from the client; the header's StreamId is the class index.</summary>
    public void OnUdpData(ReadOnlySpan<byte> pkt, in PacketHeader h, IPEndPoint ep, long now)
    {
        if (h.StreamId >= _classes.Count) return;
        var c = _classes[h.StreamId];
        if (!c.IsUdp) return;
        var cur = c.UdpEp;
        if (cur == null || !cur.Equals(ep))
        {
            c.UdpEp = ep;
            string cls = _classes.Count > 1 ? $" ({c.Def.Name})" : "";
            ServerHost.Log(cur == null ? $"{Tag} UDP path{cls} from {ep}" : $"{Tag} UDP source{cls} changed {cur} -> {ep} (NAT rebinding)");
            if (c.Def.Dscp > 0)
                lock (_lock)
                    if (_qos != null && !_qos.TryAdd(_host.UdpSocket, ep, c.Def.Dscp, out var err))
                        DscpWarn(err);
        }
        c.Eng.HandlePacket(pkt, h, now);
    }

    public SessionSnapshot Snapshot()
    {
        long now = Clock.NowUs;
        long rx = 0, tx = 0, lost = 0, exp = 0;
        foreach (var c in _classes)
        {
            var r = c.Eng.Rx.Snapshot(now);
            rx += r.Bytes; tx += c.Eng.TxBytes;
            if (c.IsUdp || _classes.All(x => !x.IsUdp)) { lost += r.Lost; exp += r.UniquePackets() + r.Lost; }
        }
        var first = _classes.FirstOrDefault(c => c.IsUdp) ?? _classes.FirstOrDefault();
        string mode = _cfg.DscpSimulate ? $"DSCP sim ({_classes.Count})" : _cfg.Protocol.ToString();
        return new SessionSnapshot(Id, ClientIp.ToString(), _clientVersion, mode, _startedAt, _started,
            rx, tx, lost, exp, first != null ? first.Eng.SmoothedRttUs / 1000 : double.NaN, _classes.Count, _clientInfo);
    }

    public void PrintStatus()
    {
        if (!_started) return;
        long now = Clock.NowUs;
        double dt = Math.Max(1e-3, (now - _prevUs) / 1e6);
        _prevUs = now;
        foreach (var c in _classes)
        {
            var rx = c.Eng.Rx.Snapshot(now);
            long tx = c.Eng.TxBytes;
            var p = c.Prev;
            long du = rx.UniquePackets() - p.rx.UniquePackets();
            long dl = Math.Max(0, rx.Lost - p.rx.Lost);
            double loss = du + dl > 0 ? 100.0 * dl / (du + dl) : 0;
            string name = _classes.Count > 1 ? c.Def.Name : c.Def.Transport.ToString();
            ServerHost.Log($"{Tag} {name,-18} rx(up) {(rx.Bytes - p.rx.Bytes) * 8 / dt / 1e6,8:0.00} Mbps loss {loss,6:0.00}% jitter {rx.JitterUs / 1000,6:0.00} ms | " +
                           $"tx(down) {(tx - p.tx) * 8 / dt / 1e6,8:0.00} Mbps | rtt {c.Eng.SmoothedRttUs / 1000,7:0.00} ms");
            c.Prev = (rx, tx);
        }
    }

    void PrintSummary()
    {
        double dur = _started ? (DateTime.Now - _startedAt).TotalSeconds : 0;
        var s = Snapshot();
        double loss = s.Expected > 0 ? 100.0 * s.Lost / s.Expected : 0;
        ServerHost.Log($"{Tag} session closed after {dur:0} s: received {s.RxBytes / 1e6:0.0} MB (loss {loss:0.###}%), sent {s.TxBytes / 1e6:0.0} MB");
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
        }
        try { _senderCts.Cancel(); } catch { }
        try { _cts.Cancel(); } catch { }
        lock (_lock)
            foreach (var c in _classes)
                foreach (var s in c.Tcp) { try { s.Shutdown(SocketShutdown.Both); } catch { } try { s.Dispose(); } catch { } }
        _ctrl.Dispose();
        try { _qos?.Dispose(); } catch { }
    }
}
