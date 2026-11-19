using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LinkTest.Core;
using LinkTest.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Logging;

namespace LinkTest.Web;

/// <summary>
/// Browser tests: serves the web page and runs tests from it. TCP traffic uses WebSockets; UDP-style traffic uses
/// a WebRTC data channel (unreliable, unordered) on the web port's UDP side. Results are saved like client
/// results and can be downloaded from the page as HTML / PDF reports or CSV.
/// </summary>
public sealed class WebServer : IAsyncDisposable
{
    readonly ServerHost _host;
    readonly ConcurrentDictionary<string, WebTestSession> _sessions = new();
    WebApplication? _app;

    public WebServer(ServerHost host)
    {
        _host = host;
        Results = new ResultStore(Options.WebResultsFolder ?? ServerOptions.DefaultWebResultsFolder);
        host.AddSessionSource(() => _sessions.Values.Select(s => s.Snapshot()).ToList());
    }

    public ServerOptions Options => _host.Options;
    public ResultStore Results { get; }
    /// <summary>UDP port of the WebRTC data channels (the web port's number).</summary>
    public int RtcPort => Options.WebPort;
    /// <summary>Why UDP-style browser tests are unavailable (null = available).</summary>
    public string? RtcUnavailable { get; private set; }
    /// <summary>Addresses to open in a browser.</summary>
    public IReadOnlyList<string> Urls { get; private set; } = Array.Empty<string>();

