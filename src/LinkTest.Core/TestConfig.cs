using System.Text.Json;
using System.Text.Json.Serialization;

namespace LinkTest.Core;

public enum TransportKind { Udp, Tcp }

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
    /// <summary>Parallel TCP connections (TCP only).</summary>
    public int Streams { get; set; } = 1;
    /// <summary>DSCP value to mark traffic with (0 = none). Needs Administrator on Windows.</summary>
    public int Dscp { get; set; }
    /// <summary>0 = run until stopped.</summary>
    public int DurationSec { get; set; }

    public TestConfig Clone() => (TestConfig)MemberwiseClone();

    public void Normalize()
    {
        UpMbps = double.IsFinite(UpMbps) ? Math.Clamp(UpMbps, 0, 100_000) : 0;
        DownMbps = double.IsFinite(DownMbps) ? Math.Clamp(DownMbps, 0, 100_000) : 0;
        PacketSize = Math.Clamp(PacketSize, Core.Protocol.HeaderSize, Core.Protocol.MaxPacket);
        BurstOnMs = Math.Clamp(BurstOnMs, 1, 60_000);
        BurstOffMs = Math.Clamp(BurstOffMs, 0, 60_000);
        Streams = Protocol == TransportKind.Tcp ? Math.Clamp(Streams, 1, 16) : 1;
        Dscp = Math.Clamp(Dscp, 0, 63);
        DurationSec = Math.Max(0, DurationSec);
    }

    public override string ToString() =>
        $"{Protocol} up {UpMbps:0.###} Mbps / down {DownMbps:0.###} Mbps, {Profile}, {PacketSize} B" +
        (Protocol == TransportKind.Tcp ? $", {Streams} stream(s)" : "") +
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
