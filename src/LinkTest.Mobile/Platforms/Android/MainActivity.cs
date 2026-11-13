using Android.App;
using Android.Content.PM;
using Android.OS;
using AndroidX.Core.View;

namespace LinkTest.Mobile;

[Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true, LaunchMode = LaunchMode.SingleTop,
    ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode |
                           ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
public class MainActivity : MauiAppCompatActivity
{
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        // Light top bar to match the white tab bar: white status bar with dark icons (the area behind the status
        // bar on Android 15+ edge-to-edge is the window background).
        var w = Window;
        if (w == null) return;
        w.DecorView.SetBackgroundColor(Android.Graphics.Color.White);
#pragma warning disable CA1422 // ignored on Android 15+, where the background above shows instead
        w.SetStatusBarColor(Android.Graphics.Color.White);
#pragma warning restore CA1422
        var insets = WindowCompat.GetInsetsController(w, w.DecorView);
        insets.AppearanceLightStatusBars = true;
    }
}
