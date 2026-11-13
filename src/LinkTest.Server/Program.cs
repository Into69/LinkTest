using System.Net;
using LinkTest.Core;
using LinkTest.Server;

var opts = ServerOptions.Parse(args);
if (opts == null)
{
    Console.WriteLine($"""
        LinkTest server {Updater.CurrentVersion}

        Usage: LinkTestServer [--port N] [--bind ADDRESS] [--key SECRET] [--max-mbps N] [--max-sessions N]
                              [--auto-update | --no-update-check]
               LinkTestServer --update | --update-from PACKAGE.zip | --version

          --port N              TCP and UDP port to listen on (default 7447)
          --bind ADDRESS        Local address to bind (default: all IPv4 + IPv6)
          --key SECRET          Require clients to present this shared key
          --max-mbps N          Per-direction rate cap per session (default 1000)
          --max-sessions N      Concurrent sessions allowed (default 8)
          --auto-update         Check GitHub every 6 h; install new versions when no tests are running, then restart
          --no-update-check     Don't check for new versions at all
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
var updater = new ServerUpdateService(host, opts, cts);
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

if (updater.RestartRequested)
{
    ServerHost.Log("Restarting with the new version…");
    Updater.Restart(args);
}
return 0;

namespace LinkTest.Server
{
    sealed class ServerOptions
    {
        public int Port = Protocol.DefaultPort;
        public IPAddress? Bind;
        public string? Key;
        public double MaxMbps = 1000;
        public int MaxSessions = 8;
        public bool AutoUpdate, NoUpdateCheck, UpdateNow, ShowVersion;
        public string? UpdateFrom;

        public static ServerOptions? Parse(string[] args)
        {
            var o = new ServerOptions();
            if (args.Length == 1 && (args[0] is "--help" or "-h" or "/?")) return null;
            for (int i = 0; i < args.Length; i++)
            {
                string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value");
                try
                {
                    switch (args[i].ToLowerInvariant())
                    {
                        case "--port": o.Port = int.Parse(Next()); break;
                        case "--bind": o.Bind = IPAddress.Parse(Next()); break;
                        case "--key": o.Key = Next(); break;
                        case "--max-mbps": o.MaxMbps = double.Parse(Next(), System.Globalization.CultureInfo.InvariantCulture); break;
                        case "--max-sessions": o.MaxSessions = int.Parse(Next()); break;
                        case "--auto-update": o.AutoUpdate = true; break;
                        case "--no-update-check": o.NoUpdateCheck = true; break;
                        case "--update": o.UpdateNow = true; break;
                        case "--update-from": o.UpdateFrom = Next(); break;
                        case "--version": o.ShowVersion = true; break;
                        default: Console.Error.WriteLine($"Unknown option {args[i]}"); return null;
                    }
                }
                catch (Exception e) { Console.Error.WriteLine(e.Message); return null; }
            }
            return o.Port is > 0 and < 65536 ? o : null;
        }
    }

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

    /// <summary>Background update check; with --auto-update also installs when idle and requests a restart.</summary>
    sealed class ServerUpdateService
    {
        readonly ServerHost _host;
        readonly ServerOptions _opts;
        readonly CancellationTokenSource _shutdown;
        Version? _announced;

        public ServerUpdateService(ServerHost host, ServerOptions opts, CancellationTokenSource shutdown)
        {
            _host = host; _opts = opts; _shutdown = shutdown;
        }

        public bool RestartRequested { get; private set; }

        public async Task RunAsync()
        {
            var ct = _shutdown.Token;
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
                while (!ct.IsCancellationRequested)
                {
                    try
                    {
                        var info = await Updater.CheckAsync(UpdateChannel.Server, ct);
                        if (info != null && await HandleAsync(info, ct)) return;
                    }
                    catch (OperationCanceledException) { return; }
                    catch (Exception e) { ServerHost.Log("Update check failed: " + e.Message); }
                    await Task.Delay(_opts.AutoUpdate ? TimeSpan.FromHours(6) : TimeSpan.FromHours(24), ct);
                }
            }
            catch (OperationCanceledException) { }
        }

        async Task<bool> HandleAsync(UpdateInfo info, CancellationToken ct)
        {
            if (!_opts.AutoUpdate)
            {
                if (_announced != info.Version)
                    ServerHost.Log($"Update available: {info.Tag} (running {Updater.CurrentVersion}). Install with 'LinkTestServer --update' or run with --auto-update. {info.PageUrl}");
                _announced = info.Version;
                return false;
            }
            if (!Updater.CanSelfUpdate(UpdateChannel.Server, out var why))
            {
                if (_announced != info.Version) ServerHost.Log($"Update {info.Tag} available but cannot auto-install: {why}");
                _announced = info.Version;
                return false;
            }

            ServerHost.Log($"Downloading update {info.Tag}…");
            string zip = await Updater.DownloadAsync(info, null, ct);
            bool told = false;
            while (_host.SessionCount > 0)
            {
                if (!told) ServerHost.Log($"Update {info.Tag} ready; waiting for {_host.SessionCount} test session(s) to finish.");
                told = true;
                await Task.Delay(TimeSpan.FromSeconds(15), ct);
            }
            Updater.Apply(zip, UpdateChannel.Server);
            ServerHost.Log($"Update {info.Tag} installed.");
            RestartRequested = true;
            _shutdown.Cancel();
            return true;
        }
    }
}
