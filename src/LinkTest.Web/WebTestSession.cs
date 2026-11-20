using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using LinkTest.Core;
using LinkTest.Server;

namespace LinkTest.Web;

/// <summary>
/// Speed test plan: after the idle baseline, <paramref name="DownSec"/> seconds of download at full speed, then
/// <paramref name="UpSec"/> seconds of upload. The first <paramref name="RampSec"/> seconds of each phase (TCP
/// slow start) are left out of the result.
/// </summary>
sealed record SpeedPlan(int DownSec, int UpSec, int RampSec)
{
    /// <summary>
    /// The plan for a link with this idle round-trip time. TCP's slow start doubles the rate once per round trip, so
    /// on a 700 ms satellite link it needs several seconds to reach full speed: leave those out and keep at least
    /// 6 measured seconds per direction.
    /// </summary>
    public static SpeedPlan For(int downSec, int upSec, double rttMs)
    {
        double rtt = double.IsFinite(rttMs) && rttMs > 0 ? rttMs / 1000 : 0;
        int baseRamp = Math.Min(downSec, upSec) >= 6 ? 2 : 1;
        int ramp = (int)Math.Clamp(Math.Ceiling(1 + 8 * rtt), baseRamp, 15);
        // Only a high-latency link lengthens the phases (to the ramp-up + 6 measured seconds).
        int Len(int s) => Math.Clamp(ramp > baseRamp ? Math.Max(s, ramp + 6) : s, 4, 60);
        return new SpeedPlan(Len(downSec), Len(upSec), ramp);
    }
}

/// <summary>Figures the web page measured in one second (download direction, RTT, what it sent).</summary>
sealed record BrowserSecond(int Index, long TxUpBytes, long RxDownTcp, long RxDownUdp, long DownLost, long DownExpected,
    double DownJitterMs, double DownMaxGapMs, long DownLostTotal, long DownExpectedTotal, double RttMs, double RttMinMs,
    double RttMaxMs, int RttCount);

/// <summary>
/// One browser test. The page and the server each measure what they receive: the server the upload (TCP bytes over
/// WebSockets, UDP-style packets over the WebRTC data channel), the page the download and the RTT. Every second the
/// page sends its figures; the session merges them with its own into a <see cref="StatSample"/>, sends that back
/// for display and keeps it for the saved record.
/// </summary>
sealed class WebTestSession : IDisposable
{
    readonly WebServer _web;
    readonly Func<JsonObject, Task> _send;
    readonly CancellationTokenSource _cts = new();
    readonly ConcurrentDictionary<int, BrowserSecond> _browser = new();
    readonly List<string> _events = new();
    readonly SampleSeries _series = new();
    readonly BufferbloatMeter _bloat = new();
    readonly UdpMeter _upUdp = new();
    long _upTcpBytes, _downTcpSent, _downUdpSent, _downUdpBlocked, _rxTotal, _txTotal;
    long _t0Us, _loadStartUs;
    volatile bool _running, _stopped, _sendingDone;
    int _stopOnce;
    double _lastRttMs = double.NaN;
    long _upLostTotal, _upExpTotal, _downLostTotal, _downExpTotal;
    IpInfo? _clientInfo;

    /// <summary>Highest UDP-style rate per direction a browser test runs at.</summary>
    public const double MaxUdpMbps = 25;

