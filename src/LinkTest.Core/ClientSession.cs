using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using LinkTest.Core.Tunnels;

namespace LinkTest.Core;

/// <summary>Client side of a test: connects to the server, runs the data streams and tunnel simulations.</summary>
/// <remarks>
/// UDP and TCP traffic each have their own <see cref="LinkEngine"/> (sequence space, loss/jitter accounting,
/// RTT echo and in-band reports). Mixed mode runs both at once.
/// </remarks>
public sealed class ClientSession : IDisposable
{
    public event Action<string>? Log;
    /// <summary>Raised once when the session has fully stopped (argument = reason).</summary>
    public event Action<string>? Ended;

    public TestConfig Config { get; private set; } = new();
    public uint SessionId { get; private set; }
    public IPEndPoint? ServerEndPoint { get; private set; }
    public DateTime StartedAt { get; private set; }
    public IReadOnlyList<TunnelSim> Tunnels => _tunnels;
    public bool IsRunning => _running && !_stopping;
    /// <summary>Server's final receive report for the UDP traffic (or TCP in TCP-only mode).</summary>
    public StatsReport? FinalServerReport { get; private set; }
    /// <summary>Server's final receive report for the TCP traffic in mixed mode.</summary>
    public StatsReport? FinalServerTcpReport { get; private set; }

    LinkEngine? _udpEng, _tcpEng;
    EngineMeter? _udpMeter, _tcpMeter;
    ControlChannel? _ctrl;
    Socket? _udp;
    readonly List<Socket> _streams = new();
    readonly List<TunnelSim> _tunnels = new();
    readonly List<Thread> _senderThreads = new();
    readonly CancellationTokenSource _senderCts = new();
    readonly CancellationTokenSource _cts = new();
    readonly TaskCompletionSource<bool> _final = new(TaskCreationOptions.RunContinuationsAsynchronously);
    QosFlow? _qos;
    double _lossPct;
    volatile bool _running, _stopping;
    int _endedRaised;
    long _startUs, _stopUs, _finalUs, _prevUs;
    [ThreadStatic] static Random? _tlsRnd;

