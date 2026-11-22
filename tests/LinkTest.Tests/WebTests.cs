using System.Buffers.Binary;
using System.Diagnostics;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using DataChannelDotnet.Data;
using DataChannelDotnet.Impl;
using LinkTest.Core;
using LinkTest.Server;
using LinkTest.Web;

namespace LinkTest.Tests;

/// <summary>A LinkTest server with browser tests enabled, on free ports, saving results to a temp folder.</summary>
public sealed class WebFixture : IAsyncLifetime
{
    readonly CancellationTokenSource _cts = new();
    Task? _run;
    public ServerHost Host { get; private set; } = null!;
    public WebServer Web { get; private set; } = null!;
    public int WebPort { get; private set; }
    public string Results { get; } = Path.Combine(Path.GetTempPath(), "linktest-web-tests-" + Environment.ProcessId);
    public const string Key = "web-secret";

    public async Task InitializeAsync()
    {
        ServerHost.ConsoleOutput = false;
        Clock.EnableHighResolutionTimer();
        // A port that looked free can still be refused (another process, Windows' reserved ranges): try a few.
        for (int attempt = 1; ; attempt++)
        {
            int port = ServerFixture.FreePort();
            int web;
            do web = ServerFixture.FreePort(); while (web == port);
            WebPort = web;
            var opts = new ServerOptions
            {
                Port = port, NoIpLookup = true, MaxSessions = 4, CertificatePath = ServerFixture.CertPath + ".web.pfx",
                Key = Key, WebPort = web, WebResultsFolder = Results,
                SnmpPort = ServerFixture.FreeUdpPort(), SnmpTrapPort = ServerFixture.FreeUdpPort(),
                WebSnmpPort = ServerFixture.FreeUdpPort(), WebSnmpTrapPort = ServerFixture.FreeUdpPort(),
            };
            Host = new ServerHost(opts) { PeriodicStatus = false };
            var cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            _run = Task.Run(() => Host.RunAsync(cts.Token));
            Web = new WebServer(Host);
            try
            {
                await Web.StartAsync(_cts.Token);
                await Task.Delay(200);
                if (_run.IsFaulted) throw new IOException("server failed: " + _run.Exception?.GetBaseException().Message);
                return;
            }
            catch (Exception) when (attempt < 5)
            {
                await Web.DisposeAsync();
                cts.Cancel();
                try { await _run; } catch { }
            }
        }
    }

    public async Task DisposeAsync()
    {
        await Web.DisposeAsync();
        _cts.Cancel();
        if (_run != null) try { await _run; } catch { }
        try { Directory.Delete(Results, true); } catch { }
        try { File.Delete(ServerFixture.CertPath + ".web.pfx"); } catch { }
    }
}

/// <summary>End-to-end browser tests: a C# stand-in for the web page talks to the real web server.</summary>
public class WebTests : IClassFixture<WebFixture>
{
    readonly WebFixture _f;
    public WebTests(WebFixture f) => _f = f;

    Uri Http(string path) => new($"http://127.0.0.1:{_f.WebPort}{path}");

    [Fact]
    public async Task Page_and_info_are_served()
    {
        using var http = new HttpClient();
        string html = await http.GetStringAsync(Http("/"));
        Assert.Contains("LinkTest", html);
        var info = JsonNode.Parse(await http.GetStringAsync(Http("/api/info")))!;
        Assert.True((bool)info["keyRequired"]!);
        Assert.True((bool)info["udp"]!, (string?)info["udpUnavailable"]);
        var help = JsonNode.Parse(await http.GetStringAsync(Http("/api/help")))!;
        Assert.NotNull(help["tile.rtt"]?["ideal"]);
        Assert.Equal(System.Net.HttpStatusCode.Forbidden, (await http.GetAsync(Http("/api/results"))).StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.NotFound, (await http.GetAsync(Http("/..%2f..%2fsecret"))).StatusCode);
    }

