using System.Text.Json;
using System.Text.Json.Serialization;

namespace LinkTest.Core;

public enum TransportKind
{
    Udp,
    Tcp,
    /// <summary>UDP and TCP at the same time; <see cref="TestConfig.UdpSharePct"/> splits the rate.</summary>
    Mixed,
}

public enum TrafficProfile
{
    /// <summary>Evenly spaced fixed-size packets.</summary>
    Constant,
    /// <summary>Internet mix of 64/576/1500-byte packets (7:4:1).</summary>
    Imix,
    /// <summary>On/off bursts; average equals the target rate.</summary>
    Bursty,
    /// <summary>Poisson arrivals with random sizes (web/office-like).</summary>
    Random,
    /// <summary>G.711 20 ms voice frames (172 B payload); rate decides the number of calls.</summary>
    Voip,
    /// <summary>30 fps video: per-frame bursts with a large I-frame every second.</summary>
    Video,
}

/// <summary>Test parameters, chosen on the client and sent to the server in the hello message.</summary>
public sealed class TestConfig
{
    public TransportKind Protocol { get; set; } = TransportKind.Udp;
    /// <summary>Client → server (client TX), Mbit/s of payload.</summary>
    public double UpMbps { get; set; } = 10;
    /// <summary>Server → client (client RX), Mbit/s of payload.</summary>
    public double DownMbps { get; set; } = 10;
    public int PacketSize { get; set; } = 1400;
    public TrafficProfile Profile { get; set; } = TrafficProfile.Constant;
    public int BurstOnMs { get; set; } = 200;
    public int BurstOffMs { get; set; } = 800;
    /// <summary>Parallel TCP connections (TCP and mixed modes).</summary>
    public int Streams { get; set; } = 1;
    /// <summary>Mixed mode: percentage of each direction's rate carried by UDP; the rest goes over TCP.</summary>
    public double UdpSharePct { get; set; } = 30;
    /// <summary>DSCP value to mark traffic with (0 = none). Needs Administrator on Windows.</summary>
    public int Dscp { get; set; }
    /// <summary>
    /// DSCP "Simulate" mode: run a multi-class enterprise mix (<see cref="Classes"/>, or the built-in
    /// <see cref="TrafficClasses.EnterpriseMix"/>), each class with its own marking and statistics.
    /// Overrides Protocol / Profile / UdpSharePct / Streams.
    /// </summary>
    public bool DscpSimulate { get; set; }
    /// <summary>Custom class list for Simulate mode (null = built-in enterprise mix).</summary>
    public List<TrafficClass>? Classes { get; set; }
    /// <summary>0 = run until stopped.</summary>
    public int DurationSec { get; set; }
    /// <summary>
    /// Seconds at the start with heartbeats only, to measure the idle RTT for the bufferbloat grade (1.6+).
    /// The server ignores it for clients older than 1.6.
    /// </summary>
    public int BaselineSec { get; set; } = 3;

    public TestConfig Clone()
    {
        var c = (TestConfig)MemberwiseClone();
        c.Classes = Classes?.Select(k => k.Clone()).ToList();
        return c;
    }

    [JsonIgnore] public bool UsesUdp => TrafficClasses.Resolve(this).Any(k => k.Transport == TransportKind.Udp);
    [JsonIgnore] public bool UsesTcp => TrafficClasses.Resolve(this).Any(k => k.Transport == TransportKind.Tcp);

    public void Normalize()
    {
        UpMbps = double.IsFinite(UpMbps) ? Math.Clamp(UpMbps, 0, 100_000) : 0;
        DownMbps = double.IsFinite(DownMbps) ? Math.Clamp(DownMbps, 0, 100_000) : 0;
        PacketSize = Math.Clamp(PacketSize, Core.Protocol.HeaderSize, Core.Protocol.MaxPacket);
        BurstOnMs = Math.Clamp(BurstOnMs, 1, 60_000);
        BurstOffMs = Math.Clamp(BurstOffMs, 0, 60_000);
        if (!Enum.IsDefined(Protocol)) Protocol = TransportKind.Udp;
        Streams = Protocol != TransportKind.Udp ? Math.Clamp(Streams, 1, 16) : 1;
        UdpSharePct = double.IsFinite(UdpSharePct) ? Math.Clamp(UdpSharePct, 0, 100) : 30;
        Dscp = Math.Clamp(Dscp, 0, 63);
        DurationSec = Math.Max(0, DurationSec);
        BaselineSec = Math.Clamp(BaselineSec, 0, 60);
        Classes = DscpSimulate ? TrafficClasses.Resolve(this) : null;   // echoed back by the server
    }

    public override string ToString() => DscpSimulate
        ? $"DSCP simulate ({TrafficClasses.Resolve(this).Count} classes) up {UpMbps:0.###} Mbps / down {DownMbps:0.###} Mbps, {PacketSize} B"
        : (Protocol == TransportKind.Mixed ? $"Mixed ({UdpSharePct:0.#}% UDP / {100 - UdpSharePct:0.#}% TCP)" : Protocol.ToString()) +
        $" up {UpMbps:0.###} Mbps / down {DownMbps:0.###} Mbps, {Profile}, {PacketSize} B" +
        (UsesTcp ? $", {Streams} TCP stream(s)" : "") +
        (Dscp > 0 ? $", DSCP {Dscp}" : "");
}

public static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public static readonly JsonSerializerOptions Pretty = new(Options) { WriteIndented = true };
}
