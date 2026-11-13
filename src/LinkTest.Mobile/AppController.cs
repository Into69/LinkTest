using System.Text.Json;
using LinkTest.Core;
using LinkTest.Core.Tunnels;

namespace LinkTest.Mobile;

/// <summary>Persisted settings (app data folder).</summary>
sealed class MobileSettings
{
    public string Host { get; set; } = "";
    public int Port { get; set; } = Protocol.DefaultPort;
    public string Key { get; set; } = "";
    public TestConfig Config { get; set; } = new() { UpMbps = 5, DownMbps = 10 };
    public double ArtificialLossPct { get; set; }
    public bool IpLookup { get; set; } = true;
    public bool CheckForUpdates { get; set; } = true;
    public bool KeepScreenOn { get; set; }
    /// <summary>Kept for older settings files: tests now always run in the foreground service.</summary>
    public bool KeepRunningInBackground { get; set; } = true;
    /// <summary>The battery-optimisation prompt was offered once (afterwards: Settings > Background testing).</summary>
    public bool BatteryPromptShown { get; set; }
    /// <summary>Report the phone's location with the test data and to the server. Off by default.</summary>
    public bool ShareLocation { get; set; }
    /// <summary>"auto", "4", "6" or "both" (IPv4 then IPv6 with a comparison).</summary>
    public string Family { get; set; } = "auto";
    public bool RequireTls { get; set; }
    public bool CapacityTest { get; set; }
    public double CapacityPct { get; set; } = 80;
    public bool MeasureMtu { get; set; } = true;
    public bool AutoTraceroute { get; set; } = true;
    public List<TunnelProfile> Tunnels { get; set; } = TunnelPresets.CreateDefaults();

    static string FilePath => Path.Combine(FileSystem.AppDataDirectory, "settings.json");

    public static MobileSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var s = JsonSerializer.Deserialize<MobileSettings>(File.ReadAllText(FilePath), Json.Options);
                if (s != null)
                {
                    s.Config ??= new();
                    if (s.Tunnels == null || s.Tunnels.Count == 0) s.Tunnels = TunnelPresets.CreateDefaults();
                    return s;
                }
            }
        }
        catch { }
        return new MobileSettings();
    }

    public void Save()
    {
        try { File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json.Options)); } catch { }
    }
}

/// <summary>Owns the test session and the 1 s sampling timer; pages subscribe to its events (UI thread).</summary>
sealed class AppController
{
    public static AppController Instance { get; } = new();

    public MobileSettings Settings { get; } = MobileSettings.Load();
    public ClientSession? Session { get; private set; }
    public StatSample? LastSample { get; private set; }
    public ConnectionInfo? Connection { get; private set; }
    public bool Connecting { get; private set; }
    public string Status { get; private set; } = "Idle";
    public IReadOnlyList<string> LogLines => _log;

    public event Action? StateChanged;
    /// <summary>A result was saved or deleted.</summary>
    public event Action? HistoryChanged;
    public ResultStore Results { get; } = new(Path.Combine(FileSystem.AppDataDirectory, "results"));
    bool _serviceRunning;
    public event Action<StatSample>? Sampled;
    public event Action<string>? LogAdded;
    public event Action<ConnectionInfo>? ConnectionChanged;
    /// <summary>Path MTU or a traceroute result arrived.</summary>
    public event Action? PathChanged;
    public MtuResult? Mtu { get; private set; }
    public IReadOnlyList<TraceResult> Traces => _traces;
    readonly List<TraceResult> _traces = new();

    readonly List<string> _log = new();
    IDispatcherTimer? _timer;
    CancellationTokenSource? _connectCts;

    public bool Busy => Session != null || Connecting;

    public void Log(string msg)
    {
        foreach (var line in msg.Replace("\r", "").Split('\n'))
        {
            string l = $"{DateTime.Now:HH:mm:ss}  {line}";
            _log.Add(l);
            if (_log.Count > 600) _log.RemoveRange(0, 100);
            LogAdded?.Invoke(l);
        }
    }

