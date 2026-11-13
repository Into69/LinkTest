using LinkTest.Core;

namespace LinkTest.Client;

static class Program
{
    [STAThread]
    static void Main()
    {
        Updater.CleanupOldFiles();
        ApplicationConfiguration.Initialize();
        Clock.EnableHighResolutionTimer();
        Application.Run(new MainForm());
    }
}
