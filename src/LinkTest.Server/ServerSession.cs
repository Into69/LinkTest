using System.Net;
using System.Net.Sockets;
using LinkTest.Core;

namespace LinkTest.Server;

sealed class ServerSession : IDisposable
{
    readonly ServerHost _host;
    readonly ControlChannel _ctrl;
    readonly IPEndPoint _remote;
    readonly object _lock = new();
    readonly List<Socket> _streams = new();
    readonly HashSet<int> _streamIdx = new();
    readonly CancellationTokenSource _cts = new();
    readonly CancellationTokenSource _senderCts = new();
    readonly List<Thread> _senders = new();
    TestConfig _cfg = new();
    LinkEngine? _engine;
    volatile IPEndPoint? _udpEp;
    QosFlow? _qos;
    bool _started, _disposed;
    DateTime _startedAt;

    // status deltas
    long _prevUs, _prevRxBytes, _prevTxBytes;
    StatsReport _prevRx;

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

            var cfg = hello.Config ?? new TestConfig();
            cfg.Normalize();
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
            _engine = new LinkEngine(Id) { TcpInfoProvider = () => { lock (_lock) return TcpInfo.Sum(_streams.ToArray()); } };
            _ctrl.Send(new ControlMessage { Type = "welcome", Version = Protocol.Version, SessionId = Id, UdpPort = _host.Options.Port, Config = cfg, Message = note });
            ServerHost.Log($"{Tag} session opened: {cfg}");

            while (!token.IsCancellationRequested)
            {
                var m = await _ctrl.ReceiveAsync(token);
                if (m == null) break;
                switch (m.Type)
                {
                    case "start":
                        Start();
                        break;
                    case "stop":
                        StopSenders();
                        _ctrl.Send(new ControlMessage { Type = "final", Report = _engine.BuildReport(Clock.NowUs) });
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

    void Start()
    {
        lock (_lock)
        {
            if (_started || _engine == null) return;
            _started = true;
            _startedAt = DateTime.Now;
            _prevUs = Clock.NowUs;
            if (_cfg.Dscp > 0)
            {
                _qos = QosFlow.TryCreate(out var err);
                if (_qos == null) ServerHost.Log($"{Tag} DSCP not applied: {err}");
            }

            if (_cfg.Protocol == TransportKind.Udp)
            {
                var spec = SenderSpec.From(_cfg, _cfg.DownMbps);
                StartSender("udp-tx", 0, spec, (b, n) =>
                {
                    var ep = _udpEp;
                    if (ep == null) return false;   // wait until the client's first datagram tells us where to send
                    _host.SendUdp(b, n, ep);
                    return true;
                });
            }
            else
            {
                for (int i = 0; i < _streams.Count; i++) StartStream(_streams[i], i);
            }
        }
    }

    /// <summary>Called by the host when a TCP data stream connects for this session.</summary>
    public bool AttachStream(Socket s, int idx)
    {
        lock (_lock)
        {
            if (_disposed || _engine == null || _cfg.Protocol != TransportKind.Tcp || idx >= _cfg.Streams || !_streamIdx.Add(idx)) return false;
            s.NoDelay = true;
            _streams.Add(s);
            if (_started) StartStream(s, _streams.Count - 1);
            return true;
        }
    }

    void StartStream(Socket s, int i)
    {
        var spec = SenderSpec.From(_cfg, _cfg.DownMbps / _cfg.Streams);
        if (_qos != null && !_qos.TryAdd(s, null, _cfg.Dscp, out var err)) ServerHost.Log($"{Tag} DSCP not applied: {err}");
        StartSender($"tcp-tx-{i}", i, spec, (b, n) => { Protocol.SendAll(s, b, n); return true; });
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
                    _engine!.HandlePacket(buf.AsSpan(0, h.Length), h, Clock.NowUs);
                }
            }
            catch { }
        })
        { IsBackground = true, Name = $"tcp-rx-{i}" };
        rx.Start();
    }

    void StartSender(string name, int streamId, SenderSpec spec, Func<byte[], int, bool> send)
    {
        var token = _senderCts.Token;
        var t = new Thread(() =>
        {
            try { _engine!.RunSender(streamId, spec, send, token); }
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

    public void OnUdpData(ReadOnlySpan<byte> pkt, in PacketHeader h, IPEndPoint ep, long now)
    {
        NoteUdpEndpoint(ep);
        _engine?.HandlePacket(pkt, h, now);
    }

    public void NoteUdpEndpoint(IPEndPoint ep)
    {
        var cur = _udpEp;
        if (cur != null && cur.Equals(ep)) return;
        _udpEp = ep;
        ServerHost.Log(cur == null ? $"{Tag} UDP path from {ep}" : $"{Tag} UDP source changed {cur} -> {ep} (NAT rebinding)");
        if (_cfg.Dscp > 0)
        {
            lock (_lock)
                if (_qos != null && !_qos.TryAdd(_host.UdpSocket, ep, _cfg.Dscp, out var err))
                    ServerHost.Log($"{Tag} DSCP not applied: {err}");
        }
    }

    public void PrintStatus()
    {
        if (!_started || _engine == null) return;
        long now = Clock.NowUs;
        double dt = Math.Max(1e-3, (now - _prevUs) / 1e6);
        var rx = _engine.Rx.Snapshot(now);
        long tx = _engine.TxBytes;
        long du = rx.UniquePackets() - _prevRx.UniquePackets();
        long dl = Math.Max(0, rx.Lost - _prevRx.Lost);
        double loss = du + dl > 0 ? 100.0 * dl / (du + dl) : 0;
        ServerHost.Log($"{Tag} {_cfg.Protocol} rx(up) {(rx.Bytes - _prevRxBytes) * 8 / dt / 1e6,8:0.00} Mbps loss {loss,6:0.00}% jitter {rx.JitterUs / 1000,6:0.00} ms | " +
                       $"tx(down) {(tx - _prevTxBytes) * 8 / dt / 1e6,8:0.00} Mbps | rtt {_engine.SmoothedRttUs / 1000,7:0.00} ms");
        _prevUs = now; _prevRx = rx; _prevRxBytes = rx.Bytes; _prevTxBytes = tx;
    }

    void PrintSummary()
    {
        if (_engine == null) return;
        var rx = _engine.Rx.Snapshot(Clock.NowUs);
        double dur = _started ? (DateTime.Now - _startedAt).TotalSeconds : 0;
        double loss = rx.UniquePackets() + rx.Lost > 0 ? 100.0 * rx.Lost / (rx.UniquePackets() + rx.Lost) : 0;
        ServerHost.Log($"{Tag} session closed after {dur:0} s: received {rx.Bytes / 1e6:0.0} MB (loss {loss:0.###}%), sent {_engine.TxBytes / 1e6:0.0} MB");
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
            foreach (var s in _streams) { try { s.Shutdown(SocketShutdown.Both); } catch { } try { s.Dispose(); } catch { } }
        _ctrl.Dispose();
        try { _qos?.Dispose(); } catch { }
    }
}