    [Fact]
    public async Task Snmp_test_runs_over_webrtc_on_the_browser_snmp_ports()
    {
        using (var http = new HttpClient())
        {
            var info = JsonNode.Parse(await http.GetStringAsync(Http("/api/info")))!;
            Assert.Equal(_f.Host.Options.WebSnmpPort, (int)info["snmp"]!["poll"]!);
            Assert.Equal(_f.Host.Options.WebSnmpTrapPort, (int)info["snmp"]!["trap"]!);
        }
        await using var b = await FakeBrowser.ConnectAsync(_f.WebPort, WebFixture.Key);
        await b.NextAsync("welcome");
        var done = await b.RunAsync(new JsonObject
        {
            ["protocol"] = "udp", ["upMbps"] = 0, ["downMbps"] = 0, ["durationSec"] = 7, ["baselineSec"] = 0, ["snmp"] = true,
        });
        var rec = Load((string)done["file"]!);
        var snmp = rec.Snmp!;
        Assert.Equal("WebRTC data channel", snmp.Transport);
        Assert.Equal(_f.Host.Options.WebSnmpPort, snmp.Polls!.Port);
        Assert.InRange(snmp.Polls.Answered, 3, 8);
        Assert.Equal(0, snmp.Polls.Failed);
        Assert.True(snmp.Informs!.Answered >= 1, snmp.Summary);
        Assert.Equal("SNMP test", rec.Mode);
        Assert.StartsWith("SNMP test:", rec.Summary);
        Assert.NotNull(done["snmp"]?["poll"]?["answered"]);
    }

    [Fact]
    public async Task Wrong_key_is_refused()
    {
        await using var b = await FakeBrowser.ConnectAsync(_f.WebPort, "nope");
        var reply = await b.NextAsync("error", "welcome");
        Assert.Equal("error", (string?)reply["t"]);
    }

    [Fact]
    public async Task Udp_test_over_webrtc_measures_both_directions()
    {
        await using var b = await FakeBrowser.ConnectAsync(_f.WebPort, WebFixture.Key);
        Assert.Equal("welcome", (string?)(await b.NextAsync("welcome", "error"))["t"]);
        var done = await b.RunAsync(new JsonObject
        {
            ["protocol"] = "udp", ["upMbps"] = 4, ["downMbps"] = 6, ["durationSec"] = 5, ["baselineSec"] = 1, ["packetSize"] = 1000,
        }, new JsonObject { ["lat"] = 45.4215, ["lon"] = -75.6972, ["acc"] = 20 });
        Assert.NotNull((string?)done["file"]);
        var rec = Load((string)done["file"]!);
        Assert.Equal(TransportKind.Udp, rec.Config.Protocol);
        Assert.InRange(rec.UpMbpsAvg, 3.2, 4.8);
        Assert.InRange(rec.DownMbpsAvg, 4.8, 7.2);
        Assert.InRange(rec.UpLossPct, 0, 1);
        Assert.InRange(rec.DownLossPct, 0, 1);
        Assert.True(rec.RttAvgMs is > 0 and < 50, $"RTT {rec.RttAvgMs}");
        Assert.True(double.IsFinite(rec.MosAvg));
        Assert.NotNull(rec.BloatGrade);
        Assert.True(b.Samples.Count >= 4);
        Assert.Contains(rec.Events, e => e.StartsWith("Browser: "));
        // Browser tests don't record locations, even if a page sends one.
        Assert.Empty(rec.Locations);
        Assert.Null(rec.Samples.Lat);
        Assert.Null(rec.Connection?.DeviceLocation);
        Assert.Equal(5, rec.Samples.T.Count);      // every second, including the last
        await AssertReportsAsync((string)done["file"]!);
    }

    [Fact]
    public async Task Tcp_test_over_websockets_measures_both_directions()
    {
        await using var b = await FakeBrowser.ConnectAsync(_f.WebPort, WebFixture.Key);
        await b.NextAsync("welcome", "error");
        var done = await b.RunAsync(new JsonObject
        {
            ["protocol"] = "tcp", ["upMbps"] = 20, ["downMbps"] = 30, ["durationSec"] = 5, ["baselineSec"] = 1, ["streams"] = 2,
        });
        var rec = Load((string)done["file"]!);
        Assert.Equal(TransportKind.Tcp, rec.Config.Protocol);
        Assert.InRange(rec.UpMbpsAvg, 15, 25);
        Assert.InRange(rec.DownMbpsAvg, 22, 38);
        Assert.True(double.IsNaN(rec.UpLossPct));
        Assert.True(rec.RttAvgMs > 0);
    }

