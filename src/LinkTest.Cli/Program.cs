using System.Globalization;
using LinkTest.Core;
using LinkTest.Core.Tunnels;

// Headless client: LinkTestCli <server> [options]
var inv = CultureInfo.InvariantCulture;
Updater.CleanupOldFiles();
if (args.Length > 0 && args[0] is "--version") { Console.WriteLine(Updater.CurrentVersion); return 0; }
if (args.Length > 0 && args[0] is "--update" or "--update-from")
{
    var ch = UpdateChannel.Cli;
    if (!Updater.CanSelfUpdate(ch, out var why)) { Console.Error.WriteLine("Cannot update: " + why); return 3; }
    try
    {
        string zip;
        if (args[0] == "--update-from")
        {
            if (args.Length < 2) { Console.Error.WriteLine("--update-from needs a zip file"); return 1; }
            zip = Path.Combine(Path.GetTempPath(), $"LinkTest-local-{Guid.NewGuid():N}.zip");
            File.Copy(args[1], zip);
        }
        else
        {
            Console.WriteLine($"Current version {Updater.CurrentVersion}; checking {Updater.ProjectUrl} …");
            var info = await Updater.CheckAsync(ch);
            if (info == null) { Console.WriteLine("Already up to date."); return 0; }
            Console.WriteLine($"Downloading {info.Tag} ({info.Size / 1e6:0.0} MB)…");
            zip = await Updater.DownloadAsync(info);
        }
        Updater.Apply(zip, ch);
        Console.WriteLine("Update installed.");
        return 0;
    }
    catch (Exception e) { Console.Error.WriteLine("Update failed: " + e.Message); return 4; }
}
if (args.Length == 0 || args[0] is "-h" or "--help" or "/?")
{
    Console.WriteLine("""
        LinkTest command-line client

        Usage: LinkTestCli <server-ip-or-fqdn> [options]
               LinkTestCli --update | --update-from PACKAGE.zip | --version
          --port N           server port (default 7447)
          --udp | --tcp      transport (default udp)
          --up MBPS          client -> server rate (default 10)
          --down MBPS        server -> client rate (default 10)
          --size BYTES       packet / write size (default 1400)
          --profile NAME     constant|imix|bursty|random|voip|video (default constant)
          --streams N        parallel TCP streams (default 1)
          --dscp N           DSCP marking (needs Administrator)
          --time SEC         duration, 0 = until Ctrl+C (default 10)
          --key SECRET       server shared key
          --tunnels LIST     'default', 'all', 'none' or comma-separated name fragments (default: default)
          --loss PCT         artificial random loss in both directions (testing aid)
          --csv FILE         write per-second samples to a CSV file
        """);
    return 0;
}

string host = args[0];
int port = Protocol.DefaultPort;
string? key = null, csvPath = null, tunnelSel = "default";
double loss = 0;
var cfg = new TestConfig { DurationSec = 10 };
for (int i = 1; i < args.Length; i++)
{
    string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value");
    switch (args[i].ToLowerInvariant())
    {
        case "--port": port = int.Parse(Next()); break;
        case "--udp": cfg.Protocol = TransportKind.Udp; break;
        case "--tcp": cfg.Protocol = TransportKind.Tcp; break;
        case "--up": cfg.UpMbps = double.Parse(Next(), inv); break;
        case "--down": cfg.DownMbps = double.Parse(Next(), inv); break;
        case "--size": cfg.PacketSize = int.Parse(Next()); break;
        case "--profile": cfg.Profile = Enum.Parse<TrafficProfile>(Next(), true); break;
        case "--streams": cfg.Streams = int.Parse(Next()); break;
        case "--dscp": cfg.Dscp = int.Parse(Next()); break;
        case "--time": cfg.DurationSec = int.Parse(Next()); break;
        case "--key": key = Next(); break;
        case "--tunnels": tunnelSel = Next(); break;
        case "--loss": loss = double.Parse(Next(), inv); break;
        case "--csv": csvPath = Next(); break;
        default: Console.Error.WriteLine($"Unknown option {args[i]}"); return 1;
    }
}

var presets = TunnelPresets.CreateDefaults();
IEnumerable<TunnelProfile> tunnels = tunnelSel.ToLowerInvariant() switch
{
    "none" => Array.Empty<TunnelProfile>(),
    "default" => presets.Where(p => p.Enabled),
    "all" => presets.Select(p => { p.Enabled = true; return p; }),
    _ => presets.Where(p => tunnelSel.Split(',').Any(f => p.Name.Contains(f.Trim(), StringComparison.OrdinalIgnoreCase)))
                .Select(p => { p.Enabled = true; return p; }),
};

Clock.EnableHighResolutionTimer();
using var session = new ClientSession();
var ended = new TaskCompletionSource<string>();
session.Log += m => Console.WriteLine($"{DateTime.Now:HH:mm:ss} {m}");
session.Ended += r => ended.TrySetResult(r);

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

try
{
    await session.StartAsync(host, port, key, cfg, tunnels.ToList(), loss, cts.Token);
}
catch (Exception e)
{
    Console.Error.WriteLine("Failed: " + e.Message);
    return 2;
}

using var csv = csvPath != null ? new CsvLogger(csvPath) : null;
Console.WriteLine("  time |   tx Mbps |   up Mbps  loss%  jit ms | down Mbps  loss%  jit ms |  rtt ms (min/max)      | MOS");
while (!cts.IsCancellationRequested && !ended.Task.IsCompleted)
{
    try { await Task.WhenAny(Task.Delay(1000, cts.Token), ended.Task); } catch (OperationCanceledException) { break; }
    if (ended.Task.IsCompleted) break;
    var s = session.TakeSample();
    csv?.Write(s, session.TunnelSummaryLine());
    static string F(double v, string fmt) => double.IsFinite(v) ? v.ToString(fmt, CultureInfo.InvariantCulture) : "-";
    Console.WriteLine($"{s.ElapsedSec,6:0} | {F(s.TxMbps, "0.00"),9} | {F(s.UpMbps, "0.00"),9} {F(s.UpLossPct, "0.00"),6} {F(s.UpJitterMs, "0.00"),7}{(s.UpReportStale ? "*" : " ")}" +
                      $"| {F(s.DownMbps, "0.00"),9} {F(s.DownLossPct, "0.00"),6} {F(s.DownJitterMs, "0.00"),7} | " +
                      $"{F(s.RttMs, "0.00"),7} ({F(s.RttMinMs, "0.0")}/{F(s.RttMaxMs, "0.0")}){"",-6} | {F(s.Mos, "0.0")}");
    if (cfg.DurationSec > 0 && s.ElapsedSec >= cfg.DurationSec) break;
}

if (!ended.Task.IsCompleted) await session.StopAsync(cts.IsCancellationRequested ? "Interrupted" : "Duration reached");
return 0;