    static void OnUi(Action a) => MainThread.BeginInvokeOnMainThread(a);

    void SetStatus(string s)
    {
        Status = s;
        StateChanged?.Invoke();
    }

    /// <summary>Detailed UDP/TCP statistics: live while running, final after the test.</summary>
    public LinkDetail? Detail { get; private set; }

    readonly KnownServers _known = new(Path.Combine(FileSystem.AppDataDirectory, "known-servers.json"));
    // IPv4-then-IPv6 comparison: 0 = none, 4 = IPv4 test running, 6 = IPv6 test running.
    int _compareStage;
    TestRecord? _compareFirst;

    /// <param name="familyOverride">Second run of an IPv4-then-IPv6 comparison.</param>
    public async Task<string?> StartAsync(System.Net.Sockets.AddressFamily? familyOverride = null)
    {
        if (Busy) return null;
        var st = Settings;
        if (string.IsNullOrWhiteSpace(st.Host)) return "Enter the server IP address or name on the Settings tab.";
        st.Save();
        var family = familyOverride;
        if (familyOverride == null)
        {
            _compareStage = 0;
            _compareFirst = null;
            switch (st.Family)
            {
                case "4": family = System.Net.Sockets.AddressFamily.InterNetwork; break;
                case "6": family = System.Net.Sockets.AddressFamily.InterNetworkV6; break;
                case "both":
                    if (st.Config.DurationSec <= 0) return "Comparing IPv4 and IPv6 runs the test twice, one after the other: set a duration (for example 120 s) on the Settings tab.";
                    family = System.Net.Sockets.AddressFamily.InterNetwork;
                    _compareStage = 4;
                    break;
            }
        }

        // Opt-in location: ask for permission before the test, while LinkTest is in front.
        _locationAllowed = false;
        if (st.ShareLocation)
        {
            try
            {
                var perm = await Permissions.RequestAsync<Permissions.LocationWhenInUse>();
                _locationAllowed = perm == PermissionStatus.Granted;
                if (!_locationAllowed) Log("Location not reported: permission was not granted (Android settings > Apps > LinkTest > Permissions > Location).");
            }
            catch (Exception e) { Log("Location not reported: " + e.Message); }
        }

        var session = new ClientSession();
        session.Log += m => OnUi(() => Log(m));
        session.Ended += r => OnUi(() => OnEnded(session, r));
        session.ConnectionChanged += c => OnUi(() => { Connection = c; ConnectionChanged?.Invoke(c); });
        session.MtuMeasured += m => OnUi(() => { Mtu = m; PathChanged?.Invoke(); });
        session.TraceCompleted += t => OnUi(() => { _traces.Add(t); if (_traces.Count > 10) _traces.RemoveAt(0); PathChanged?.Invoke(); });
        session.MeasureMtu = st.MeasureMtu;
        session.AutoTraceroute = st.AutoTraceroute;
        session.Family = family;
        session.UseTls = true;
        session.RequireTls = st.RequireTls;
        session.KnownServers = _known;
        session.CapacityTest = st.CapacityTest;
        session.CapacityTargetPct = st.CapacityPct;
        session.CapacityMeasured += () => OnUi(() => SetStatus($"Running · {session.Config} (capacity up {Cap(session.CapacityUpMbps, session.CapacityUpAtLimit)} / down {Cap(session.CapacityDownMbps, session.CapacityDownAtLimit)})"));
        Mtu = null;
        _traces.Clear();
        Connecting = true;
        LastSample = null;
        Detail = null;
        Connection = null;
        SetStatus($"Connecting to {st.Host}…");
        var connectCts = _connectCts = new CancellationTokenSource();
        try
        {
            var tunnels = st.Tunnels.Where(t => t.Enabled).Select(t => t.Clone()).ToList();
            await session.StartAsync(st.Host, st.Port, st.Key, st.Config, tunnels, st.ArtificialLossPct, connectCts.Token, st.IpLookup);
        }
        catch (Exception e)
        {
            session.Dispose();
            Connecting = false;
            string msg = e is OperationCanceledException ? "Cancelled" : e.Message;
            Log("Could not start: " + msg);
            SetStatus("Idle");
            if (e is ServerCertificateChangedException changed && Application.Current?.Windows.FirstOrDefault()?.Page is { } page)
            {
                if (_connectCts == connectCts) _connectCts = null;
                if (await page.DisplayAlertAsync("Server certificate changed", changed.Message, "Trust and connect", "Cancel"))
                {
                    _known.Trust(changed.Server, changed.Presented);
                    Log("Trusting the new server certificate " + Tls.ShortFingerprint(changed.Presented));
                    return await StartAsync(familyOverride);
                }
                _compareStage = 0;
                return null;
            }
            if (_compareStage == 6) Log("IPv4 vs IPv6 comparison not possible: the IPv6 test could not start.");
            _compareStage = 0;
            return e is OperationCanceledException ? null : msg;
        }
        finally
        {
            connectCts.Dispose();
            if (_connectCts == connectCts) _connectCts = null;   // a retry may already have its own
        }

        Session = session;
        Connecting = false;
        if (st.KeepScreenOn) DeviceDisplay.Current.KeepScreenOn = true;
        // Always: the foreground service (wake lock, Wi-Fi locks, notification) keeps the test running with the
        // screen off, the phone asleep or another app in front.
        StartService(session);
        _lastTickUtc = DateTime.UtcNow;
        // The test may have ended while the permission prompt was open.
        if (Session != session || !session.IsRunning) return null;
        _timer ??= CreateTimer();
        _timer.Start();
        SetStatus($"Running · {session.Config}");
        return null;
    }