    [Fact]
    public async Task Speed_test_measures_download_then_upload()
    {
        await using var b = await FakeBrowser.ConnectAsync(_f.WebPort, WebFixture.Key);
        await b.NextAsync("welcome");
        var done = await b.RunAsync(new JsonObject { ["protocol"] = "speed", ["downSec"] = 4, ["upSec"] = 4, ["baselineSec"] = 1, ["streams"] = 2 });
        var sp = done["speed"]!;
        Assert.True((double)sp["down"]! > 50, $"download {sp["down"]}");
        Assert.True((double)sp["up"]! > 50, $"upload {sp["up"]}");
        Assert.True(sp["idleRtt"] != null);
        var rec = Load((string)done["file"]!);
        Assert.Equal("Speed test", rec.Mode);
        Assert.NotNull(rec.Speed);
        Assert.Equal(rec.Speed!.DownMbps, rec.DownMbpsAvg);
        // Download only in its phase: nothing sent to the browser during the upload seconds.
        var phases = b.Samples.Select(s => ((string?)s["phase"], (double?)s["down"] ?? 0)).ToList();
        Assert.Contains(phases, p => p.Item1 == "download" && p.Item2 > 50);
        Assert.All(phases.Where(p => p.Item1 == "upload").Skip(1), p => Assert.True(p.Item2 < 5, $"download during upload: {p.Item2}"));
        await AssertReportsAsync((string)done["file"]!);
    }

    [Fact]
    public async Task Late_browser_reports_are_still_merged()
    {
        // High latency / a full queue: the page's per-second figures arrive 3 s late.
        await using var b = await FakeBrowser.ConnectAsync(_f.WebPort, WebFixture.Key);
        await b.NextAsync("welcome");
        b.SecDelayMs = 3000;
        var done = await b.RunAsync(new JsonObject { ["protocol"] = "udp", ["upMbps"] = 2, ["downMbps"] = 3, ["durationSec"] = 6, ["baselineSec"] = 1 });
        var rec = Load((string)done["file"]!);
        Assert.Equal(6, rec.Samples.T.Count);
        Assert.All(rec.Samples.Down.Skip(1), v => Assert.True(double.IsFinite(v), "a second without the browser's figures"));
        Assert.DoesNotContain(rec.Events, e => e.Contains("No figures from the browser"));
        Assert.InRange(rec.DownMbpsAvg, 2.4, 3.6);
    }

    [Fact]
    public async Task High_latency_extends_the_speed_test_and_udp_warmup()
    {
        await using var b = await FakeBrowser.ConnectAsync(_f.WebPort, WebFixture.Key);
        await b.NextAsync("welcome");
        var fast = await b.PlanAsync(new JsonObject { ["protocol"] = "speed", ["downSec"] = 10, ["upSec"] = 10, ["rttMs"] = 20 });
        Assert.Equal(10, (int)fast["downSec"]!);
        Assert.Equal(2, (int)fast["rampSec"]!);
        var sat = await b.PlanAsync(new JsonObject { ["protocol"] = "speed", ["downSec"] = 10, ["upSec"] = 10, ["rttMs"] = 800 });
        Assert.Equal(8, (int)sat["rampSec"]!);                 // TCP slow start: several round trips
        Assert.Equal(14, (int)sat["downSec"]!);                // ramp + at least 6 measured seconds
        Assert.Equal(14, (int)sat["upSec"]!);
        var udp = await b.PlanAsync(new JsonObject { ["protocol"] = "udp", ["upMbps"] = 1, ["downMbps"] = 1, ["durationSec"] = 30, ["rttMs"] = 700 });
        Assert.Equal(5, (int)udp["warmupSec"]!);
    }

    [Fact]
    public async Task Two_udp_tests_share_the_webrtc_port()
    {
        async Task<TestRecord> One()
        {
            await using var b = await FakeBrowser.ConnectAsync(_f.WebPort, WebFixture.Key);
            await b.NextAsync("welcome");
            var done = await b.RunAsync(new JsonObject { ["protocol"] = "udp", ["upMbps"] = 2, ["downMbps"] = 3, ["durationSec"] = 4, ["baselineSec"] = 1 });
            return Load((string)done["file"]!);
        }
        var r = await Task.WhenAll(One(), One());
        Assert.All(r, x => Assert.InRange(x.DownMbpsAvg, 2.4, 3.6));
        Assert.All(r, x => Assert.InRange(x.UpMbpsAvg, 1.6, 2.4));
        Assert.NotEqual(r[0].Id, r[1].Id);
    }

