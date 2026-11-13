using System.Globalization;
using System.Net;
using LinkTest.Core;

namespace LinkTest.Server;

public sealed class ServerOptions
{
    public int Port { get; set; } = Protocol.DefaultPort;
    /// <summary>Local address to bind; null = all IPv4 + IPv6.</summary>
    public IPAddress? Bind { get; set; }
    public string? Key { get; set; }
    public double MaxMbps { get; set; } = 1000;
    public int MaxSessions { get; set; } = 8;
    /// <summary>Do not query ipinfo.io for client ISP / location (reverse DNS is still done).</summary>
    public bool NoIpLookup { get; set; }
    /// <summary>Refuse clients that don't encrypt the control connection (clients older than 1.9).</summary>
    public bool RequireTls { get; set; }
    /// <summary>Server certificate file (created on first start); null = the per-user default.</summary>
    public string? CertificatePath { get; set; }
    public bool AutoUpdate { get; set; }
    public bool NoUpdateCheck { get; set; }
    public bool UpdateNow { get; set; }
    public bool ShowVersion { get; set; }
    public string? UpdateFrom { get; set; }

    /// <summary>Parses command-line arguments; returns null (after printing a message) when invalid or --help.</summary>
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
                    case "--max-mbps": o.MaxMbps = double.Parse(Next(), CultureInfo.InvariantCulture); break;
                    case "--max-sessions": o.MaxSessions = int.Parse(Next()); break;
                    case "--auto-update": o.AutoUpdate = true; break;
                    case "--require-tls": o.RequireTls = true; break;
                    case "--cert": o.CertificatePath = Next(); break;
                    case "--no-ip-lookup": o.NoIpLookup = true; break;
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

/// <summary>Background update check; with AutoUpdate also installs when no tests run and requests a restart.</summary>
public sealed class ServerUpdateService
{
    readonly ServerHost _host;
    readonly ServerOptions _opts;
    readonly UpdateChannel _channel;
    readonly CancellationTokenSource _shutdown;
    Version? _announced;

    /// <param name="shutdown">Cancelled by the service after installing an update, to stop the server.</param>
    public ServerUpdateService(ServerHost host, ServerOptions opts, UpdateChannel channel, CancellationTokenSource shutdown)
    {
        _host = host; _opts = opts; _channel = channel; _shutdown = shutdown;
    }

    /// <summary>True once an update was downloaded and the server was stopped to install it.</summary>
    public bool RestartRequested { get; private set; }
    /// <summary>
    /// Downloaded update package. The program installs it with <see cref="Updater.ApplyAndRestart"/> after the
    /// server has stopped and must exit right afterwards.
    /// </summary>
    public string? PendingPackage { get; private set; }
    /// <summary>Latest available update that was not installed automatically.</summary>
    public UpdateInfo? Available { get; private set; }
    public event Action<UpdateInfo>? UpdateAvailable;

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
                    var info = await Updater.CheckAsync(_channel, ct);
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
        string why = "";
        if (!_opts.AutoUpdate || !Updater.CanSelfUpdate(_channel, out why))
        {
            if (_announced != info.Version)
            {
                Available = info;
                UpdateAvailable?.Invoke(info);
                ServerHost.Log(!_opts.AutoUpdate
                    ? $"Update available: {info.Tag} (running {Updater.CurrentVersion}). {info.PageUrl}"
                    : $"Update {info.Tag} available but cannot auto-install: {why}");
            }
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
        ServerHost.Log($"Update {info.Tag} downloaded; stopping the server to install it.");
        PendingPackage = zip;
        RestartRequested = true;
        _shutdown.Cancel();
        return true;
    }
}
