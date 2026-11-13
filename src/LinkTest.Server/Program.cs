using System.Net;
using LinkTest.Core;
using LinkTest.Server;

var opts = ServerOptions.Parse(args);
if (opts == null)
{
    Console.WriteLine("""
        LinkTest server

        Usage: LinkTestServer [--port N] [--bind ADDRESS] [--key SECRET] [--max-mbps N] [--max-sessions N]

          --port N          TCP and UDP port to listen on (default 7447)
          --bind ADDRESS    Local address to bind (default: all IPv4 + IPv6)
          --key SECRET      Require clients to present this shared key
          --max-mbps N      Per-direction rate cap per session (default 1000)
          --max-sessions N  Concurrent sessions allowed (default 8)

        Windows Firewall (run once as Administrator):
          netsh advfirewall firewall add rule name="LinkTest TCP" dir=in action=allow protocol=TCP localport=7447
          netsh advfirewall firewall add rule name="LinkTest UDP" dir=in action=allow protocol=UDP localport=7447
        """);
    return args.Length == 0 || args.Contains("--help") || args.Contains("-h") ? 0 : 1;
}

Clock.EnableHighResolutionTimer();
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

var host = new ServerHost(opts);
try
{
    await host.RunAsync(cts.Token);
}
catch (Exception e)
{
    ServerHost.Log("Fatal: " + e.Message);
    return 2;
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
                        default: Console.Error.WriteLine($"Unknown option {args[i]}"); return null;
                    }
                }
                catch (Exception e) { Console.Error.WriteLine(e.Message); return null; }
            }
            return o.Port is > 0 and < 65536 ? o : null;
        }
    }
}
