using LinkTest.Core;
using LinkTest.Server;

var opts = ServerOptions.Parse(args);
if (opts == null)
{
    Console.WriteLine($"""
        LinkTest server {Updater.CurrentVersion}  (a GUI version is available: LinkTestServerGui.exe)

        Usage: LinkTestServer [--port N] [--bind ADDRESS] [--key SECRET] [--max-mbps N] [--max-sessions N]
                              [--auto-update | --no-update-check] [--no-ip-lookup] [--require-tls] [--cert FILE]
               LinkTestServer --update | --update-from PACKAGE.zip | --version

          --port N              TCP and UDP port to listen on (default 7447)
          --bind ADDRESS        Local address to bind (default: all IPv4 + IPv6)
          --key SECRET          Require clients to present this shared key
          --max-mbps N          Per-direction rate cap per session (default 1000)
          --max-sessions N      Concurrent sessions allowed (default 8)
          --auto-update         Check GitHub every 6 h; install new versions when no tests are running, then restart
          --no-update-check     Don't check for new versions at all
          --no-ip-lookup        Don't query ipinfo.io for client ISP / location (reverse DNS still shown)
          --require-tls         Refuse clients that don't encrypt the control connection (clients older than 1.9)
          --cert FILE           Server certificate (PFX, created on first start; default: per-user LinkTest folder)
          --update              Install the latest release now and exit
          --update-from FILE    Install from a downloaded release zip (offline update) and exit

        Windows Firewall (run once as Administrator):
          netsh advfirewall firewall add rule name="LinkTest TCP" dir=in action=allow protocol=TCP localport=7447
          netsh advfirewall firewall add rule name="LinkTest UDP" dir=in action=allow protocol=UDP localport=7447
        """);
    return args.Length == 1 && args[0] is "--help" or "-h" or "/?" ? 0 : 1;
}

Updater.CleanupOldFiles();

if (opts.ShowVersion)
{
    Console.WriteLine(Updater.CurrentVersion);
    return 0;
}
if (opts.UpdateFrom != null || opts.UpdateNow)
    return await UpdateCommand.RunAsync(opts.UpdateFrom);

Clock.EnableHighResolutionTimer();
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

var host = new ServerHost(opts);
var updater = new ServerUpdateService(host, opts, UpdateChannel.Server, cts);
updater.UpdateAvailable += _ => { if (!opts.AutoUpdate) ServerHost.Log("Install it with 'LinkTestServer --update' or run with --auto-update."); };
if (!opts.NoUpdateCheck) _ = updater.RunAsync();

try
{
    await host.RunAsync(cts.Token);
}
catch (Exception e)
{
    ServerHost.Log("Fatal: " + e.Message);
    return 2;
}

if (updater.PendingPackage != null)
{
    ServerHost.Log("Installing the update and restarting…");
    try
    {
        // Must be the last thing this process does (see Updater.ApplyAndRestart).
        Updater.ApplyAndRestart(updater.PendingPackage, UpdateChannel.Server, args);
    }
    catch (Exception e)
    {
        Updater.Log("Server update failed: " + e);
        ServerHost.Log($"Update failed: {Updater.Describe(e)} (details in {Updater.LogPath})");
        return 5;
    }
}
return 0;

/// <summary>One-shot update used by --update and --update-from.</summary>
static class UpdateCommand
{
    public static async Task<int> RunAsync(string? fromZip)
    {
        var ch = UpdateChannel.Server;
        if (!Updater.CanSelfUpdate(ch, out var why)) { Console.Error.WriteLine("Cannot update: " + why); return 3; }
        try
        {
            string zip;
            if (fromZip != null)
            {
                zip = Path.Combine(Path.GetTempPath(), $"LinkTest-local-{Guid.NewGuid():N}.zip");
                File.Copy(fromZip, zip);   // Apply deletes its input; keep the user's file
            }
            else
            {
                Console.WriteLine($"Current version {Updater.CurrentVersion}; checking {Updater.ProjectUrl} …");
                var info = await Updater.CheckAsync(ch);
                if (info == null) { Console.WriteLine("Already up to date."); return 0; }
                Console.WriteLine($"Downloading {info.Tag} ({info.Size / 1e6:0.0} MB)…" + (info.Sha256 == null ? " (no checksum published)" : ""));
                zip = await Updater.DownloadAsync(info);
            }
            Updater.Apply(zip, ch);
            Console.WriteLine("Update installed. Start LinkTestServer again to run the new version.");
            return 0;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine("Update failed: " + e.Message);
            return 4;
        }
    }
}
