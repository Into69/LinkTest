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

    /// <summary>Measure the path MTU (both directions) when the test starts. Needs a 1.6+ server.</summary>
    public bool MeasureMtu { get; set; } = true;
    /// <summary>Run a traceroute at the start and whenever loss, stalls or tunnel drops appear (max once a minute).</summary>
    public bool AutoTraceroute { get; set; } = true;
    /// <summary>Interval loss (%) in either direction that triggers a traceroute.</summary>
    public double TraceLossThresholdPct { get; set; } = 2;

    public MtuResult? Mtu { get; private set; }
    public event Action<MtuResult>? MtuMeasured;
    public IReadOnlyList<TraceResult> Traces { get { lock (_traces) return _traces.ToList(); } }
    public event Action<TraceResult>? TraceCompleted;

    readonly List<TraceResult> _traces = new();
    readonly BufferbloatMeter _bloat = new();
    readonly SampleSeries _series = new();
    readonly List<ClassSeries> _classSeries = new();
    readonly List<GeoFix> _locations = new();
    readonly List<IpChange> _ipChanges = new();
    volatile GeoFix? _lastFix;
    string? _publicIp;
    bool _online;
    readonly List<string> _events = new();
    double _mosSum, _maxGap;
    int _mosCount;
    string _host = "";
    string? _serverAppVersion;

    /// <summary>The saved-result record, available once the test has stopped.</summary>
    public TestRecord? Record { get; private set; }
    Version _serverVersion = new(0, 0, 0);
    int _tracing;
    long _lastTraceUs;

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
        /// <summary>Latest detailed statistics from the server for this class (1.8+ servers).</summary>
        public volatile ClassDetail? ServerDetail;
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

    /// <param name="family">InterNetwork / InterNetworkV6 to insist on one address family; null = IPv4 first, then IPv6.</param>
    public static async Task<IPAddress> ResolveAsync(string host, CancellationToken ct, AddressFamily? family = null)
    {
        host = host.Trim();
        if (host.StartsWith('[') && host.EndsWith(']')) host = host[1..^1];
        string fam = family == AddressFamily.InterNetworkV6 ? "IPv6" : "IPv4";
        if (IPAddress.TryParse(host, out var ip))
        {
            ip = Protocol.Normalize(ip);
            if (family != null && ip.AddressFamily != family)
                throw new InvalidOperationException($"{host} is not an {fam} address: enter the server's name (or its {fam} address) to test over {fam}.");
            return ip;
        }
        var addrs = await Dns.GetHostAddressesAsync(host, ct);
        if (family != null)
            return addrs.FirstOrDefault(a => a.AddressFamily == family)
                   ?? throw new InvalidOperationException($"{host} has no {fam} address (no {(family == AddressFamily.InterNetworkV6 ? "AAAA" : "A")} record).");
        return addrs.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
               ?? addrs.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetworkV6)
               ?? throw new SocketException((int)SocketError.HostNotFound);
    }

    /// <summary>Only test over this address family (null = IPv4 first, then IPv6).</summary>
    public AddressFamily? Family { get; set; }
    /// <summary>"IPv4" or "IPv6" once connected.</summary>
    public string FamilyName => ServerEndPoint?.AddressFamily == AddressFamily.InterNetworkV6 ? "IPv6" : "IPv4";

    // ------------------------------------------------------------ capacity test
    /// <summary>
    /// Find the link's capacity first: ramp each direction up from a few Mbps to the configured rate (used as the most to
    /// try) until it saturates, then run the test at <see cref="CapacityTargetPct"/> of what was found. Needs a 1.9+ server.
    /// </summary>
    public bool CapacityTest { get; set; }
    public double CapacityTargetPct { get; set; } = 80;
    public double CapacityUpMbps { get; private set; } = double.NaN;
    public double CapacityDownMbps { get; private set; } = double.NaN;
    /// <summary>True when the direction never saturated (capacity is at least the value found).</summary>
    public bool CapacityUpAtLimit { get; private set; }
    public bool CapacityDownAtLimit { get; private set; }
    /// <summary>Seconds from the start until the measured test begins (idle baseline + capacity probe).</summary>
    public double WarmupSec { get; private set; }
    public event Action? CapacityMeasured;

    readonly System.Collections.Concurrent.ConcurrentQueue<StatSample> _probeQ = new();
    volatile bool _probing;
    double _baseUp, _baseDown, _maxUp, _maxDown, _serverCap = double.PositiveInfinity;

    /// <summary>Sets new upload / download rates while the test runs (1.9+ server; both base rates must be above 0).</summary>
    void SetRates(double up, double down)
    {
        if (_baseUp > 0) foreach (var c in _classes) c.Eng.RateScale = up / _baseUp;
        _ctrl?.TrySend(new ControlMessage { Type = "rate", Config = new TestConfig { UpMbps = up, DownMbps = down } });
    }

    async Task RunCapacityProbeAsync()
    {
        const double start = 2, growth = 1.6;
        try
        {
            _probing = true;
            await Task.Delay(TimeSpan.FromSeconds(Config.BaselineSec + 1), _cts.Token);
            double maxUp = Math.Min(_maxUp, _serverCap), maxDn = Math.Min(_maxDown, _serverCap);
            double rUp = maxUp > 0 ? Math.Min(start, maxUp) : 0, rDn = maxDn > 0 ? Math.Min(start, maxDn) : 0;
            bool upDone = rUp <= 0, dnDone = rDn <= 0;
            double capUp = 0, capDn = 0;
            Emit($"Capacity test: ramping up to {(maxUp > 0 ? $"{maxUp:0.###} Mbps up" : "")}{(maxUp > 0 && maxDn > 0 ? " / " : "")}{(maxDn > 0 ? $"{maxDn:0.###} Mbps down" : "")}…");
            while (!(upDone && dnDone) && !_cts.IsCancellationRequested && !_stopping)
            {
                SetRates(rUp, rDn);
                while (_probeQ.TryDequeue(out _)) { }
                var got = new List<StatSample>();
                while (got.Count < 3 && !_cts.IsCancellationRequested)   // the first second after a change is mixed
                {
                    await Task.Delay(250, _cts.Token);
                    while (_probeQ.TryDequeue(out var s)) got.Add(s);
                }
                var use = got.Skip(1).ToList();
                double Avg(Func<StatSample, double> f) { var v = use.Select(f).Where(double.IsFinite).ToList(); return v.Count > 0 ? v.Average() : double.NaN; }
                double rxUp = Avg(s => s.UpMbps), rxDn = Avg(s => s.DownMbps);
                double lossUp = Avg(s => s.UpLossPct), lossDn = Avg(s => s.DownLossPct);
                if (!upDone)
                {
                    if (double.IsFinite(rxUp)) capUp = Math.Max(capUp, rxUp);
                    bool saturated = !(rxUp >= 0.9 * rUp) || lossUp > 5;
                    if (saturated || rUp >= maxUp) { upDone = true; CapacityUpAtLimit = !saturated; rUp = Final(capUp, maxUp); }
                    else rUp = Math.Min(maxUp, rUp * growth);
                }
                if (!dnDone)
                {
                    if (double.IsFinite(rxDn)) capDn = Math.Max(capDn, rxDn);
                    bool saturated = !(rxDn >= 0.9 * rDn) || lossDn > 5;
                    if (saturated || rDn >= maxDn) { dnDone = true; CapacityDownAtLimit = !saturated; rDn = Final(capDn, maxDn); }
                    else rDn = Math.Min(maxDn, rDn * growth);
                }
            }
            if (_cts.IsCancellationRequested || _stopping) return;
            CapacityUpMbps = maxUp > 0 ? capUp : double.NaN;
            CapacityDownMbps = maxDn > 0 ? capDn : double.NaN;
            SetRates(rUp, rDn);
            Config.UpMbps = Math.Round(rUp, 3);
            Config.DownMbps = Math.Round(rDn, 3);
            WarmupSec = (Clock.NowUs - _startUs) / 1e6 + 1;
            string Cap(double v, bool limit) => double.IsFinite(v) ? $"{(limit ? "at least " : "")}{v:0.##} Mbps" : "not tested";
            Emit($"Capacity: up {Cap(CapacityUpMbps, CapacityUpAtLimit)}, down {Cap(CapacityDownMbps, CapacityDownAtLimit)}. " +
                 $"Testing at {CapacityTargetPct:0}%: up {Config.UpMbps:0.###} / down {Config.DownMbps:0.###} Mbps.");
            CapacityMeasured?.Invoke();
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { Emit("Capacity test failed: " + e.Message); }
        finally { _probing = false; }

        double Final(double cap, double max) => Math.Max(0.05, Math.Min(max, cap * CapacityTargetPct / 100));
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
            var ip = await ResolveAsync(host, ct, Family);
            if (CapacityTest)
            {
                // The configured rates are the most to try; the test starts low and ramps up.
                _maxUp = config.UpMbps; _maxDown = config.DownMbps;
                if (_maxUp <= 0 && _maxDown <= 0) throw new InvalidOperationException("Set an upload and/or download rate: the capacity test ramps up to it.");
                config.UpMbps = _maxUp > 0 ? Math.Min(2, _maxUp) : 0;
                config.DownMbps = _maxDown > 0 ? Math.Min(2, _maxDown) : 0;
                _baseUp = config.UpMbps; _baseDown = config.DownMbps;
            }
            ServerEndPoint = new IPEndPoint(ip, port);
            _host = host;
            Emit($"Connecting to {host} ({ip}) port {port}…");

            var (ctrlSock, ctrl) = await OpenControlAsync(host, ct);
            _ctrl = ctrl;
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
            if (welcome.MaxMbps > 0) _serverCap = welcome.MaxMbps;
            Updater.TryParseVersion(welcome.AppVersion ?? "0", out _serverVersion);
            _serverAppVersion = welcome.AppVersion;
            if (_serverVersion < new Version(1, 6, 0))
            {
                // Older servers don't pause their downstream for the idle baseline, nor answer MTU probes.
                Config.BaselineSec = 0;
                if (MeasureMtu) Emit($"Path-MTU test and bufferbloat grade need a 1.6+ server (server is {(welcome.AppVersion ?? "older than 1.4")}).");
            }
            if (!string.IsNullOrEmpty(welcome.Message)) Emit("Server: " + welcome.Message);
            _lossPct = Math.Clamp(artificialLossPct, 0, 100);
            _online = ipLookup;
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
                    rt.Eng.TcpDetailProvider = () => TcpInfo.Detail(socks);
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
                sim.Event += (s, e) =>
                {
                    Emit($"[{s.Profile.Name}] {e}");
                    if (e.StartsWith("DOWN", StringComparison.Ordinal)) MaybeTrace($"{s.Profile.Name} dropped");
                };
                _tunnels.Add(sim);
            }
            if (_tunnels.Count > 0) StartThread("tunnels", TunnelLoop, isSender: true);

            if (MeasureMtu && _serverVersion >= new Version(1, 6, 0)) _ = Task.Run(RunMtuTestAsync);
            if (CapacityTest)
            {
                if (_serverVersion < new Version(1, 9, 0)) throw new NotSupportedException("The capacity test needs a 1.9+ server; update the server.");
                _ = Task.Run(RunCapacityProbeAsync);
            }
            else WarmupSec = Config.BaselineSec;
            if (AutoTraceroute) _ = Task.Run(async () => { await Task.Delay(1500, _cts.Token); MaybeTrace("start (baseline)", force: true); });

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
            : local != null && Protocol.Normalize(local.Address).Equals(seenIp) ? "No NAT - the server sees this device's own address"
            : "Behind NAT - the server sees a different (translated) address";
        var info = new ConnectionInfo($"{host} ({ServerEndPoint}, {FamilyName})", localStr, seenByServer, nat, null, null, false, null, ControlSecurity);
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
                    done = done with { NatStatus = $"Not applicable - the test runs over a private path ({seen.Scope}); this device's internet address is {pub.Ip}" };
                SetConnection(done with { DeviceLocation = _lastFix });
                Emit("Client IP: " + (pub != null ? pub.Summary : seen != null ? seen.Summary : localStr) +
                     (seenByServer != null ? $" | seen by server as {seenByServer}" : ""));
                if (pub != null && pub.Error == null) _publicIp = pub.Ip;
                if (online) _ = Task.Run(MonitorPublicIpAsync);
            }
            catch (OperationCanceledException) { }
            catch (Exception e) { Emit("IP lookup failed: " + e.Message); }
        });
    }

    /// <summary>Seconds between checks of this device's public IP (sooner when the network changes).</summary>
    public int PublicIpCheckSec { get; set; } = 60;

    /// <summary>Re-checks the public IP every minute and whenever the network changes; on a change, looks it up again.</summary>
    async Task MonitorPublicIpAsync()
    {
        var changed = new SemaphoreSlim(0);
        System.Net.NetworkInformation.NetworkAddressChangedEventHandler onChange = (_, _) => { try { changed.Release(); } catch { } };
        try { System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged += onChange; } catch { }
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                bool netChanged = await changed.WaitAsync(TimeSpan.FromSeconds(PublicIpCheckSec), _cts.Token);
                if (netChanged) await Task.Delay(3000, _cts.Token);   // let the new network settle
                while (changed.CurrentCount > 0) changed.Wait(0);
                var ip = await IpLookup.PublicAddressAsync(_cts.Token);
                if (ip == null) continue;
                string now = Protocol.Normalize(ip).ToString();
                string? before = _publicIp;
                if (before == null) { _publicIp = now; continue; }
                if (now == before) continue;
                _publicIp = now;
                IpLookup.Forget(now);
                var info = await IpLookup.LookupAsync(ip, true, _cts.Token);
                lock (_ipChanges) _ipChanges.Add(new IpChange(DateTime.Now, before, now, info.Summary));
                Emit($"Public IP changed: {before} -> {now} - {info.Summary}");
                if (Connection is { } c) SetConnection(c with { PublicInfo = info });
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { Emit("Public IP check stopped: " + e.Message); }
        finally { try { System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged -= onChange; } catch { } }
    }

    /// <summary>The server reports that this client's traffic now arrives from another address (1.9+ servers).</summary>
    void OnSeenByServerChanged(string address)
    {
        if (Connection is not { } c || c.SeenByServer == address) return;
        Emit($"The server now sees this client as {address} (was {c.SeenByServer ?? "?"})");
        SetConnection(c with { SeenByServer = address });
        int colon = address.LastIndexOf(':');
        if (!IPAddress.TryParse(colon > 0 ? address[..colon].Trim('[', ']') : address, out var ip)) return;
        _ = Task.Run(async () =>
        {
            try
            {
                var info = await IpLookup.LookupAsync(ip, _online, _cts.Token);
                if (Connection is { } cur) SetConnection(cur with { SeenByServerInfo = info });
                Emit("Seen-by-server address: " + info.Summary);
            }
            catch { }
        });
    }

    /// <summary>The most recent location fix of the device (null unless the app reports locations).</summary>
    public GeoFix? Location => _lastFix;
    public IReadOnlyList<GeoFix> Locations { get { lock (_locations) return _locations.ToList(); } }

    /// <summary>
    /// Records the position of the device with the test data and passes it to the server (1.9+). Only call this when
    /// the user turned location reporting on.
    /// </summary>
    public void ReportLocation(GeoFix fix)
    {
        if (!fix.IsValid || !IsRunning) return;
        var prev = _lastFix;
        _lastFix = fix;
        lock (_locations)
        {
            // A fix every 10 s, or sooner when the device moved: enough for a drive test, bounded in size.
            var last = _locations.Count > 0 ? _locations[^1] : null;
            if (last == null || (fix.At - last.At).TotalSeconds >= 10 || last.DistanceTo(fix) > 25)
            {
                _locations.Add(fix);
                if (_locations.Count > 20000) _locations.RemoveRange(0, 1000);
            }
        }
        if (prev == null) Emit("Device location: " + fix.Text + (fix.Source.Length > 0 ? $" ({fix.Source})" : ""));
        if (_serverVersion >= new Version(1, 9, 0)) _ctrl?.TrySend(new ControlMessage { Type = "location", Location = fix });
        if (Connection is { } c) SetConnection(c with { DeviceLocation = fix });
    }

    void SetConnection(ConnectionInfo c)
    {
        Connection = c;
        ConnectionChanged?.Invoke(c);
    }

    /// <summary>Try TLS for the control connection (1.9+ servers).</summary>
    public bool UseTls { get; set; } = true;
    /// <summary>Refuse servers that can't encrypt the control connection.</summary>
    public bool RequireTls { get; set; }
    /// <summary>Remembered server certificates (trust on first use); null = accept any certificate.</summary>
    public KnownServers? KnownServers { get; set; }
    /// <summary>"TLS 1.3 · certificate AB:CD:…" or a note that the connection is not encrypted.</summary>
    public string ControlSecurity { get; private set; } = "";
    public string? ServerFingerprint { get; private set; }

    async Task<(Socket, ControlChannel)> OpenControlAsync(string host, CancellationToken ct)
    {
        string serverKey = $"{host}:{ServerEndPoint!.Port}";
        if (UseTls || RequireTls)
        {
            var s = await ConnectTcpAsync(ServerEndPoint, ct);
            try
            {
                s.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
                s.Send(new[] { Protocol.ConnControlTls });
                using var t = CancellationTokenSource.CreateLinkedTokenSource(ct);
                t.CancelAfter(TimeSpan.FromSeconds(10));
                var (ssl, fp) = await Tls.ClientHandshakeAsync(new NetworkStream(s, ownsSocket: true), host, t.Token);
                var known = KnownServers?.Get(serverKey);
                if (known != null && !string.Equals(known, fp, StringComparison.OrdinalIgnoreCase))
                {
                    await ssl.DisposeAsync();
                    throw new ServerCertificateChangedException(serverKey, known, fp);
                }
                bool first = KnownServers != null && known == null;
                if (first) KnownServers!.Trust(serverKey, fp);
                ServerFingerprint = fp;
                var ch = new ControlChannel(s, ssl);
                ControlSecurity = $"{ch.Security} · server certificate {Tls.ShortFingerprint(fp)}" +
                                  (KnownServers == null ? "" : first ? " (remembered on first use)" : " (matches the remembered one)");
                Emit("Control connection encrypted: " + ControlSecurity);
                return (s, ch);
            }
            catch (ServerCertificateChangedException) { throw; }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { s.Dispose(); throw; }
            catch (Exception e)
            {
                s.Dispose();
                // Servers older than 1.9 close the connection on the TLS request.
                if (RequireTls) throw new IOException("The server does not support an encrypted connection (it may be older than 1.9): " + e.Message, e);
                Emit("Server doesn't support encryption (older than 1.9?) - the control connection is not encrypted.");
            }
        }
        var plain = await ConnectTcpAsync(ServerEndPoint, ct);
        plain.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
        plain.Send(new[] { Protocol.ConnControl });
        ControlSecurity = "not encrypted";
        return (plain, new ControlChannel(plain));
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
        if (name.Contains("tx") || name == "tunnels") try { t.Priority = ThreadPriority.AboveNormal; } catch { /* not allowed on some platforms */ }
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
                    case "stats":
                        if (!_final.Task.IsCompleted && m.Details != null)
                            for (int i = 0; i < m.Details.Count && i < _classes.Count; i++) _classes[i].ServerDetail = m.Details[i];
                        break;
                    case "final":
                        if (m.Details != null)
                            for (int i = 0; i < m.Details.Count && i < _classes.Count; i++) _classes[i].ServerDetail = m.Details[i];
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
                    case "seen":
                        if (!string.IsNullOrEmpty(m.ClientAddress)) OnSeenByServerChanged(m.ClientAddress);
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
        double elapsed = (now - _startUs) / 1e6;
        _bloat.Add(elapsed, Config.BaselineSec, p.RttMs);
        bool loaded = Config.UpMbps + Config.DownMbps > 0;
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

        var sample = new StatSample
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
            BaselinePhase = Config.BaselineSec > 0 && elapsed <= Config.BaselineSec,
            IdleRttMs = _bloat.IdleMs,
            LoadedRttMs = loaded ? _bloat.LoadedMs : double.NaN,
            BloatGrade = loaded ? _bloat.CurrentGrade : null,
            Mos = StatSample.EstimateMos(double.IsFinite(p.RttMs) ? p.RttMs : p.SmoothedRttMs, worstJit, worstLoss),
            Detail = BuildDetail(now),
        };

        // Trouble triggers a traceroute (rate limited inside MaybeTrace).
        if (elapsed > 3)
        {
            double worst = Math.Max(Ui0(sample.UpLossPct), sample.DownLossPct);
            double gap = Math.Max(Ui0(sample.UpMaxGapMs), Ui0(sample.DownMaxGapMs));
            if (worst >= TraceLossThresholdPct) MaybeTrace($"loss {worst:0.#}%");
            else if (gap >= 1000) MaybeTrace($"{gap / 1000:0.0} s stall");
        }
        if (_probing) _probeQ.Enqueue(sample);
        lock (_series)
        {
            _series.Add(sample);
            _series.SetLocation(_lastFix);
            for (int i = 0; i < classStats.Count; i++)
            {
                if (_classSeries.Count <= i) _classSeries.Add(new ClassSeries { Name = classStats[i].Name });
                _classSeries[i].Add(classStats[i]);
            }
            if (double.IsFinite(sample.Mos)) { _mosSum += sample.Mos; _mosCount++; }
            double g = Math.Max(Ui0(sample.UpMaxGapMs), Ui0(sample.DownMaxGapMs));
            if (g > _maxGap) _maxGap = g;
        }
        return sample;

        static double Ui0(double v) => double.IsFinite(v) ? v : 0;
    }

    /// <summary>Server supports detailed statistics (1.8+).</summary>
    public bool ServerSendsDetail => _serverVersion >= new Version(1, 8, 0);

    /// <summary>Detailed UDP / TCP statistics in both directions, from both ends' reports.</summary>
    public LinkDetail BuildDetail(long? nowUs = null, bool final = false)
    {
        long now = nowUs ?? Clock.NowUs;
        double dur = Math.Max(1e-3, ((_stopUs > 0 ? _stopUs : now) - _startUs) / 1e6);
        return LinkDetail.Build(_classes.Select(c => (c.Def.Transport, c.ServerDetail, c.Eng.BuildDetail(now, final))), dur, ServerSendsDetail);
    }

    async Task RunMtuTestAsync()
    {
        try
        {
            await Task.Delay(500, _cts.Token);
            var server = new IPEndPoint(ServerEndPoint!.Address, ((IPEndPoint)_probe!.RemoteEndPoint!).Port);
            double rtt = _classes.Count > 0 ? _classes[0].Eng.SmoothedRttUs / 1000 : double.NaN;
            var r = await PathMtu.MeasureAsync(server, SessionId, rtt > 0 ? rtt : double.NaN, _cts.Token);
            Mtu = r;
            Emit(r.Summary);
            if (r.Ok)
            {
                var best = VpnOverhead.All.Select(v => $"{v.Name.Split(' ')[0]} {v.TunnelMtu(r.PathMtu, r.Ipv6)}").Distinct();
                Emit("  recommended tunnel MTU: " + string.Join(", ", best));
            }
            MtuMeasured?.Invoke(r);
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { Emit("MTU test failed: " + e.Message); }
    }

    /// <summary>Starts a traceroute to the server unless one ran in the last minute (or is running).</summary>
    void MaybeTrace(string reason, bool force = false)
    {
        if (!AutoTraceroute || _stopping || ServerEndPoint == null) return;
        long now = Clock.NowUs;
        if (!force && now - Interlocked.Read(ref _lastTraceUs) < 60_000_000) return;
        if (Interlocked.Exchange(ref _tracing, 1) == 1) return;
        Interlocked.Exchange(ref _lastTraceUs, now);
        _ = Task.Run(async () =>
        {
            try
            {
                var r = await PathTracer.TraceAsync(ServerEndPoint.Address, reason, ct: _cts.Token);
                lock (_traces)
                {
                    _traces.Add(r);
                    if (_traces.Count > 20) _traces.RemoveAt(0);
                }
                Emit(r.Summary);
                TraceCompleted?.Invoke(r);
            }
            catch (OperationCanceledException) { }
            catch (Exception e) { Emit("Traceroute failed: " + e.Message); }
            finally { Interlocked.Exchange(ref _tracing, 0); Interlocked.Exchange(ref _lastTraceUs, Clock.NowUs); }
        });
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
            string summary = BuildSummary();
            Emit(summary);
            try { Record = BuildRecord(reason, summary); } catch (Exception e) { Emit("Could not build the test record: " + e.Message); }
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
        if (Config.BaselineSec > 0 && Config.UpMbps + Config.DownMbps > 0)
            sb.AppendLine($"(averages include the {Config.BaselineSec} s idle baseline at the start)");
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
        if (Config.UpMbps + Config.DownMbps > 0 && _bloat.CurrentGrade is string g)
            sb.AppendLine($"Bufferbloat: grade {g} - RTT +{_bloat.IncreaseMs:0.0} ms under load (idle {_bloat.IdleMs:0.0} ms, loaded {_bloat.LoadedMs:0.0} ms): {BufferbloatMeter.Describe(g)}");
        if (Mtu != null) sb.AppendLine(Mtu.Summary);
        var lossyTrace = Traces.LastOrDefault(t => t.LossStartsAt != null);
        if (lossyTrace != null) sb.AppendLine("Last trouble trace: " + lossyTrace.Summary);
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

    void Emit(string msg)
    {
        lock (_events)
        {
            foreach (var line in msg.Replace("\r", "").Split('\n'))
                _events.Add($"{DateTime.Now:HH:mm:ss}  {line}");
            if (_events.Count > 5000) _events.RemoveRange(0, 1000);
        }
        Log?.Invoke(msg);
    }

    TestRecord BuildRecord(string reason, string summary)
    {
        long now = Clock.NowUs;
        double dur = Math.Max(1e-3, ((_stopUs > 0 ? _stopUs : now) - _startUs) / 1e6);
        double downDur = Math.Max(1e-3, ((_finalUs > 0 ? _finalUs : now) - _startUs) / 1e6);
        var quality = _classes.Any(c => c.IsUdp) ? _classes.Where(c => c.IsUdp).ToList() : _classes;
        var primary = quality.FirstOrDefault() ?? _classes.First();

        StatsReport? Up(ClassRuntime c) => c.Final ?? (c.Eng.TryGetPeerReport(out var p, out _) ? p : null);
        long upBytes = 0, upLost = 0, upExp = 0, dnLost = 0, dnExp = 0;
        foreach (var c in _classes) if (Up(c) is StatsReport u) upBytes += u.Bytes;
        foreach (var c in quality)
        {
            if (Up(c) is StatsReport u) { upLost += u.Lost; upExp += u.UniquePackets() + u.Lost; }
            var rx = c.Eng.Rx.Snapshot(now);
            dnLost += rx.Lost; dnExp += rx.UniquePackets() + rx.Lost;
        }
        long dnBytes = _classes.Sum(c => c.Eng.Rx.Snapshot(now).Bytes);
        var rt = primary.Eng.GetRttTotals();

        var classes = _classes.Select(c =>
        {
            var u = Up(c);
            var rx = c.Eng.Rx.Snapshot(now);
            var crt = c.Eng.GetRttTotals();
            return new ClassSummary(c.Def.Name, c.Def.Transport, c.Def.Dscp, c.Def.SharePct,
                u is StatsReport a ? a.Bytes * 8 / dur / 1e6 : double.NaN, u is StatsReport b ? LossPct(b) : double.NaN,
                rx.Bytes * 8 / downDur / 1e6, LossPct(rx), rx.JitterUs / 1000, crt.Samples > 0 ? crt.AvgUs / 1000 : double.NaN);
        }).ToList();

        bool loaded = Config.UpMbps + Config.DownMbps > 0;
        var record = new TestRecord
        {
            Id = $"{StartedAt:yyyyMMddHHmmss}-{SessionId:X8}",
            StartedAt = StartedAt,
            EndedAt = DateTime.Now,
            EndReason = reason,
            AppVersion = Updater.CurrentVersion.ToString(),
            ServerVersion = _serverAppVersion,
            Server = $"{_host} ({ServerEndPoint})",
            Config = Config.Clone(),
            DurationSec = Math.Round(dur, 1),
            UpMbpsAvg = upBytes * 8 / dur / 1e6,
            DownMbpsAvg = dnBytes * 8 / downDur / 1e6,
            UpLossPct = upExp > 0 ? 100.0 * upLost / upExp : double.NaN,
            DownLossPct = dnExp > 0 ? 100.0 * dnLost / dnExp : double.NaN,   // nothing received: not measured
            RttAvgMs = rt.Samples > 0 ? rt.AvgUs / 1000 : double.NaN,
            RttMinMs = rt.Samples > 0 ? rt.MinUs / 1000 : double.NaN,
            RttMaxMs = rt.Samples > 0 ? rt.MaxUs / 1000 : double.NaN,
            JitterUpMs = Up(primary) is StatsReport pu ? pu.JitterUs / 1000 : double.NaN,
            JitterDownMs = primary.Eng.Rx.Snapshot(now).JitterUs / 1000,
            BloatGrade = loaded ? _bloat.CurrentGrade : null,
            IdleRttMs = _bloat.IdleMs,
            LoadedRttMs = loaded ? _bloat.LoadedMs : double.NaN,
            Summary = summary,
            Connection = Connection,
            Mtu = Mtu,
            Traces = Traces.ToList(),
            Tunnels = _tunnels.Select(t => t.GetStatus(now)).ToList(),
            Classes = classes,
            Detail = BuildDetail(now, final: true),
        };
        lock (_series)
        {
            record.Samples = _series.Clone();
            record.ClassSeries = _classSeries.Select(c => c.Clone()).ToList();
            record.Family = FamilyName;
            if (CapacityTest && WarmupSec > 0)
            {
                // Averages of the measured part only (the ramp-up would drag them down).
                double After(List<double> v) { var x = v.Where((val, i) => i < _series.T.Count && _series.T[i] > WarmupSec + 1 && double.IsFinite(val)).ToList(); return x.Count > 0 ? x.Average() : double.NaN; }
                if (double.IsFinite(After(_series.Up))) record.UpMbpsAvg = After(_series.Up);
                if (double.IsFinite(After(_series.Down))) record.DownMbpsAvg = After(_series.Down);
            }
            record.WarmupSec = WarmupSec;
            record.CapacityUpMbps = CapacityUpMbps; record.CapacityDownMbps = CapacityDownMbps;
            record.CapacityUpAtLimit = CapacityUpAtLimit; record.CapacityDownAtLimit = CapacityDownAtLimit;
            lock (_locations) record.Locations = _locations.ToList();
            lock (_ipChanges) record.IpChanges = _ipChanges.ToList();
            record.MaxGapMs = _maxGap;
            record.MosAvg = _mosCount > 0 ? _mosSum / _mosCount : double.NaN;
        }
        lock (_events) record.Events = _events.ToList();
        return record;
    }

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
