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
        ApplySystemBars();
    }

    /// <summary>
    /// Top bar matching the app's bar (white, or dark in the dark theme), with icons that contrast with it. On
    /// Android 15+ edge-to-edge, the area behind the status bar is the window background.
    /// </summary>
    public void ApplySystemBars()
    {
        var w = Window;
        if (w == null) return;
        bool dark = Ui.Dark;
        var bg = dark ? Android.Graphics.Color.ParseColor("#1B1F27") : Android.Graphics.Color.White;
        w.DecorView.SetBackgroundColor(bg);
#pragma warning disable CA1422 // ignored on Android 15+, where the background above shows instead
        w.SetStatusBarColor(bg);
#pragma warning restore CA1422
        var insets = WindowCompat.GetInsetsController(w, w.DecorView);
        if (insets != null) insets.AppearanceLightStatusBars = !dark;
    }
}
