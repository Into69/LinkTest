namespace LinkTest.Mobile;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder.UseMauiApp<App>();
        AndroidPing.Register();
        return builder.Build();
    }
}

public sealed class App : Application
{
    bool _rebuildWhenIdle;

    public App()
    {
        UserAppTheme = ToAppTheme(AppController.Instance.Settings.ColorMode);
        LinkTest.Core.Updater.CleanupOldFiles();
        // Theme "System": follow Android's dark-theme switch.
        RequestedThemeChanged += (_, _) => { if (UserAppTheme == AppTheme.Unspecified) Rebuild(); };
    }

    static AppTheme ToAppTheme(string? mode) => mode switch { "light" => AppTheme.Light, "dark" => AppTheme.Dark, _ => AppTheme.Unspecified };

    protected override Window CreateWindow(IActivationState? activationState) => new(Root()) { Title = "LinkTest" };

    static Page Root() => new NavigationPage(new MainTabs()) { BarBackgroundColor = Ui.CardBg, BarTextColor = Ui.Brand };

    /// <summary>Applies the Theme setting ("system", "light" or "dark").</summary>
    public static void ApplyTheme(string mode)
    {
        if (Current is not App app) return;
        var theme = ToAppTheme(mode);
        if (app.UserAppTheme == theme) return;
        bool wasDark = Ui.Dark;
        app.UserAppTheme = theme;
        if (Ui.Dark != wasDark) app.Rebuild();
    }

    /// <summary>
    /// The pages take their colours when they are built, so a theme change builds them again. A running test keeps
    /// its pages (live charts, log): the rebuild then waits until it ends.
    /// </summary>
    void Rebuild()
    {
        var app = AppController.Instance;
        if (app.Busy)
        {
            if (!_rebuildWhenIdle) app.StateChanged += RebuildWhenIdle;
            _rebuildWhenIdle = true;
            return;
        }
        _rebuildWhenIdle = false;
        Dispatcher.Dispatch(() =>
        {
            if (Windows.FirstOrDefault() is not { } w) return;
            app.DetachUi();
            w.Page = Root();
#if ANDROID
            Platform.CurrentActivity?.RunOnUiThread(() => (Platform.CurrentActivity as MainActivity)?.ApplySystemBars());
#endif
        });
    }

    void RebuildWhenIdle()
    {
        if (!AppController.Instance.Busy) Rebuild();
    }
}
