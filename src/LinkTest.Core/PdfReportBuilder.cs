using System.Globalization;

namespace LinkTest.Core;

/// <summary>
/// PDF report on how well the link carried each type of simulated traffic: a scorecard per traffic type,
/// time within its requirements, a compliance timeline, VPN tunnel stability and the detailed statistics.
/// </summary>
public static class PdfReportBuilder
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    const double M = 40, W = PdfWriter.PageWidth - 2 * M, Bottom = PdfWriter.PageHeight - 50;

    static readonly PdfColor Text = PdfColor.Hex("1b1f24"), Muted = PdfColor.Hex("5f6b7a"), Line = PdfColor.Hex("d9dde3"),
        Accent = PdfColor.Hex("0b5cad"), Band = PdfColor.Hex("f2f4f7"),
        Good = PdfColor.Hex("1a7f37"), Warn = PdfColor.Hex("b35900"), Bad = PdfColor.Hex("c62828"), Grey = PdfColor.Hex("9aa4b1"),
        GoodBg = PdfColor.Hex("e3f4e6"), WarnBg = PdfColor.Hex("fdf0dc"), BadBg = PdfColor.Hex("fbe4e4"), GreyBg = PdfColor.Hex("eceff3"),
        S1 = PdfColor.Hex("0b6fd6"), S2 = PdfColor.Hex("178a3e"), S3 = PdfColor.Hex("c9541a");

    static string N(double v, string fmt = "0.##") => double.IsFinite(v) ? v.ToString(fmt, Inv) : "–";

    /// <summary>Side-by-side PDF of two tests (e.g. IPv4 vs IPv6).</summary>
    public static byte[] BuildComparison(Comparison c)
    {
        var a = c.RecA; var b = c.RecB;
        var pdf = new PdfWriter { Title = $"LinkTest {c.LabelA} vs {c.LabelB} {a.StartedAt:yyyy-MM-dd HH:mm}", Author = "LinkTest " + a.AppVersion };
        var f = new Flow(pdf, a);
        f.Page.Rect(0, 0, PdfWriter.PageWidth, 78, fill: PdfColor.Hex("0b1e3a"));
        f.Page.Text(M, 34, $"{c.LabelA} vs {c.LabelB}", 20, true, PdfColor.White);
        f.Page.Text(M, 52, $"{a.Server.Split(' ')[0]} · {a.Mode}", 10.5, false, PdfColor.Hex("c9d6e8"));
        f.Page.Text(PdfWriter.PageWidth - M, 34, $"{a.StartedAt:yyyy-MM-dd HH:mm}", 11, true, PdfColor.White, PdfAlign.Right);
        f.Y = 100;
        var head = PdfText.Wrap(c.Headline, W - 30, 11.5, true);
        double boxH = 22 + head.Count * 15 + 14;
        f.Page.Rect(M, f.Y, W, boxH, fill: Band);
        f.Page.Rect(M, f.Y, 5, boxH, fill: Accent);
        double y = f.Y + 20;
        foreach (var l in head) { f.Page.Text(M + 16, y, l, 11.5, true, Text); y += 15; }
        f.Page.Text(M + 16, y + 2, "The tests ran one after the other with the same settings; conditions on the link can change in between.", 8, false, Muted);
        f.Y += boxH + 14;

        f.Heading("Results");
        var cols = new (string h, double w, PdfAlign a)[] { ("", 175, PdfAlign.Left), (c.LabelA, 110, PdfAlign.Right), (c.LabelB, 110, PdfAlign.Right), ($"{c.LabelB} - {c.LabelA}", 120.28, PdfAlign.Right) };
        f.TableHeader(cols);
        foreach (var l in c.Lines)
        {
            f.Need(14);
            double x = M, ry = f.Y;
            string[] cells = { l.Name, l.A, l.B, l.Diff };
            for (int i = 0; i < cells.Length; i++)
            {
                var col = i == 1 && l.Better < 0 || i == 2 && l.Better > 0 ? Good : (PdfColor?)null;
                f.Cell(x, ry + 10, cols[i].w, cells[i], cols[i].a, 8, i == 0 || col != null, col);
                x += cols[i].w;
            }
            f.Page.Line(M, ry + 14, M + W, ry + 14, Line, 0.4);
            f.Y += 14;
        }
        f.Small("Green = clearly better (small differences are treated as alike).");

        int n = Math.Min(a.Samples.T.Count, b.Samples.T.Count);
        if (n > 1)
        {
            f.Heading("Over time");
            var t = a.Samples.T.Take(n).ToList();
            List<double> Cut(List<double> v) => v.Take(n).ToList();
            f.Chart("Download throughput (Mbps)", t, new[] { (c.LabelA, S1, Cut(a.Samples.Down)), (c.LabelB, S3, Cut(b.Samples.Down)) }, 0);
            f.Chart("Round-trip time (ms)", t, new[] { (c.LabelA, S1, Cut(a.Samples.Rtt)), (c.LabelB, S3, Cut(b.Samples.Rtt)) }, 0);
            static double Z(double v) => double.IsFinite(v) ? v : 0;
            List<double> Worst(SampleSeries s) => Enumerable.Range(0, n).Select(i => Math.Max(Z(s.LossUp[i]), Z(s.LossDown[i]))).ToList();
            f.Chart("Packet loss, worst direction (%)", t, new[] { (c.LabelA, S1, Worst(a.Samples)), (c.LabelB, S3, Worst(b.Samples)) }, 0);
        }
        f.Heading("Addresses");
        foreach (var (lbl, r) in new[] { (c.LabelA, a), (c.LabelB, b) })
        {
            f.Need(14);
            f.Page.Text(M, f.Y + 10, lbl, 8.5, true, Muted);
            f.Page.Text(M + 110, f.Y + 10, PdfText.Fit(r.Server + (r.Connection?.PublicInfo is { } p ? " · public " + p.Ip : ""), W - 110, 8.5, false), 8.5, false, Text);
            f.Y += 13;
        }
        f.Footers();
        return pdf.ToBytes();
    }

    public static byte[] Build(TestRecord r)
    {
        var pdf = new PdfWriter { Title = $"LinkTest traffic report {r.StartedAt:yyyy-MM-dd HH:mm}", Author = "LinkTest " + r.AppVersion };
        var f = new Flow(pdf, r);
        var classes = TrafficAssessment.Evaluate(r);

        // ---- Title
        f.Page.Rect(0, 0, PdfWriter.PageWidth, 78, fill: PdfColor.Hex("0b1e3a"));
        f.Page.Text(M, 34, "Link quality report", 20, true, PdfColor.White);
        f.Page.Text(M, 52, "How well the link carried each type of simulated traffic", 10.5, false, PdfColor.Hex("c9d6e8"));
        f.Page.Text(PdfWriter.PageWidth - M, 34, $"{r.StartedAt:yyyy-MM-dd HH:mm}", 11, true, PdfColor.White, PdfAlign.Right);
        f.Page.Text(PdfWriter.PageWidth - M, 52, $"Duration {Clock.FormatDuration(r.DurationSec)}", 10, false, PdfColor.Hex("c9d6e8"), PdfAlign.Right);
        f.Y = 100;

        // ---- Overall verdict
        var judged = classes.Where(a => a.Verdict is TrafficVerdict.Good or TrafficVerdict.Marginal or TrafficVerdict.Poor).ToList();
        var worst = judged.Count == 0 ? TrafficVerdict.NoData : judged.Max(a => a.Verdict);
        var (vc, vbg) = Colors(worst);
        var headline = PdfText.Wrap(TrafficAssessment.Headline(classes), W - 30, 12.5, true);
        var tunnelsLine = r.Tunnels.Count == 0 ? null
            : r.Tunnels.All(t => t.Drops == 0) ? $"All {r.Tunnels.Count} simulated VPN tunnel{(r.Tunnels.Count == 1 ? "" : "s")} stayed up for the whole test."
            : $"{r.Tunnels.Count(t => t.Drops > 0)} of {r.Tunnels.Count} simulated VPN tunnels dropped at least once ({r.Tunnels.Sum(t => t.Drops)} drops in total).";
        double boxH = 20 + headline.Count * 16 + (tunnelsLine != null ? 15 : 0) + 15;
        f.Page.Rect(M, f.Y, W, boxH, fill: vbg);
        f.Page.Rect(M, f.Y, 5, boxH, fill: vc);
        double y = f.Y + 22;
        foreach (var l in headline) { f.Page.Text(M + 16, y, l, 12.5, true, vc); y += 16; }
        if (tunnelsLine != null) { f.Page.Text(M + 16, y + 1, tunnelsLine, 9.5, false, Text); y += 15; }
        f.Page.Text(M + 16, y + 1, $"Server {r.Server} · {r.Mode} · {N(r.Config.UpMbps, "0.###")} Mbps up / {N(r.Config.DownMbps, "0.###")} Mbps down · ended: {r.EndReason}", 8.5, false, Muted);
        f.Y += boxH + 18;

        // ---- Scorecard
        f.Heading("Traffic scorecard");
        f.Para("Each type of traffic is judged against what it needs to work well (limits below). \"Within limits\" is the share of " +
               "seconds in which it met all of them after the idle baseline; delivered is the share of its target rate that arrived.", 8.5);
        var cols = new (string h, double w, PdfAlign a)[]
        {
            ("Traffic type", 100, PdfAlign.Left), ("Marking", 38, PdfAlign.Left), ("Proto", 26, PdfAlign.Left),
            ("Target Mbps", 50, PdfAlign.Right), ("Delivered", 50, PdfAlign.Right), ("Loss / retr.", 62, PdfAlign.Right),
            ("Jitter", 34, PdfAlign.Right), ("RTT", 34, PdfAlign.Right), ("Within limits", 50, PdfAlign.Right), ("Verdict", 71.28, PdfAlign.Center),
        };
        f.TableHeader(cols);
        foreach (var a in classes)
        {
            var s = a.Summary;
            string dscp = DscpNames.Name(a.Def.Dscp).Split(' ')[0];
            string target = $"{Mb(a.TargetUpMbps)} / {Mb(a.TargetDownMbps)}";
            string deliv = $"{N(a.DeliveredUpPct, "0")} / {N(a.DeliveredDownPct, "0")}%";
            string loss = a.IsUdp ? $"{N(s?.UpLossPct ?? double.NaN, "0.##")} / {N(s?.DownLossPct ?? double.NaN, "0.##")}%"
                                  : $"{N(a.RetransUpPct, "0.#")} / {N(a.RetransDownPct, "0.#")}%";
            string jit = a.IsUdp ? Ms(a.JitterMs) : "–";
            string rtt = Ms(s?.RttAvgMs ?? double.NaN);
            string within = N(a.WithinPct, "0.#") + (double.IsFinite(a.WithinPct) ? "%" : "");
            var (c, bg) = Colors(a.Verdict);
            f.Need(17);
            double ry = f.Y;
            f.Page.Line(M, ry + 17, M + W, ry + 17, Line);
            string[] cells = { $"{a.Name}", dscp, a.IsUdp ? "UDP" : "TCP", target, deliv, loss, jit, rtt, within };
            double x = M;
            for (int i = 0; i < cells.Length; i++)
            {
                var (_, w, al) = cols[i];
                f.Cell(x, ry + 11.5, w, cells[i], al, 7.6, i == 0);
                x += w;
            }
            f.Page.Rect(x + 4, ry + 3, cols[^1].w - 8, 11.5, fill: bg);
            f.Page.Text(x + cols[^1].w / 2, ry + 11.6, VerdictText(a.Verdict), 7.6, true, c, PdfAlign.Center);
            f.Y += 17;
        }
        f.Y += 4;
        f.Small("Loss / retr.: packet loss up / down for UDP; for TCP, the share of data the sender had to retransmit (TCP hides loss by resending). " +
                "Jitter and RTT are test averages; RTT for TCP includes queueing in the TCP stream.");

        // ---- Findings
        var notes = classes.Where(a => a.Verdict is TrafficVerdict.Marginal or TrafficVerdict.Poor && a.Reasons.Count > 0).ToList();
        if (notes.Count > 0)
        {
            f.Heading("What fell short");
            foreach (var a in notes)
            {
                var (c, _) = Colors(a.Verdict);
                f.Need(14);
                f.Page.Rect(M, f.Y + 3, 7, 7, fill: c);
                f.Page.Text(M + 12, f.Y + 10, $"{a.Name}{(a.Name == a.Req.Kind ? "" : $" ({a.Req.Kind})")} - {VerdictText(a.Verdict).ToLowerInvariant()}", 9, true, Text);
                f.Y += 14;
                foreach (var reason in a.Reasons) f.Bullet(reason);
                f.Y += 3;
            }
        }

        // ---- Timeline
        if (classes.Any(a => a.SecondsEvaluated > 0))
        {
            f.Heading("Within limits over time");
            f.Para("One strip per traffic type, left to right over the test: green = within its limits that second, red = over a limit, grey = not judged (idle baseline or no data).", 8.5);
            double labelW = 110, stripW = W - labelW - 45;
            int n = classes.Max(a => a.Seconds.Count);
            foreach (var a in classes.Where(a => !a.Req.Informational))
            {
                f.Need(15);
                f.Cell(M, f.Y + 9, labelW - 6, a.Name, PdfAlign.Left, 8, false);
                Strip(f.Page, M + labelW, f.Y + 1, stripW, 10, a.Seconds, n);
                f.Page.Text(M + W, f.Y + 9, N(a.WithinPct, "0.#") + (double.IsFinite(a.WithinPct) ? "%" : ""), 8, true, Colors(a.Verdict).c, PdfAlign.Right);
                f.Y += 14;
            }
            f.Page.Text(M + labelW, f.Y + 8, "0:00:00", 7, false, Muted);
            f.Page.Text(M + labelW + stripW, f.Y + 8, Clock.FormatDuration(r.Samples.T.Count > 0 ? r.Samples.T[^1] : r.DurationSec), 7, false, Muted, PdfAlign.Right);
            f.Y += 16;
        }

        // ---- Delivered rate per traffic type over time (steady classes) and the overall charts
        if (r.Samples.T.Count > 1)
        {
            f.Heading("Throughput, latency and loss");
            var t = r.Samples.T;
            string shade = r.WarmupSec > r.Config.BaselineSec + 0.5 ? "warm-up" : "idle";
            f.Chart("Throughput (Mbps)", t, new[] { ("Upload", S1, r.Samples.Up), ("Download", S2, r.Samples.Down) }, r.JudgeAfterSec, shade);
            f.Chart("Round-trip time (ms)", t, new[] { ("Average", S1, r.Samples.Rtt), ("Max", S3, r.Samples.RttMax) }, r.JudgeAfterSec, shade);
            f.Chart("Packet loss (%)", t, new[] { ("Upload", S1, r.Samples.LossUp), ("Download", S3, r.Samples.LossDown) }, r.JudgeAfterSec, shade);
        }

        // ---- Route
        if (RouteMap.Build(r, W, 300) is { } route)
        {
            f.Need(380);
            f.Heading("Route");
            f.Para(route.Summary + " Each second is coloured by link quality: poor = loss over 1%, RTT over 150 ms or a 1 s gap; " +
                   "fair = loss over 0.1%, RTT over 50 ms, jitter over 30 ms or a gap over 250 ms.", 8.5);
            double y0 = f.Y;
            f.Page.Rect(M, y0, W, route.Height, fill: Band);
            foreach (var run in route.Runs)
            {
                var c = run.Level switch { StatLevel.Good => Good, StatLevel.Fair => Warn, StatLevel.Poor => Bad, _ => Grey };
                f.Page.Polyline(run.Points.Select(p => (M + p.x, y0 + p.y)).ToList(), c, 2.6);
            }
            foreach (var (pt, label) in new[] { (route.Start, "Start"), (route.End, "End") })
            {
                f.Page.Rect(M + pt.x - 3.5, y0 + pt.y - 3.5, 7, 7, fill: PdfColor.White, stroke: Text, lineWidth: 1.2);
                f.Page.Text(M + pt.x + 7, y0 + pt.y + 3, label, 8, true, Text);
            }
            double sy = y0 + route.Height - 10;
            f.Page.Line(M + 12, sy, M + 12 + route.ScaleLength, sy, Text, 1.2);
            f.Page.Text(M + 12, sy - 4, route.ScaleText, 7, false, Muted);
            f.Page.Text(M + W - 8, y0 + 14, "N ^", 8, true, Muted, PdfAlign.Right);
            f.Y += route.Height + 8;
            double lx = M;
            foreach (var (lvl, name) in new[] { (StatLevel.Good, "good"), (StatLevel.Fair, "fair"), (StatLevel.Poor, "poor"), (StatLevel.Info, "no data") })
            {
                var c = lvl switch { StatLevel.Good => Good, StatLevel.Fair => Warn, StatLevel.Poor => Bad, _ => Grey };
                f.Page.Rect(lx, f.Y + 2, 8, 6, fill: c);
                f.Page.Text(lx + 11, f.Y + 8, name, 7.5, false, Muted);
                lx += 60;
            }
            f.Y += 16;
        }

        // ---- Tunnels
        if (r.Tunnels.Count > 0)
        {
            f.Need(140);   // keep the heading, its note and the first rows of the table together
            f.Heading("VPN tunnel simulations");
            f.Para("Each simulated tunnel ran its real keepalive / dead-peer-detection timers over the link: a tunnel drops when enough probes are lost in a row.", 8.5);
            var tc = new (string h, double w, PdfAlign a)[]
            {
                ("Tunnel", 150, PdfAlign.Left), ("Verdict", 125, PdfAlign.Left), ("Available", 55, PdfAlign.Right), ("Drops", 40, PdfAlign.Right),
                ("Flaps", 40, PdfAlign.Right), ("Downtime", 50, PdfAlign.Right), ("Longest", 55.28, PdfAlign.Right),
            };
            f.TableHeader(tc);
            foreach (var tn in r.Tunnels)
            {
                var c = tn.Drops == 0 ? Good : tn.AvailabilityPct >= 99 ? Warn : Bad;
                f.Row(tc, new[] { tn.Name, tn.Verdict, N(tn.AvailabilityPct, "0.##") + "%", tn.Drops.ToString(Inv), tn.Flaps.ToString(Inv),
                    N(tn.DownSec, "0.#") + " s", N(tn.LongestOutageSec, "0.#") + " s" }, colorCol: 1, color: c);
            }
        }

        // ---- Requirements
        f.Need(140);   // keep the heading, its note and the first rows of the table together
        f.Heading("Requirements used");
        var rc = new (string h, double w, PdfAlign a)[] { ("Traffic type", 120, PdfAlign.Left), ("Judged as", 95, PdfAlign.Left), ("Limits", 300.28, PdfAlign.Left) };
        f.TableHeader(rc);
        foreach (var a in classes) f.Row(rc, new[] { a.Name, a.Req.Kind, a.Req.LimitsText });
        f.Small("Based on ITU-T G.114 / Y.1541 and common enterprise QoS guidance (RFC 4594). Each second is judged on RTT, jitter and rate, and on loss " +
                $"averaged over the last {TrafficAssessment.LossWindowSec}-{TrafficAssessment.MaxLossWindowSec} s (long enough to cover about 2000 packets, so single lost packets on low-rate traffic don't count as failures). " +
                "Rates are judged per second only for steady traffic; bursty and variable-rate traffic (video, random, bursty profiles) is judged on its average. " +
                "Maintained = within limits at least 99% of the time and on average; marginal = 90-99% or slightly over a limit on average; not maintained = less than 90% or well over a limit.");

        // ---- Detailed statistics
        if (r.Detail is { Columns.Count: > 0 } d)
        {
            f.Need(140);   // keep the heading, its note and the first rows of the table together
            f.Heading("Detailed statistics");
            f.Para("UDP and TCP in both directions. Upload is measured by the server, download by the client; TCP figures come from the sending side's kernel.", 8.5);
            double first = 150, cw = (W - first) / d.Columns.Count;
            var dc = new[] { ("", first, PdfAlign.Left) }
                .Concat(d.Columns.Select(c => (c.Title.Replace("↑ ", "").Replace("↓ ", ""), cw, PdfAlign.Right))).ToArray();
            f.TableHeader(dc);
            string? section = null;
            foreach (var (sec, name, values, _) in d.Rows())
            {
                if (sec != section)
                {
                    section = sec;
                    f.Need(16);
                    f.Page.Text(M, f.Y + 12, sec, 8, true, Accent);
                    f.Y += 15;
                }
                f.Row(dc, new[] { name }.Concat(values.Select(v => v.Replace("≤ ", "≤"))).ToArray(), size: 7.3, height: 12.5);
            }
        }

        // ---- Setup and events
        f.Heading("Test setup");
        var setup = new List<(string, string)>
        {
            ("Server", r.Server + (r.ServerVersion != null ? $" (LinkTest {r.ServerVersion})" : "")),
            ("Client", "LinkTest " + r.AppVersion + (r.Connection?.PublicInfo?.Summary is { } pub ? " · " + pub : "")),
            ("Traffic", $"{r.Mode}, {N(r.Config.UpMbps, "0.###")} Mbps up / {N(r.Config.DownMbps, "0.###")} Mbps down, {r.Config.Profile} profile, {r.Config.PacketSize} B packets"),
            ("Started / ended", $"{r.StartedAt:yyyy-MM-dd HH:mm:ss} / {r.EndedAt:HH:mm:ss} ({r.EndReason})"),
            ("Idle baseline", r.Config.BaselineSec > 0 ? $"{r.Config.BaselineSec} s, bufferbloat grade {r.BloatGrade ?? "–"}" : "off"),
        };
        if (r.Locations.Any(l => l.IsValid)) setup.Add(("Device location", GeoFix.Describe(r.Locations)));
        if (r.Family != null) setup.Add(("Address family", r.Family));
        if (r.Connection?.Security is { } ctrlSec) setup.Add(("Control connection", ctrlSec));
        if (double.IsFinite(r.CapacityUpMbps) || double.IsFinite(r.CapacityDownMbps))
            setup.Add(("Capacity test", $"found up {Cap(r.CapacityUpMbps, r.CapacityUpAtLimit)}, down {Cap(r.CapacityDownMbps, r.CapacityDownAtLimit)}; " +
                                        $"tested at {N(r.Config.UpMbps, "0.###")} / {N(r.Config.DownMbps, "0.###")} Mbps after a {N(r.WarmupSec, "0")} s ramp-up (left out of the judgement)"));
        foreach (var ch in r.IpChanges) setup.Add(($"Public IP changed {ch.At:HH:mm:ss}", $"{ch.From} -> {ch.To}: {ch.Details}"));
        if (r.Mtu is { } mtu) setup.Add(("Path MTU", mtu.Summary));
        foreach (var tr in r.Traces.Where(x => x.Error == null).Take(4)) setup.Add(($"Traceroute {tr.At:HH:mm:ss}", tr.Summary));
        foreach (var (k, v) in setup)
        {
            var lines = PdfText.Wrap(v, W - 110, 8.5, false);
            f.Need(12 * lines.Count + 2);
            f.Page.Text(M, f.Y + 10, k, 8.5, true, Muted);
            foreach (var l in lines) { f.Page.Text(M + 110, f.Y + 10, l, 8.5, false, Text); f.Y += 12; }
        }

        var events = r.Events.Where(e => e.Contains("DOWN") || e.Contains(" UP ") || e.Contains("] UP") || e.Contains("stall", StringComparison.OrdinalIgnoreCase)
                                       || e.Contains("Traceroute") || e.Contains("lost", StringComparison.OrdinalIgnoreCase) || e.Contains("error", StringComparison.OrdinalIgnoreCase))
                             .Take(40).ToList();
        if (events.Count > 0)
        {
            f.Heading("Notable events");
            foreach (var e in events) { f.Need(11); f.Cell(M, f.Y + 9, W, e, PdfAlign.Left, 7.5, false); f.Y += 10.5; }
        }

        f.Footers();
        return pdf.ToBytes();
    }

    static string Ms(double v) => !double.IsFinite(v) ? "–" : (v >= 10 ? v.ToString("0", Inv) : v >= 1 ? v.ToString("0.#", Inv) : v.ToString("0.##", Inv)) + " ms";

    static string Cap(double v, bool atLimit) => double.IsFinite(v) ? $"{(atLimit ? "at least " : "")}{N(v, "0.##")} Mbps" : "not tested";

    static string Mb(double v) => !double.IsFinite(v) ? "–" : v >= 10 ? v.ToString("0", Inv) : v >= 1 ? v.ToString("0.#", Inv) : v.ToString("0.##", Inv);

    static string VerdictText(TrafficVerdict v) => v switch
    {
        TrafficVerdict.Good => "Maintained", TrafficVerdict.Marginal => "Marginal", TrafficVerdict.Poor => "Not maintained",
        TrafficVerdict.Info => "Info only", _ => "No data",
    };

    static (PdfColor c, PdfColor bg) Colors(TrafficVerdict v) => v switch
    {
        TrafficVerdict.Good => (Good, GoodBg), TrafficVerdict.Marginal => (Warn, WarnBg), TrafficVerdict.Poor => (Bad, BadBg), _ => (Grey, GreyBg),
    };

    /// <summary>Compliance strip: consecutive seconds of the same state drawn as one rectangle.</summary>
    static void Strip(PdfPage p, double x, double y, double w, double h, List<bool?> sec, int n)
    {
        p.Rect(x, y, w, h, fill: GreyBg);
        if (n == 0) return;
        double step = w / n;
        int i = 0;
        while (i < sec.Count)
        {
            int j = i;
            while (j < sec.Count && sec[j] == sec[i]) j++;
            var c = sec[i] switch { true => Good, false => Bad, _ => Grey };
            // Keep isolated bad seconds visible on long tests.
            double width = Math.Max((j - i) * step, sec[i] == false ? 0.8 : 0);
            p.Rect(x + i * step, y, Math.Min(width, w - i * step), h, fill: c);
            i = j;
        }
    }

    /// <summary>Top-to-bottom layout with automatic page breaks.</summary>
    sealed class Flow
    {
        readonly PdfWriter _pdf;
        readonly TestRecord _r;
        public PdfPage Page;
        public double Y;

        public Flow(PdfWriter pdf, TestRecord r) { _pdf = pdf; _r = r; Page = pdf.AddPage(); Y = M; }

        public void Need(double h)
        {
            if (Y + h <= Bottom) return;
            Page = _pdf.AddPage();
            Y = M;
        }

        public void Heading(string s)
        {
            Need(75);   // keep a heading with at least a few lines of what follows
            Y += 10;
            Page.Text(M, Y + 12, s, 13, true, Accent);
            Page.Line(M, Y + 17, M + W, Y + 17, Accent, 0.8);
            Y += 25;
        }

        public void Para(string s, double size = 9)
        {
            foreach (var l in PdfText.Wrap(s, W, size, false)) { Need(size + 4); Page.Text(M, Y + size, l, size, false, Muted); Y += size + 3.5; }
            Y += 4;
        }

        public void Small(string s) => Para(s, 7.5);

        public void Bullet(string s)
        {
            var lines = PdfText.Wrap(s, W - 24, 8.5, false);
            Need(lines.Count * 11.5);
            Page.Text(M + 14, Y + 9, "•", 8.5, false, Muted);
            foreach (var l in lines) { Page.Text(M + 24, Y + 9, l, 8.5, false, Text); Y += 11.5; }
        }

        public void Cell(double x, double baseline, double w, string s, PdfAlign a, double size, bool bold, PdfColor? color = null)
        {
            s = PdfText.Fit(s, w - 6, size, bold);
            double tx = a switch { PdfAlign.Right => x + w - 3, PdfAlign.Center => x + w / 2, _ => x + 3 };
            Page.Text(tx, baseline, s, size, bold, color ?? Text, a);
        }

        (string h, double w, PdfAlign a)[]? _header;

        public void TableHeader((string h, double w, PdfAlign a)[] cols)
        {
            _header = cols;
            Need(40);
            DrawHeader();
        }

        void DrawHeader()
        {
            Page.Rect(M, Y, W, 15, fill: Band);
            double x = M;
            foreach (var (h, w, a) in _header!) { Cell(x, Y + 10.5, w, h, a, 7.4, true, Muted); x += w; }
            Y += 15;
        }

        public void Row((string h, double w, PdfAlign a)[] cols, string[] cells, int colorCol = -1, PdfColor? color = null, double size = 7.8, double height = 14)
        {
            if (Y + height > Bottom) { Need(height + 20); if (_header != null) DrawHeader(); }
            double x = M;
            for (int i = 0; i < cols.Length && i < cells.Length; i++)
            {
                Cell(x, Y + height - 4, cols[i].w, cells[i], cols[i].a, size, i == 0, i == colorCol ? color : null);
                x += cols[i].w;
            }
            Page.Line(M, Y + height, M + W, Y + height, Line, 0.4);
            Y += height;
        }

        public void Chart(string title, List<double> t, (string name, PdfColor color, List<double> data)[] series, double baselineSec, string shadeLabel = "idle")
        {
            const double h = 110;
            Need(h + 30);
            Page.Text(M, Y + 10, title, 9, true, Text);
            double lx = M + W;
            foreach (var (name, color, _) in series.Reverse())
            {
                double tw = PdfText.Width(name, 7.5, false);
                Page.Text(lx, Y + 10, name, 7.5, false, Muted, PdfAlign.Right);
                Page.Rect(lx - tw - 11, Y + 4, 8, 6, fill: color);
                lx -= tw + 22;
            }
            double px = M + 34, py = Y + 18, pw = W - 34, ph = h - 18;
            double max = 0;
            foreach (var (_, _, d) in series) foreach (var v in d) if (double.IsFinite(v) && v > max) max = v;
            max = Nice(max * 1.1);
            for (int i = 0; i <= 4; i++)
            {
                double gy = py + ph - ph * i / 4;
                Page.Line(px, gy, px + pw, gy, Line, 0.4);
                Page.Text(px - 4, gy + 2.5, (max * i / 4).ToString(max < 4 ? "0.##" : "0", Inv), 6.5, false, Muted, PdfAlign.Right);
            }
            double t0 = t[0], t1 = Math.Max(t[^1], t0 + 1);
            if (baselineSec > 0 && baselineSec > t0)
            {
                double bx = px + pw * Math.Min(1, (baselineSec - t0) / (t1 - t0));
                Page.Rect(px, py, bx - px, ph, fill: PdfColor.Hex("f4f6f9"));
                Page.Text(px + 3, py + 8, shadeLabel, 6.5, false, Muted);
            }
            foreach (var (_, color, d) in series)
            {
                var pts = new List<(double, double)>(t.Count);
                for (int i = 0; i < t.Count && i < d.Count; i++)
                    pts.Add(double.IsFinite(d[i]) ? (px + pw * (t[i] - t0) / (t1 - t0), py + ph - ph * Math.Clamp(d[i] / max, 0, 1)) : (double.NaN, double.NaN));
                Page.Polyline(pts, color, 0.9);
            }
            Page.Rect(px, py, pw, ph, stroke: Line);
            Page.Text(px, py + ph + 9, Clock.FormatDuration(t0), 6.5, false, Muted);
            Page.Text(px + pw, py + ph + 9, Clock.FormatDuration(t1), 6.5, false, Muted, PdfAlign.Right);
            Y += h + 18;
        }

        static double Nice(double v)
        {
            if (v <= 0 || !double.IsFinite(v)) return 1;
            double e = Math.Pow(10, Math.Floor(Math.Log10(v))), m = v / e;
            return (m <= 1 ? 1 : m <= 2 ? 2 : m <= 2.5 ? 2.5 : m <= 5 ? 5 : 10) * e;
        }

        public void Footers()
        {
            int n = _pdf.Pages.Count;
            for (int i = 0; i < n; i++)
            {
                var p = _pdf.Pages[i];
                p.Line(M, PdfWriter.PageHeight - 34, M + W, PdfWriter.PageHeight - 34, Line, 0.4);
                p.Text(M, PdfWriter.PageHeight - 22, $"LinkTest {_r.AppVersion} · {_r.Server} · {_r.StartedAt:yyyy-MM-dd HH:mm}", 7, false, Muted);
                p.Text(M + W, PdfWriter.PageHeight - 22, $"Page {i + 1} of {n}", 7, false, Muted, PdfAlign.Right);
            }
        }
    }
}
