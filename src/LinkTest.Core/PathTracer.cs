using System.Net;
using System.Net.NetworkInformation;
using System.Text;

namespace LinkTest.Core;

public sealed record TraceHop(int Ttl, IPAddress? Address, string? Hostname, int Sent, int Received, double AvgRttMs, double MaxRttMs)
{
    public double LossPct => Sent > 0 ? 100.0 * (Sent - Received) / Sent : 0;
}

/// <summary>An MTR-style trace: every hop probed several times, with loss and RTT per hop.</summary>
public sealed record TraceResult(DateTime At, string Reason, IPAddress Target, IReadOnlyList<TraceHop> Hops, bool ReachedTarget, string? Error = null)
{
    /// <summary>
    /// First hop from which loss persists all the way to the end of the path - where the problem most likely
    /// starts. Loss on a single intermediate hop that does not continue is usually ICMP rate limiting.
    /// </summary>
    public TraceHop? LossStartsAt
    {
        get
        {
            if (Hops.Count == 0) return null;
            int start = -1;
            for (int i = Hops.Count - 1; i >= 0; i--)
            {
                if (Hops[i].Received == 0 && i != Hops.Count - 1) continue;   // silent hop (ICMP filtered) - skip
                if (Hops[i].LossPct >= 20) start = i; else break;
            }
            return start >= 0 ? Hops[start] : null;
        }
    }

    public string Summary
    {
        get
        {
            if (Error != null) return $"Traceroute ({Reason}): {Error}";
            var l = LossStartsAt;
            return $"Traceroute ({Reason}): {Hops.Count} hops" + (ReachedTarget ? "" : ", target did not answer") +
                   (l != null ? $"; loss starts at hop {l.Ttl} ({l.Address?.ToString() ?? "*"}, {l.LossPct:0}%)" : "; no persistent loss along the path");
        }
    }

    public string Format()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{At:HH:mm:ss}  {Summary}");
        if (Error != null) return sb.ToString();
        sb.AppendLine($"  {"Hop",3}  {"Address",-40} {"Loss",6} {"Avg ms",8} {"Max ms",8}");
        foreach (var h in Hops)
        {
            string addr = h.Address == null ? "*" : h.Hostname != null ? $"{h.Address} ({h.Hostname})" : h.Address.ToString();
            if (addr.Length > 40) addr = addr[..39] + "…";
            sb.AppendLine($"  {h.Ttl,3}  {addr,-40} {(h.Received == 0 && h.Address == null ? "  -" : $"{h.LossPct,5:0}%")} " +
                          $"{(h.Received > 0 && double.IsFinite(h.AvgRttMs) ? h.AvgRttMs.ToString("0.0") : "-"),8} {(h.Received > 0 && double.IsFinite(h.MaxRttMs) ? h.MaxRttMs.ToString("0.0") : "-"),8}");
        }
        return sb.ToString();
    }
}

/// <summary>Result of probing one TTL: replying address, probes sent / answered, RTTs (NaN when unknown).</summary>
public sealed record HopProbeResult(IPAddress? Address, int Sent, int Received, double AvgRttMs, double MaxRttMs, bool ReachedTarget, string? Error);

/// <summary>ICMP traceroute using increasing TTLs (hops probed in parallel).</summary>
public static class PathTracer
{
    /// <summary>
    /// Platform-specific hop prober (target, ttl, probes, timeoutMs). Android sets this: .NET's Ping falls back to
    /// launching the ping tool there, which is unsafe to run many times in parallel.
    /// </summary>
    public static Func<IPAddress, int, int, int, CancellationToken, Task<HopProbeResult>>? CustomProber { get; set; }
    /// <summary>How many hops are probed at the same time.</summary>
    public static int MaxParallelHops { get; set; } = 30;

