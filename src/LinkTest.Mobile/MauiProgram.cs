namespace LinkTest.Mobile;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder.UseMauiApp<App>();
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
        new(new NavigationPage(new MainTabs()) { BarBackgroundColor = Ui.Brand, BarTextColor = Colors.White }) { Title = "LinkTest" };
}
