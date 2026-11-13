using Android.App;
using Android.Content;
using Android.Net.Wifi;
using Android.OS;
using Android.Runtime;

namespace LinkTest.Mobile;

/// <summary>
/// Foreground service that keeps a test running when the screen is off or the app is in the background:
/// shows an ongoing notification and holds a partial wake lock and a Wi-Fi lock while a test runs.
/// Declared in AndroidManifest.xml (foregroundServiceType "specialUse").
/// </summary>
[Register("io.github.into69.linktest.TestService")]
public sealed class TestService : Service
{
    public const int NotificationId = 4471;
    const string ChannelId = "linktest_running";

    PowerManager.WakeLock? _wake;
    WifiManager.WifiLock? _wifi, _wifiLowLatency;

    public override IBinder? OnBind(Intent? intent) => null;

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        EnsureChannel(this);
        var n = BuildNotification(this, intent?.GetStringExtra("text") ?? "Test running");
        // With location reporting on, the service is also a "location" service so fixes keep coming with the screen off.
        bool location = intent?.GetBooleanExtra("location", false) == true;
        if (OperatingSystem.IsAndroidVersionAtLeast(34))
            StartForeground(NotificationId, n, location
                ? Android.Content.PM.ForegroundService.TypeSpecialUse | Android.Content.PM.ForegroundService.TypeLocation
                : Android.Content.PM.ForegroundService.TypeSpecialUse);
        else if (OperatingSystem.IsAndroidVersionAtLeast(29) && location)
            StartForeground(NotificationId, n, Android.Content.PM.ForegroundService.TypeLocation);
        else
            StartForeground(NotificationId, n);

        if (_wake == null && GetSystemService(PowerService) is PowerManager pm)
        {
            _wake = pm.NewWakeLock(WakeLockFlags.Partial, "LinkTest::Test");
            _wake?.SetReferenceCounted(false);
            _wake?.Acquire();
        }
        if (_wifi == null && GetSystemService(WifiService) is WifiManager wm)
        {
            // HIGH_PERF keeps Wi-Fi out of power save with the screen off and the app in the background (Android 13
            // and older; Android 14+ maps it to LOW_LATENCY). LOW_LATENCY only applies while the screen is on and the
            // app is in front; holding both gives the best available mode in every state.
#pragma warning disable CA1422
            _wifi = wm.CreateWifiLock(Android.Net.WifiMode.FullHighPerf, "LinkTest::Wifi");
#pragma warning restore CA1422
            _wifi?.SetReferenceCounted(false);
            _wifi?.Acquire();
            if (OperatingSystem.IsAndroidVersionAtLeast(29))
            {
                _wifiLowLatency = wm.CreateWifiLock(Android.Net.WifiMode.FullLowLatency, "LinkTest::WifiLowLatency");
                _wifiLowLatency?.SetReferenceCounted(false);
                _wifiLowLatency?.Acquire();
            }
        }
        return StartCommandResult.NotSticky;
    }

    public override void OnDestroy()
    {
        try { if (_wake?.IsHeld == true) _wake.Release(); } catch { }
        try { if (_wifi?.IsHeld == true) _wifi.Release(); } catch { }
        try { if (_wifiLowLatency?.IsHeld == true) _wifiLowLatency.Release(); } catch { }
        base.OnDestroy();
    }

    static void EnsureChannel(Context ctx)
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(26)) return;
        var nm = (NotificationManager)ctx.GetSystemService(NotificationService)!;
        if (nm.GetNotificationChannel(ChannelId) != null) return;
        var ch = new NotificationChannel(ChannelId, "Running tests", NotificationImportance.Low)
        {
            Description = "Shown while a LinkTest test is running in the background",
        };
        nm.CreateNotificationChannel(ch);
    }

    static Notification BuildNotification(Context ctx, string text)
    {
        var open = ctx.PackageManager!.GetLaunchIntentForPackage(ctx.PackageName!)!;
        open.SetFlags(ActivityFlags.SingleTop);
        var pi = PendingIntent.GetActivity(ctx, 0, open, PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);
#pragma warning disable CA1422
        var b = OperatingSystem.IsAndroidVersionAtLeast(26) ? new Notification.Builder(ctx, ChannelId) : new Notification.Builder(ctx);
#pragma warning restore CA1422
        b.SetContentTitle("LinkTest is running")
         .SetContentText(text)
         .SetSmallIcon(Android.Resource.Drawable.StatSysUpload)
         .SetOngoing(true)
         .SetOnlyAlertOnce(true)
         .SetContentIntent(pi);
        return b.Build()!;
    }

    /// <summary>Swiping the app away from Recents must not end a running test: the service (and process) stay.</summary>
    public override void OnTaskRemoved(Intent? rootIntent) { }

    /// <summary>Battery-optimisation (Doze / app standby) exemption, so the system doesn't restrict a running test.</summary>
    public static class Battery
    {
        public static bool IsExempt
        {
            get
            {
                if (!OperatingSystem.IsAndroidVersionAtLeast(23)) return true;
                var ctx = Android.App.Application.Context;
                return ctx.GetSystemService(PowerService) is PowerManager pm && pm.IsIgnoringBatteryOptimizations(ctx.PackageName);
            }
        }

        /// <summary>Opens the system prompt asking to let LinkTest ignore battery optimisation.</summary>
        public static void RequestExemption()
        {
            if (!OperatingSystem.IsAndroidVersionAtLeast(23)) return;
            var ctx = Android.App.Application.Context;
            try
            {
                var i = new Intent(Android.Provider.Settings.ActionRequestIgnoreBatteryOptimizations, Android.Net.Uri.Parse("package:" + ctx.PackageName));
                i.AddFlags(ActivityFlags.NewTask);
                ctx.StartActivity(i);
            }
            catch
            {
                // Some devices hide the direct prompt: open the battery-optimisation list instead.
                var i = new Intent(Android.Provider.Settings.ActionIgnoreBatteryOptimizationSettings);
                i.AddFlags(ActivityFlags.NewTask);
                ctx.StartActivity(i);
            }
        }
    }

    /// <summary>Static helpers used by the app.</summary>
    public static class Control
    {
        public static void Start(string text, bool withLocation = false)
        {
            var ctx = Android.App.Application.Context;
            var i = new Intent(ctx, typeof(TestService));
            i.PutExtra("text", text);
            i.PutExtra("location", withLocation);
            if (OperatingSystem.IsAndroidVersionAtLeast(26)) ctx.StartForegroundService(i); else ctx.StartService(i);
        }

        public static void Update(string text)
        {
            var ctx = Android.App.Application.Context;
            EnsureChannel(ctx);
            var nm = (NotificationManager)ctx.GetSystemService(NotificationService)!;
            nm.Notify(NotificationId, BuildNotification(ctx, text));
        }

        public static void Stop()
        {
            var ctx = Android.App.Application.Context;
            ctx.StopService(new Intent(ctx, typeof(TestService)));
        }
    }
}
