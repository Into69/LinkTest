namespace LinkTest.Core;

/// <summary>
/// One stream of traffic in a test: its transport, share of the configured rate, DSCP marking and shape.
/// Every test is a list of classes: UDP, TCP and Mixed modes are one or two classes; DSCP "Simulate" uses
/// a multi-class enterprise mix so each class can be measured separately under its own marking.
/// </summary>
public sealed class TrafficClass
{
    public string Name { get; set; } = "";
    /// <summary>Udp or Tcp.</summary>
    public TransportKind Transport { get; set; } = TransportKind.Udp;
    /// <summary>Percentage of each direction's rate carried by this class.</summary>
    public double SharePct { get; set; } = 100;
    public int Dscp { get; set; }
    public TrafficProfile Profile { get; set; } = TrafficProfile.Constant;
    /// <summary>Packet / write size; 0 = use the test's packet size.</summary>
    public int PacketSize { get; set; }
    /// <summary>TCP connections for this class.</summary>
    public int Streams { get; set; } = 1;

    public TrafficClass Clone() => (TrafficClass)MemberwiseClone();

    public string DscpName => DscpNames.Name(Dscp);

    public override string ToString() => $"{Name} ({Transport}, {DscpName}, {SharePct:0.#}%)";
}

public static class DscpNames
{
    static readonly Dictionary<int, string> Names = new()
    {
        [0] = "BE", [8] = "CS1", [10] = "AF11", [12] = "AF12", [14] = "AF13", [16] = "CS2", [18] = "AF21", [20] = "AF22",
        [22] = "AF23", [24] = "CS3", [26] = "AF31", [28] = "AF32", [30] = "AF33", [32] = "CS4", [34] = "AF41", [36] = "AF42",
        [38] = "AF43", [40] = "CS5", [44] = "VA", [46] = "EF", [48] = "CS6", [56] = "CS7", [1] = "LE",
    };

    public static string Name(int dscp) => Names.TryGetValue(dscp, out var n) ? $"{n} ({dscp})" : dscp.ToString();
}

public static class TrafficClasses
{
    public const int MaxClasses = 16;
    public const int MaxTcpConnections = 32;

    /// <summary>
    /// A typical enterprise WAN mix. Class names and DSCP values follow RFC 4594 / the Cisco QoS baseline.
    /// Shares respect common design guidance: real-time (strict priority) traffic ≤ 33% of the link,
    /// best effort ≥ 25%, scavenger ~1%. The per-class percentages are representative, not a standard.
    /// </summary>
    public static List<TrafficClass> EnterpriseMix() => new()
    {
        new() { Name = "Voice",              Transport = TransportKind.Udp, SharePct = 10, Dscp = 46, Profile = TrafficProfile.Voip },
        new() { Name = "Video conferencing", Transport = TransportKind.Udp, SharePct = 18, Dscp = 34, Profile = TrafficProfile.Video },
        new() { Name = "Network control",    Transport = TransportKind.Udp, SharePct = 2,  Dscp = 48, Profile = TrafficProfile.Constant, PacketSize = 120 },
        new() { Name = "OAM / management",   Transport = TransportKind.Udp, SharePct = 1,  Dscp = 16, Profile = TrafficProfile.Random,   PacketSize = 200 },
        new() { Name = "Call signaling",     Transport = TransportKind.Tcp, SharePct = 2,  Dscp = 24, Profile = TrafficProfile.Random,   PacketSize = 300 },
        new() { Name = "Streaming video",    Transport = TransportKind.Tcp, SharePct = 12, Dscp = 26, Profile = TrafficProfile.Video },
        new() { Name = "Transactional data", Transport = TransportKind.Tcp, SharePct = 12, Dscp = 18, Profile = TrafficProfile.Random },
        new() { Name = "Bulk data",          Transport = TransportKind.Tcp, SharePct = 12, Dscp = 10, Profile = TrafficProfile.Constant },
        new() { Name = "Best effort",        Transport = TransportKind.Tcp, SharePct = 30, Dscp = 0,  Profile = TrafficProfile.Imix },
        new() { Name = "Scavenger",          Transport = TransportKind.Tcp, SharePct = 1,  Dscp = 8,  Profile = TrafficProfile.Constant },
    };