    IDispatcherTimer CreateTimer()
    {
        var t = Application.Current!.Dispatcher.CreateTimer();
        t.Interval = TimeSpan.FromSeconds(1);
        t.Tick += (_, _) => Tick();
        return t;
    }

    DateTime _lastTickUtc;
    bool _locationAllowed;
    int _locationBusy;
    long _locationTick;

    /// <summary>Reads a location fix (every 10 s) and hands it to the session.</summary>
    async void PollLocation(ClientSession s)
    {
        if (Interlocked.Exchange(ref _locationBusy, 1) == 1) return;
        try
        {
            var l = await Geolocation.Default.GetLocationAsync(new GeolocationRequest(GeolocationAccuracy.Best, TimeSpan.FromSeconds(8)));
            if (l != null && s.IsRunning)
                s.ReportLocation(new GeoFix(DateTime.Now, l.Latitude, l.Longitude, l.Accuracy ?? double.NaN, l.Altitude ?? double.NaN,
                    l.Speed ?? double.NaN, l.IsFromMockProvider ? "mock" : "GPS / network"));
        }
        catch (Exception e)
        {
            if (_locationTick <= 1) Log("Location unavailable: " + e.Message);
        }
        finally { Interlocked.Exchange(ref _locationBusy, 0); }
    }

    void Tick()
    {
        var s = Session;
        if (s == null || !s.IsRunning) return;
        // The 1 s timer only stalls if Android suspended the app despite the foreground service (battery saver,
        // aggressive manufacturer power management): say so, so the gap isn't mistaken for a network problem.
        var nowUtc = DateTime.UtcNow;
        double paused = (nowUtc - _lastTickUtc).TotalSeconds;
        _lastTickUtc = nowUtc;
        if (paused > 5)
            Log($"Warning: the phone paused LinkTest for about {paused:0} s (power saving). Allow LinkTest to ignore battery optimisation in Settings > Background testing.");
        if (_locationAllowed && _locationTick++ % 10 == 0) PollLocation(s);
        var sample = s.TakeSample();
        LastSample = sample;
        Detail = sample.Detail;
        Sampled?.Invoke(sample);
        if (_serviceRunning && (int)sample.ElapsedSec % 5 == 0)
            try { TestService.Control.Update(NotificationText(sample)); } catch { }
        if (s.Config.DurationSec > 0 && sample.ElapsedSec >= s.Config.DurationSec)
            _ = s.StopAsync("Duration reached");
    }