    public static async Task<IPAddress> ResolveAsync(string host, CancellationToken ct)
    {
        host = host.Trim();
        if (host.StartsWith('[') && host.EndsWith(']')) host = host[1..^1];
        if (IPAddress.TryParse(host, out var ip)) return ip;
        var addrs = await Dns.GetHostAddressesAsync(host, ct);
        return addrs.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
               ?? addrs.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetworkV6)
               ?? throw new SocketException((int)SocketError.HostNotFound);
    }

    /// <param name="artificialLossPct">Extra random loss applied by the client in both directions (testing aid).</param>
    public async Task StartAsync(string host, int port, string? key, TestConfig config,
        IEnumerable<TunnelProfile> tunnels, double artificialLossPct, CancellationToken ct)
    {
        if (_running) throw new InvalidOperationException("Already started");
        _running = true;
        try
        {
            config = config.Clone();
            config.Normalize();
            var ip = await ResolveAsync(host, ct);
            ServerEndPoint = new IPEndPoint(ip, port);
            Emit($"Connecting to {host} ({ip}) port {port}…");

            var ctrlSock = await ConnectTcpAsync(ServerEndPoint, ct);
            ctrlSock.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
            ctrlSock.Send(new[] { Protocol.ConnControl });
            _ctrl = new ControlChannel(ctrlSock);
            _ctrl.Send(new ControlMessage
            {
                Type = "hello", Version = Protocol.Version, AppVersion = Updater.CurrentVersion.ToString(),
                Key = string.IsNullOrEmpty(key) ? null : key, Config = config,
            });

            using var helloTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            helloTimeout.CancelAfter(TimeSpan.FromSeconds(10));
            ControlMessage? welcome;
            try { welcome = await _ctrl.ReceiveAsync(helloTimeout.Token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new TimeoutException("No answer from server (is it a LinkTest server?)"); }
            if (welcome == null)
                throw new IOException("Server closed the connection" +
                    (config.Protocol == TransportKind.Mixed ? " - it may be older than 1.2 and not support mixed TCP+UDP mode; update the server" : ""));
            if (welcome.Type == "error") throw new InvalidOperationException("Server refused the test: " + welcome.Message);
            if (welcome.Type != "welcome") throw new InvalidDataException("Unexpected reply from server: " + welcome.Type);

            SessionId = welcome.SessionId;
            Config = welcome.Config ?? config;
            if (!string.IsNullOrEmpty(welcome.Message)) Emit("Server: " + welcome.Message);

            _lossPct = Math.Clamp(artificialLossPct, 0, 100);
            if (Config.UsesUdp) { _udpEng = new LinkEngine(SessionId) { TxDropPct = _lossPct }; _udpMeter = new EngineMeter(_udpEng, null); }
            if (Config.UsesTcp) { _tcpEng = new LinkEngine(SessionId); _tcpMeter = new EngineMeter(_tcpEng, () => TcpInfo.Sum(_streams)); }

            // UDP socket: carries UDP data and tunnel probes in every mode.
            _udp = new Socket(ip.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
            Protocol.TuneUdp(_udp);
            _udp.Connect(new IPEndPoint(ip, welcome.UdpPort));

            if (_tcpEng != null)
            {
                for (int i = 0; i < Config.Streams; i++)
                {
                    var s = await ConnectTcpAsync(ServerEndPoint, ct);
                    var hello = new byte[7];
                    hello[0] = Protocol.ConnStream;
                    BinaryPrimitives.WriteUInt32LittleEndian(hello.AsSpan(1), SessionId);
                    BinaryPrimitives.WriteUInt16LittleEndian(hello.AsSpan(5), (ushort)i);
                    s.Send(hello);
                    var ack = new byte[1];
                    using var t = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    t.CancelAfter(TimeSpan.FromSeconds(10));
                    if (!await Protocol.ReceiveExactAsync(s, ack, t.Token) || ack[0] != 1)
                        throw new IOException($"Server rejected TCP stream {i}");
                    _streams.Add(s);
                }
                _tcpEng.TcpInfoProvider = () => TcpInfo.Sum(_streams);
            }

            if (Config.Dscp > 0) ApplyDscp();

            // Receivers
            StartThread("udp-rx", UdpReceiveLoop, isSender: false);
            for (int i = 0; i < _streams.Count; i++)
            {
                var s = _streams[i];
                StartThread($"tcp-rx-{i}", () => TcpReceiveLoop(s), isSender: false);
            }
            _ = Task.Run(ControlLoopAsync);

            // Go
            _ctrl.Send(new ControlMessage { Type = "start" });
            StartedAt = DateTime.Now;
            _startUs = _prevUs = Clock.NowUs;

            var token = _senderCts.Token;
            var (udpUp, tcpUp) = Config.SplitRate(Config.UpMbps);
            if (_udpEng != null)
            {
                var spec = SenderSpec.From(Config, udpUp);
                var eng = _udpEng;
                StartThread("udp-tx", () => eng.RunSender(0, spec, SendUdp, token), isSender: true);
            }
            if (_tcpEng != null)
            {
                var eng = _tcpEng;
                for (int i = 0; i < _streams.Count; i++)
                {
                    var s = _streams[i];
                    int idx = i;
                    var spec = SenderSpec.From(Config, tcpUp / _streams.Count);
                    StartThread($"tcp-tx-{i}", () => eng.RunSender(idx, spec, (b, n) => { Protocol.SendAll(s, b, n); return true; }, token), isSender: true);
                }
            }

            int ti = 0;
            foreach (var p in tunnels.Where(t => t.Enabled))
            {
                var sim = new TunnelSim(ti++, p, SendProbe);
                sim.Event += (s, e) => Emit($"[{s.Profile.Name}] {e}");
                _tunnels.Add(sim);
            }
            if (_tunnels.Count > 0) StartThread("tunnels", TunnelLoop, isSender: true);

            Emit($"Test running (session {SessionId:X8}): {Config}" + (artificialLossPct > 0 ? $", artificial loss {artificialLossPct}%" : ""));
        }
        catch
        {
            _stopping = true;
            Teardown();
            throw;
        }
    }

    static async Task<Socket> ConnectTcpAsync(IPEndPoint ep, CancellationToken ct)
    {
        var s = new Socket(ep.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        using var t = CancellationTokenSource.CreateLinkedTokenSource(ct);
        t.CancelAfter(TimeSpan.FromSeconds(8));
        try
        {
            await s.ConnectAsync(ep, t.Token);
            return s;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            s.Dispose();
            throw new TimeoutException($"Timed out connecting to {ep} (server not running, or firewall blocking TCP {ep.Port})");
        }
        catch { s.Dispose(); throw; }
    }

    void ApplyDscp()
    {
        _qos = QosFlow.TryCreate(out var err);
        if (_qos == null) { Emit("DSCP not applied: " + err); return; }
        bool ok = _qos.TryAdd(_udp!, null, Config.Dscp, out err);
        foreach (var s in _streams) ok &= _qos.TryAdd(s, null, Config.Dscp, out err);
        Emit(ok ? $"Client traffic marked DSCP {Config.Dscp}" : "DSCP not applied: " + err);
    }

    void StartThread(string name, Action body, bool isSender)
    {
        var t = new Thread(() =>
        {
            try { body(); }
            catch (Exception e) when (!_stopping)
            {
                Emit($"{name} stopped: {e.Message}");
                _ = StopAsync("Connection lost: " + e.Message);
            }
            catch { }
        })
        { IsBackground = true, Name = name };
        if (name.Contains("tx") || name == "tunnels") t.Priority = ThreadPriority.AboveNormal;
        if (isSender) _senderThreads.Add(t);
        t.Start();
    }

    static bool Chance(double pct) => pct > 0 && (_tlsRnd ??= new Random()).NextDouble() * 100 < pct;

    bool SendUdp(byte[] b, int n)
    {
        try { _udp!.Send(b, 0, n, SocketFlags.None); }
        catch (SocketException) { /* transient (e.g. no route / buffer) - counts as lost */ }
        return true;
    }

    void SendProbe(TunnelSim sim, long seq, ushort kind, int size)
    {
        long now = Clock.NowUs;
        if (sim.Profile.Transport == TunnelTransport.Tcp)
        {
            _ctrl?.TrySend(new ControlMessage { Type = "tprobe", Tunnel = sim.Index, Seq = seq, Kind = kind, Time = now });
            return;
        }
        if (Chance(_lossPct)) return;
        var buf = new byte[Math.Clamp(size, Protocol.HeaderSize, 9000)];
        new PacketHeader
        {
            Type = PacketType.ProbeReq, SessionId = SessionId, StreamId = (ushort)sim.Index, Flags = kind,
            Seq = seq, SendUs = now, Length = buf.Length,
        }.Write(buf);
        try { _udp?.Send(buf); } catch (SocketException) { } catch (ObjectDisposedException) { }
    }

    void UdpReceiveLoop()
    {
        var buf = new byte[65536];
        while (!_cts.IsCancellationRequested)
        {
            int n;
            try { n = _udp!.Receive(buf); }
            catch (SocketException) { if (_cts.IsCancellationRequested) break; continue; }
            catch (ObjectDisposedException) { break; }
            long now = Clock.NowUs;
            if (!PacketHeader.TryRead(buf.AsSpan(0, n), out var h) || h.SessionId != SessionId) continue;
            if (Chance(_lossPct)) continue;
            if (h.Type == PacketType.Data)
                _udpEng?.HandlePacket(buf.AsSpan(0, n), h, now);
            else if (h.Type == PacketType.ProbeResp && h.StreamId < _tunnels.Count)
                _tunnels[h.StreamId].OnResponse(h.Seq, h.Flags, h.SendUs, now);
        }
    }

    void TcpReceiveLoop(Socket s)
    {
        var buf = new byte[Protocol.MaxPacket];
        var eng = _tcpEng!;
        while (!_cts.IsCancellationRequested)
        {
            if (!Protocol.ReceiveExact(s, buf, 0, Protocol.HeaderSize)) throw new IOException("TCP stream closed by server");
            if (!PacketHeader.TryRead(buf, out var h)) throw new InvalidDataException("Corrupt TCP stream");
            if (!Protocol.ReceiveExact(s, buf, Protocol.HeaderSize, h.Length - Protocol.HeaderSize)) throw new IOException("TCP stream closed by server");
            if (h.Type == PacketType.Data) eng.HandlePacket(buf.AsSpan(0, h.Length), h, Clock.NowUs);
        }
    }

    async Task ControlLoopAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var m = await _ctrl!.ReceiveAsync(_cts.Token);
                if (m == null) break;
                switch (m.Type)
                {
                    case "tprobe_r":
                        if (m.Tunnel >= 0 && m.Tunnel < _tunnels.Count)
                            _tunnels[m.Tunnel].OnResponse(m.Seq, (ushort)m.Kind, m.Time, Clock.NowUs);
                        break;
                    case "final":
                        FinalServerReport = m.Report;
                        FinalServerTcpReport = m.TcpReport;
                        _finalUs = Clock.NowUs;
                        _final.TrySetResult(true);
                        break;
                    case "info":
                    case "error":
                        Emit("Server: " + m.Message);
                        break;
                }
            }
        }
        catch (Exception e) when (!_stopping) { Emit("Control connection error: " + e.Message); }
        catch { }
        _final.TrySetResult(false);
        if (!_stopping) _ = StopAsync("Control connection to server lost");
    }

    void TunnelLoop()
    {
        long now = Clock.NowUs;
        foreach (var t in _tunnels) t.Start(now);
        while (!_senderCts.IsCancellationRequested)
        {
            now = Clock.NowUs;
            foreach (var t in _tunnels) t.Tick(now);
            Thread.Sleep(5);
        }
    }

    /// <summary>Compute statistics since the previous call. Call from one thread (e.g. a 1 s UI timer).</summary>
    public StatSample TakeSample()
    {
        long now = Clock.NowUs;
        double dt = Math.Max(1e-3, (now - _prevUs) / 1e6);
        _prevUs = now;

        var u = _udpMeter?.Take(now, dt);
        var t = _tcpMeter?.Take(now, dt);
        var p = u ?? t!;                         // headline quality figures come from UDP when present
        bool mixed = u != null && t != null;

        static double Sum(double a, double b) => double.IsFinite(a) && double.IsFinite(b) ? a + b : double.NaN;
        double worstJit = Math.Max(p.DownJitterMs, double.IsFinite(p.UpJitterMs) ? p.UpJitterMs : 0);
        double worstLoss = Math.Max(p.DownLossPct, double.IsFinite(p.UpLossPct) ? p.UpLossPct : 0);

        return new StatSample
        {
            Time = DateTime.Now,
            ElapsedSec = (now - _startUs) / 1e6,
            IntervalSec = dt,
            TxMbps = mixed ? u!.TxMbps + t!.TxMbps : p.TxMbps,
            UpMbps = mixed ? Sum(u!.UpMbps, t!.UpMbps) : p.UpMbps,
            UpLossPct = p.UpLossPct, UpJitterMs = p.UpJitterMs, UpMaxGapMs = p.UpMaxGapMs,
            UpLostTotal = p.UpLostTotal, UpOutOfOrderTotal = p.UpOooTotal, UpLossTotalPct = p.UpLossTotalPct,
            UpReportStale = p.UpStale || (t?.UpStale ?? false), UpReportAgeSec = p.UpAgeSec,
            ServerTxMbps = mixed ? Sum(u!.ServerTxMbps, t!.ServerTxMbps) : p.ServerTxMbps,
            DownMbps = mixed ? u!.DownMbps + t!.DownMbps : p.DownMbps,
            DownLossPct = p.DownLossPct, DownJitterMs = p.DownJitterMs, DownMaxGapMs = p.DownMaxGapMs,
            DownLostTotal = p.DownLostTotal, DownOutOfOrderTotal = p.DownOooTotal, DownLossTotalPct = p.DownLossTotalPct,
            RttMs = p.RttMs, RttMinMs = p.RttMinMs, RttMaxMs = p.RttMaxMs,
            UpRetransBytes = t?.UpRetransBytes ?? 0, DownRetransBytes = t?.DownRetransBytes ?? 0,
            UpTimeoutsTotal = t?.UpTimeouts ?? 0, DownTimeoutsTotal = t?.DownTimeouts ?? 0,
            Mixed = mixed,
            UpMbpsUdp = mixed ? u!.UpMbps : double.NaN, UpMbpsTcp = mixed ? t!.UpMbps : double.NaN,
            DownMbpsUdp = mixed ? u!.DownMbps : double.NaN, DownMbpsTcp = mixed ? t!.DownMbps : double.NaN,
            RttTcpMs = mixed ? t!.RttMs : double.NaN,
            Mos = StatSample.EstimateMos(double.IsFinite(p.RttMs) ? p.RttMs : p.SmoothedRttMs, worstJit, worstLoss),
        };
    }

    public string TunnelSummaryLine()
    {
        long now = Clock.NowUs;
        return string.Join("; ", _tunnels.Select(t => { var st = t.GetStatus(now); return $"{st.Name}={st.State}"; }));
    }

    /// <summary>Stop gracefully: stop senders, let packets drain, collect the server's final report.</summary>
    public async Task StopAsync(string reason = "Stopped by user")
    {
        if (!_running || _stopping) return;
        _stopping = true;
        try
        {
            _stopUs = Clock.NowUs;
            _senderCts.Cancel();
            await Task.Run(() => { foreach (var t in _senderThreads) t.Join(2000); });
            await Task.Delay(500);   // let in-flight upload packets arrive
            if (_ctrl != null && _ctrl.TrySend(new ControlMessage { Type = "stop" }))
                await Task.WhenAny(_final.Task, Task.Delay(3000));
            await Task.Delay(300);   // in-flight download packets
            Emit(BuildSummary());
        }
        catch (Exception e) { Emit("Error while stopping: " + e.Message); }
        finally
        {
            Teardown();
            if (Interlocked.Exchange(ref _endedRaised, 1) == 0) Ended?.Invoke(reason);
        }
    }

    public string BuildSummary()
    {
        long now = Clock.NowUs;
        double dur = Math.Max(1e-3, ((_stopUs > 0 ? _stopUs : now) - _startUs) / 1e6);
        // The server keeps sending until it gets "stop", so download is averaged up to its final report.
        double downDur = Math.Max(1e-3, ((_finalUs > 0 ? _finalUs : now) - _startUs) / 1e6);
        var sb = new StringBuilder();
        sb.AppendLine($"===== Summary ({dur:0} s, {Config}) =====");
        bool mixed = _udpEng != null && _tcpEng != null;
        if (_udpEng != null) AppendClass(sb, _udpEng, FinalServerReport, mixed ? " UDP" : "", dur, downDur, now);
        if (_tcpEng != null) AppendClass(sb, _tcpEng, mixed ? FinalServerTcpReport : FinalServerReport, mixed ? " TCP" : "", dur, downDur, now);
        if (_engineLag() > 0)
            sb.AppendLine($"Note: client sender fell behind schedule {_engineLag()} time(s) - local CPU/NIC may be limiting.");
        foreach (var t in _tunnels)
        {
            var st = t.GetStatus(now);
            sb.AppendLine($"Tunnel {st.Name}: {st.Verdict}; availability {st.AvailabilityPct:0.##}%, drops {st.Drops}, flaps {st.Flaps}, " +
                          $"downtime {st.DownSec:0.0} s, longest outage {st.LongestOutageSec:0.0} s" +
                          (double.IsFinite(st.AvgRecoverSec) ? $", avg recovery {st.AvgRecoverSec:0.0} s" : ""));
        }
        return sb.ToString().TrimEnd();

        long _engineLag() => (_udpEng?.SenderLagEvents ?? 0) + (_tcpEng?.SenderLagEvents ?? 0);
    }

    static void AppendClass(StringBuilder sb, LinkEngine eng, StatsReport? final, string tag, double dur, double downDur, long now)
    {
        StatsReport? up = final;
        if (up == null && eng.TryGetPeerReport(out var p, out _)) up = p;
        if (up is StatsReport u)
        {
            double loss = u.UniquePackets() + u.Lost > 0 ? 100.0 * u.Lost / (u.UniquePackets() + u.Lost) : 0;
            sb.AppendLine($"Upload{tag,-4}  (client→server): {u.Bytes * 8 / dur / 1e6,8:0.00} Mbps avg, {u.Packets} pkts, lost {u.Lost} ({loss:0.###}%), out-of-order {u.OutOfOrder}, jitter {u.JitterUs / 1000:0.00} ms");
        }
        else sb.AppendLine($"Upload{tag,-4}  (client→server): no report received from server");
        var rx = eng.Rx.Snapshot(now);
        double dl = rx.UniquePackets() + rx.Lost > 0 ? 100.0 * rx.Lost / (rx.UniquePackets() + rx.Lost) : 0;
        sb.AppendLine($"Download{tag,-4}(server→client): {rx.Bytes * 8 / downDur / 1e6,8:0.00} Mbps avg, {rx.Packets} pkts, lost {rx.Lost} ({dl:0.###}%), out-of-order {rx.OutOfOrder}, jitter {rx.JitterUs / 1000:0.00} ms");
        var rt = eng.GetRttTotals();
        sb.AppendLine(rt.Samples > 0
            ? $"RTT{tag}: avg {rt.AvgUs / 1000:0.00} ms, min {rt.MinUs / 1000:0.00} ms, max {rt.MaxUs / 1000:0.00} ms ({rt.Samples} samples)"
            : $"RTT{tag}: no samples");
    }

    void Teardown()
    {
        try { _senderCts.Cancel(); } catch { }
        try { _cts.Cancel(); } catch { }
        try { _udp?.Dispose(); } catch { }
        foreach (var s in _streams) { try { s.Shutdown(SocketShutdown.Both); } catch { } try { s.Dispose(); } catch { } }
        try { _ctrl?.Dispose(); } catch { }
        try { _qos?.Dispose(); } catch { }
    }

    void Emit(string msg) => Log?.Invoke(msg);

    public void Dispose()
    {
        _stopping = true;
        Teardown();
    }

    /// <summary>Per-interval figures for one engine (UDP or TCP).</summary>
    sealed class ClassSample
    {
        public double TxMbps, UpMbps = double.NaN, UpLossPct = double.NaN, UpJitterMs = double.NaN, UpMaxGapMs = double.NaN, UpLossTotalPct = double.NaN;
        public long UpLostTotal, UpOooTotal;
        public bool UpStale = true;
        public double UpAgeSec = double.NaN, ServerTxMbps = double.NaN;
        public double DownMbps, DownLossPct, DownJitterMs, DownMaxGapMs, DownLossTotalPct;
        public long DownLostTotal, DownOooTotal;
        public double RttMs, RttMinMs, RttMaxMs, SmoothedRttMs;
        public long UpRetransBytes, DownRetransBytes;
        public int UpTimeouts, DownTimeouts;
    }

    /// <summary>Keeps the previous snapshot of one engine to turn cumulative counters into interval rates.</summary>
    sealed class EngineMeter
    {
        readonly LinkEngine _e;
        readonly Func<(long, int)>? _localTcp;
        StatsReport _prevLocal, _prevPeer;
        bool _hasPrevPeer;
        long _prevTx, _prevUpRetrans;

        public EngineMeter(LinkEngine e, Func<(long, int)>? localTcp) { _e = e; _localTcp = localTcp; }

        public ClassSample Take(long now, double dt)
        {
            var s = new ClassSample();
            var local = _e.Rx.Snapshot(now);
            long tx = _e.TxBytes;
            var rtt = _e.TakeRttInterval();

            s.TxMbps = (tx - _prevTx) * 8 / dt / 1e6;
            s.DownMbps = (local.Bytes - _prevLocal.Bytes) * 8 / dt / 1e6;
            long dUnique = local.UniquePackets() - _prevLocal.UniquePackets();
            long dLost = Math.Max(0, local.Lost - _prevLocal.Lost);
            s.DownLossPct = dUnique + dLost > 0 ? 100.0 * dLost / (dUnique + dLost) : 0;
            s.DownLossTotalPct = local.UniquePackets() + local.Lost > 0 ? 100.0 * local.Lost / (local.UniquePackets() + local.Lost) : 0;
            s.DownJitterMs = local.JitterUs / 1000;
            s.DownMaxGapMs = local.MaxGapUs / 1000.0;
            s.DownLostTotal = local.Lost;
            s.DownOooTotal = local.OutOfOrder;
            s.RttMs = rtt.AvgUs / 1000; s.RttMinMs = rtt.MinUs / 1000; s.RttMaxMs = rtt.MaxUs / 1000;
            s.SmoothedRttMs = _e.SmoothedRttUs / 1000;

            if (_localTcp != null)
            {
                var (r, to) = _localTcp();
                s.UpRetransBytes = Math.Max(0, r - _prevUpRetrans);
                s.UpTimeouts = to;
                _prevUpRetrans = r;
            }

            if (_e.TryGetPeerReport(out var peer, out long peerAt))
            {
                s.UpAgeSec = (now - peerAt) / 1e6;
                s.UpLostTotal = peer.Lost; s.UpOooTotal = peer.OutOfOrder;
                s.UpLossTotalPct = peer.UniquePackets() + peer.Lost > 0 ? 100.0 * peer.Lost / (peer.UniquePackets() + peer.Lost) : 0;
                s.UpJitterMs = peer.JitterUs / 1000;
                s.UpMaxGapMs = peer.MaxGapUs / 1000.0;
                s.DownTimeouts = peer.TcpTimeouts;
                if (_hasPrevPeer && peer.TimeUs > _prevPeer.TimeUs)
                {
                    s.UpStale = false;
                    double pdt = (peer.TimeUs - _prevPeer.TimeUs) / 1e6;
                    s.UpMbps = (peer.Bytes - _prevPeer.Bytes) * 8 / pdt / 1e6;
                    s.ServerTxMbps = (peer.TxBytes - _prevPeer.TxBytes) * 8 / pdt / 1e6;
                    long pu = peer.UniquePackets() - _prevPeer.UniquePackets();
                    long pl = Math.Max(0, peer.Lost - _prevPeer.Lost);
                    s.UpLossPct = pu + pl > 0 ? 100.0 * pl / (pu + pl) : 0;
                    s.DownRetransBytes = Math.Max(0, peer.TcpRetransBytes - _prevPeer.TcpRetransBytes);
                    _prevPeer = peer;
                }
                else if (!_hasPrevPeer)
                {
                    _prevPeer = peer;
                    _hasPrevPeer = true;
                    s.UpStale = false;
                }
                if (s.UpAgeSec > 1.5) s.UpStale = true;
            }

            _prevLocal = local;
            _prevTx = tx;
            return s;
        }
    }
}
