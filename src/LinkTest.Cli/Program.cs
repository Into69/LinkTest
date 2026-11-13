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
          --udp | --tcp | --mixed   transport (default udp); mixed = UDP and TCP at once
          --udp-share PCT    mixed mode: % of each rate carried by UDP (default 30)
          --up MBPS          client -> server rate (default 10)
          --down MBPS        server -> client rate (default 10)
          --size BYTES       packet / write size (default 1400)
          --profile NAME     constant|imix|bursty|random|voip|video (default constant)
          --streams N        parallel TCP streams (default 1)
          --dscp N|simulate  DSCP marking (needs Administrator); 'simulate' runs a 10-class enterprise
                             mix (voice EF, video AF41, ... scavenger CS1), each class measured separately
          --time SEC         duration, 0 = until Ctrl+C (default 10)
          --key SECRET       server shared key
          --tunnels LIST     'default', 'all', 'none' or comma-separated name fragments (default: default)
          --loss PCT         artificial random loss in both directions (testing aid)
          --csv FILE         write per-second samples to a CSV file
          --no-ip-lookup     don't query ipinfo.io for this client's public IP details
          --baseline SEC     idle seconds at the start for the bufferbloat grade (default 3, 0 = off)
          --no-mtu           skip the path-MTU test
          --no-trace         no automatic traceroutes
          --report FILE      write an HTML report (charts, tables, traceroutes) when the test ends
          --json FILE        save the full test record as JSON
        """);
    return 0;
}

string host = args[0];
int port = Protocol.DefaultPort;
string? key = null, csvPath = null, tunnelSel = "default", reportPath = null, jsonPath = null;
double loss = 0;
bool ipLookup = true, measureMtu = true, autoTrace = true;
var cfg = new TestConfig { DurationSec = 10 };
for (int i = 1; i < args.Length; i++)
{
    string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value");
    switch (args[i].ToLowerInvariant())
    {
        case "--port": port = int.Parse(Next()); break;
        case "--udp": cfg.Protocol = TransportKind.Udp; break;
        case "--tcp": cfg.Protocol = TransportKind.Tcp; break;
        case "--mixed": cfg.Protocol = TransportKind.Mixed; break;
        case "--udp-share": cfg.UdpSharePct = double.Parse(Next(), inv); break;
        case "--up": cfg.UpMbps = double.Parse(Next(), inv); break;
        case "--down": cfg.DownMbps = double.Parse(Next(), inv); break;
        case "--size": cfg.PacketSize = int.Parse(Next()); break;
        case "--profile": cfg.Profile = Enum.Parse<TrafficProfile>(Next(), true); break;
        case "--streams": cfg.Streams = int.Parse(Next()); break;
        case "--dscp":
            string d = Next();
            if (d.Equals("simulate", StringComparison.OrdinalIgnoreCase)) cfg.DscpSimulate = true;
            else cfg.Dscp = int.Parse(d);
            break;
        case "--time": cfg.DurationSec = int.Parse(Next()); break;
        case "--key": key = Next(); break;
        case "--tunnels": tunnelSel = Next(); break;
        case "--loss": loss = double.Parse(Next(), inv); break;
        case "--csv": csvPath = Next(); break;
        case "--no-ip-lookup": ipLookup = false; break;
        case "--baseline": cfg.BaselineSec = int.Parse(Next()); break;
        case "--no-mtu": measureMtu = false; break;
        case "--no-trace": autoTrace = false; break;
        case "--report": reportPath = Next(); break;
        case "--json": jsonPath = Next(); break;
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
session.TraceCompleted += t => { if (t.Error == null) Console.Write(t.Format()); };
session.MeasureMtu = measureMtu;
session.AutoTraceroute = autoTrace;

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

try
{
    await session.StartAsync(host, port, key, cfg, tunnels.ToList(), loss, cts.Token, ipLookup);
}
catch (Exception e)
{
    Console.Error.WriteLine("Failed: " + e.Message);
    return 2;
}

using var csv = csvPath != null ? new CsvLogger(csvPath) : null;
// Interactive: type "loss N" + Enter to change the artificial loss while the test runs.
_ = Task.Run(async () =>
{
    try
    {
        string? line;
        while ((line = await Console.In.ReadLineAsync()) != null)
        {
            var parts = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 && parts[0].Equals("loss", StringComparison.OrdinalIgnoreCase) &&
                double.TryParse(parts[1].TrimEnd('%'), NumberStyles.Float, inv, out var pct))
            {
                session.ArtificialLossPct = pct;
                Console.WriteLine($"{DateTime.Now:HH:mm:ss} Artificial loss changed to {session.ArtificialLossPct}%");
            }
            else if (parts.Length > 0) Console.WriteLine("Commands: loss <percent>");
        }
    }
    catch { }
});
Console.WriteLine("  time |   tx Mbps |   up Mbps  loss%  jit ms | down Mbps  loss%  jit ms |  rtt ms (min/max)      | MOS");
while (!cts.IsCancellationRequested && !ended.Task.IsCompleted)
{
    try { await Task.WhenAny(Task.Delay(1000, cts.Token), ended.Task); } catch (OperationCanceledException) { break; }
    if (ended.Task.IsCompleted) break;
    var s = session.TakeSample();
    csv?.Write(s, session.TunnelSummaryLine());
    if (s.Classes.Count > 2 && (int)Math.Round(s.ElapsedSec) % 5 == 0)
    {
        static string G(double v, string fmt) => double.IsFinite(v) ? v.ToString(fmt, CultureInfo.InvariantCulture) : "-";
        Console.WriteLine($"    {"class",-20} {"dscp",-10} {"proto",-5} {"up Mbps",8} {"loss%",6} {"down Mbps",9} {"loss%",6} {"jit ms",7} {"rtt ms",7}");
        foreach (var c in s.Classes)
            Console.WriteLine($"    {c.Name,-20} {DscpNames.Name(c.Dscp),-10} {c.Transport,-5} {G(c.UpMbps, "0.00"),8} {G(c.UpLossPct, "0.00"),6} " +
                              $"{G(c.DownMbps, "0.00"),9} {G(c.DownLossPct, "0.00"),6} {G(c.DownJitterMs, "0.00"),7} {G(c.RttMs, "0.00"),7}");
    }
    static string F(double v, string fmt) => double.IsFinite(v) ? v.ToString(fmt, CultureInfo.InvariantCulture) : "-";
    Console.WriteLine($"{s.ElapsedSec,6:0} | {F(s.TxMbps, "0.00"),9} | {F(s.UpMbps, "0.00"),9} {F(s.UpLossPct, "0.00"),6} {F(s.UpJitterMs, "0.00"),7}{(s.UpReportStale ? "*" : " ")}" +
                      $"| {F(s.DownMbps, "0.00"),9} {F(s.DownLossPct, "0.00"),6} {F(s.DownJitterMs, "0.00"),7} | " +
                      $"{F(s.RttMs, "0.00"),7} ({F(s.RttMinMs, "0.0")}/{F(s.RttMaxMs, "0.0")}){"",-6} | {F(s.Mos, "0.0")}" +
                      (s.Mixed ? $" | up udp {F(s.UpMbpsUdp, "0.0")} tcp {F(s.UpMbpsTcp, "0.0")}, down udp {F(s.DownMbpsUdp, "0.0")} tcp {F(s.DownMbpsTcp, "0.0")}, tcp rtt {F(s.RttTcpMs, "0.00")}" : ""));
    if (cfg.DurationSec > 0 && s.ElapsedSec >= cfg.DurationSec) break;
}

if (!ended.Task.IsCompleted) await session.StopAsync(cts.IsCancellationRequested ? "Interrupted" : "Duration reached");
if (session.Record is { } rec)
{
    try
    {
        if (reportPath != null) { File.WriteAllText(reportPath, ReportBuilder.Build(rec)); Console.WriteLine("Report written to " + Path.GetFullPath(reportPath)); }
        if (jsonPath != null) { File.WriteAllText(jsonPath, rec.ToJson()); Console.WriteLine("Record written to " + Path.GetFullPath(jsonPath)); }
    }
    catch (Exception e) { Console.Error.WriteLine("Could not write report: " + e.Message); }
}
return 0;