    TestRecord Load(string file) => TestRecord.FromJson(File.ReadAllText(Path.Combine(_f.Results, file)))!;

    async Task AssertReportsAsync(string file)
    {
        using var http = new HttpClient();
        http.DefaultRequestHeaders.Add("X-LinkTest-Key", WebFixture.Key);
        var list = JsonNode.Parse(await http.GetStringAsync(Http("/api/results")))!["results"]!.AsArray();
        Assert.Contains(list, r => (string?)r!["file"] == file);
        var pdf = await http.GetByteArrayAsync(Http($"/api/results/{file}/report.pdf"));
        Assert.Equal("%PDF", Encoding.ASCII.GetString(pdf, 0, 4));
        Assert.Contains("<html", await http.GetStringAsync(Http($"/api/results/{file}/report.html")), StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith("time,", await http.GetStringAsync(Http($"/api/results/{file}/data.csv")));
    }
}

/// <summary>Does what app.js does in a browser, so the server can be tested end to end.</summary>
sealed class FakeBrowser : IAsyncDisposable
{
    readonly ClientWebSocket _ws = new();
    readonly int _port;
    readonly System.Threading.Channels.Channel<JsonObject> _inbox = System.Threading.Channels.Channel.CreateUnbounded<JsonObject>();
    readonly CancellationTokenSource _cts = new(TimeSpan.FromSeconds(60));
    readonly List<ClientWebSocket> _bulk = new();
    RtcPeerConnection? _pc;
    readonly List<RtcCandidate> _cands = new();
    bool _remoteSet;
    void TryAdd(RtcCandidate c) { try { _pc?.AddRemoteCandidate(c); } catch { } }
    DataChannelDotnet.IRtcDataChannel? _dc;
    public List<JsonObject> Samples { get; } = new();

    // SNMP test links ("snmp-poll", "snmp-trap"): the server sends SNMP requests, the page echoes them.
    sealed class SnmpLink
    {
        public required RtcPeerConnection Pc;
        public DataChannelDotnet.IRtcDataChannel? Dc;
        public readonly List<RtcCandidate> Cands = new();
        public bool RemoteSet;
    }
    readonly Dictionary<string, SnmpLink> _snmp = new();

    async Task OpenSnmpAsync(string link)
    {
        var l = new SnmpLink { Pc = new RtcPeerConnection(new RtcPeerConfiguration()) };
        lock (_snmp) _snmp[link] = l;
        var offer = new TaskCompletionSource<string>();
        l.Pc.OnLocalDescriptionSafe += (_, d) => { if (d.Type == RtcDescriptionType.Offer) offer.TrySetResult(d.Sdp); };
        l.Pc.OnCandidateSafe += (pc, c) => _ = SendAsync(new JsonObject { ["t"] = "cand", ["link"] = link, ["c"] = c.Content, ["mid"] = c.Mid });
        var dc = l.Dc = l.Pc.CreateDataChannel(new RtcCreateDataChannelArgs { Label = link, Unordered = true, Unreliable = true, MaxRetransmits = 0, Protocol = RtcDataChannelProtocol.Binary });
        dc.OnBinaryReceivedSafe += (_, e) => { try { dc.Send(e.Data); } catch { } };
        await SendAsync(new JsonObject { ["t"] = "offer", ["link"] = link, ["sdp"] = await offer.Task.WaitAsync(TimeSpan.FromSeconds(10)) });
    }

    void OnSnmpMessage(JsonObject o)
    {
        SnmpLink? l;
        lock (_snmp) _snmp.TryGetValue((string)o["link"]!, out l);
        if (l == null) return;
        lock (l.Cands)
        {
            if ((string?)o["t"] == "answer")
            {
                l.Pc.SetRemoteDescription(new RtcDescription { Sdp = (string)o["sdp"]!, Type = RtcDescriptionType.Answer });
                l.RemoteSet = true;
                foreach (var c in l.Cands) try { l.Pc.AddRemoteCandidate(c); } catch { }
                l.Cands.Clear();
            }
            else
            {
                var c = new RtcCandidate { Content = (string)o["c"]!, Mid = (string?)o["mid"] ?? "" };
                if (l.RemoteSet) try { l.Pc.AddRemoteCandidate(c); } catch { }
                else l.Cands.Add(c);
            }
        }
    }
    /// <summary>Delays the per-second reports, like a high-latency or bufferbloated link would.</summary>
    public int SecDelayMs { get; set; }

