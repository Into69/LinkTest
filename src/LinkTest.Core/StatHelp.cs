using System.Text;

namespace LinkTest.Core;

public enum StatLevel { Good, Fair, Poor, Info }

/// <summary>Explanation of one statistic: what it is, what to aim for, and how to read its values.</summary>
public sealed record StatInfo(string Title, string What, string Ideal, IReadOnlyList<(StatLevel level, string text)> Ranges)
{
    /// <summary>Plain-text help (dialogs, alerts).</summary>
    public string ToText()
    {
        var sb = new StringBuilder(What);
        if (Ideal.Length > 0) sb.Append("\n\nIdeal: ").Append(Ideal);
        if (Ranges.Count > 0)
        {
            sb.Append('\n');
            foreach (var (level, text) in Ranges) sb.Append('\n').Append(LevelName(level)).Append(": ").Append(text);
        }
        return sb.ToString();
    }

    /// <summary>One-line summary for hover tooltips.</summary>
    public string Short => Ideal.Length > 0 ? $"{What}\nIdeal: {Ideal}" : What;

    public static string LevelName(StatLevel l) => l switch
    {
        StatLevel.Good => "Good", StatLevel.Fair => "Fair", StatLevel.Poor => "Poor", _ => "Note",
    };
}

/// <summary>
/// Help texts for every statistic the GUIs show. Keys: <c>tile.*</c> (big numbers), <c>chart.*</c>, <c>class.*</c>
/// (traffic class columns), <c>tunnel.*</c>, <c>detail.*</c> (Statistics rows, by row name), <c>server.*</c>
/// (server session columns). Ideal values are for a healthy wired / fibre business link; mobile, Wi-Fi and
/// satellite links are naturally worse on latency and jitter.
/// </summary>
public static class StatHelp
{
    static (StatLevel, string) G(string t) => (StatLevel.Good, t);
    static (StatLevel, string) F(string t) => (StatLevel.Fair, t);
    static (StatLevel, string) P(string t) => (StatLevel.Poor, t);
    static (StatLevel, string) I(string t) => (StatLevel.Info, t);

    static readonly Dictionary<string, StatInfo> Items = new(StringComparer.OrdinalIgnoreCase);
    static readonly Dictionary<string, List<string>> Groups = new(StringComparer.OrdinalIgnoreCase);

    static void Add(string group, string key, string title, string what, string ideal, params (StatLevel, string)[] ranges)
    {
        Items[key] = new StatInfo(title, what, ideal, ranges);
        if (!Groups.TryGetValue(group, out var list)) Groups[group] = list = new List<string>();
        list.Add(key);
    }

    public static StatInfo? Get(string key) => Items.TryGetValue(key, out var i) ? i : null;

    /// <summary>Help for a row of the Statistics table (by its row name).</summary>
    public static StatInfo? Detail(string rowName) => Get("detail." + rowName);

    /// <summary>All entries of a group in display order: "tiles", "charts", "classes", "tunnels", "detail", "server".</summary>
    public static IReadOnlyList<StatInfo> Group(string group) =>
        Groups.TryGetValue(group, out var keys) ? keys.Select(k => Items[k]).ToList() : Array.Empty<StatInfo>();

    /// <summary>Plain-text help for a whole group (one block per entry).</summary>
    public static string GroupText(string group) => string.Join("\n\n", Group(group).Select(i => $"■ {i.Title}\n{i.ToText()}"));