    /// <summary>The classes a configuration runs. Both client and server derive them the same way.</summary>
    public static List<TrafficClass> Resolve(TestConfig c)
    {
        if (c.DscpSimulate)
        {
            var list = (c.Classes is { Count: > 0 } ? c.Classes : EnterpriseMix())
                .Take(MaxClasses).Select(x => x.Clone()).ToList();
            double total = 0;
            foreach (var x in list)
            {
                x.Transport = x.Transport == TransportKind.Tcp ? TransportKind.Tcp : TransportKind.Udp;
                x.SharePct = double.IsFinite(x.SharePct) ? Math.Clamp(x.SharePct, 0, 100) : 0;
                x.Dscp = Math.Clamp(x.Dscp, 0, 63);
                x.PacketSize = x.PacketSize <= 0 ? 0 : Math.Clamp(x.PacketSize, Protocol.HeaderSize, Protocol.MaxPacket);
                x.Streams = x.Transport == TransportKind.Tcp ? Math.Clamp(x.Streams, 1, 4) : 1;
                if (!Enum.IsDefined(x.Profile)) x.Profile = TrafficProfile.Constant;
                if (string.IsNullOrWhiteSpace(x.Name)) x.Name = x.DscpName;
                if (x.Name.Length > 40) x.Name = x.Name[..40];
                total += x.SharePct;
            }
            if (total > 0) foreach (var x in list) x.SharePct = x.SharePct * 100 / total;
            int tcp = 0;
            foreach (var x in list.Where(x => x.Transport == TransportKind.Tcp))
            {
                x.Streams = Math.Min(x.Streams, Math.Max(1, MaxTcpConnections - tcp));
                tcp += x.Streams;
            }
            return list;
        }

        return c.Protocol switch
        {
            TransportKind.Tcp => new() { new() { Name = "TCP", Transport = TransportKind.Tcp, SharePct = 100, Dscp = c.Dscp, Profile = c.Profile, Streams = c.Streams } },
            TransportKind.Mixed => new()
            {
                new() { Name = "UDP", Transport = TransportKind.Udp, SharePct = c.UdpSharePct, Dscp = c.Dscp, Profile = c.Profile },
                new() { Name = "TCP", Transport = TransportKind.Tcp, SharePct = 100 - c.UdpSharePct, Dscp = c.Dscp, Profile = c.Profile, Streams = c.Streams },
            },
            _ => new() { new() { Name = "UDP", Transport = TransportKind.Udp, SharePct = 100, Dscp = c.Dscp, Profile = c.Profile } },
        };
    }

    public static SenderSpec Spec(TestConfig c, TrafficClass k, double totalMbps, int streams = 1) => new()
    {
        RateBps = totalMbps * 1_000_000 * k.SharePct / 100 / Math.Max(1, streams),
        Profile = k.Profile,
        PacketSize = k.PacketSize > 0 ? k.PacketSize : c.PacketSize,
        BurstOnMs = c.BurstOnMs,
        BurstOffMs = c.BurstOffMs,
        StartDelayUs = c.BaselineSec * 1_000_000L,
    };

    /// <summary>
    /// Stream handshake index: TCP ordinal (n-th TCP class) in the high byte, stream number in the low byte.
    /// The first TCP class therefore uses plain 0..N-1, as older clients/servers expect.
    /// </summary>
    public static int EncodeStream(int tcpOrdinal, int stream) => (tcpOrdinal << 8) | stream;
    public static (int tcpOrdinal, int stream) DecodeStream(int idx) => (idx >> 8, idx & 0xFF);
}
