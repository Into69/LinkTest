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
    WifiManager.WifiLock? _wifi;

    public override IBinder? OnBind(Intent? intent) => null;

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        EnsureChannel(this);
        var n = BuildNotification(this, intent?.GetStringExtra("text") ?? "Test running");
        if (OperatingSystem.IsAndroidVersionAtLeast(34))
            StartForeground(NotificationId, n, Android.Content.PM.ForegroundService.TypeSpecialUse);
        else
            StartForeground(NotificationId, n);

        if (_wake == null && GetSystemService(PowerService) is PowerManager pm)
        {
            _wake = pm.NewWakeLock(WakeLockFlags.Partial, "LinkTest::Test");
            _wake?.Acquire();
        }
        if (_wifi == null && GetSystemService(WifiService) is WifiManager wm)
        {
#pragma warning disable CA1422 // high-performance Wi-Fi lock is deprecated on new Android, still honoured on older devices
            _wifi = wm.CreateWifiLock(OperatingSystem.IsAndroidVersionAtLeast(29) ? Android.Net.WifiMode.FullLowLatency : Android.Net.WifiMode.FullHighPerf, "LinkTest::Wifi");
#pragma warning restore CA1422
            _wifi?.Acquire();
        }
        return StartCommandResult.NotSticky;
    }

    public override void OnDestroy()
    {
        try { if (_wake?.IsHeld == true) _wake.Release(); } catch { }
        try { if (_wifi?.IsHeld == true) _wifi.Release(); } catch { }
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

    /// <summary>Static helpers used by the app.</summary>
    public static class Control
    {
        public static void Start(string text)
        {
            var ctx = Android.App.Application.Context;
            var i = new Intent(ctx, typeof(TestService));
            i.PutExtra("text", text);
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
