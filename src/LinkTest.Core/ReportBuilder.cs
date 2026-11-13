using System.Globalization;
using System.Net;
using System.Text;

namespace LinkTest.Core;

/// <summary>
/// Builds a self-contained HTML report (inline CSS and SVG charts, no scripts or external files) for one test,
/// suitable for emailing to an ISP or vendor.
/// </summary>
public static class ReportBuilder
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    static string E(string? s) => WebUtility.HtmlEncode(s ?? "");
    static string N(double v, string fmt = "0.##") => double.IsFinite(v) ? v.ToString(fmt, Inv) : "–";

    /// <summary>Writes the HTML report (<paramref name="ext"/> "html") or the CSV ("csv") into a folder; returns the file path.</summary>
    public static string WriteFile(TestRecord r, string folder, string ext)
    {
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, r.SuggestedFileName(ext));
        File.WriteAllText(path, ext == "csv" ? r.ToCsv() : Build(r));
        return path;
    }

    public static string Build(TestRecord r)
    {
        var sb = new StringBuilder();
        sb.Append("""
            <!doctype html>
            <html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
            <title>@@TITLE@@</title>
            <style>
            :root{--bg:#f5f6f8;--card:#fff;--text:#1b1f24;--muted:#5f6b7a;--line:#e3e6ea;--accent:#0b5cad;--good:#1a7f37;--warn:#b35900;--bad:#c62828;
              --s1:#0b6fd6;--s2:#178a3e;--s3:#c9541a;--s4:#e3a77f;--s5:#7b5cc4}
            @media (prefers-color-scheme:dark){:root{--bg:#121417;--card:#1b1e23;--text:#e7e9ec;--muted:#9aa4b1;--line:#2c3138;--accent:#6aa9f0;--good:#4cc373;--warn:#f0a34a;--bad:#ff6b6b;
              --s1:#5aa2f0;--s2:#4cc373;--s3:#f08a52;--s4:#b98a6c;--s5:#a68ef0}}
            *{box-sizing:border-box}body{margin:0;background:var(--bg);color:var(--text);font:14px/1.45 "Segoe UI",system-ui,-apple-system,Roboto,sans-serif}
            main{max-width:1040px;margin:0 auto;padding:24px 16px 48px}
            h1{font-size:24px;margin:0 0 4px}h2{font-size:17px;margin:28px 0 10px;color:var(--accent)}
            .sub{color:var(--muted)}
            .cards{display:grid;grid-template-columns:repeat(auto-fill,minmax(150px,1fr));gap:10px;margin-top:16px}
            .card{background:var(--card);border:1px solid var(--line);border-radius:8px;padding:10px 12px}
            .card .k{font-size:12px;color:var(--muted)}.card .v{font-size:22px;font-weight:600;margin-top:2px}.card .d{font-size:12px;color:var(--muted)}
            .good{color:var(--good)}.warn{color:var(--warn)}.bad{color:var(--bad)}
            .panel{background:var(--card);border:1px solid var(--line);border-radius:8px;padding:12px;overflow-x:auto}
            table{border-collapse:collapse;width:100%;font-size:13px}th,td{text-align:left;padding:5px 8px;border-bottom:1px solid var(--line);white-space:nowrap}
            th{color:var(--muted);font-weight:600}td.n,th.n{text-align:right;font-variant-numeric:tabular-nums}
            pre{margin:0;font:12px/1.4 Consolas,ui-monospace,monospace;white-space:pre;overflow-x:auto}
            svg{width:100%;height:auto;display:block}svg text{fill:var(--muted);font:11px system-ui,sans-serif}
            .legend{display:flex;flex-wrap:wrap;gap:14px;font-size:12px;color:var(--muted);margin:0 0 6px}.legend i{display:inline-block;width:10px;height:10px;border-radius:2px;margin-right:5px;vertical-align:-1px}
            details summary{cursor:pointer;color:var(--accent)}footer{margin-top:32px;color:var(--muted);font-size:12px}
            </style></head><body><main>
            """);
        sb.Replace("@@TITLE@@", E($"LinkTest report {r.StartedAt.ToString("yyyy-MM-dd HH:mm", Inv)}"));

        sb.Append($"<h1>Link quality report</h1><div class=\"sub\">{E(r.StartedAt.ToString("dddd d MMMM yyyy, HH:mm:ss", Inv))} – {E(r.EndedAt.ToString("HH:mm:ss", Inv))}" +
                  $" · {Clock.FormatDuration(r.DurationSec)} · server {E(r.Server)} · {E(r.Mode)}</div>");

        // Key figures
        sb.Append("<div class=\"cards\">");
        Card(sb, "Upload (avg)", $"{N(r.UpMbpsAvg)} Mbps", $"target {N(r.Config.UpMbps)} Mbps");
        Card(sb, "Download (avg)", $"{N(r.DownMbpsAvg)} Mbps", $"target {N(r.Config.DownMbps)} Mbps");
        Card(sb, "Packet loss ↑ / ↓", $"{N(r.UpLossPct)} / {N(r.DownLossPct)} %", "UDP traffic", Level(Math.Max(Z(r.UpLossPct), Z(r.DownLossPct)), 0.5, 3));
        Card(sb, "Round-trip time", $"{N(r.RttAvgMs, "0.0")} ms", $"min {N(r.RttMinMs, "0.0")} / max {N(r.RttMaxMs, "0.0")}", Level(r.RttAvgMs, 100, 250));
        Card(sb, "Jitter ↑ / ↓", $"{N(r.JitterUpMs, "0.0")} / {N(r.JitterDownMs, "0.0")} ms", "", Level(Math.Max(Z(r.JitterUpMs), Z(r.JitterDownMs)), 10, 30));
        Card(sb, "Longest gap", $"{N(r.MaxGapMs, "0")} ms", "no packets arriving", Level(r.MaxGapMs, 250, 1000));
        if (r.BloatGrade != null)
            Card(sb, "Bufferbloat", r.BloatGrade, $"+{N(Math.Max(0, r.LoadedRttMs - r.IdleRttMs), "0.0")} ms under load",
                r.BloatGrade is "A+" or "A" ? "good" : r.BloatGrade is "B" or "C" ? "warn" : "bad");
        Card(sb, "VoIP MOS (avg)", N(r.MosAvg, "0.0"), "estimate, 1–4.5", r.MosAvg >= 4 ? "good" : r.MosAvg >= 3.6 ? "warn" : double.IsFinite(r.MosAvg) ? "bad" : null);
        if (r.Mtu is { Ok: true } m) Card(sb, "Path MTU", $"{m.PathMtu} B", $"up {m.UpPathMtu} / down {m.DownPathMtu}", m.PathMtu < 1500 && !m.Ipv6 ? "warn" : null);
        if (r.Tunnels.Count > 0)
        {
            int drops = r.Tunnels.Sum(t => t.Drops);
            Card(sb, "VPN tunnel drops", drops.ToString(Inv), $"{r.Tunnels.Count} simulated", drops == 0 ? "good" : "bad");
        }
        sb.Append("</div>");

        // Charts
        var s = r.Samples;
        if (s.T.Count > 1)
        {
            sb.Append("<h2>Throughput</h2><div class=\"panel\">");
            Chart(sb, s.T, "Mbps", (Series("Upload", "--s1", s.Up, false)), Series("Download", "--s2", s.Down, false));
            sb.Append("</div><h2>Latency</h2><div class=\"panel\">");
            Chart(sb, s.T, "ms", Series("RTT", "--s3", s.Rtt, false), Series("RTT max", "--s4", s.RttMax, true),
                Series("Jitter ↑", "--s1", s.JitUp, false), Series("Jitter ↓", "--s2", s.JitDown, false));
            sb.Append("</div><h2>Packet loss</h2><div class=\"panel\">");
            Chart(sb, s.T, "%", Series("Loss ↑", "--s1", s.LossUp, true), Series("Loss ↓", "--s5", s.LossDown, true));
            sb.Append("</div>");
        }

        // Settings and connection
        sb.Append("<h2>Test settings</h2><div class=\"panel\"><table>");
        Row(sb, "Mode", r.Mode);
        Row(sb, "Configured rates", $"up {N(r.Config.UpMbps)} Mbps, down {N(r.Config.DownMbps)} Mbps");
        if (!r.Config.DscpSimulate) Row(sb, "Traffic profile", $"{r.Config.Profile}, {r.Config.PacketSize} B packets" + (r.Config.Protocol != TransportKind.Udp ? $", {r.Config.Streams} TCP stream(s)" : ""));
        Row(sb, "DSCP", r.Config.DscpSimulate ? "Simulate (per-class marking)" : r.Config.Dscp > 0 ? DscpNames.Name(r.Config.Dscp) : "none");
        Row(sb, "Idle baseline", r.Config.BaselineSec > 0 ? $"{r.Config.BaselineSec} s (included in averages)" : "off");
        Row(sb, "Ended", r.EndReason);
        Row(sb, "Versions", $"client {r.AppVersion}, server {r.ServerVersion ?? "unknown"}");
        sb.Append("</table></div>");

        if (r.Connection is { } c)
        {
            sb.Append("<h2>Connection</h2><div class=\"panel\"><table>");
            Row(sb, "Server", c.Server);
            Row(sb, "Client address", c.LocalAddress);
            Row(sb, "Seen by server", c.SeenByServer ?? "–");
            Row(sb, "NAT", c.NatStatus);
            if (c.PublicInfo is { } p)
            {
                Row(sb, "Public IP", p.Ip + (p.Hostname != null ? $" ({p.Hostname})" : ""));
                Row(sb, "ISP / ASN", p.Org ?? "–");
                Row(sb, "Location (approx.)", p.Location.Length > 0 ? p.Location : "–");
            }
            sb.Append("</table></div>");
        }

        if (r.Classes.Count > 1)
        {
            sb.Append("<h2>Traffic classes</h2><div class=\"panel\"><table><tr><th>Class</th><th>DSCP</th><th>Proto</th><th class=n>Share</th>" +
                      "<th class=n>Up Mbps</th><th class=n>Up loss</th><th class=n>Down Mbps</th><th class=n>Down loss</th><th class=n>Jitter ↓</th><th class=n>RTT</th></tr>");
            foreach (var k in r.Classes)
            {
                bool udp = k.Transport == TransportKind.Udp;
                sb.Append($"<tr><td>{E(k.Name)}</td><td>{E(DscpNames.Name(k.Dscp))}</td><td>{(udp ? "UDP" : "TCP")}</td><td class=n>{N(k.SharePct, "0.#")}%</td>" +
                          $"<td class=n>{N(k.UpMbps)}</td><td class=n>{(udp ? N(k.UpLossPct) + "%" : "–")}</td><td class=n>{N(k.DownMbps)}</td>" +
                          $"<td class=n>{(udp ? N(k.DownLossPct) + "%" : "–")}</td><td class=n>{U(k.JitterDownMs, "0.##", " ms")}</td><td class=n>{U(k.RttAvgMs, "0.0", " ms")}</td></tr>");
            }
            sb.Append("</table></div>");
        }

        if (r.Tunnels.Count > 0)
        {
            sb.Append("<h2>VPN tunnel simulations</h2><div class=\"panel\"><table><tr><th>Tunnel</th><th>Verdict</th><th class=n>Availability</th>" +
                      "<th class=n>Drops</th><th class=n>Flaps</th><th class=n>Downtime</th><th class=n>Longest outage</th><th class=n>Avg recovery</th></tr>");
            foreach (var t in r.Tunnels)
            {
                string cls = t.Drops == 0 ? "good" : t.State == Tunnels.TunnelState.Up ? "warn" : "bad";
                sb.Append($"<tr><td>{E(t.Name)}</td><td class=\"{cls}\">{E(t.Verdict)}</td><td class=n>{N(t.AvailabilityPct)}%</td><td class=n>{t.Drops}</td>" +
                          $"<td class=n>{t.Flaps}</td><td class=n>{U(t.DownSec, "0.0", " s")}</td><td class=n>{U(t.LongestOutageSec, "0.0", " s")}</td><td class=n>{U(t.AvgRecoverSec, "0.0", " s")}</td></tr>");
            }
            sb.Append("</table></div>");
        }

        if (r.Mtu is { } mtu)
        {
            sb.Append($"<h2>Path MTU</h2><div class=\"panel\"><p style=\"margin:0 0 8px\">{E(mtu.Summary)}</p>");
            if (mtu.Ok)
            {
                sb.Append("<table><tr><th>Tunnel type</th><th class=n>Overhead</th><th class=n>Max tunnel MTU</th><th class=n>TCP MSS</th></tr>");
                foreach (var v in VpnOverhead.All)
                    sb.Append($"<tr><td>{E(v.Name)}</td><td class=n>{v.Overhead(mtu.Ipv6)} B</td><td class=n>{v.TunnelMtu(mtu.PathMtu, mtu.Ipv6)}</td><td class=n>{v.TcpMss(mtu.PathMtu, mtu.Ipv6)}</td></tr>");
                sb.Append("</table>");
            }
            sb.Append("</div>");
        }

        if (r.Traces.Count > 0)
        {
            sb.Append("<h2>Traceroutes</h2><div class=\"panel\"><pre>");
            foreach (var t in r.Traces) sb.Append(E(t.Format())).Append('\n');
            sb.Append(E("Loss on a single hop that does not continue to later hops is usually ICMP rate limiting, not real loss."));
            sb.Append("</pre></div>");
        }

        sb.Append("<h2>Details</h2><div class=\"panel\"><details><summary>Text summary</summary><pre>").Append(E(r.Summary)).Append("</pre></details>");
        sb.Append($"<details><summary>Event log ({r.Events.Count} lines)</summary><pre>").Append(E(string.Join("\n", r.Events))).Append("</pre></details></div>");

        sb.Append($"<footer>Generated by LinkTest {E(r.AppVersion)} · {E(Updater.ProjectUrl)} · test id {E(r.Id)}</footer></main></body></html>");
        return sb.ToString();
    }

    static double Z(double v) => double.IsFinite(v) ? v : 0;
    static string U(double v, string fmt, string unit) => double.IsFinite(v) ? v.ToString(fmt, Inv) + unit : "–";
    static string? Level(double v, double warn, double bad) => !double.IsFinite(v) ? null : v >= bad ? "bad" : v >= warn ? "warn" : "good";

    static void Card(StringBuilder sb, string k, string v, string d, string? cls = null) =>
        sb.Append($"<div class=\"card\"><div class=\"k\">{E(k)}</div><div class=\"v{(cls != null ? " " + cls : "")}\">{E(v)}</div><div class=\"d\">{E(d)}</div></div>");

    static void Row(StringBuilder sb, string k, string v) => sb.Append($"<tr><th>{E(k)}</th><td style=\"white-space:normal\">{E(v)}</td></tr>");

    sealed record ChartSeries(string Name, string ColorVar, List<double> Values, bool UseMax);
    static ChartSeries Series(string name, string color, List<double> values, bool useMax) => new(name, color, values, useMax);

    /// <summary>Line chart as inline SVG; long tests are bucketed to at most ~600 points (average, or max for spiky series).</summary>
    static void Chart(StringBuilder sb, List<double> t, string unit, params ChartSeries[] series)
    {
        const int W = 960, H = 220, L = 46, R = 10, T = 8, B = 24;
        int n = t.Count;
        int buckets = Math.Min(600, n);
        double tMax = Math.Max(1, t[^1]);

        var reduced = series.Select(s =>
        {
            var pts = new double[buckets];
            for (int b = 0; b < buckets; b++)
            {
                int from = b * n / buckets, to = Math.Max(from + 1, (b + 1) * n / buckets);
                double acc = s.UseMax ? double.NaN : 0; int cnt = 0;
                for (int i = from; i < to && i < s.Values.Count; i++)
                {
                    double v = s.Values[i];
                    if (!double.IsFinite(v)) continue;
                    if (s.UseMax) acc = double.IsNaN(acc) ? v : Math.Max(acc, v); else acc += v;
                    cnt++;
                }
                pts[b] = cnt == 0 ? double.NaN : s.UseMax ? acc : acc / cnt;
            }
            return pts;
        }).ToList();

        double max = 1;
        foreach (var p in reduced) foreach (var v in p) if (double.IsFinite(v) && v > max) max = v;
        max = Nice(max * 1.1);

        sb.Append("<div class=\"legend\">");
        foreach (var s in series) sb.Append($"<span><i style=\"background:var({s.ColorVar})\"></i>{E(s.Name)}</span>");
        sb.Append($"<span>({E(unit)})</span></div>");
        sb.Append($"<svg viewBox=\"0 0 {W} {H}\" role=\"img\">");
        double pw = W - L - R, ph = H - T - B;
        for (int i = 0; i <= 4; i++)
        {
            double y = T + ph - ph * i / 4;
            sb.Append($"<line x1=\"{L}\" x2=\"{W - R}\" y1=\"{y:0.#}\" y2=\"{y:0.#}\" stroke=\"var(--line)\"/>");
            sb.Append($"<text x=\"{L - 6}\" y=\"{y + 4:0.#}\" text-anchor=\"end\">{(max * i / 4).ToString(max < 10 ? "0.##" : "0", Inv)}</text>");
        }
        for (int i = 0; i <= 4; i++)
        {
            double sec = tMax * i / 4;
            double x = L + pw * i / 4;
            sb.Append($"<text x=\"{x:0.#}\" y=\"{H - 6}\" text-anchor=\"{(i == 0 ? "start" : i == 4 ? "end" : "middle")}\">{Clock.FormatDuration(sec)}</text>");
        }
        for (int si = 0; si < series.Length; si++)
        {
            var pts = reduced[si];
            var seg = new StringBuilder();
            void Flush()
            {
                if (seg.Length > 0)
                    sb.Append($"<polyline fill=\"none\" stroke=\"var({series[si].ColorVar})\" stroke-width=\"1.6\" stroke-linejoin=\"round\" points=\"{seg}\"/>");
                seg.Clear();
            }
            for (int b = 0; b < buckets; b++)
            {
                if (!double.IsFinite(pts[b])) { Flush(); continue; }
                double x = L + pw * (buckets == 1 ? 0 : (double)b / (buckets - 1));
                double y = T + ph - ph * Math.Clamp(pts[b] / max, 0, 1);
                seg.Append(x.ToString("0.#", Inv)).Append(',').Append(y.ToString("0.#", Inv)).Append(' ');
            }
            Flush();
        }
        sb.Append("</svg>");
    }

    static double Nice(double v)
    {
        if (v <= 0 || !double.IsFinite(v)) return 1;
        double exp = Math.Pow(10, Math.Floor(Math.Log10(v)));
        double f = v / exp;
        return (f <= 1 ? 1 : f <= 2 ? 2 : f <= 2.5 ? 2.5 : f <= 5 ? 5 : 10) * exp;
    }
}
