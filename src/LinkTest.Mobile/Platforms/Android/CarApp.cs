using Android.Content;
using Android.Content.PM;
using Android.Runtime;
using AndroidX.Car.App;
using AndroidX.Car.App.Model;
using AndroidX.Car.App.Validation;
using LinkTest.Core;
using CarAction = AndroidX.Car.App.Model.Action;
using LifecycleState = AndroidX.Lifecycle.Lifecycle.State;

namespace LinkTest.Mobile;

/// <summary>
/// Android Auto: a single car screen with the live figures of the current test and a Start / Stop button.
/// The test itself runs in <see cref="AppController"/> exactly as when started on the phone.
/// </summary>
[Register("io.github.into69.linktest.CarService")]
public sealed class CarService : CarAppService
{
    public override HostValidator CreateHostValidator()
    {
        // Debug builds accept any host (e.g. the desktop head unit); release builds only Android Auto / Automotive.
        bool debuggable = (ApplicationInfo!.Flags & ApplicationInfoFlags.Debuggable) != 0;
        return debuggable
            ? HostValidator.AllowAllHostsValidator!
            : new HostValidator.Builder(ApplicationContext!).AddAllowedHosts(Resource.Array.hosts_allowlist_sample).Build();
    }

    public override Session OnCreateSession() => new CarSession();
}

sealed class CarSession : Session
{
    public override Screen OnCreateScreen(Intent intent) => new LiveCarScreen(CarContext);
}

sealed class LiveCarScreen : Screen
{
    readonly AppController _app = AppController.Instance;
    DateTime _lastRefreshUtc;

    public LiveCarScreen(CarContext context) : base(context)
    {
        _app.StateChanged += Refresh;
        _app.Sampled += OnSample;
    }

    // Car hosts limit how often a template may change: refresh at most every 2 s while a test runs.
    void OnSample(StatSample _)
    {
        if ((DateTime.UtcNow - _lastRefreshUtc).TotalSeconds >= 2) Refresh();
    }

    void Refresh()
    {
        if (Lifecycle.CurrentState == LifecycleState.Destroyed)
        {
            _app.StateChanged -= Refresh;
            _app.Sampled -= OnSample;
            return;
        }
        _lastRefreshUtc = DateTime.UtcNow;
        Invalidate();
    }

    public override ITemplate OnGetTemplate()
    {
        var s = _app.LastSample;
        bool running = _app.Busy;
        string host = string.IsNullOrWhiteSpace(_app.Settings.Host) ? "no server set (set it on the phone)" : _app.Settings.Host;

        // Always the same six rows, so each update counts as a refresh rather than a new screen.
        var list = new ItemList.Builder();
        list.AddItem(RowOf(_app.Status, host));
        if (s == null)
        {
            foreach (var t in new[] { "Throughput up / down", "Loss up / down", "Latency", "Call quality", "Elapsed" })
                list.AddItem(RowOf(t, "–"));
        }
        else
        {
            list.AddItem(RowOf("Throughput up / down", $"{Ui.Mbps(s.UpMbps)} / {Ui.Mbps(s.DownMbps)}"));
            list.AddItem(RowOf("Loss up / down", $"{Ui.Fmt(s.UpLossPct, "0.##")} / {Ui.Fmt(s.DownLossPct, "0.##")} %"));
            list.AddItem(RowOf("Latency", $"RTT {Ui.Ms(s.RttMs)} · jitter {Ui.Fmt(s.UpJitterMs, "0.0")} / {Ui.Fmt(s.DownJitterMs, "0.0")} ms"));
            list.AddItem(RowOf("Call quality", $"MOS {Ui.Fmt(s.Mos, "0.0")}" + (s.BloatGrade != null ? $" · bufferbloat {s.BloatGrade}" : "")));
            var el = TimeSpan.FromSeconds(s.ElapsedSec);
            int dur = _app.Session?.Config.DurationSec ?? 0;
            list.AddItem(RowOf("Elapsed", $"{(int)el.TotalHours}:{el.Minutes:00}:{el.Seconds:00}" + (dur > 0 ? $" of {dur} s" : "")));
        }

        var toggle = new CarAction.Builder()
            .SetTitle(running ? "Stop" : "Start")
            .SetOnClickListener(new Click(running ? Stop : Start))
            .Build();

        return new ListTemplate.Builder()
            .SetTitle("LinkTest")
            .SetHeaderAction(CarAction.AppIcon!)
            .SetSingleList(list.Build())
            .SetActionStrip(new ActionStrip.Builder().AddAction(toggle).Build())
            .Build();
    }

    static Row RowOf(string title, string text) => new Row.Builder().SetTitle(title).AddText(text).Build();

    async void Start()
    {
        try
        {
            if (await _app.StartAsync() is { } err) Toast(err);
        }
        catch (Exception e) { Toast("Could not start: " + e.Message); }
        Refresh();
    }

    async void Stop()
    {
        try { await _app.StopAsync(); }
        catch (Exception e) { Toast(e.Message); }
        Refresh();
    }

    void Toast(string msg) => CarToast.MakeText(CarContext, msg, CarToast.LengthLong)!.Show();

    sealed class Click(System.Action a) : Java.Lang.Object, IOnClickListener
    {
        public void OnClick() => a();
    }
}