    public async Task StopAsync()
    {
        if (_connectCts != null) { _connectCts.Cancel(); return; }
        var s = Session;
        if (s == null) return;
        SetStatus("Stopping…");
        await s.StopAsync();
    }

    public void SetArtificialLoss(double pct)
    {
        Settings.ArtificialLossPct = pct;
        if (Session is { IsRunning: true } s && Math.Abs(s.ArtificialLossPct - pct) > 1e-9)
        {
            s.ArtificialLossPct = pct;
            Log($"Artificial loss changed to {pct:0.#}%");
        }
    }

    void OnEnded(ClientSession session, string reason)
    {
        if (Session != session) return;
        _timer?.Stop();
        Log("Test ended: " + reason);
        if (session.Record?.Detail is { } finalDetail) Detail = finalDetail;
        if (session.Record is { } rec)
        {
            try { Results.Save(rec); Log("Result saved - see the History tab to share the report."); HistoryChanged?.Invoke(); }
            catch (Exception e) { Log("Could not save the result: " + e.Message); }
        }
        session.Dispose();
        Session = null;
        DeviceDisplay.Current.KeepScreenOn = false;
        StopService();
        SetStatus("Idle - " + reason);
        ContinueComparison(session.Record, reason);
    }

    static string Cap(double v, bool atLimit) => double.IsFinite(v) ? $"{(atLimit ? "≥" : "")}{v:0.#} Mbps" : "–";

    /// <summary>IPv4-then-IPv6: after the IPv4 test start the IPv6 one; after that, offer the comparison report.</summary>
    async void ContinueComparison(TestRecord? rec, string reason)
    {
        if (_compareStage == 0) return;
        if (rec == null || reason == "Stopped by user")
        {
            Log("IPv4 vs IPv6 comparison cancelled.");
            _compareStage = 0;
            return;
        }
        if (_compareStage == 4)
        {
            _compareFirst = rec;
            _compareStage = 6;
            Log("IPv4 test done - starting the IPv6 test…");
            await Task.Delay(2000);
            var err = await StartAsync(System.Net.Sockets.AddressFamily.InterNetworkV6);
            if (err != null) Log("IPv6 test: " + err);
            return;
        }
        _compareStage = 0;
        if (_compareFirst == null) return;
        try
        {
            var cmp = new Comparison(_compareFirst, rec);
            string dir = Path.Combine(FileSystem.CacheDirectory, "reports");
            Directory.CreateDirectory(dir);
            string pdf = Path.Combine(dir, cmp.SuggestedFileName("pdf"));
            File.WriteAllBytes(pdf, PdfReportBuilder.BuildComparison(cmp));
            File.WriteAllText(Path.ChangeExtension(pdf, "html"), ReportBuilder.BuildComparison(cmp));
            Log("IPv4 vs IPv6: " + cmp.Headline);
            if (Application.Current?.Windows.FirstOrDefault()?.Page is { } page &&
                await page.DisplayAlertAsync("IPv4 vs IPv6", cmp.Headline + "\n\nShare the comparison report (PDF)?", "Share", "Later"))
                await Share.Default.RequestAsync(new ShareFileRequest { Title = "LinkTest IPv4 vs IPv6", File = new ShareFile(pdf, "application/pdf") });
        }
        catch (Exception e) { Log("Could not make the comparison: " + e.Message); }
    }

    /// <summary>
    /// Starts the foreground service right away, while LinkTest is certainly in front (Android 12+ does not let a
    /// background app start one), then asks for the notification and battery permissions without holding up the test.
    /// </summary>
    void StartService(ClientSession session)
    {
        try
        {
            _locationTick = 0;
            TestService.Control.Start($"Testing {Settings.Host} · {session.Config}", withLocation: _locationAllowed);
            _serviceRunning = true;
        }
        catch (Exception e) { Log("Background mode unavailable: " + e.Message); }
        _ = AskBackgroundPermissionsAsync(session);
    }

