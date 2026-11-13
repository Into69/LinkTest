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
    /// <summary>Foreground service + notification so tests keep running with the screen off.</summary>
    public bool KeepRunningInBackground { get; set; } = true;
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

    public async Task<string?> StartAsync()
    {
        if (Busy) return null;
        var st = Settings;
        if (string.IsNullOrWhiteSpace(st.Host)) return "Enter the server IP address or name on the Settings tab.";
        st.Save();

        var session = new ClientSession();
        session.Log += m => OnUi(() => Log(m));
        session.Ended += r => OnUi(() => OnEnded(session, r));
        session.ConnectionChanged += c => OnUi(() => { Connection = c; ConnectionChanged?.Invoke(c); });
        session.MtuMeasured += m => OnUi(() => { Mtu = m; PathChanged?.Invoke(); });
        session.TraceCompleted += t => OnUi(() => { _traces.Add(t); if (_traces.Count > 10) _traces.RemoveAt(0); PathChanged?.Invoke(); });
        session.MeasureMtu = st.MeasureMtu;
        session.AutoTraceroute = st.AutoTraceroute;
        Mtu = null;
        _traces.Clear();
        Connecting = true;
        LastSample = null;
        Connection = null;
        SetStatus($"Connecting to {st.Host}…");
        _connectCts = new CancellationTokenSource();
        try
        {
            var tunnels = st.Tunnels.Where(t => t.Enabled).Select(t => t.Clone()).ToList();
            await session.StartAsync(st.Host, st.Port, st.Key, st.Config, tunnels, st.ArtificialLossPct, _connectCts.Token, st.IpLookup);
        }
        catch (Exception e)
        {
            session.Dispose();
            Connecting = false;
            string msg = e is OperationCanceledException ? "Cancelled" : e.Message;
            Log("Could not start: " + msg);
            SetStatus("Idle");
            return e is OperationCanceledException ? null : msg;
        }
        finally
        {
            _connectCts?.Dispose();
            _connectCts = null;
        }

        Session = session;
        Connecting = false;
        if (st.KeepScreenOn) DeviceDisplay.Current.KeepScreenOn = true;
        if (st.KeepRunningInBackground) await StartServiceAsync(session);
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

    void Tick()
    {
        var s = Session;
        if (s == null || !s.IsRunning) return;
        var sample = s.TakeSample();
        LastSample = sample;
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
    }

    async Task StartServiceAsync(ClientSession session)
    {
        try
        {
            // Android 13+: the ongoing notification needs permission (the service still runs if refused).
            if (OperatingSystem.IsAndroidVersionAtLeast(33))
                await Permissions.RequestAsync<Permissions.PostNotifications>();
            TestService.Control.Start($"Testing {Settings.Host} · {session.Config}");
            _serviceRunning = true;
        }
        catch (Exception e) { Log("Background mode unavailable: " + e.Message); }
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
