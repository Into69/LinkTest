using LinkTest.Core;

namespace LinkTest.ServerGui;

static class Program
{
    /// <param name="args">--autostart: start the server immediately (used after a self-update restart).</param>
    [STAThread]
    static void Main(string[] args)
    {
        Updater.CleanupOldFiles();
        ApplicationConfiguration.Initialize();
        Clock.EnableHighResolutionTimer();
        Application.Run(new ServerForm(args.Contains("--autostart", StringComparer.OrdinalIgnoreCase)));
    }
}