    async Task AskBackgroundPermissionsAsync(ClientSession session)
    {
        try
        {
            // Android 13+: the ongoing notification needs permission (the service runs either way).
            if (OperatingSystem.IsAndroidVersionAtLeast(33))
                await Permissions.RequestAsync<Permissions.PostNotifications>();
            if (_serviceRunning && Session == session) TestService.Control.Update($"Testing {Settings.Host} · {session.Config}");

            // Once: offer the battery-optimisation exemption, so Doze / app standby can't restrict long tests.
            if (!Settings.BatteryPromptShown && !TestService.Battery.IsExempt && Session == session && session.IsRunning
                && Application.Current?.Windows.FirstOrDefault()?.Page is { } page)
            {
                Settings.BatteryPromptShown = true;
                Settings.Save();
                if (await page.DisplayAlertAsync("Keep tests running",
                        "To make sure Android never pauses a test while the screen is off, allow LinkTest to ignore battery optimisation. " +
                        "It only uses extra power while a test runs.\n\nYou can change this later in Settings > Background testing.",
                        "Allow", "Not now"))
                    TestService.Battery.RequestExemption();
            }
        }
        catch (Exception e) { Log("Could not ask for background permissions: " + e.Message); }
    }

    void StopService()
    {
        if (!_serviceRunning) return;
        _serviceRunning = false;
        try { TestService.Control.Stop(); } catch { }
    }

    static string NotificationText(StatSample s)
    {
        var el = TimeSpan.FromSeconds(s.ElapsedSec);
        return $"{(int)el.TotalHours}:{el.Minutes:00}:{el.Seconds:00} · ↑ {Ui.Mbps(s.UpMbps)} ↓ {Ui.Mbps(s.DownMbps)} · loss {Ui.Fmt(s.UpLossPct, "0.#")}/{Ui.Fmt(s.DownLossPct, "0.#")}% · RTT {Ui.Ms(s.RttMs)}";
    }

    public void NotifyHistoryChanged() => HistoryChanged?.Invoke();

    /// <summary>Tunnel and class status for the Details page (live while running).</summary>
    public IReadOnlyList<TunnelStatus> TunnelStatuses()
    {
        var s = Session;
        if (s == null) return Array.Empty<TunnelStatus>();
        long now = Clock.NowUs;
        return s.Tunnels.Select(t => t.GetStatus(now)).ToList();
    }

    // ------------------------------------------------------------------ updates

    public async Task CheckForUpdatesAsync(Page page, bool manual)
    {
        try
        {
            var info = await Updater.CheckAsync(UpdateChannel.Android);
            if (info == null)
            {
                if (manual) await page.DisplayAlertAsync("LinkTest", $"You are running the latest version ({Updater.CurrentVersion}).", "OK");
                return;
            }
            Log($"Update available: {info.Tag} (running {Updater.CurrentVersion})");
            if (!await page.DisplayAlertAsync("Update available",
                    $"LinkTest {info.Tag} is available (you have {Updater.CurrentVersion}).\n\nDownload and install it now?", "Install", "Later"))
                return;
            if (Busy)
            {
                await page.DisplayAlertAsync("LinkTest", "Stop the running test before updating.", "OK");
                return;
            }
            SetStatus($"Downloading {info.Tag}…");
            string apk = await Updater.DownloadAsync(info);
            Log($"Downloaded {info.Tag}; opening the Android installer.");
            SetStatus("Idle");
            // Android does not let apps replace themselves: hand the APK to the system installer.
            await Launcher.Default.OpenAsync(new OpenFileRequest("Install LinkTest " + info.Tag,
                new ReadOnlyFile(apk, "application/vnd.android.package-archive")));
        }
        catch (Exception e)
        {
            SetStatus("Idle");
            Log("Update failed: " + Updater.Describe(e));
            if (manual) await page.DisplayAlertAsync("LinkTest", "Update failed: " + Updater.Describe(e), "OK");
        }
    }
}
