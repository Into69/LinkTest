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
    public App()
    {
        UserAppTheme = AppTheme.Light;
        LinkTest.Core.Updater.CleanupOldFiles();
    }

    protected override Window CreateWindow(IActivationState? activationState) =>
        new(new NavigationPage(new MainTabs()) { BarBackgroundColor = Colors.White, BarTextColor = Ui.Brand }) { Title = "LinkTest" };
}