    public static async Task<TraceResult> TraceAsync(IPAddress target, string reason, int maxHops = 30, int probesPerHop = 5,
        int timeoutMs = 1000, bool resolveNames = true, CancellationToken ct = default)
    {
        target = Protocol.Normalize(target);
        var at = DateTime.Now;
        try
        {
            using var gate = new SemaphoreSlim(Math.Max(1, MaxParallelHops));
            async Task<HopProbe> Gated(int ttl)
            {
                await gate.WaitAsync(ct);
                try
                {
                    if (CustomProber == null) return await ProbeHopAsync(target, ttl, probesPerHop, timeoutMs, ct);
                    var r = await CustomProber(target, ttl, probesPerHop, timeoutMs, ct);
                    var hop = new TraceHop(ttl, r.Address, null, r.Sent, r.Received, r.AvgRttMs, r.MaxRttMs);
                    return new HopProbe(hop, r.ReachedTarget, r.Received == 0 ? r.Error : null);
                }
                finally { gate.Release(); }
            }
            var tasks = Enumerable.Range(1, maxHops).Select(Gated).ToArray();
            var hops = (await Task.WhenAll(tasks)).ToList();

            if (hops.All(h => h.Error != null))
                return new TraceResult(at, reason, target, Array.Empty<TraceHop>(), false, "ICMP is not available on this device: " + hops[0].Error);

            int reached = hops.FindIndex(h => h.ReachedTarget);
            int last = reached >= 0 ? reached : hops.FindLastIndex(h => h.Hop.Received > 0);
            var list = hops.Take(Math.Max(0, last) + 1).Select(h => h.Hop).ToList();

            if (resolveNames)
            {
                var names = list.Where(h => h.Address != null).Select(h => h.Address!).Distinct()
                    .ToDictionary(a => a, a => IpLookup.ReverseDnsAsync(a, ct));
                await Task.WhenAll(names.Values);
                list = list.Select(h => h.Address != null && names.TryGetValue(h.Address, out var n) ? h with { Hostname = n.Result } : h).ToList();
            }
            return new TraceResult(at, reason, target, list, reached >= 0);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e)
        {
            return new TraceResult(at, reason, target, Array.Empty<TraceHop>(), false, e.Message);
        }
    }

    sealed record HopProbe(TraceHop Hop, bool ReachedTarget, string? Error);

    static async Task<HopProbe> ProbeHopAsync(IPAddress target, int ttl, int probes, int timeoutMs, CancellationToken ct)
    {
        using var ping = new Ping();
        var options = new PingOptions(ttl, dontFragment: false);
        var counts = new Dictionary<IPAddress, int>();
        var rtts = new List<double>();
        int sent = 0;
        bool reached = false;
        string? error = null;
        for (int i = 0; i < probes && !ct.IsCancellationRequested; i++)
        {
            sent++;
            try
            {
                long t0 = Clock.NowUs;
                // Default payload (null): on Android .NET uses the system ping tool, which rejects custom payloads.
                var reply = await ping.SendPingAsync(target, TimeSpan.FromMilliseconds(timeoutMs), null, options, ct);
                double ms = (Clock.NowUs - t0) / 1000.0;
                if (reply.Status is IPStatus.Success or IPStatus.TtlExpired or IPStatus.TimeExceeded && reply.Address != null &&
                    !reply.Address.Equals(IPAddress.Any) && !reply.Address.Equals(IPAddress.IPv6Any))
                {
                    var a = Protocol.Normalize(reply.Address);
                    counts[a] = counts.GetValueOrDefault(a) + 1;
                    rtts.Add(reply.Status == IPStatus.Success && reply.RoundtripTime > 0 ? reply.RoundtripTime : ms);
                    if (reply.Status == IPStatus.Success && a.Equals(target)) reached = true;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception e)
            {
                error ??= (e.InnerException ?? e).Message;
            }
        }
        var addr = counts.Count > 0 ? counts.MaxBy(kv => kv.Value).Key : null;
        var hop = new TraceHop(ttl, addr, null, sent, rtts.Count, rtts.Count > 0 ? rtts.Average() : double.NaN, rtts.Count > 0 ? rtts.Max() : double.NaN);
        return new HopProbe(hop, reached, rtts.Count == 0 ? error : null);
    }
}
