using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using LinkTest.Core.Tunnels;

namespace LinkTest.Core;

/// <summary>Client side of a test: connects to the server, runs the traffic classes and tunnel simulations.</summary>
/// <remarks>
/// A test is a list of <see cref="TrafficClass"/>es (see <see cref="TrafficClasses.Resolve"/>). Each class has
/// its own <see cref="LinkEngine"/> (sequence space, loss/jitter accounting, RTT echo, in-band reports) and its
/// own sockets, so it can carry its own DSCP marking and be measured separately.
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
    public IReadOnlyList<TrafficClass> Classes => _classes.Select(c => c.Def).ToList();
    public bool IsRunning => _running && !_stopping;

    /// <summary>Addresses and IP-lookup results for this connection (updated as lookups complete).</summary>
    public ConnectionInfo? Connection { get; private set; }
    /// <summary>Raised (from a background thread) whenever <see cref="Connection"/> changes.</summary>
    public event Action<ConnectionInfo>? ConnectionChanged;

    /// <summary>Artificial random loss applied to UDP traffic and probes in both directions; can be changed while running.</summary>
    public double ArtificialLossPct
    {
        get => Volatile.Read(ref _lossPct);
        set
        {
            double v = double.IsFinite(value) ? Math.Clamp(value, 0, 100) : 0;
            Volatile.Write(ref _lossPct, v);
            foreach (var c in _classes) if (c.IsUdp) c.Eng.TxDropPct = v;
        }
    }

    sealed class ClassRuntime
    {
        public required int Index;
        public required TrafficClass Def;
        public required LinkEngine Eng;
        public Socket? Udp;
        public readonly List<Socket> Tcp = new();
        public EngineMeter Meter = null!;
        public StatsReport? Final;
        public bool IsUdp => Def.Transport == TransportKind.Udp;
    }

    readonly List<ClassRuntime> _classes = new();
    ControlChannel? _ctrl;
    Socket? _probe;                      // tunnel probes
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

    /// <param name="artificialLossPct">Extra random loss applied by the client to UDP in both directions (testing aid).</param>
    /// <param name="ipLookup">Look up this client's public IP details online (ipinfo.io).</param>
    public async Task StartAsync(string host, int port, string? key, TestConfig config,
        IEnumerable<TunnelProfile> tunnels, double artificialLossPct, CancellationToken ct, bool ipLookup = true)
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
            if (config.DscpSimulate && welcome.Config?.DscpSimulate != true)
                throw new NotSupportedException("The server is older than 1.3 and does not support DSCP Simulate mode; update the server.");

            SessionId = welcome.SessionId;
            Config = welcome.Config ?? config;
            if (!string.IsNullOrEmpty(welcome.Message)) Emit("Server: " + welcome.Message);
            _lossPct = Math.Clamp(artificialLossPct, 0, 100);
            StartConnectionLookup(host, ctrlSock.LocalEndPoint as IPEndPoint, welcome.ClientAddress, ipLookup);

            // Classes, sockets and engines
            var defs = TrafficClasses.Resolve(Config);
            int tcpOrdinal = 0;
            for (int i = 0; i < defs.Count; i++)
            {
                var rt = new ClassRuntime { Index = i, Def = defs[i], Eng = new LinkEngine(SessionId) };
                if (rt.IsUdp)
                {
                    rt.Eng.TxDropPct = _lossPct;
                    rt.Udp = new Socket(ip.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
                    Protocol.TuneUdp(rt.Udp);
                    rt.Udp.Connect(new IPEndPoint(ip, welcome.UdpPort));
                    rt.Meter = new EngineMeter(rt.Eng, null);
                }
                else
                {
                    for (int n = 0; n < rt.Def.Streams; n++)
                        rt.Tcp.Add(await OpenStreamAsync(TrafficClasses.EncodeStream(tcpOrdinal, n), ct));
                    tcpOrdinal++;
                    var socks = rt.Tcp;
                    rt.Eng.TcpInfoProvider = () => TcpInfo.Sum(socks);
                    rt.Meter = new EngineMeter(rt.Eng, () => TcpInfo.Sum(socks));
                }
                _classes.Add(rt);
            }
            _probe = new Socket(ip.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
            Protocol.TuneUdp(_probe);
            _probe.Connect(new IPEndPoint(ip, welcome.UdpPort));

            ApplyDscp();

            // Receivers
            foreach (var rt in _classes)
            {
                var c = rt;
                if (c.IsUdp) StartThread($"udp-rx-{c.Index}", () => UdpReceiveLoop(c), isSender: false);
                else foreach (var s in c.Tcp) { var sock = s; StartThread($"tcp-rx-{c.Index}", () => TcpReceiveLoop(c, sock), isSender: false); }
            }
            StartThread("probe-rx", ProbeReceiveLoop, isSender: false);
            _ = Task.Run(ControlLoopAsync);

            // Go
            _ctrl.Send(new ControlMessage { Type = "start" });
            StartedAt = DateTime.Now;
            _startUs = _prevUs = Clock.NowUs;

            var token = _senderCts.Token;
            foreach (var rt in _classes)
            {
                var c = rt;
                if (c.IsUdp)
                {
                    var spec = TrafficClasses.Spec(Config, c.Def, Config.UpMbps);
                    var sock = c.Udp!;
                    StartThread($"udp-tx-{c.Index}", () => c.Eng.RunSender(c.Index, spec, (b, n) => SendUdp(sock, b, n), token), isSender: true);
                }
                else
                {
                    var spec = TrafficClasses.Spec(Config, c.Def, Config.UpMbps, c.Tcp.Count);
                    for (int n = 0; n < c.Tcp.Count; n++)
                    {
                        var s = c.Tcp[n];
                        int idx = n;
                        StartThread($"tcp-tx-{c.Index}", () => c.Eng.RunSender(idx, spec, (b, len) => { Protocol.SendAll(s, b, len); return true; }, token), isSender: true);
                    }
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
            if (_classes.Count > 2)
                foreach (var c in _classes)
                    Emit($"  class {c.Def.Name}: {c.Def.Transport}, {c.Def.DscpName}, {c.Def.SharePct:0.#}% = " +
                         $"{Config.UpMbps * c.Def.SharePct / 100:0.###} / {Config.DownMbps * c.Def.SharePct / 100:0.###} Mbps, {c.Def.Profile}");
        }
        catch
        {
            _stopping = true;
            Teardown();
            throw;
        }
    }

    void StartConnectionLookup(string host, IPEndPoint? local, string? seenByServer, bool online)
    {
        string localStr = local == null ? "?" : $"{Protocol.Normalize(local.Address)}:{local.Port}";
        IPAddress? seenIp = null;
        if (seenByServer != null)
        {
            int colon = seenByServer.LastIndexOf(':');
            string ipPart = colon > 0 ? seenByServer[..colon].Trim('[', ']') : seenByServer;
            IPAddress.TryParse(ipPart, out seenIp);
        }
        string nat = seenIp == null ? "Unknown (server older than 1.4)"
            : local != null && Protocol.Normalize(local.Address).Equals(seenIp) ? "No NAT - the server sees this computer's own address"
            : "Behind NAT - the server sees a different (translated) address";
        var info = new ConnectionInfo($"{host} ({ServerEndPoint})", localStr, seenByServer, nat, null, null, false);
        SetConnection(info);

        _ = Task.Run(async () =>
        {
            try
            {
                Task<IpInfo>? seenTask = seenIp != null ? IpLookup.LookupAsync(seenIp, online, _cts.Token) : null;
                Task<IpInfo>? pubTask = online ? IpLookup.LookupAsync(null, true, _cts.Token) : null;
                IpInfo? seen = seenTask != null ? await seenTask : null;
                IpInfo? pub = pubTask != null ? await pubTask : null;
                var done = info with { SeenByServerInfo = seen, PublicInfo = pub, LookupComplete = true };
                if (seen != null && pub != null && pub.Error == null && !seen.IsPublic && pub.Ip != seen.Ip)
                    done = done with { NatStatus = $"Not applicable - the test runs over a private path ({seen.Scope}); this computer's internet address is {pub.Ip}" };
                SetConnection(done);
                Emit("Client IP: " + (pub != null ? pub.Summary : seen != null ? seen.Summary : localStr) +
                     (seenByServer != null ? $" | seen by server as {seenByServer}" : ""));
            }
            catch (OperationCanceledException) { }
            catch (Exception e) { Emit("IP lookup failed: " + e.Message); }
        });
    }

    void SetConnection(ConnectionInfo c)
    {
        Connection = c;
        ConnectionChanged?.Invoke(c);
    }

    async Task<Socket> OpenStreamAsync(int idx, CancellationToken ct)
    {
        var s = await ConnectTcpAsync(ServerEndPoint!, ct);
        try
        {
            var hello = new byte[7];
            hello[0] = Protocol.ConnStream;
            BinaryPrimitives.WriteUInt32LittleEndian(hello.AsSpan(1), SessionId);
            BinaryPrimitives.WriteUInt16LittleEndian(hello.AsSpan(5), (ushort)idx);
            s.Send(hello);
            var ack = new byte[1];
            using var t = CancellationTokenSource.CreateLinkedTokenSource(ct);
            t.CancelAfter(TimeSpan.FromSeconds(10));
            if (!await Protocol.ReceiveExactAsync(s, ack, t.Token) || ack[0] != 1)
                throw new IOException($"Server rejected TCP stream {idx}");
            return s;
        }
        catch { s.Dispose(); throw; }
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
        if (!_classes.Any(c => c.Def.Dscp > 0)) return;
        _qos = QosFlow.TryCreate(out var err);
        if (_qos == null) { Emit("DSCP not applied: " + err); return; }
        string? firstErr = null;
        int ok = 0, total = 0;
        foreach (var c in _classes.Where(c => c.Def.Dscp > 0))
            foreach (var s in c.IsUdp ? new[] { c.Udp! } : c.Tcp.ToArray())
            {
                total++;
                if (_qos.TryAdd(s, null, c.Def.Dscp, out err)) ok++;
                else firstErr ??= err;
            }
        Emit(ok == total ? $"Client traffic DSCP-marked ({total} socket(s))" : $"DSCP marking applied to {ok}/{total} socket(s): {firstErr}");
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

    static bool SendUdp(Socket s, byte[] b, int n)
    {
        try { s.Send(b, 0, n, SocketFlags.None); }
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
        if (Chance(ArtificialLossPct)) return;
        var buf = new byte[Math.Clamp(size, Protocol.HeaderSize, 9000)];
        new PacketHeader
        {
            Type = PacketType.ProbeReq, SessionId = SessionId, StreamId = (ushort)sim.Index, Flags = kind,
            Seq = seq, SendUs = now, Length = buf.Length,
        }.Write(buf);
        try { _probe?.Send(buf); } catch (SocketException) { } catch (ObjectDisposedException) { }
    }

    void UdpReceiveLoop(ClassRuntime c)
    {
        var buf = new byte[65536];
        var sock = c.Udp!;
        while (!_cts.IsCancellationRequested)
        {
            int n;
            try { n = sock.Receive(buf); }
            catch (SocketException) { if (_cts.IsCancellationRequested) break; continue; }
            catch (ObjectDisposedException) { break; }
            long now = Clock.NowUs;
            if (!PacketHeader.TryRead(buf.AsSpan(0, n), out var h) || h.SessionId != SessionId || h.Type != PacketType.Data) continue;
            if (Chance(ArtificialLossPct)) continue;
            c.Eng.HandlePacket(buf.AsSpan(0, n), h, now);
        }
    }

    void ProbeReceiveLoop()
    {
        var buf = new byte[65536];
        while (!_cts.IsCancellationRequested)
        {
            int n;
            try { n = _probe!.Receive(buf); }
            catch (SocketException) { if (_cts.IsCancellationRequested) break; continue; }
            catch (ObjectDisposedException) { break; }
            long now = Clock.NowUs;
            if (!PacketHeader.TryRead(buf.AsSpan(0, n), out var h) || h.SessionId != SessionId) continue;
            if (Chance(ArtificialLossPct)) continue;
            if (h.Type == PacketType.ProbeResp && h.StreamId < _tunnels.Count)
                _tunnels[h.StreamId].OnResponse(h.Seq, h.Flags, h.SendUs, now);
        }
    }

    void TcpReceiveLoop(ClassRuntime c, Socket s)
    {
        var buf = new byte[Protocol.MaxPacket];
        while (!_cts.IsCancellationRequested)
        {
            if (!Protocol.ReceiveExact(s, buf, 0, Protocol.HeaderSize)) throw new IOException("TCP stream closed by server");
            if (!PacketHeader.TryRead(buf, out var h)) throw new InvalidDataException("Corrupt TCP stream");
            if (!Protocol.ReceiveExact(s, buf, Protocol.HeaderSize, h.Length - Protocol.HeaderSize)) throw new IOException("TCP stream closed by server");
            if (h.Type == PacketType.Data) c.Eng.HandlePacket(buf.AsSpan(0, h.Length), h, Clock.NowUs);
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
                        if (m.ClassReports != null)
                            for (int i = 0; i < m.ClassReports.Count && i < _classes.Count; i++) _classes[i].Final = m.ClassReports[i];
                        else
                        {
                            // Server 1.2: Report = first class, TcpReport = TCP class in mixed mode
                            if (_classes.Count > 0) _classes[0].Final = m.Report;
                            if (_classes.Count > 1) _classes[1].Final = m.TcpReport;
                        }
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

        var samples = _classes.Select(c => (c, s: c.Meter.Take(now, dt))).ToList();
        var udp = samples.Where(x => x.c.IsUdp).ToList();
        var tcp = samples.Where(x => !x.c.IsUdp).ToList();
        // Quality figures come from UDP (TCP hides loss by retransmitting); the first UDP class is the headline.
        var quality = udp.Count > 0 ? udp : tcp;
        var p = quality[0].s;
        bool mixed = udp.Count > 0 && tcp.Count > 0;

        static double SumF(IEnumerable<double> v) { double t = 0; foreach (var x in v) { if (!double.IsFinite(x)) return double.NaN; t += x; } return t; }
        static double Pct(long lost, long exp) => exp > 0 ? 100.0 * lost / exp : 0;
        static double Max(IEnumerable<double> v) { double m = double.NaN; foreach (var x in v) if (double.IsFinite(x) && !(m >= x)) m = x; return m; }

        long upLostD = quality.Sum(x => x.s.UpLostD), upExpD = quality.Sum(x => x.s.UpExpD);
        long dnLostD = quality.Sum(x => x.s.DownLostD), dnExpD = quality.Sum(x => x.s.DownExpD);
        long upLostT = quality.Sum(x => x.s.UpLostTotal), upExpT = quality.Sum(x => x.s.UpExpTotal);
        long dnLostT = quality.Sum(x => x.s.DownLostTotal), dnExpT = quality.Sum(x => x.s.DownExpTotal);

        double upLoss = quality.All(x => double.IsNaN(x.s.UpLossPct)) ? double.NaN : Pct(upLostD, upExpD);
        double dnLoss = Pct(dnLostD, dnExpD);
        double upJit = Max(quality.Select(x => x.s.UpJitterMs));
        double dnJit = Max(quality.Select(x => x.s.DownJitterMs));
        double worstJit = Math.Max(double.IsFinite(dnJit) ? dnJit : 0, double.IsFinite(upJit) ? upJit : 0);
        double worstLoss = Math.Max(dnLoss, double.IsFinite(upLoss) ? upLoss : 0);

        var classStats = samples.Select(x => new ClassStat(
            x.c.Def.Name, x.c.Def.Transport, x.c.Def.Dscp, x.c.Def.SharePct,
            x.s.UpMbps, x.s.UpLossPct, x.s.UpJitterMs, x.s.DownMbps, x.s.DownLossPct, x.s.DownJitterMs,
            x.s.RttMs, x.s.UpRetransBytes, x.s.DownRetransBytes, x.s.UpLostTotal, x.s.DownLostTotal)).ToList();

        return new StatSample
        {
            Time = DateTime.Now,
            ElapsedSec = (now - _startUs) / 1e6,
            IntervalSec = dt,
            TxMbps = samples.Sum(x => x.s.TxMbps),
            UpMbps = SumF(samples.Select(x => x.s.UpMbps)),
            UpLossPct = upLoss, UpJitterMs = upJit, UpMaxGapMs = Max(samples.Select(x => x.s.UpMaxGapMs)),
            UpLostTotal = upLostT, UpOutOfOrderTotal = quality.Sum(x => x.s.UpOooTotal),
            UpLossTotalPct = upExpT > 0 ? Pct(upLostT, upExpT) : double.NaN,
            UpReportStale = samples.Any(x => x.s.UpStale), UpReportAgeSec = Max(samples.Select(x => x.s.UpAgeSec)),
            ServerTxMbps = SumF(samples.Select(x => x.s.ServerTxMbps)),
            DownMbps = samples.Sum(x => x.s.DownMbps),
            DownLossPct = dnLoss, DownJitterMs = double.IsFinite(dnJit) ? dnJit : 0,
            DownMaxGapMs = Max(samples.Select(x => x.s.DownMaxGapMs)),
            DownLostTotal = dnLostT, DownOutOfOrderTotal = quality.Sum(x => x.s.DownOooTotal), DownLossTotalPct = Pct(dnLostT, dnExpT),
            RttMs = p.RttMs, RttMinMs = p.RttMinMs, RttMaxMs = p.RttMaxMs,
            UpRetransBytes = tcp.Sum(x => x.s.UpRetransBytes), DownRetransBytes = tcp.Sum(x => x.s.DownRetransBytes),
            UpTimeoutsTotal = tcp.Sum(x => x.s.UpTimeouts), DownTimeoutsTotal = tcp.Sum(x => x.s.DownTimeouts),
            Mixed = mixed,
            UpMbpsUdp = mixed ? SumF(udp.Select(x => x.s.UpMbps)) : double.NaN,
            UpMbpsTcp = mixed ? SumF(tcp.Select(x => x.s.UpMbps)) : double.NaN,
            DownMbpsUdp = mixed ? udp.Sum(x => x.s.DownMbps) : double.NaN,
            DownMbpsTcp = mixed ? tcp.Sum(x => x.s.DownMbps) : double.NaN,
            RttTcpMs = mixed ? tcp[0].s.RttMs : double.NaN,
            Classes = classStats,
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
        if (_classes.Count <= 2)
        {
            foreach (var c in _classes)
                AppendClassDetail(sb, c, _classes.Count > 1 ? " " + c.Def.Transport.ToString().ToUpperInvariant() : "", dur, downDur, now);
        }
        else
        {
            sb.AppendLine($"{"Class",-20} {"DSCP",-10} {"Proto",-5} {"Up Mbps",8} {"Up loss",8} {"Down Mbps",9} {"Dn loss",8} {"Jitter dn",9} {"RTT",8}");
            foreach (var c in _classes)
            {
                StatsReport? up = c.Final;
                if (up == null && c.Eng.TryGetPeerReport(out var pr, out _)) up = pr;
                var rx = c.Eng.Rx.Snapshot(now);
                var rt = c.Eng.GetRttTotals();
                string upM = up is StatsReport u ? $"{u.Bytes * 8 / dur / 1e6:0.00}" : "-";
                string upL = up is StatsReport u2 ? $"{LossPct(u2):0.##}%" : "-";
                sb.AppendLine($"{Trunc(c.Def.Name, 20),-20} {c.Def.DscpName,-10} {c.Def.Transport,-5} {upM,8} {upL,8} {rx.Bytes * 8 / downDur / 1e6,9:0.00} " +
                              $"{LossPct(rx),7:0.##}% {rx.JitterUs / 1000,7:0.00}ms {(rt.Samples > 0 ? $"{rt.AvgUs / 1000:0.0}ms" : "-"),8}");
            }
        }
        long lag = _classes.Sum(c => c.Eng.SenderLagEvents);
        if (lag > 0) sb.AppendLine($"Note: client sender fell behind schedule {lag} time(s) - local CPU/NIC may be limiting.");
        foreach (var t in _tunnels)
        {
            var st = t.GetStatus(now);
            sb.AppendLine($"Tunnel {st.Name}: {st.Verdict}; availability {st.AvailabilityPct:0.##}%, drops {st.Drops}, flaps {st.Flaps}, " +
                          $"downtime {st.DownSec:0.0} s, longest outage {st.LongestOutageSec:0.0} s" +
                          (double.IsFinite(st.AvgRecoverSec) ? $", avg recovery {st.AvgRecoverSec:0.0} s" : ""));
        }
        return sb.ToString().TrimEnd();

        static string Trunc(string s, int n) => s.Length <= n ? s : s[..(n - 1)] + "…";
    }

    static double LossPct(StatsReport r) => r.UniquePackets() + r.Lost > 0 ? 100.0 * r.Lost / (r.UniquePackets() + r.Lost) : 0;

    static void AppendClassDetail(StringBuilder sb, ClassRuntime c, string tag, double dur, double downDur, long now)
    {
        StatsReport? up = c.Final;
        if (up == null && c.Eng.TryGetPeerReport(out var p, out _)) up = p;
        if (up is StatsReport u)
            sb.AppendLine($"Upload{tag,-4}  (client→server): {u.Bytes * 8 / dur / 1e6,8:0.00} Mbps avg, {u.Packets} pkts, lost {u.Lost} ({LossPct(u):0.###}%), out-of-order {u.OutOfOrder}, jitter {u.JitterUs / 1000:0.00} ms");
        else sb.AppendLine($"Upload{tag,-4}  (client→server): no report received from server");
        var rx = c.Eng.Rx.Snapshot(now);
        sb.AppendLine($"Download{tag,-4}(server→client): {rx.Bytes * 8 / downDur / 1e6,8:0.00} Mbps avg, {rx.Packets} pkts, lost {rx.Lost} ({LossPct(rx):0.###}%), out-of-order {rx.OutOfOrder}, jitter {rx.JitterUs / 1000:0.00} ms");
        var rt = c.Eng.GetRttTotals();
        sb.AppendLine(rt.Samples > 0
            ? $"RTT{tag}: avg {rt.AvgUs / 1000:0.00} ms, min {rt.MinUs / 1000:0.00} ms, max {rt.MaxUs / 1000:0.00} ms ({rt.Samples} samples)"
            : $"RTT{tag}: no samples");
    }

    void Teardown()
    {
        try { _senderCts.Cancel(); } catch { }
        try { _cts.Cancel(); } catch { }
        try { _probe?.Dispose(); } catch { }
        foreach (var c in _classes)
        {
            try { c.Udp?.Dispose(); } catch { }
            foreach (var s in c.Tcp) { try { s.Shutdown(SocketShutdown.Both); } catch { } try { s.Dispose(); } catch { } }
        }
        try { _ctrl?.Dispose(); } catch { }
        try { _qos?.Dispose(); } catch { }
    }

    void Emit(string msg) => Log?.Invoke(msg);

    public void Dispose()
    {
        _stopping = true;
        Teardown();
    }

    /// <summary>Per-interval figures for one class.</summary>
    sealed class ClassSample
    {
        public double TxMbps, UpMbps = double.NaN, UpLossPct = double.NaN, UpJitterMs = double.NaN, UpMaxGapMs = double.NaN;
        public long UpLostTotal, UpExpTotal, UpOooTotal, UpLostD, UpExpD;
        public bool UpStale = true;
        public double UpAgeSec = double.NaN, ServerTxMbps = double.NaN;
        public double DownMbps, DownLossPct, DownJitterMs, DownMaxGapMs;
        public long DownLostTotal, DownExpTotal, DownOooTotal, DownLostD, DownExpD;
        public double RttMs, RttMinMs, RttMaxMs, SmoothedRttMs;
        public long UpRetransBytes, DownRetransBytes;
        public int UpTimeouts, DownTimeouts;
    }

    /// <summary>Keeps the previous snapshot of one engine to turn cumulative counters into interval figures.</summary>
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
            s.DownLostD = Math.Max(0, local.Lost - _prevLocal.Lost);
            s.DownExpD = Math.Max(0, local.UniquePackets() - _prevLocal.UniquePackets()) + s.DownLostD;
            s.DownLossPct = s.DownExpD > 0 ? 100.0 * s.DownLostD / s.DownExpD : 0;
            s.DownLostTotal = local.Lost;
            s.DownExpTotal = local.UniquePackets() + local.Lost;
            s.DownJitterMs = local.JitterUs / 1000;
            s.DownMaxGapMs = local.MaxGapUs / 1000.0;
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
                s.UpLostTotal = peer.Lost;
                s.UpExpTotal = peer.UniquePackets() + peer.Lost;
                s.UpOooTotal = peer.OutOfOrder;
                s.UpJitterMs = peer.JitterUs / 1000;
                s.UpMaxGapMs = peer.MaxGapUs / 1000.0;
                s.DownTimeouts = peer.TcpTimeouts;
                if (_hasPrevPeer && peer.TimeUs > _prevPeer.TimeUs)
                {
                    s.UpStale = false;
                    double pdt = (peer.TimeUs - _prevPeer.TimeUs) / 1e6;
                    s.UpMbps = (peer.Bytes - _prevPeer.Bytes) * 8 / pdt / 1e6;
                    s.ServerTxMbps = (peer.TxBytes - _prevPeer.TxBytes) * 8 / pdt / 1e6;
                    s.UpLostD = Math.Max(0, peer.Lost - _prevPeer.Lost);
                    s.UpExpD = Math.Max(0, peer.UniquePackets() - _prevPeer.UniquePackets()) + s.UpLostD;
                    s.UpLossPct = s.UpExpD > 0 ? 100.0 * s.UpLostD / s.UpExpD : 0;
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