    // Download meter (data-channel packets and bulk bytes)
    long _rxTcp, _rxUdp, _txUp;
    readonly object _lock = new();
    long _highest = -1, _intervalStart = -1, _intervalPk, _lostTot, _expTot;
    double _jit, _lastTransit = double.NaN, _lastArr = double.NaN, _maxGap;
    readonly List<double> _rtts = new();
    readonly Stopwatch _clock = Stopwatch.StartNew();
    double NowMs => _clock.Elapsed.TotalMilliseconds;

    FakeBrowser(int port) => _port = port;

    public static async Task<FakeBrowser> ConnectAsync(int port, string key)
    {
        var b = new FakeBrowser(port);
        await b._ws.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/ws/control"), b._cts.Token);
        _ = b.ReceiveLoopAsync();
        await b.SendAsync(new JsonObject { ["t"] = "hello", ["key"] = key });
        return b;
    }

    async Task ReceiveLoopAsync()
    {
        var buf = new byte[64 * 1024];
        try
        {
            while (_ws.State == WebSocketState.Open)
            {
                using var ms = new MemoryStream();
                WebSocketReceiveResult r;
                do { r = await _ws.ReceiveAsync(buf, _cts.Token); ms.Write(buf, 0, r.Count); } while (!r.EndOfMessage);
                if (r.MessageType == WebSocketMessageType.Close) break;
                if (JsonNode.Parse(Encoding.UTF8.GetString(ms.ToArray())) is JsonObject o)
                {
                    if (o["link"] != null) OnSnmpMessage(o);
                    else if ((string?)o["t"] == "cand")
                    {
                        var c = new RtcCandidate { Content = (string)o["c"]!, Mid = (string?)o["mid"] ?? "" };
                        lock (_cands) { if (_remoteSet) TryAdd(c); else _cands.Add(c); }
                    }
                    else if ((string?)o["t"] == "pong") { double sent = (double)o["id"]!; lock (_lock) _rtts.Add(NowMs - sent); }
                    else await _inbox.Writer.WriteAsync(o);
                }
            }
        }
        catch { }
        _inbox.Writer.TryComplete();
    }

    readonly SemaphoreSlim _sendLock = new(1, 1);
    async Task SendAsync(JsonObject o)
    {
        await _sendLock.WaitAsync();
        try { await _ws.SendAsync(Encoding.UTF8.GetBytes(o.ToJsonString()), WebSocketMessageType.Text, true, _cts.Token); }
        finally { _sendLock.Release(); }
    }

    public async Task<JsonObject> NextAsync(params string[] types)
    {
        while (true)
        {
            var o = await _inbox.Reader.ReadAsync(_cts.Token);
            string t = (string?)o["t"] ?? "";
            if (t == "sample") Samples.Add(o);
            if (types.Contains(t)) return o;
            if (t == "error") throw new InvalidOperationException((string?)o["msg"]);
        }
    }