    public uint Id { get; }
    public string Token { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
    public TestConfig Config { get; }
    public IPAddress ClientIp { get; }
    public string ServerName { get; }
    public string Security { get; }
    public string UserAgent { get; }
    public DateTime StartedAt { get; private set; } = DateTime.Now;
    public bool UsesUdp => UpUdpMbps + DownUdpMbps > 0;
    public bool UsesTcp => UpTcpMbps + DownTcpMbps > 0;
    public double UpUdpMbps { get; }
    public double DownUdpMbps { get; }
    public double UpTcpMbps { get; }
    public double DownTcpMbps { get; }
    public int Streams { get; }
    public WebRtcLink? Rtc { get; private set; }
    /// <summary>Set for a speed test.</summary>
    public SpeedPlan? Speed { get; }
    /// <summary>
    /// UDP-style tests: seconds after the baseline left out of the averages while WebRTC's congestion window opens
    /// (several round trips, so only noticeable on high-latency links).
    /// </summary>
    public int WarmupSec { get; }
    /// <summary>Round-trip time the page measured before the test (ms; NaN if unknown).</summary>
    public double PreRttMs { get; }
    public bool IsRunning => _running && !_stopped;
    /// <summary>The test ended (or was never started and was abandoned).</summary>
    public bool Stopped => _stopped;
    /// <summary>Path of the saved result once the test ended.</summary>
    public string? SavedPath { get; private set; }

    public WebTestSession(WebServer web, uint id, TestConfig cfg, IPAddress client, string serverName, string security, string userAgent,
        Func<JsonObject, Task> send, SpeedPlan? speed = null, double preRttMs = double.NaN)
    {
        _web = web; _send = send; Speed = speed; PreRttMs = preRttMs;
        _lateUs = double.IsFinite(preRttMs) ? preRttMs * 1000 : 0;
        Id = id; Config = cfg; ClientIp = client; ServerName = serverName; Security = security; UserAgent = userAgent;
        double udpShare = cfg.Protocol switch { TransportKind.Udp => 1, TransportKind.Tcp => 0, _ => cfg.UdpSharePct / 100 };
        // Browsers' WebRTC data channels stop behaving like plain UDP above ~25 Mbps (sender throttling, queueing):
        // cap the UDP-style part; in a mixed test TCP carries the rest.
        UpUdpMbps = Math.Min(cfg.UpMbps * udpShare, MaxUdpMbps); DownUdpMbps = Math.Min(cfg.DownMbps * udpShare, MaxUdpMbps);
        if (cfg.Protocol == TransportKind.Udp) { cfg.UpMbps = UpUdpMbps; cfg.DownMbps = DownUdpMbps; }
        UpTcpMbps = cfg.UpMbps - UpUdpMbps; DownTcpMbps = cfg.DownMbps - DownUdpMbps;
        Streams = Math.Clamp(cfg.Streams, 1, 8);
        WarmupSec = UpUdpMbps + DownUdpMbps > 0 && PreRttMs > 250 ? (int)Math.Ceiling(PreRttMs / 1000 * 6) : 0;
        Event($"Browser test from {client} ({BrowserName(userAgent)}): {Describe()}");
        if (double.IsFinite(preRttMs)) Event($"Round-trip time before the test: {preRttMs:0} ms");
        if (WarmupSec > 0) Event($"High latency: the first {WarmupSec} s of traffic are left out while the WebRTC channel's congestion window opens");
        if (speed is { RampSec: > 2 }) Event($"High latency: {speed.RampSec} s of each phase left out as TCP ramp-up; {speed.DownSec} s download / {speed.UpSec} s upload");
    }

    public string Describe()
    {
        var c = Config;
        if (Speed is { } sp) return $"speed test, {Streams} TCP connection(s): {sp.DownSec} s download, then {sp.UpSec} s upload";
        string what = c.Protocol switch
        {
            TransportKind.Tcp => $"TCP{(Streams > 1 ? $" ×{Streams}" : "")}",
            TransportKind.Mixed => $"mixed {c.UdpSharePct:0}% UDP",
            _ => "UDP (WebRTC)",
        };
        return $"{what} {Pacing.Describe(c.Profile)}, up {c.UpMbps:0.###} / down {c.DownMbps:0.###} Mbps" +
               (c.DurationSec > 0 ? $", {c.DurationSec} s" : "");
    }

    public static string BrowserName(string ua)
    {
        if (string.IsNullOrEmpty(ua)) return "browser";
        string name = ua.Contains("Edg/") ? "Edge" : ua.Contains("OPR/") ? "Opera" : ua.Contains("Firefox/") ? "Firefox"
            : ua.Contains("Chrome/") ? "Chrome" : ua.Contains("Safari/") ? "Safari" : "browser";
        string os = ua.Contains("Android") ? "Android" : ua.Contains("iPhone") || ua.Contains("iPad") ? "iOS" : ua.Contains("Windows") ? "Windows"
            : ua.Contains("Mac OS") ? "macOS" : ua.Contains("Linux") ? "Linux" : "";
        return os.Length > 0 ? $"{name} on {os}" : name;
    }

    void Event(string msg)
    {
        lock (_events) _events.Add($"{DateTime.Now:HH:mm:ss} {msg}");
    }

    // ───────────── WebRTC (UDP-style traffic) ─────────────

    readonly List<(string c, string? mid)> _earlyCandidates = new();

    /// <summary>A candidate from the page; ones that arrive before the offer are kept until it is applied.</summary>
    public void AddRemoteCandidate(string c, string? mid)
    {
        lock (_earlyCandidates)
        {
            if (Rtc is { HasRemoteDescription: true } rtc) rtc.AddRemoteCandidate(c, mid);
            else _earlyCandidates.Add((c, mid));
        }
    }

    /// <summary>Answers the page's WebRTC offer; extra candidates for the address the page used are sent separately.</summary>
    public async Task<string> AnswerAsync(string offer, IEnumerable<IPAddress> pageAddresses)
    {
        var rtc = Rtc = new WebRtcLink(_web.RtcPort, _web.Options.Bind);
        rtc.LocalCandidate += (c, mid) => _ = _send(new JsonObject { ["t"] = "cand", ["c"] = c, ["mid"] = mid });
        rtc.Packet += OnPacket;
        rtc.Closed += why => { if (_running && !_stopped) Event("WebRTC connection " + why); };
        string answer = await rtc.AnswerAsync(offer, _cts.Token);
        lock (_earlyCandidates)
        {
            foreach (var (c, mid) in _earlyCandidates) rtc.AddRemoteCandidate(c, mid);
            _earlyCandidates.Clear();
        }
        foreach (var (c, mid) in WebRtcLink.ExtraCandidates(answer, pageAddresses, _web.RtcPort))
            _ = _send(new JsonObject { ["t"] = "cand", ["c"] = c, ["mid"] = mid });
        return answer;
    }

    void OnPacket(ReadOnlySpan<byte> p)
    {
        if (p.Length < WebPacket.HeaderSize) return;
        Interlocked.Add(ref _rxTotal, p.Length);
        switch (p[0])
        {
            case WebPacket.Data:
                _upUdp.Add(BinaryPrimitives.ReadUInt32LittleEndian(p[1..]), BinaryPrimitives.ReadDoubleLittleEndian(p[5..]), p.Length, WebClock.NowMs);
                break;
            case WebPacket.EchoRequest:
                Span<byte> reply = stackalloc byte[p.Length];
                p.CopyTo(reply);
                reply[0] = WebPacket.EchoReply;
                Rtc?.TrySend(reply, maxBuffered: 0);    // straight back, even when the queue is full
                break;
        }
    }

    // ───────────── TCP bulk streams (WebSockets) ─────────────

    /// <summary>Counts the bytes the page uploads on one bulk WebSocket until the test ends.</summary>
    public async Task RunUploadStreamAsync(WebSocket ws)
    {
        var buf = new byte[64 * 1024];
        try
        {
            while (!_stopped && ws.State == WebSocketState.Open)
            {
                var r = await ws.ReceiveAsync(buf, _cts.Token);
                if (r.MessageType == WebSocketMessageType.Close) break;
                if (_running) { Interlocked.Add(ref _upTcpBytes, r.Count); Interlocked.Add(ref _rxTotal, r.Count); }
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
        await CloseQuietly(ws);
    }

    /// <summary>Sends download traffic on one bulk WebSocket at this stream's share of the TCP download rate.</summary>
    public async Task RunDownloadStreamAsync(WebSocket ws)
    {
        var pace = new Pacing(Config.Profile is TrafficProfile.Bursty ? TrafficProfile.Bursty : TrafficProfile.Constant,
            DownTcpMbps / Streams, 1400, Config.BurstOnMs, Config.BurstOffMs);
        var chunk = new byte[Speed != null ? 64 * 1024 : 16 * 1024];
        RandomNumberGenerator.Fill(chunk);       // incompressible
        double sent = 0, debtCapBytes = DownTcpMbps / Streams * 1e6 / 8 * 0.25;   // don't catch up more than 250 ms
        double offsetMs = 0;
        try
        {
            while (!_stopped && ws.State == WebSocketState.Open)
            {
                if (!_running || _sendingDone || _loadStartUs == 0 || Clock.NowUs < _loadStartUs ||
                    (Speed != null && Clock.NowUs > _loadStartUs + Speed.DownSec * 1_000_000L)) { await Task.Delay(20, _cts.Token); continue; }
                double ms = (Clock.NowUs - _loadStartUs) / 1000.0 - offsetMs;
                double due = pace.DueBytes(ms);
                if (due - sent > debtCapBytes + chunk.Length) { sent = due - debtCapBytes; }
                if (due - sent < chunk.Length) { await Task.Delay(2, _cts.Token); continue; }
                await ws.SendAsync(chunk, WebSocketMessageType.Binary, true, _cts.Token);
                sent += chunk.Length;
                Interlocked.Add(ref _downTcpSent, chunk.Length);
                Interlocked.Add(ref _txTotal, chunk.Length);
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
        await CloseQuietly(ws);
    }

    static async Task CloseQuietly(WebSocket ws)
    {
        try
        {
            if (ws.State is WebSocketState.Open or WebSocketState.CloseReceived)
                await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "test ended", new CancellationTokenSource(1000).Token);
        }
        catch { }
    }

    // ───────────── Running ─────────────

    /// <summary>The page is ready (data channel open, bulk sockets connected): start the clock.</summary>
    public void Begin()
    {
        if (_running) return;
        StartedAt = DateTime.Now;
        _t0Us = Clock.NowUs;
        _loadStartUs = _t0Us + Config.BaselineSec * 1_000_000L;
        _running = true;
        if (DownUdpMbps > 0 && Rtc != null)
            new Thread(UdpSendLoop) { IsBackground = true, Name = $"web-udp-{Id:X8}", Priority = ThreadPriority.AboveNormal }.Start();
        _ = TickLoopAsync();
        _ = LookupClientAsync();      // reverse DNS always; ipinfo.io unless --no-ip-lookup
        ServerHost.Log($"Browser test {Id:X8} from {ClientIp} started: {Describe()}");
    }

    async Task LookupClientAsync()
    {
        try { _clientInfo = await IpLookup.LookupAsync(ClientIp, !_web.Options.NoIpLookup, _cts.Token); } catch { }
    }

    void UdpSendLoop()
    {
        var rtc = Rtc!;
        var pace = new Pacing(Config.Profile, DownUdpMbps, Math.Clamp(Config.PacketSize, WebPacket.MinSize, WebPacket.MaxSize), Config.BurstOnMs, Config.BurstOffMs);
        var buf = new byte[pace.PacketSize];
        RandomNumberGenerator.Fill(buf);
        buf[0] = WebPacket.Data;
        uint seq = 0;
        double sent = 0;
        // Keep the channel's send queue short (~20 ms of data): a longer queue would delay the echo replies and
        // show up as latency of our own making. More than that queued = the path is congested.
        long maxQueue = Math.Max(16 * 1024, (long)(DownUdpMbps * 1e6 / 8 * 0.02));
        try { rtc.Opened.Wait(TimeSpan.FromSeconds(10)); } catch { }
        while (!_stopped)
        {
            long now = Clock.NowUs;
            if (now < _loadStartUs || _sendingDone) { Thread.Sleep(5); continue; }
            double due = pace.DueBytes((now - _loadStartUs) / 1000.0);
            if (due - sent > pace.PacketSize * 2000.0) sent = due - pace.PacketSize * 200.0;   // after a stall: don't flood
            while (sent + buf.Length <= due && !_stopped)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(1), seq);
                BinaryPrimitives.WriteDoubleLittleEndian(buf.AsSpan(5), WebClock.NowMs);
                if (rtc.TrySend(buf, maxQueue))
                {
                    seq++;
                    Interlocked.Add(ref _downUdpSent, buf.Length);
                    Interlocked.Add(ref _txTotal, buf.Length);
                }
                else Interlocked.Add(ref _downUdpBlocked, buf.Length);
                sent += buf.Length;
            }
            Thread.Sleep(1);
        }
    }

    // How late the page's per-second figures arrive (µs after the end of their second; recent maximum). On a
    // high-latency or bufferbloated link this is seconds, and the merge waits accordingly.
    double _lateUs;
    readonly Queue<double> _lateRecent = new();

    bool _haveLate;

    /// <summary>
    /// How long to wait for the page's figures of a second before going on without them: twice their recent
    /// lateness (2-20 s); before the first one arrived, at least 5 s.
    /// </summary>
    long MergeWaitUs => (long)Math.Clamp(2 * _lateUs + 1_000_000, _haveLate ? 2_000_000 : 5_000_000, 20_000_000);

    public void OnBrowserSecond(BrowserSecond s)
    {
        if (_running && s.Index >= 0)
        {
            double late = Clock.NowUs - (_t0Us + (s.Index + 1) * 1_000_000.0);
            lock (_lateRecent)
            {
                _lateRecent.Enqueue(Math.Max(0, late));
                if (_lateRecent.Count > 10) _lateRecent.Dequeue();
                _lateUs = _lateRecent.Max();
                _haveLate = true;
            }
        }
        if (s.Index >= 0 && s.Index < 100_000) _browser[s.Index] = s;
        if (double.IsFinite(s.RttMs)) _lastRttMs = s.RttMs;
    }

    async Task TickLoopAsync()
    {
        long prevUpTcp = 0, prevDownTcp = 0, prevDownUdp = 0, prevBlocked = 0;
        var pending = new Queue<(int i, long upTcp, UdpMeter.Interval upUdp, long downTcp, long downUdp, long blocked, long readyUs)>();
        int i = 0;
        try
        {
            while (!_stopped)
            {
                long due = _t0Us + (i + 1) * 1_000_000L;
                long wait = due - Clock.NowUs;
                if (wait > 0) await Task.Delay(TimeSpan.FromMicroseconds(wait), _cts.Token);
                long upTcp = Interlocked.Read(ref _upTcpBytes), downTcp = Interlocked.Read(ref _downTcpSent);
                long downUdp = Interlocked.Read(ref _downUdpSent), blocked = Interlocked.Read(ref _downUdpBlocked);
                pending.Enqueue((i, upTcp - prevUpTcp, _upUdp.TakeInterval(), downTcp - prevDownTcp, downUdp - prevDownUdp, blocked - prevBlocked, Clock.NowUs));
                prevUpTcp = upTcp; prevDownTcp = downTcp; prevDownUdp = downUdp; prevBlocked = blocked;
                i++;
                await MergeAsync(pending, flush: false);
                if (Config.DurationSec > 0 && i >= Config.DurationSec)
                {
                    // Stop the traffic, then wait (up to 2 s) for the page's figures of the last seconds.
                    _sendingDone = true;
                    long until = Clock.NowUs + MergeWaitUs;
                    while (pending.Count > 0 && Clock.NowUs < until && !_stopped)
                    {
                        await Task.Delay(100, _cts.Token);
                        await MergeAsync(pending, flush: false);
                    }
                    await MergeAsync(pending, flush: true);
                    _ = StopAsync("Duration reached");
                    break;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { ServerHost.Log($"Browser test {Id:X8}: {e.Message}"); _ = StopAsync("Server error: " + e.Message); }
    }

    /// <summary>Emits each second once the page's figures for it arrived (or 2 s later without them; all with <paramref name="flush"/>).</summary>
    async Task MergeAsync(Queue<(int i, long upTcp, UdpMeter.Interval upUdp, long downTcp, long downUdp, long blocked, long readyUs)> pending, bool flush)
    {
        // The page's figures arrive in order (one TCP connection): once a later second's are in, an earlier one is lost.
        bool Overtaken(int i) => _browser.Keys.Any(k => k > i);
        while (pending.Count > 0 && (flush || _browser.ContainsKey(pending.Peek().i) || Overtaken(pending.Peek().i) ||
                                     Clock.NowUs - pending.Peek().readyUs > MergeWaitUs))
        {
            var p = pending.Dequeue();
            _browser.TryRemove(p.i, out var b);
            await EmitSampleAsync(p.i, p.upTcp, p.upUdp, p.downTcp, p.downUdp, p.blocked, b);
        }
    }

    // Speed test: per-second figures of each phase (after its ramp-up for the rates).
    readonly List<double> _spDown = new(), _spUp = new(), _spRttIdle = new(), _spRttDown = new(), _spRttUp = new();

    /// <summary>"idle", "download" or "upload" for the second ending at <paramref name="sec"/> (speed test).</summary>
    string Phase(double sec) => Speed is not { } sp ? (sec <= Config.BaselineSec ? "idle" : "load")
        : sec <= Config.BaselineSec ? "idle" : sec <= Config.BaselineSec + sp.DownSec ? "download" : "upload";

    double _maxGap, _mosSum, _rttMin = double.NaN;
    int _mosCount;
    // Per-transport sums of the per-second rates after the idle baseline (for the per-class summary).
    double _sumUpUdp, _sumDownUdp, _sumUpTcp, _sumDownTcp;
    int _loadedSeconds;

    async Task EmitSampleAsync(int i, long upTcp, UdpMeter.Interval upUdp, long downTcp, long downUdp, long blocked, BrowserSecond? b)
    {
        double sec = i + 1;
        bool idle = sec <= Config.BaselineSec;
        bool baseline = sec <= Config.BaselineSec + WarmupSec;     // left out of the figures: idle baseline, then (UDP, high RTT) the warm-up
        static double Mb(long bytes) => bytes * 8 / 1e6;
        static double Pct(long lost, long exp) => exp > 0 ? 100.0 * lost / exp : double.NaN;
        double upLoss = UpUdpMbps > 0 && upUdp.Expected > 0 ? Pct(upUdp.Lost, upUdp.Expected) : double.NaN;
        double downLoss = DownUdpMbps > 0 && b is { DownExpected: > 0 } ? Pct(b.DownLost, b.DownExpected) : double.NaN;
        if (UpUdpMbps > 0 && !baseline) { _upLostTotal += upUdp.Lost; _upExpTotal += upUdp.Expected; }
        if (DownUdpMbps > 0 && b != null && !baseline) { _downLostTotal += b.DownLost; _downExpTotal += b.DownExpected; }
        double rtt = b?.RttMs ?? double.NaN;
        _bloat.Add(sec, Config.BaselineSec, rtt);
        double upJit = UpUdpMbps > 0 ? upUdp.JitterMs : double.NaN, downJit = DownUdpMbps > 0 ? b?.DownJitterMs ?? double.NaN : double.NaN;
        double upGap = UpUdpMbps > 0 && !baseline ? upUdp.MaxGapMs : double.NaN;
        double downGap = DownUdpMbps > 0 && !baseline ? b?.DownMaxGapMs ?? double.NaN : double.NaN;
        double mos = UsesUdp && double.IsFinite(rtt)
            ? StatSample.EstimateMos(rtt, Math.Max(Z(upJit), Z(downJit)), Math.Max(Z(upLoss), Z(downLoss))) : double.NaN;
        bool loaded = Config.UpMbps + Config.DownMbps > 0;
        var s = new StatSample
        {
            Time = DateTime.Now,
            ElapsedSec = sec,
            IntervalSec = 1,
            TxMbps = b != null ? Mb(b.TxUpBytes) : double.NaN,
            UpMbps = Mb(upTcp + upUdp.Bytes),
            UpLossPct = upLoss,
            UpJitterMs = upJit,
            UpMaxGapMs = upGap,
            UpLostTotal = upUdp.TotalLost,
            UpOutOfOrderTotal = upUdp.OutOfOrder,
            UpLossTotalPct = Pct(_upLostTotal, _upExpTotal),
            UpReportStale = false,
            ServerTxMbps = Mb(downTcp + downUdp),
            DownMbps = b != null ? Mb(b.RxDownTcp + b.RxDownUdp) : double.NaN,
            DownLossPct = downLoss,
            DownJitterMs = downJit,
            DownMaxGapMs = downGap,
            DownLostTotal = _downLostTotal,
            DownLossTotalPct = Pct(_downLostTotal, _downExpTotal),
            RttMs = rtt,
            RttMinMs = b?.RttMinMs ?? double.NaN,
            RttMaxMs = b?.RttMaxMs ?? double.NaN,
            Mixed = Config.Protocol == TransportKind.Mixed,
            UpMbpsUdp = Mb(upUdp.Bytes), UpMbpsTcp = Mb(upTcp),
            DownMbpsUdp = b != null ? Mb(b.RxDownUdp) : double.NaN, DownMbpsTcp = b != null ? Mb(b.RxDownTcp) : double.NaN,
            BaselinePhase = idle,
            IdleRttMs = _bloat.IdleMs,
            LoadedRttMs = loaded ? _bloat.LoadedMs : double.NaN,
            BloatGrade = loaded ? _bloat.CurrentGrade : null,
            Mos = mos,
        };
        string phase = Phase(sec);
        if (Speed is { } plan)
        {
            double inPhase = phase == "download" ? sec - Config.BaselineSec : sec - Config.BaselineSec - plan.DownSec;
            lock (_spDown)
            {
                if (phase == "idle" && double.IsFinite(rtt)) _spRttIdle.Add(rtt);
                if (phase == "download")
                {
                    if (inPhase > plan.RampSec && double.IsFinite(s.DownMbps)) _spDown.Add(s.DownMbps);
                    if (inPhase > 1 && double.IsFinite(rtt)) _spRttDown.Add(rtt);
                }
                if (phase == "upload")
                {
                    if (inPhase > plan.RampSec && double.IsFinite(s.UpMbps)) _spUp.Add(s.UpMbps);
                    if (inPhase > 1 && double.IsFinite(rtt)) _spRttUp.Add(rtt);
                }
            }
        }
        lock (_series)
        {
            _series.Add(s);
            if (!baseline) _maxGap = Math.Max(_maxGap, Math.Max(Z(upGap), Z(downGap)));
            if (double.IsFinite(mos) && !baseline) { _mosSum += mos; _mosCount++; }
            if (double.IsFinite(s.RttMinMs)) _rttMin = double.IsFinite(_rttMin) ? Math.Min(_rttMin, s.RttMinMs) : s.RttMinMs;
            if (!baseline && b != null)
            {
                _sumUpUdp += s.UpMbpsUdp; _sumUpTcp += s.UpMbpsTcp; _sumDownUdp += s.DownMbpsUdp; _sumDownTcp += s.DownMbpsTcp;
                _loadedSeconds++;
            }
        }
        if (b == null && sec > 2) Event($"No figures from the browser for second {sec:0} (tab in the background, or the connection stalled for over {MergeWaitUs / 1e6:0} s)");
        if (blocked > 0 && !baseline && DownUdpMbps > 0)
            Event($"{sec:0} s: {Mb(blocked):0.##} Mb of download not sent - the WebRTC channel's congestion control held it back");
        var json = SampleJson(s, Mb(blocked));
        json["phase"] = phase;
        await _send(json);
    }

    static double Z(double v) => double.IsFinite(v) ? v : 0;

    static JsonNode? N(double v) => double.IsFinite(v) ? JsonValue.Create(Math.Round(v, 3)) : null;

    static JsonObject SampleJson(StatSample s, double heldBackMb) => new()
    {
        ["t"] = "sample",
        ["sec"] = s.ElapsedSec,
        ["baseline"] = s.BaselinePhase,
        ["tx"] = N(s.TxMbps), ["up"] = N(s.UpMbps), ["down"] = N(s.DownMbps), ["serverTx"] = N(s.ServerTxMbps),
        ["upUdp"] = N(s.UpMbpsUdp), ["upTcp"] = N(s.UpMbpsTcp), ["downUdp"] = N(s.DownMbpsUdp), ["downTcp"] = N(s.DownMbpsTcp),
        ["lossUp"] = N(s.UpLossPct), ["lossDown"] = N(s.DownLossPct), ["lossUpTotal"] = N(s.UpLossTotalPct), ["lossDownTotal"] = N(s.DownLossTotalPct),
        ["jitUp"] = N(s.UpJitterMs), ["jitDown"] = N(s.DownJitterMs), ["gapUp"] = N(s.UpMaxGapMs), ["gapDown"] = N(s.DownMaxGapMs),
        ["rtt"] = N(s.RttMs), ["rttMin"] = N(s.RttMinMs), ["rttMax"] = N(s.RttMaxMs),
        ["mos"] = N(s.Mos), ["bloat"] = s.BloatGrade, ["bloatMs"] = N(s.BloatIncreaseMs), ["idleRtt"] = N(s.IdleRttMs),
        ["heldBack"] = heldBackMb > 0 ? Math.Round(heldBackMb, 3) : null,
    };

    // ───────────── Stopping ─────────────

    public async Task StopAsync(string reason)
    {
        if (Interlocked.Exchange(ref _stopOnce, 1) == 1) return;
        _stopped = true;
        _cts.Cancel();
        Event("Test ended: " + reason);
        TestRecord? rec = null;
        SpeedResult? speed = null;
        try
        {
            if (_running && _series.T.Count > 0)
            {
                rec = BuildRecord(reason);
                SavedPath = _web.Results.Save(rec);
                speed = rec.Speed;
            }
        }
        catch (Exception e) { ServerHost.Log($"Browser test {Id:X8}: could not save the result: {e.Message}"); }
        ServerHost.Log($"Browser test {Id:X8} from {ClientIp} ended: {reason}" + (rec != null ? " - " + rec.Summary : ""));
        try
        {
            await _send(new JsonObject
            {
                ["t"] = "done", ["reason"] = reason,
                ["file"] = SavedPath != null ? Path.GetFileName(SavedPath) : null,
                ["summary"] = rec?.Summary,
                ["speed"] = speed == null ? null : new JsonObject
                {
                    ["down"] = N(speed.DownMbps), ["up"] = N(speed.UpMbps), ["downPeak"] = N(speed.DownPeakMbps), ["upPeak"] = N(speed.UpPeakMbps),
                    ["idleRtt"] = N(speed.IdleRttMs), ["downRtt"] = N(speed.DownLoadedRttMs), ["upRtt"] = N(speed.UpLoadedRttMs),
                    ["idleJitter"] = N(speed.IdleJitterMs), ["bloat"] = rec?.BloatGrade,
                },
            });
        }
        catch { }
        Dispose();
    }

    TestRecord BuildRecord(string reason)
    {
        List<double> After(List<double> v)
        {
            lock (_series) return v.Where((x, i) => i < _series.T.Count && _series.T[i] > Config.BaselineSec + WarmupSec && double.IsFinite(x)).ToList();
        }
        static double Avg(List<double> v) => v.Count > 0 ? v.Average() : double.NaN;
        SampleSeries series;
        lock (_series) series = _series.Clone();
        var rtts = series.Rtt.Where(double.IsFinite).ToList();
        double upAvg = Avg(After(series.Up)), downAvg = Avg(After(series.Down));
        bool loaded = Config.UpMbps + Config.DownMbps > 0;
        double PerSec(double sum) => _loadedSeconds > 0 ? sum / _loadedSeconds : double.NaN;
        var classes = new List<ClassSummary>();
        double udpShare = Config.Protocol == TransportKind.Mixed ? Config.UdpSharePct : 100;
        if (UsesUdp)
            classes.Add(new ClassSummary("UDP (WebRTC)", TransportKind.Udp, 0, udpShare,
                UpUdpMbps > 0 ? PerSec(_sumUpUdp) : double.NaN, UpUdpMbps > 0 ? Pct(_upLostTotal, _upExpTotal) : double.NaN,
                DownUdpMbps > 0 ? PerSec(_sumDownUdp) : double.NaN, DownUdpMbps > 0 ? Pct(_downLostTotal, _downExpTotal) : double.NaN,
                Avg(After(series.JitDown)), Avg(rtts)));
        if (UsesTcp)
            classes.Add(new ClassSummary("TCP (WebSocket)", TransportKind.Tcp, 0, 100 - (UsesUdp ? udpShare : 0),
                UpTcpMbps > 0 ? PerSec(_sumUpTcp) : double.NaN, double.NaN,
                DownTcpMbps > 0 ? PerSec(_sumDownTcp) : double.NaN, double.NaN, double.NaN, Avg(rtts)));
        var cfg = Config.Clone();
        cfg.Profile = Pacing.Supported(cfg.Profile);
        var info = _clientInfo;
        var rec = new TestRecord
        {
            Id = $"{StartedAt:yyyyMMddHHmmss}-W{Id:X8}",
            StartedAt = StartedAt,
            EndedAt = DateTime.Now,
            EndReason = reason,
            AppVersion = $"web ({BrowserName(UserAgent)})",
            ServerVersion = Updater.CurrentVersion.ToString(),
            Server = ServerName,
            Config = cfg,
            DurationSec = series.T.Count > 0 ? series.T[^1] : 0,
            UpMbpsAvg = upAvg,
            DownMbpsAvg = downAvg,
            UpLossPct = UpUdpMbps > 0 ? Pct(_upLostTotal, _upExpTotal) : double.NaN,
            DownLossPct = DownUdpMbps > 0 ? Pct(_downLostTotal, _downExpTotal) : double.NaN,
            RttAvgMs = Avg(rtts),
            RttMinMs = _rttMin,
            RttMaxMs = series.RttMax.Where(double.IsFinite).DefaultIfEmpty(double.NaN).Max(),
            JitterUpMs = Avg(After(series.JitUp)),
            JitterDownMs = Avg(After(series.JitDown)),
            MaxGapMs = UsesUdp ? _maxGap : double.NaN,
            MosAvg = _mosCount > 0 ? _mosSum / _mosCount : double.NaN,
            BloatGrade = loaded ? _bloat.CurrentGrade : null,
            IdleRttMs = _bloat.IdleMs,
            LoadedRttMs = loaded ? _bloat.LoadedMs : double.NaN,
            Classes = classes,
            Samples = series,
            Connection = new ConnectionInfo(ServerName, $"web browser ({BrowserName(UserAgent)})", ClientIp.ToString(), "–",
                info, info, true, null, Security),
            WarmupSec = WarmupSec,
            Family = ClientIp.IsIPv4MappedToIPv6 || ClientIp.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? "IPv4" : "IPv6",
        };
        lock (_events)
        {
            rec.Events = _events.ToList();
            rec.Events.Insert(0, "Browser: " + UserAgent);
            if (Rtc?.SelectedPair is { } pair) rec.Events.Insert(1, "WebRTC path: " + pair);
        }
        if (Speed is { } sp)
        {
            lock (_spDown)
            {
                static double Mean(List<double> v) => v.Count > 0 ? v.Average() : double.NaN;
                static double Max(List<double> v) => v.Count > 0 ? v.Max() : double.NaN;
                // Peaks over the whole phase (ramp included): the best second, wherever it fell.
                var downAll = series.T.Select((t, i) => (t, v: series.Down[i])).Where(x => Phase(x.t) == "download" && double.IsFinite(x.v)).Select(x => x.v).ToList();
                var upAll = series.T.Select((t, i) => (t, v: series.Up[i])).Where(x => Phase(x.t) == "upload" && double.IsFinite(x.v)).Select(x => x.v).ToList();
                rec.Speed = new SpeedResult
                {
                    DownMbps = Mean(_spDown), UpMbps = Mean(_spUp), DownPeakMbps = Max(downAll), UpPeakMbps = Max(upAll),
                    IdleRttMs = _spRttIdle.Count > 0 ? _spRttIdle.OrderBy(x => x).ElementAt(_spRttIdle.Count / 2) : double.NaN,
                    DownLoadedRttMs = Mean(_spRttDown), UpLoadedRttMs = Mean(_spRttUp),
                    IdleJitterMs = _spRttIdle.Count > 1 ? _spRttIdle.Zip(_spRttIdle.Skip(1), (a, b) => Math.Abs(b - a)).Average() : double.NaN,
                    DownSec = sp.DownSec, UpSec = sp.UpSec, Connections = Streams, RampSec = sp.RampSec,
                };
                rec.DownMbpsAvg = rec.Speed.DownMbps;
                rec.UpMbpsAvg = rec.Speed.UpMbps;
            }
            rec.Classes = rec.Classes.Select(c => c with { UpMbps = rec.UpMbpsAvg, DownMbps = rec.DownMbpsAvg }).ToList();
        }
        rec.Summary = Summary(rec);
        return rec;

        static double Pct(long lost, long exp) => exp > 0 ? 100.0 * lost / exp : double.NaN;
    }

    static string Summary(TestRecord r)
    {
        static string F(double v, string f) => double.IsFinite(v) ? v.ToString(f, System.Globalization.CultureInfo.InvariantCulture) : "–";
        var parts = new List<string> { $"up {F(r.UpMbpsAvg, "0.0")} / down {F(r.DownMbpsAvg, "0.0")} Mbps", $"RTT {F(r.RttAvgMs, "0.0")} ms" };
        if (double.IsFinite(r.UpLossPct) || double.IsFinite(r.DownLossPct)) parts.Add($"loss {F(r.UpLossPct, "0.##")} / {F(r.DownLossPct, "0.##")} %");
        if (double.IsFinite(r.MosAvg)) parts.Add($"MOS {F(r.MosAvg, "0.0")}");
        if (r.BloatGrade != null) parts.Add($"bufferbloat {r.BloatGrade}");
        if (r.Speed is { } sp) return "Speed test: " + sp.Summary + (r.BloatGrade != null ? $", bufferbloat {r.BloatGrade}" : "");
        return "Browser test: " + string.Join(", ", parts);
    }

    public SessionSnapshot Snapshot() => new(Id, ClientIp.ToString(), "web", Speed != null ? "Web speed test" : Config.Protocol == TransportKind.Tcp ? "Web TCP" : Config.Protocol == TransportKind.Mixed ? "Web mixed" : "Web UDP",
        StartedAt, IsRunning, Interlocked.Read(ref _rxTotal), Interlocked.Read(ref _txTotal), _upLostTotal, _upExpTotal, _lastRttMs,
        UsesUdp && UsesTcp ? 2 : 1, _clientInfo, null, null, Security.StartsWith("HTTPS") ? Security : null);   // 🔒 only when encrypted

    public void Dispose()
    {
        _stopped = true;
        try { _cts.Cancel(); } catch { }
        Rtc?.Dispose();
        _web.Unregister(this);
    }
}
