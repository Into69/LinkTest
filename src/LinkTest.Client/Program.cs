using LinkTest.Core;
using LinkTest.WinUi;

namespace LinkTest.Client;

static class Program
{
    [STAThread]
    static void Main()
    {
        Updater.CleanupOldFiles();
        ApplicationConfiguration.Initialize();
        Theme.Apply(ClientSettings.Load().ColorMode);   // before the first window
        Clock.EnableHighResolutionTimer();
        Application.Run(new MainForm());
    }
}