    /// <summary>Runs one test with these settings and returns the server's "done" message.</summary>
    public async Task<JsonObject> RunAsync(JsonObject config, JsonObject? location = null)
    {
        await SendAsync(new JsonObject { ["t"] = "start", ["config"] = config });
        var st = await NextAsync("started");
        string token = (string)st["token"]!;
        double upUdp = (double)st["upUdp"]!, upTcp = (double)st["upTcp"]!, downTcp = (double)st["downTcp"]!;
        int streams = (int)st["streams"]!, size = (int)st["packetSize"]!, baseline = (int)st["baselineSec"]!;
        if (st["snmp"] != null) { await OpenSnmpAsync("snmp-poll"); await OpenSnmpAsync("snmp-trap"); }

        if ((bool)st["udp"]!)
        {
            _pc = new RtcPeerConnection(new RtcPeerConfiguration());
            var offer = new TaskCompletionSource<string>();
            _pc.OnLocalDescriptionSafe += (_, d) => { if (d.Type == RtcDescriptionType.Offer) offer.TrySetResult(d.Sdp); };
            _pc.OnCandidateSafe += (pc, c) => _ = SendAsync(new JsonObject { ["t"] = "cand", ["c"] = c.Content, ["mid"] = c.Mid });
            _dc = _pc.CreateDataChannel(new RtcCreateDataChannelArgs { Label = "udp", Unordered = true, Unreliable = true, MaxRetransmits = 0, Protocol = RtcDataChannelProtocol.Binary });
            var open = new TaskCompletionSource();
            _dc.OnOpen += _ => open.TrySetResult();
            _dc.OnBinaryReceivedSafe += (_, e) => OnPacket(e.Data);
            await SendAsync(new JsonObject { ["t"] = "offer", ["sdp"] = await offer.Task.WaitAsync(TimeSpan.FromSeconds(10)) });
            var answer = await NextAsync("answer");
            _pc.SetRemoteDescription(new RtcDescription { Sdp = (string)answer["sdp"]!, Type = RtcDescriptionType.Answer });
            lock (_cands) { _remoteSet = true; foreach (var c in _cands) TryAdd(c); _cands.Clear(); }
            await open.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        int n = upTcp + downTcp > 0 ? streams : 0;
        for (int i = 0; i < n; i++)
            foreach (var dir in new[] { "up", "down" })
            {
                var ws = new ClientWebSocket();
                await ws.ConnectAsync(new Uri($"ws://127.0.0.1:{_port}/ws/bulk?token={token}&dir={dir}"), _cts.Token);
                _bulk.Add(ws);
                if (dir == "down") _ = BulkReceiveAsync(ws);
            }

        await SendAsync(new JsonObject { ["t"] = "go" });
        await NextAsync("go");
        double t0 = NowMs;
        if (location != null) { location["t"] = "loc"; await SendAsync(location); }
        var stop = new CancellationTokenSource();
        var tasks = new List<Task> { SecondsLoopAsync(t0, stop.Token), EchoLoopAsync(stop.Token) };
        if (upUdp > 0) tasks.Add(Task.Run(() => UdpSendLoop(t0 + baseline * 1000, upUdp, size, stop.Token)));
        foreach (var ws in _bulk.Where((_, i) => i % 2 == 0))
            tasks.Add(BulkSendAsync(ws, t0 + baseline * 1000, upTcp / streams, stop.Token));
        var done = await NextAsync("done");
        stop.Cancel();
        try { await Task.WhenAll(tasks); } catch { }
        return done;
    }

    void OnPacket(ReadOnlySpan<byte> p)
    {
        if (p.Length < 13) return;
        double now = NowMs;
        if (p[0] == 3) { lock (_lock) _rtts.Add(now - BinaryPrimitives.ReadDoubleLittleEndian(p[5..])); return; }
        if (p[0] != 1) return;
        uint seq = BinaryPrimitives.ReadUInt32LittleEndian(p[1..]);
        double transit = now - BinaryPrimitives.ReadDoubleLittleEndian(p[5..]);
        lock (_lock)
        {
            _rxUdp += p.Length; _intervalPk++;
            if (seq > _highest) _highest = seq;
            if (double.IsFinite(_lastTransit)) _jit += (Math.Abs(transit - _lastTransit) - _jit) / 16;
            _lastTransit = transit;
            if (double.IsFinite(_lastArr)) _maxGap = Math.Max(_maxGap, now - _lastArr);
            _lastArr = now;
        }
    }

    async Task SecondsLoopAsync(double t0, CancellationToken ct)
    {
        long prevTcp = 0, prevUdp = 0, prevTx = 0;
        for (int i = 0; !ct.IsCancellationRequested; i++)
        {
            double wait = t0 + (i + 1) * 1000 - NowMs;
            if (wait > 0) try { await Task.Delay(TimeSpan.FromMilliseconds(wait), ct); } catch { return; }
            JsonObject o;
            lock (_lock)
            {
                long exp = Math.Max(0, _highest - _intervalStart), lost = Math.Max(0, exp - _intervalPk);
                _lostTot += lost; _expTot += exp;
                var r = _rtts.ToList(); _rtts.Clear();
                long tcp = Interlocked.Read(ref _rxTcp), tx = Interlocked.Read(ref _txUp);
                o = new JsonObject
                {
                    ["t"] = "sec", ["i"] = i, ["txUp"] = tx - prevTx, ["rxTcp"] = tcp - prevTcp, ["rxUdp"] = _rxUdp - prevUdp,
                    ["dLost"] = lost, ["dExp"] = exp, ["dJit"] = _jit, ["dGap"] = _maxGap, ["dLostTot"] = _lostTot, ["dExpTot"] = _expTot,
                    ["rtt"] = r.Count > 0 ? r.Average() : null, ["rttMin"] = r.Count > 0 ? r.Min() : null, ["rttMax"] = r.Count > 0 ? r.Max() : null, ["rttN"] = r.Count,
                };
                prevTcp = tcp; prevUdp = _rxUdp; prevTx = tx;
                _intervalStart = _highest; _intervalPk = 0; _maxGap = 0;
            }
            if (SecDelayMs > 0) _ = Task.Delay(SecDelayMs, CancellationToken.None).ContinueWith(_ => SendAsync(o)).Unwrap();
            else try { await SendAsync(o); } catch { return; }
        }
    }

    /// <summary>Starts a test and returns the server's plan ("started"), then stops it.</summary>
    public async Task<JsonObject> PlanAsync(JsonObject config)
    {
        await SendAsync(new JsonObject { ["t"] = "start", ["config"] = config });
        var st = await NextAsync("started");
        await SendAsync(new JsonObject { ["t"] = "stop" });
        await NextAsync("done");
        return st;
    }

    async Task EchoLoopAsync(CancellationToken ct)
    {
        var buf = new byte[13];
        uint id = 0;
        while (!ct.IsCancellationRequested)
        {
            if (_dc != null)
            {
                buf[0] = 2;
                BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(1), id++);
                BinaryPrimitives.WriteDoubleLittleEndian(buf.AsSpan(5), NowMs);
                try { _dc.Send(buf); } catch { }
            }
            else
            {
                // TCP-only test: the RTT comes from WebSocket pings (the pong is handled in the receive loop).
                try { await SendAsync(new JsonObject { ["t"] = "ping", ["id"] = NowMs }); } catch { return; }
            }
            try { await Task.Delay(100, ct); } catch { return; }
        }
    }

