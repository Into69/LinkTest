using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using LinkTest.Core;

namespace LinkTest.Mobile;

/// <summary>
/// Traceroute hop probing on Android using the system ping tool via Java's process API.
/// (.NET's Ping launches the same tool through its own process code, which crashes the runtime when many
/// run in parallel; Java's Runtime.exec is safe.)
/// </summary>
static class AndroidPing
{
    static readonly Regex Reply = new(@"bytes from ([0-9a-fA-F:.]+).*?time=([\d.]+)\s*ms", RegexOptions.Compiled);
    static readonly Regex Exceeded = new(@"From ([0-9a-fA-F:.]+).*(Time to live exceeded|Hop limit|Time exceeded)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>ping6's hop-limit flag, once one has worked ("-h" on some builds, "-t" on others); null = not known yet.</summary>
    static volatile string? _v6HopFlag;

    public static void Register()
    {
        PathTracer.CustomProber = ProbeAsync;
        PathTracer.MaxParallelHops = 6;
    }

    static Task<HopProbeResult> ProbeAsync(IPAddress target, int ttl, int probes, int timeoutMs, CancellationToken ct) => Task.Run(() =>
    {
        bool v6 = target.AddressFamily == AddressFamily.InterNetworkV6;
        int waitSec = Math.Max(1, (int)Math.Ceiling(timeoutMs / 1000.0));
        int deadline = (int)Math.Ceiling(probes * 0.2) + waitSec + 1;
        string[] cmd =
        {
            v6 ? "/system/bin/ping6" : "/system/bin/ping", "-n", "-c", probes.ToString(CultureInfo.InvariantCulture), "-i", "0.2",
            "-W", waitSec.ToString(CultureInfo.InvariantCulture), "-w", deadline.ToString(CultureInfo.InvariantCulture),
            v6 ? _v6HopFlag ?? "-h" : "-t", ttl.ToString(CultureInfo.InvariantCulture), target.ToString(),
        };
        var result = Run(cmd, target, probes, ct);
        if (!v6 || _v6HopFlag != null) return result;
        // First IPv6 trace: find out which flag this ping6 takes, then remember it.
        if (result.Error == null) { _v6HopFlag = "-h"; return result; }
        cmd[^3] = "-t";
        result = Run(cmd, target, probes, ct);
        if (result.Error == null) _v6HopFlag = "-t";
        return result;
    }, ct);

    static HopProbeResult Run(string[] cmd, IPAddress target, int probes, CancellationToken ct)
    {
        Java.Lang.Process? proc = null;
        try
        {
            proc = Java.Lang.Runtime.GetRuntime()!.Exec(cmd)!;
            using var reader = new Java.IO.BufferedReader(new Java.IO.InputStreamReader(proc.InputStream));
            var counts = new Dictionary<IPAddress, int>();
            var rtts = new List<double>();
            int received = 0;
            bool reached = false;
            string? line;
            while ((line = reader.ReadLine()) != null && !ct.IsCancellationRequested)
            {
                var m = Reply.Match(line);
                if (m.Success && IPAddress.TryParse(m.Groups[1].Value.TrimEnd(':'), out var a))
                {
                    received++;
                    counts[a] = counts.GetValueOrDefault(a) + 1;
                    if (double.TryParse(m.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var ms)) rtts.Add(ms);
                    if (Protocol.Normalize(a).Equals(target)) reached = true;
                    continue;
                }
                var x = Exceeded.Match(line);
                if (x.Success && IPAddress.TryParse(x.Groups[1].Value.TrimEnd(':'), out var hop))
                {
                    received++;
                    counts[hop] = counts.GetValueOrDefault(hop) + 1;
                }
            }
            proc.WaitFor();
            var addr = counts.Count > 0 ? counts.MaxBy(kv => kv.Value).Key : null;
            return new HopProbeResult(addr, probes, Math.Min(received, probes),
                rtts.Count > 0 ? rtts.Average() : double.NaN, rtts.Count > 0 ? rtts.Max() : double.NaN, reached,
                proc.ExitValue() == 2 && received == 0 ? "ping failed" : null);
        }
        catch (Exception e)
        {
            return new HopProbeResult(null, probes, 0, double.NaN, double.NaN, false, e.Message);
        }
        finally
        {
            try { proc?.Destroy(); } catch { }
        }
    }
}
