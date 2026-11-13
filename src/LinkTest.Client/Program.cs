using LinkTest.Core;

namespace LinkTest.Client;

static class Program
{
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();
        Clock.EnableHighResolutionTimer();
        Application.Run(new MainForm());
    }
}