    public async Task StartAsync(CancellationToken ct)
    {
        RtcUnavailable = WebRtcLink.CheckAvailable();
        X509Certificate2? cert = null;
        if (Options.WebHttpsPort > 0)
            cert = Tls.LoadOrCreateServerCertificate(Options.CertificatePath ?? Tls.DefaultServerCertPath);

        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = Array.Empty<string>() });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k =>
        {
            k.AddServerHeader = false;
            void Listen(int port, Action<ListenOptions>? configure)
            {
                if (Options.Bind is { } b) k.Listen(b, port, l => configure?.Invoke(l));
                else k.ListenAnyIP(port, l => configure?.Invoke(l));
            }
            Listen(Options.WebPort, null);
            if (cert != null) Listen(Options.WebHttpsPort, l => l.UseHttps(cert));
        });
        var app = builder.Build();
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(15) });
        app.Use(async (ctx, next) =>
        {
            var h = ctx.Response.Headers;
            h["X-Content-Type-Options"] = "nosniff";
            h["Referrer-Policy"] = "no-referrer";
            h["Content-Security-Policy"] = "default-src 'self'; img-src 'self' data:; style-src 'self' 'unsafe-inline'; connect-src 'self'; frame-ancestors 'none'";
            await next();
        });

        app.MapGet("/", ctx => ServeFile(ctx, "index.html"));
        app.MapGet("/{file}", (HttpContext ctx, string file) => ServeFile(ctx, file));
        app.MapGet("/api/info", InfoAsync);
        app.MapGet("/api/help", HelpAsync);
        app.MapGet("/api/results", ResultsAsync);
        app.MapGet("/api/results/{file}/{kind}", ResultFileAsync);
        app.Map("/ws/control", ControlAsync);
        app.Map("/ws/bulk", BulkAsync);

        try { await app.StartAsync(ct); }
        catch (IOException e) when (e.InnerException is Microsoft.AspNetCore.Connections.AddressInUseException || e.Message.Contains("address already in use", StringComparison.OrdinalIgnoreCase))
        {
            await app.DisposeAsync();
            throw new IOException($"Web port {Options.WebPort}{(cert != null ? $" or {Options.WebHttpsPort}" : "")} is already in use.", e);
        }
        _app = app;

        // LocalAddresses gives "address (interface)"; IPv4 first, they're easier to type.
        var hosts = ServerHost.LocalAddresses().Select(a => a.Split(' ')[0])
            .OrderBy(a => a.Contains(':') ? 1 : 0).Select(a => a.Contains(':') ? $"[{a}]" : a).ToList();
        if (hosts.Count == 0) hosts.Add("localhost");
        var urls = hosts.Select(h => $"http://{h}:{Options.WebPort}/").ToList();
        if (cert != null) urls.AddRange(hosts.Select(h => $"https://{h}:{Options.WebHttpsPort}/"));
        Urls = urls;
        ServerHost.Log($"Browser tests on web port {Options.WebPort} (HTTP)" + (cert != null ? $" and {Options.WebHttpsPort} (HTTPS)" : "") +
                       $" (WebRTC on UDP {RtcPort})" + (RtcUnavailable != null ? $" - UDP tests unavailable: {RtcUnavailable}" : ""));
        foreach (var u in urls.Where(u => !u.Contains('[')).Take(3).Concat(urls.Where(u => u.Contains('[')).Take(1))) ServerHost.Log("  open " + u);
        ServerHost.Log($"  browser results saved in {Results.Folder}");
    }

    internal void Unregister(WebTestSession s) => _sessions.TryRemove(new KeyValuePair<string, WebTestSession>(s.Token, s));

    // ───────────── Static page ─────────────

    static readonly Dictionary<string, string> ContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".html"] = "text/html; charset=utf-8", [".js"] = "text/javascript; charset=utf-8", [".css"] = "text/css; charset=utf-8",
        [".svg"] = "image/svg+xml", [".ico"] = "image/x-icon", [".png"] = "image/png", [".webmanifest"] = "application/manifest+json",
    };

    static async Task ServeFile(HttpContext ctx, string file)
    {
        if (file.Contains('/') || file.Contains('\\') || file.Contains("..") ||
            typeof(WebServer).Assembly.GetManifestResourceStream("wwwroot/" + file) is not { } s)
        {
            ctx.Response.StatusCode = 404;
            return;
        }
        await using (s)
        {
            ctx.Response.ContentType = ContentTypes.GetValueOrDefault(Path.GetExtension(file), "application/octet-stream");
            ctx.Response.Headers.CacheControl = "no-cache";
            await s.CopyToAsync(ctx.Response.Body);
        }
    }

    // ───────────── API ─────────────

    bool KeyOk(string? presented) =>
        Options.Key == null || (presented != null &&
            CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(presented), Encoding.UTF8.GetBytes(Options.Key)));

    bool KeyOk(HttpContext ctx) => KeyOk(ctx.Request.Headers["X-LinkTest-Key"].FirstOrDefault() ?? ctx.Request.Query["key"].FirstOrDefault());

    static Task WriteJson(HttpContext ctx, JsonNode node, int status = 200)
    {
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json; charset=utf-8";
        ctx.Response.Headers.CacheControl = "no-store";
        return ctx.Response.WriteAsync(node.ToJsonString());
    }

    Task InfoAsync(HttpContext ctx) => WriteJson(ctx, new JsonObject
    {
        ["version"] = Updater.CurrentVersion.ToString(),
        ["keyRequired"] = Options.Key != null,
        ["maxMbps"] = Options.MaxMbps,
        ["udp"] = RtcUnavailable == null,
        ["udpUnavailable"] = RtcUnavailable,
        ["maxUdpMbps"] = WebTestSession.MaxUdpMbps,
        ["https"] = ctx.Request.IsHttps,
        ["httpsPort"] = Options.WebHttpsPort > 0 ? Options.WebHttpsPort : null,
        ["httpPort"] = Options.WebPort,
        ["client"] = Unmap(ctx.Connection.RemoteIpAddress)?.ToString(),
    });

    static JsonObject? _help;
    static Task HelpAsync(HttpContext ctx)
    {
        _help ??= new JsonObject(StatHelp.All.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)new JsonObject
        {
            ["title"] = kv.Value.Title, ["what"] = kv.Value.What, ["ideal"] = kv.Value.Ideal,
            ["ranges"] = new JsonArray(kv.Value.Ranges.Select(r => (JsonNode?)new JsonObject
            {
                ["level"] = r.level.ToString().ToLowerInvariant(), ["text"] = r.text,
            }).ToArray()),
        })));
        ctx.Response.Headers.CacheControl = "max-age=3600";
        return WriteJson(ctx, _help.DeepClone());
    }

    Task ResultsAsync(HttpContext ctx)
    {
        if (!KeyOk(ctx)) return WriteJson(ctx, new JsonObject { ["error"] = "Wrong or missing key." }, 403);
        static JsonNode? N(double v) => double.IsFinite(v) ? JsonValue.Create(Math.Round(v, 3)) : null;
        var list = new JsonArray();
        foreach (var r in Results.List().Take(200))
        {
            var s = r.Summary;
            list.Add(new JsonObject
            {
                ["file"] = Path.GetFileName(r.Path),
                ["started"] = s.StartedAt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                ["duration"] = Math.Round(s.DurationSec),
                ["mode"] = s.Mode,
                ["client"] = s.Connection?.SeenByServer,
                ["browser"] = s.AppVersion,
                ["up"] = N(s.UpMbpsAvg), ["down"] = N(s.DownMbpsAvg),
                ["lossUp"] = N(s.UpLossPct), ["lossDown"] = N(s.DownLossPct),
                ["rtt"] = N(s.RttAvgMs), ["mos"] = N(s.MosAvg), ["bloat"] = s.BloatGrade,
                ["ended"] = s.EndReason,
            });
        }
        return WriteJson(ctx, new JsonObject { ["folder"] = Results.Folder, ["results"] = list });
    }

    async Task ResultFileAsync(HttpContext ctx, string file, string kind)
    {
        if (!KeyOk(ctx)) { ctx.Response.StatusCode = 403; return; }
        if (!file.StartsWith("LinkTest_", StringComparison.Ordinal) || !file.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ||
            file.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || file.Contains(".."))
        {
            ctx.Response.StatusCode = 400;
            return;
        }
        string path = Path.Combine(Results.Folder, file);
        if (!File.Exists(path)) { ctx.Response.StatusCode = 404; return; }
        TestRecord rec;
        try { rec = TestRecord.FromJson(await File.ReadAllTextAsync(path)) ?? throw new InvalidDataException(); }
        catch { ctx.Response.StatusCode = 500; return; }

        ctx.Response.Headers.CacheControl = "no-store";
        void Attach(string name) => ctx.Response.Headers.ContentDisposition = $"attachment; filename=\"{name}\"";
        switch (kind)
        {
            case "report.html":
                // The report has inline styles and scripts of its own: relax the page policy for it.
                ctx.Response.Headers.ContentSecurityPolicy = "default-src 'none'; img-src data:; style-src 'unsafe-inline'; script-src 'unsafe-inline'";
                ctx.Response.ContentType = "text/html; charset=utf-8";
                await ctx.Response.WriteAsync(ReportBuilder.Build(rec));
                break;
            case "report.pdf":
                Attach(rec.SuggestedFileName("pdf"));
                ctx.Response.ContentType = "application/pdf";
                await ctx.Response.Body.WriteAsync(PdfReportBuilder.Build(rec));
                break;
            case "data.csv":
                Attach(rec.SuggestedFileName("csv"));
                ctx.Response.ContentType = "text/csv; charset=utf-8";
                await ctx.Response.WriteAsync(rec.ToCsv());
                break;
            case "record.json":
                Attach(file);
                ctx.Response.ContentType = "application/json; charset=utf-8";
                await ctx.Response.SendFileAsync(path);
                break;
            default:
                ctx.Response.StatusCode = 404;
                break;
        }
    }

    // ───────────── Test connections ─────────────

    static IPAddress? Unmap(IPAddress? a) => a is { IsIPv4MappedToIPv6: true } ? a.MapToIPv4() : a;

    /// <summary>Addresses the page used to reach the server (Host header), as extra WebRTC candidates.</summary>
    static List<IPAddress> PageAddresses(HttpContext ctx)
    {
        var list = new List<IPAddress>();
        string host = ctx.Request.Host.Host.Trim('[', ']');
        if (IPAddress.TryParse(host, out var ip)) list.Add(Unmap(ip)!);
        else if (host.Length > 0 && !host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
        {
            try { list.AddRange(Dns.GetHostAddresses(host).Select(a => Unmap(a)!)); } catch { }
        }
        if (Unmap(ctx.Connection.LocalIpAddress) is { } local) list.Add(local);
        return list.Distinct().ToList();
    }

    async Task ControlAsync(HttpContext ctx)
    {
        if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 400; return; }
        using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
        var client = Unmap(ctx.Connection.RemoteIpAddress) ?? IPAddress.None;
        string serverName = ctx.Request.Host.ToString();
        string security = ctx.Request.IsHttps ? "HTTPS (TLS, self-signed server certificate)" : "HTTP (not encrypted)";
        string ua = ctx.Request.Headers.UserAgent.ToString();
        if (ua.Length > 300) ua = ua[..300];
        var pageAddresses = PageAddresses(ctx);
        var sendLock = new SemaphoreSlim(1, 1);
        var aborted = ctx.RequestAborted;

        async Task Send(JsonObject o)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(o.ToJsonString());
            await sendLock.WaitAsync();
            try { if (ws.State == WebSocketState.Open) await ws.SendAsync(bytes, WebSocketMessageType.Text, true, aborted); }
            catch { }
            finally { sendLock.Release(); }
        }
        Task Error(string msg) => Send(new JsonObject { ["t"] = "error", ["msg"] = msg });

        bool authed = Options.Key == null;
        WebTestSession? session = null;
        try
        {
            while (ws.State == WebSocketState.Open)
            {
                string? text = await ReceiveTextAsync(ws, aborted);
                if (text == null) break;
                JsonObject? m;
                try { m = JsonNode.Parse(text) as JsonObject; } catch { m = null; }
                if (m == null) continue;
                string t = (string?)m["t"] ?? "";
                switch (t)
                {
                    case "hello":
                        authed = KeyOk((string?)m["key"]);
                        if (!authed) { await Error("Wrong or missing key. Enter the server key and try again."); break; }
                        await Send(new JsonObject { ["t"] = "welcome", ["version"] = Updater.CurrentVersion.ToString(), ["maxMbps"] = Options.MaxMbps, ["udp"] = RtcUnavailable == null });
                        break;
                    case "ping":
                        await Send(new JsonObject { ["t"] = "pong", ["id"] = m["id"]?.DeepClone() });
                        break;
                    case "start":
                        if (!authed) { await Error("Wrong or missing key."); break; }
                        if (session is { Stopped: false }) { await Error("A test is already running."); break; }
                        session = await StartSessionAsync(m, client, serverName, security, ua, Send, Error);
                        break;
                    case "offer" when session != null:
                        try
                        {
                            string answer = await session.AnswerAsync((string?)m["sdp"] ?? "", pageAddresses);
                            await Send(new JsonObject { ["t"] = "answer", ["sdp"] = answer });
                        }
                        catch (Exception e) { await Error("WebRTC setup failed: " + e.Message); await session.StopAsync("WebRTC setup failed"); }
                        break;
                    case "cand" when session != null:
                        session.AddRemoteCandidate((string?)m["c"] ?? "", (string?)m["mid"]);
                        break;
                    case "go" when session != null:
                        session.Begin();
                        await Send(new JsonObject { ["t"] = "go" });
                        break;
                    case "sec" when session != null:
                        session.OnBrowserSecond(ParseSecond(m));
                        break;
                    case "loc" when session != null:
                        session.AddLocation(new GeoFix(DateTime.Now, D(m["lat"]), D(m["lon"]), D(m["acc"]), D(m["alt"]), D(m["speed"]), "browser"));
                        break;
                    case "stop" when session != null:
                        await session.StopAsync("Stopped by user");
                        break;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
        finally
        {
            if (session != null) await session.StopAsync("Browser disconnected");
        }
    }

    async Task<WebTestSession?> StartSessionAsync(JsonObject m, IPAddress client, string serverName, string security, string ua,
        Func<JsonObject, Task> send, Func<string, Task> error)
    {
        if (_host.SessionCount >= Options.MaxSessions) { await error($"The server is busy ({Options.MaxSessions} tests running). Try again later."); return null; }
        TestConfig cfg;
        SpeedPlan? speed;
        try { (cfg, speed) = ParseConfig(m["config"] as JsonObject); }
        catch (Exception e) { await error("Invalid test settings: " + e.Message); return null; }
        double rtt = D((m["config"] as JsonObject)?["rttMs"]);
        var s = new WebTestSession(this, NewId(), cfg, client, serverName, security, ua, send, speed,
            double.IsFinite(rtt) && rtt is > 0 and < 60_000 ? rtt : double.NaN);
        if (s.UsesUdp && RtcUnavailable != null) { await error("UDP tests aren't available on this server: " + RtcUnavailable); s.Dispose(); return null; }
        _sessions[s.Token] = s;
        await send(new JsonObject
        {
            ["t"] = "started", ["token"] = s.Token, ["id"] = s.Id.ToString("X8"), ["describe"] = s.Describe(),
            ["udp"] = s.UsesUdp, ["streams"] = s.Streams,
            ["upUdp"] = s.UpUdpMbps, ["downUdp"] = s.DownUdpMbps, ["upTcp"] = s.UpTcpMbps, ["downTcp"] = s.DownTcpMbps,
            ["packetSize"] = Math.Clamp(cfg.PacketSize, WebPacket.MinSize, WebPacket.MaxSize),
            ["profile"] = Pacing.Supported(cfg.Profile).ToString().ToLowerInvariant(),
            ["burstOnMs"] = cfg.BurstOnMs, ["burstOffMs"] = cfg.BurstOffMs,
            ["baselineSec"] = cfg.BaselineSec, ["durationSec"] = cfg.DurationSec,
            ["speed"] = speed != null, ["downSec"] = speed?.DownSec, ["upSec"] = speed?.UpSec, ["rampSec"] = speed?.RampSec,
            ["warmupSec"] = s.WarmupSec,
        });
        return s;
    }

    uint NewId()
    {
        Span<byte> b = stackalloc byte[4];
        RandomNumberGenerator.Fill(b);
        return BitConverter.ToUInt32(b) | 0x8000_0000;
    }

    (TestConfig, SpeedPlan?) ParseConfig(JsonObject? c)
    {
        if (c == null) throw new ArgumentException("missing");
        if (((string?)c["protocol"])?.ToLowerInvariant() == "speed")
        {
            // Speed test: TCP at the server's rate cap, download first, then upload.
            int down = (int)Math.Clamp(D(c["downSec"], 10), 4, 60), up = (int)Math.Clamp(D(c["upSec"], 10), 4, 60);
            int baseline = (int)Math.Clamp(D(c["baselineSec"], 2), 1, 10);
            var plan = SpeedPlan.For(down, up, D(c["rttMs"]));
            var sc = new TestConfig
            {
                Protocol = TransportKind.Tcp, UpMbps = Options.MaxMbps, DownMbps = Options.MaxMbps, Profile = TrafficProfile.Constant,
                Streams = (int)Math.Clamp(D(c["streams"], 6), 1, 8), BaselineSec = baseline, DurationSec = baseline + plan.DownSec + plan.UpSec,
            };
            return (sc, plan);
        }
        var cfg = new TestConfig
        {
            Protocol = ((string?)c["protocol"])?.ToLowerInvariant() switch { "tcp" => TransportKind.Tcp, "mixed" => TransportKind.Mixed, _ => TransportKind.Udp },
            UpMbps = Math.Clamp(D(c["upMbps"], 5), 0, Options.MaxMbps),
            DownMbps = Math.Clamp(D(c["downMbps"], 10), 0, Options.MaxMbps),
            PacketSize = (int)Math.Clamp(D(c["packetSize"], 1200), WebPacket.MinSize, WebPacket.MaxSize),
            Profile = Enum.TryParse<TrafficProfile>((string?)c["profile"], true, out var p) ? Pacing.Supported(p) : TrafficProfile.Constant,
            BurstOnMs = (int)Math.Clamp(D(c["burstOnMs"], 200), 10, 10_000),
            BurstOffMs = (int)Math.Clamp(D(c["burstOffMs"], 800), 0, 10_000),
            Streams = (int)Math.Clamp(D(c["streams"], 1), 1, 8),
            UdpSharePct = Math.Clamp(D(c["udpSharePct"], 30), 0, 100),
            DurationSec = (int)Math.Clamp(D(c["durationSec"], 30), 0, 24 * 3600),
            BaselineSec = (int)Math.Clamp(D(c["baselineSec"], 3), 0, 30),
        };
        if (cfg.UpMbps + cfg.DownMbps <= 0 && cfg.DurationSec == 0) throw new ArgumentException("set a rate or a duration");
        return (cfg, null);
    }

    static double D(JsonNode? n, double dflt = double.NaN)
    {
        try
        {
            if (n is JsonValue v)
            {
                if (v.TryGetValue(out double d)) return d;
                if (v.TryGetValue(out string? s) && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return d;
            }
        }
        catch { }
        return dflt;
    }

    static BrowserSecond ParseSecond(JsonObject m)
    {
        static long L(JsonNode? n) => (long)Math.Max(0, D(n, 0));
        return new BrowserSecond((int)D(m["i"], -1), L(m["txUp"]), L(m["rxTcp"]), L(m["rxUdp"]), L(m["dLost"]), L(m["dExp"]),
            D(m["dJit"]), D(m["dGap"]), L(m["dLostTot"]), L(m["dExpTot"]), D(m["rtt"]), D(m["rttMin"]), D(m["rttMax"]), (int)D(m["rttN"], 0));
    }

    async Task BulkAsync(HttpContext ctx)
    {
        if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 400; return; }
        string token = ctx.Request.Query["token"].FirstOrDefault() ?? "";
        if (!_sessions.TryGetValue(token, out var s) || s.Stopped) { ctx.Response.StatusCode = 404; return; }
        using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
        if (ctx.Request.Query["dir"] == "down") await s.RunDownloadStreamAsync(ws);
        else await s.RunUploadStreamAsync(ws);
    }

    static async Task<string?> ReceiveTextAsync(WebSocket ws, CancellationToken ct)
    {
        var buf = new byte[16 * 1024];
        using var ms = new MemoryStream();
        while (true)
        {
            var r = await ws.ReceiveAsync(buf, ct);
            if (r.MessageType == WebSocketMessageType.Close) return null;
            ms.Write(buf, 0, r.Count);
            if (ms.Length > 256 * 1024) return null;       // control messages are small
            if (r.EndOfMessage) return r.MessageType == WebSocketMessageType.Text ? Encoding.UTF8.GetString(ms.ToArray()) : "";
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var s in _sessions.Values) await s.StopAsync("Server stopped");
        if (_app != null)
        {
            try { await _app.StopAsync(new CancellationTokenSource(3000).Token); } catch { }
            await _app.DisposeAsync();
        }
    }
}