    static StatHelp()
    {
        // ---------------------------------------------------------------- big-number tiles
        Add("tiles", "tile.upload", "Upload (client → server)",
            "Throughput the server received from this client in the last second. The small line shows what the client actually sent; a gap between the two is loss or a rate limit on the way up.",
            "the rate you asked for (within a few percent)",
            G("95% or more of the requested upload rate"), F("80-95%: congestion, shaping or a busy CPU"), P("under 80%: the link or a policer can't carry the rate"),
            I("TCP slows itself down when there is loss, so a low TCP figure often means loss further along the path"));
        Add("tiles", "tile.download", "Download (server → client)",
            "Throughput this client received from the server in the last second. The small line shows what the server sent.",
            "the rate you asked for (within a few percent)",
            G("95% or more of the requested download rate"), F("80-95%"), P("under 80%: the link or a policer can't carry the rate"));
        Add("tiles", "tile.rtt", "Round-trip time",
            "Time for a packet to reach the server and come back, measured on the test traffic itself (so it includes any queueing the load causes). Min / max are over the last second.",
            "under 50 ms; steady, without spikes",
            G("under 50 ms (fibre / cable / DSL in-country: typically 5-30 ms)"), F("50-150 ms (long distance, 4G/5G, LEO satellite)"),
            P("over 150 ms: calls start to feel laggy; over 300 ms conversations talk over each other (geostationary satellite is ~600 ms)"));
        Add("tiles", "tile.jitter", "Jitter ↑ / ↓",
            "How much the packet delay varies from one packet to the next (RFC 3550 smoothed jitter), upload / download. Voice and video use a jitter buffer to hide it, at the cost of extra delay.",
            "under 10 ms",
            G("under 10 ms"), F("10-30 ms: real-time apps cope, with a larger jitter buffer"), P("over 30 ms: choppy voice, frozen video"));
        Add("tiles", "tile.loss", "Packet loss ↑ / ↓",
            "Share of UDP test packets that never arrived in the last second, upload / download. The line below is the total for the whole test.",
            "0%",
            G("0-0.1%"), F("0.1-1%: noticeable in calls, TCP slows down"), P("over 1%: voice and video degrade badly; TCP throughput drops sharply"),
            I("TCP-only tests show 0 here because TCP resends lost data: see TCP retransmits instead"));
        Add("tiles", "tile.gap", "Longest gap ↑ / ↓",
            "Longest time without receiving any packet in the last ~2 seconds, upload / download. Packets (or 50 ms heartbeats) arrive continuously, so a long gap is a stall or a short outage.",
            "under 100 ms",
            G("under 100 ms"), F("100-250 ms: brief stall, audible in calls"),
            P("over 250 ms; a gap of 1 s or more drops calls and can trip VPN dead-peer detection (BFD in under 1 s)"));
        Add("tiles", "tile.retrans", "TCP retransmits ↑ / ↓",
            "TCP data the senders had to send again in the last second (KB/s), and the number of retransmission timeouts (RTOs). Retransmission is how TCP recovers from loss, so it reveals loss on TCP traffic.",
            "0 KB/s and 0 RTOs",
            G("none, or under 0.1% of the throughput"), F("under 1% of the throughput"), P("over 1%, or repeated RTOs: each RTO is a stall of at least 200 ms"));
        Add("tiles", "tile.mos", "VoIP MOS (estimated)",
            "Estimated call quality on a 1-5 scale (ITU-T E-model, simplified) from the measured RTT, jitter and loss: what a G.711 phone call over this link would sound like.",
            "4.0 or higher (4.4 is the best G.711 can score)",
            G("4.0-4.4: toll quality, users satisfied"), F("3.6-4.0: some users dissatisfied"), P("under 3.6: many users dissatisfied; under 3.1 most are"));
        Add("tiles", "tile.bloat", "Bufferbloat",
            "Extra delay the load adds: round-trip time while the test is sending, compared with the idle round-trip time measured in the baseline at the start. Large buffers in modems and routers cause it.",
            "A+ or A (under 30 ms extra)",
            G("A+ under 5 ms, A under 30 ms extra"), F("B under 60 ms, C under 200 ms: calls suffer while someone uploads or downloads"),
            P("D under 400 ms, F 400 ms or more: the link becomes unusable for real-time traffic under load (enable SQM / smart queueing)"));
        Add("tiles", "tile.elapsed", "Elapsed",
            "Time since the test started, and its planned duration.",
            "long enough to catch intermittent problems: 10-15 min for a check, hours or overnight for a link that drops now and then");

        // ---------------------------------------------------------------- charts
        Add("charts", "chart.throughput", "Throughput chart",
            "Upload (received by the server), download (received by this client) and what the client sent, one point per second.",
            "flat lines at the requested rates",
            G("steady lines close to the requested rates"), F("dips or saw-tooth: congestion or TCP backing off"), P("drops to zero: outages"));
        Add("charts", "chart.latency", "Latency chart",
            "Round-trip time (average and maximum per second), jitter in each direction and, in mixed mode, the RTT seen on the TCP streams.",
            "a low, flat RTT line with little gap between average and max",
            G("RTT flat and low; jitter near the bottom"), F("max spiking above the average: queueing"), P("RTT climbing under load: bufferbloat"));
        Add("charts", "chart.loss", "Packet loss chart",
            "Share of UDP packets lost each second, upload and download.",
            "flat at 0",
            G("0 almost everywhere"), F("occasional small spikes"), P("frequent spikes or a raised baseline"));

        // ---------------------------------------------------------------- traffic classes table
        Add("classes", "class.class", "Class", "The traffic type being simulated (with DSCP Simulate: an enterprise mix from voice to scavenger).", "");
        Add("classes", "class.dscp", "DSCP", "The QoS marking on the class's packets. Networks that honour it should protect EF (voice) and AF4x (video) first.", "",
            I("marking needs Administrator on Windows; many internet links ignore or clear it"));
        Add("classes", "class.proto", "Proto", "Transport of the class: UDP (loss is visible) or TCP (loss is hidden by retransmission).", "");
        Add("classes", "class.share", "Share", "Part of the upload and download rate this class carries.", "");
        Add("classes", "class.upMbps", "Up Mbps", "Upload throughput of this class, as received by the server.", "its share of the upload rate",
            G("95% or more of its share"), F("80-95%"), P("under 80%"));
        Add("classes", "class.upLoss", "Up loss %", "UDP packets of this class lost on the way to the server in the last second.", "0%",
            G("0-0.1%"), F("0.1-1%"), P("over 1%"));
        Add("classes", "class.upJitter", "Up jitter", "Delay variation of this class on the way up (ms).", "under 10 ms",
            G("under 10 ms"), F("10-30 ms"), P("over 30 ms"));
        Add("classes", "class.downMbps", "Down Mbps", "Download throughput of this class, as received by this client.", "its share of the download rate",
            G("95% or more of its share"), F("80-95%"), P("under 80%"));
        Add("classes", "class.downLoss", "Down loss %", "UDP packets of this class lost on the way down in the last second.", "0%",
            G("0-0.1%"), F("0.1-1%"), P("over 1%"));
        Add("classes", "class.downJitter", "Down jitter", "Delay variation of this class on the way down (ms).", "under 10 ms",
            G("under 10 ms"), F("10-30 ms"), P("over 30 ms"));
        Add("classes", "class.rtt", "RTT", "Round-trip time measured on this class's own packets. With working QoS, voice and video keep a low RTT even when best effort queues up.",
            "under 50 ms; real-time classes no higher than the rest",
            G("under 50 ms"), F("50-150 ms"), P("over 150 ms, or much higher than the other classes"));
        Add("classes", "class.lost", "Lost ↑ / ↓", "Total UDP packets of this class lost since the start, upload / download.", "0");
        Add("classes", "class.retrans", "TCP retrans KB ↑ / ↓", "TCP data of this class resent in the last second (KB), upload / download.", "0",
            G("0"), F("under 1% of the class's throughput"), P("over 1%"));

        // ---------------------------------------------------------------- tunnels table
        Add("tunnels", "tunnel.tunnel", "Tunnel", "A simulated VPN or routing protocol: it sends real keepalive / dead-peer-detection probes with that protocol's timers over the link.", "");
        Add("tunnels", "tunnel.state", "State", "UP, DOWN (the protocol would have declared the peer dead) or CONNECTING (re-establishing).", "UP for the whole test");
        Add("tunnels", "tunnel.verdict", "Verdict", "Summary: Stable, drops but recovers, flapping, or could not stay up.", "Stable");
        Add("tunnels", "tunnel.avail", "Avail. %", "Share of the test the tunnel was up.", "100%",
            G("100%"), F("99-100%"), P("under 99%"));
        Add("tunnels", "tunnel.drops", "Drops", "Times the tunnel was declared down because too many probes in a row went unanswered.", "0",
            G("0"), P("any: the real tunnel would have dropped its traffic"));
        Add("tunnels", "tunnel.flaps", "Flaps", "Drops that came soon after the tunnel had recovered (within its stable-hold time): an unstable link.", "0",
            G("0"), P("any: routing and VPN reconvergence would be repeated"));
        Add("tunnels", "tunnel.downtime", "Downtime", "Total time the tunnel was down.", "0 s");
        Add("tunnels", "tunnel.longest", "Longest out", "Longest single outage, from drop to back up.", "0 s");
        Add("tunnels", "tunnel.detect", "Detect", "Average time from the last answered probe to the tunnel being declared down: how long traffic would be black-holed before failover.", "",
            I("set by the protocol's timers, e.g. BFD 300 ms × 3 ≈ 1 s, IKEv2 DPD tens of seconds"));
        Add("tunnels", "tunnel.recover", "Recover", "Average outage length until the tunnel was back up (detection + reconnect + handshake).", "as short as the protocol allows");
        Add("tunnels", "tunnel.probeRtt", "Probe RTT", "Round-trip time of the last keepalive probe.", "close to the link RTT");
        Add("tunnels", "tunnel.event", "Last event", "Most recent state change of this tunnel.", "");

        // ---------------------------------------------------------------- Statistics rows (by row name)
        void D(string name, string what, string ideal, params (StatLevel, string)[] ranges) => Add("detail", "detail." + name, name, what, ideal, ranges);
        D("Measured by", "Which end counted what arrived: the server for upload, this client for download. The sending end supplies its own counters.", "");
        D("Average throughput", "Data received divided by the test time.", "the requested rate (UDP / TCP share of it in mixed mode)",
            G("95% or more of the requested rate"), F("80-95%"), P("under 80%"));
        D("Packets sent", "Packets the sending end transmitted (heartbeats included).", "");
        D("Packets received", "Packets that arrived, duplicates included.", "equal to packets sent (minus losses)");
        D("Data received", "Bytes that arrived.", "");
        D("Average packet rate", "Packets per second received.", "");
        D("Packet size min / avg / max", "Sizes of the application messages (48 B = heartbeats; TCP carries them inside larger segments).", "");
        D("Sender fell behind", "Times the sender was more than 200 ms behind its schedule: the CPU or the socket buffer could not keep up, so lower throughput may be local.", "0",
            G("0"), P("any: results for that direction may understate the link"));
        D("Lost packets", "UDP sequence numbers that never arrived, and their share.", "0%",
            G("0-0.1%"), F("0.1-1%"), P("over 1%"));
        D("Loss events", "Separate runs of consecutive lost packets.", "0");
        D("Longest loss burst", "Most packets lost in a row. One long burst is an outage; many single losses are random loss.", "1 packet or less",
            G("1-2 packets"), F("3-10: short drop-outs, audible in calls"), P("over 10: outages; over ~50 at 50 pps voice is a dropped second"));
        D("Average loss burst", "Lost packets per loss event.", "close to 1 (scattered, random loss)",
            G("about 1: random loss (radio noise, light congestion)"), P("well above 1: bursts from buffer overflow, policing or link drops"));
        D("Burst lengths 1 / 2-3 / 4-10 / 11-100 / >100", "How many loss events fell in each length range.", "everything in the first bucket, or none at all");
        D("Out of order", "Packets that arrived after a later packet of the same stream (reordering, e.g. from load-balanced paths).", "0%",
            G("0%"), F("under 0.1%"), P("over 0.1%: TCP mistakes it for loss and slows down; some VPNs drop reordered packets"));
        D("Reorder distance avg / max", "How many newer packets had already arrived when a reordered one came in.", "0-2 packets",
            G("1-2: harmless"), P("over 3: triggers TCP fast retransmit and can exceed VPN anti-replay windows (often 64 packets)"));
        D("Duplicates", "Packets that arrived more than once.", "0",
            G("0"), P("any: a network loop or a misbehaving link layer"));
        D("Very late (> 8192 behind)", "Packets so late they fell outside the duplicate-detection window.", "0");
        D("Jitter avg / peak", "RFC 3550 delay variation: average over the streams and the highest value seen.", "under 10 ms average, under 30 ms peak",
            G("under 10 ms"), F("10-30 ms"), P("over 30 ms"));
        D("Delay variation avg", "How far one-way delay sat above its recent minimum on average: time spent waiting in queues on the path (clock offsets between the hosts cancel out).", "under 5 ms",
            G("under 5 ms"), F("5-30 ms"), P("over 30 ms: persistent queueing"));
        D("Delay variation p50 / p95 / p99", "Half, 95% and 99% of packets were delayed by at most this much above the minimum (upper bounds from a histogram).", "p99 under 30 ms",
            G("p99 under 30 ms"), F("p99 30-100 ms"), P("p99 over 100 ms: real-time traffic will see late packets"));
        D("Delay variation max", "Largest extra delay any packet had.", "under 100 ms",
            G("under 50 ms"), F("50-250 ms"), P("over 250 ms"));
        D("Longest silence", "Longest time without any packet over the whole test (heartbeats arrive every 50 ms).", "under 100 ms",
            G("under 100 ms"), F("100-1000 ms"), P("1 s or more: an outage"));
        D("Stalls ≥ 100 ms / ≥ 1 s", "Number of gaps between packets of at least 100 ms and of at least 1 s.", "0 / 0",
            G("0 / 0"), F("a few short ones"), P("any of 1 s or more"));
        D("Streams", "TCP connections carrying this traffic.", "");
        D("RTT / min RTT", "The sending kernel's smoothed round-trip time and the lowest it has seen. A large difference means data is queueing in the network.", "RTT close to min RTT",
            G("RTT within ~20 ms of min RTT"), P("RTT far above min RTT: queueing / bufferbloat"));
        D("Congestion window", "How much unacknowledged data TCP allows itself in flight. It shrinks after loss.", "large enough for rate × RTT",
            I("needed ≈ rate × RTT, e.g. 100 Mbps × 20 ms = 250 kB"));
        D("Bytes in flight", "Data sent but not yet acknowledged.", "");
        D("Retransmitted", "Data TCP had to send again, and its share of everything sent: TCP's view of the loss.", "0%",
            G("under 0.1%"), F("0.1-1%"), P("over 1%: throughput suffers"));
        D("Fast retransmits", "Losses TCP recovered quickly after duplicate ACKs (the good kind of recovery).", "0");
        D("Retransmission timeouts", "Losses TCP only recovered from after a timeout: a stall of at least one RTO (200 ms or more).", "0",
            G("0"), P("any: noticeable stalls"));
        D("Duplicate ACKs received", "ACKs repeating the same point: the receiver saw a gap (loss or reordering).", "low");
        D("Reordering seen", "Times the kernel detected reordering (Linux / Android only).", "0");
        D("MSS", "Largest TCP segment payload on the connection: the path MTU minus 40-60 bytes of headers.", "1460 B on a standard 1500-byte path",
            I("lower values point to a tunnel or PPPoE on the path; very large values appear on loopback"));

        // ---------------------------------------------------------------- server sessions table
        Add("server", "server.session", "Session", "Session ID the server assigned to this test.", "");
        Add("server", "server.client", "Client", "The client's address as the server sees it (after NAT). If its traffic later arrives from another address, the new one is shown too and looked up again.", "");
        Add("server", "server.host", "Host (reverse DNS)", "Reverse-DNS name of the client address.", "");
        Add("server", "server.isp", "ISP / ASN", "Network the client address belongs to (from ipinfo.io).", "");
        Add("server", "server.location", "IP location", "Approximate location of the client address (from ipinfo.io; city-level, often the ISP's site).", "");
        Add("server", "server.device", "Device location", "Where the client device is, from its own location service (GPS / Wi-Fi), with its accuracy. Only shown when the client turned location reporting on (off by default). Double-click the row to open it on a map.", "");
        Add("server", "server.version", "Version", "LinkTest version of the client. Clients older than the server miss newer features (detailed statistics need 1.8+). 🔒 = the control connection is encrypted (TLS, 1.9+ clients).", "same as the server, with 🔒");
        Add("server", "server.mode", "Mode", "Transport of the test: UDP, TCP, Mixed or DSCP simulate (number of classes).", "");
        Add("server", "server.duration", "Duration", "Time since the client started the test.", "");
        Add("server", "server.rx", "Rx / upload Mbps", "Data received from the client per second: the client's upload.", "the client's requested upload rate",
            G("95% or more of the requested rate"), F("80-95%"), P("under 80%"));
        Add("server", "server.tx", "Tx / download Mbps", "Data the server is sending to the client: the client's download.", "the client's requested download rate");
        Add("server", "server.loss", "Loss %", "Share of the client's UDP packets that did not arrive (whole session).", "0%",
            G("0-0.1%"), F("0.1-1%"), P("over 1%"));
        Add("server", "server.rtt", "RTT ms", "Round-trip time to the client, measured on the test traffic.", "under 50 ms",
            G("under 50 ms"), F("50-150 ms"), P("over 150 ms"));
        Add("server", "server.classes", "Classes", "Number of traffic classes in the test (10 for DSCP simulate).", "");
    }
}