    void UdpSendLoop(double startMs, double mbps, int size, CancellationToken ct)
    {
        var buf = new byte[size];
        buf[0] = 1;
        uint seq = 0;
        double bytesPerMs = mbps * 1e6 / 8 / 1000, sent = 0;
        while (!ct.IsCancellationRequested)
        {
            double now = NowMs;
            if (now < startMs) { Thread.Sleep(5); continue; }
            double due = (now - startMs) * bytesPerMs;
            while (sent + size <= due)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(1), seq++);
                BinaryPrimitives.WriteDoubleLittleEndian(buf.AsSpan(5), NowMs);
                try { _dc!.Send(buf); } catch { }
                sent += size;
                Interlocked.Add(ref _txUp, size);
            }
            Thread.Sleep(1);
        }
    }

    async Task BulkSendAsync(ClientWebSocket ws, double startMs, double mbps, CancellationToken ct)
    {
        var chunk = new byte[16 * 1024];
        double bytesPerMs = mbps * 1e6 / 8 / 1000, sent = 0;
        try
        {
            while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
            {
                double now = NowMs;
                if (now < startMs || (now - startMs) * bytesPerMs - sent < chunk.Length) { await Task.Delay(2, ct); continue; }
                await ws.SendAsync(chunk, WebSocketMessageType.Binary, true, ct);
                sent += chunk.Length;
                Interlocked.Add(ref _txUp, chunk.Length);
            }
        }
        catch { }
    }

    async Task BulkReceiveAsync(ClientWebSocket ws)
    {
        var buf = new byte[64 * 1024];
        try
        {
            while (ws.State == WebSocketState.Open)
            {
                var r = await ws.ReceiveAsync(buf, _cts.Token);
                if (r.MessageType == WebSocketMessageType.Close) break;
                Interlocked.Add(ref _rxTcp, r.Count);
            }
        }
        catch { }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var w in _bulk) w.Dispose();
        try { await _ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None); } catch { }
        _ws.Dispose();
        _dc?.Dispose();
        _pc?.Dispose();
        _cts.Dispose();
    }
}
