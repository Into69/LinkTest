using System.ComponentModel;
using System.Text.Json.Serialization;

namespace LinkTest.Core.Tunnels;

public enum TunnelTransport { Udp, Tcp }

/// <summary>
/// Timers of a VPN/tunnel liveness mechanism. The simulation sends real probes with these timers over the
/// link under test (the server echoes them), so a tunnel only "drops" if the link really loses enough packets.
/// </summary>
public sealed class TunnelProfile
{
    [Category("General"), Description("Display name.")]
    public string Name { get; set; } = "Custom";

    [Category("General"), Description("Notes about what this models.")]
    public string Notes { get; set; } = "";

    [Browsable(false)]
    public bool Enabled { get; set; } = true;

    [Category("General"), Description("UDP probes go over the test path as datagrams. TCP probes go over the TCP control connection, so lost packets are retransmitted by TCP and show up as delay rather than loss.")]
    public TunnelTransport Transport { get; set; } = TunnelTransport.Udp;

    [Category("Liveness (DPD / keepalive)"), DisplayName("Keepalive interval (ms)"), Description("Idle time after a successful exchange before the next keepalive/DPD probe is sent.")]
    public int KeepaliveIntervalMs { get; set; } = 10_000;

    [Category("Liveness (DPD / keepalive)"), DisplayName("Probe timeout (ms)"), Description("How long to wait for a reply to a probe before counting it as missed and retransmitting.")]
    public int ProbeTimeoutMs { get; set; } = 2_000;

    [Category("Liveness (DPD / keepalive)"), DisplayName("Timeout backoff"), Description("Multiplier applied to the probe timeout after each miss (1 = fixed, 1.8 = strongSwan style, 2 = doubling).")]
    public double RetryBackoff { get; set; } = 1.0;

    [Category("Liveness (DPD / keepalive)"), DisplayName("Max missed probes"), Description("Consecutive unanswered probes before the tunnel is declared DOWN.")]
    public int MaxMissed { get; set; } = 5;

    [Category("Liveness (DPD / keepalive)"), DisplayName("Keepalive size (bytes)"), Description("UDP payload size of a keepalive probe (min 48).")]
    public int KeepaliveBytes { get; set; } = 96;

    [Category("Re-establish (handshake)"), DisplayName("Handshake round trips"), Description("Request/response exchanges needed to bring the tunnel up (IKEv2 = 2, WireGuard = 1, OpenVPN TLS ≈ 6). Each must succeed in order.")]
    public int HandshakeRoundTrips { get; set; } = 2;

    [Category("Re-establish (handshake)"), DisplayName("Handshake timeout (ms)"), Description("Initial wait for each handshake reply before retransmitting.")]
    public int HandshakeTimeoutMs { get; set; } = 2_000;

    [Category("Re-establish (handshake)"), DisplayName("Handshake backoff"), Description("Multiplier applied to the handshake timeout after each retransmission.")]
    public double HandshakeBackoff { get; set; } = 2.0;

    [Category("Re-establish (handshake)"), DisplayName("Handshake transmissions"), Description("Transmissions of one handshake message before the attempt is abandoned (0 = keep trying forever).")]
    public int HandshakeRetries { get; set; } = 5;

    [Category("Re-establish (handshake)"), DisplayName("Reconnect delay (ms)"), Description("Wait after a drop or failed attempt before starting a new handshake.")]
    public int ReconnectDelayMs { get; set; } = 5_000;

    [Category("Re-establish (handshake)"), DisplayName("Handshake size (bytes)"), Description("UDP payload size of handshake messages; large values (certificates) fragment on small MTUs.")]
    public int HandshakeBytes { get; set; } = 1_200;

    [Category("Stability"), DisplayName("Stable hold time (s)"), Description("A tunnel must stay up this long after (re)establishing to count as stable. Dropping sooner counts as a flap.")]
    public int StableHoldSec { get; set; } = 60;

    /// <summary>Approximate worst-case time from total loss to DOWN, in seconds.</summary>
    [JsonIgnore, Category("Computed"), DisplayName("Detection time (s)"), Description("Approximate time from the link going dark to the tunnel being declared down (excluding the idle keepalive interval).")]
    public double DetectionSec
    {
        get
        {
            double t = 0, to = Math.Max(1, ProbeTimeoutMs);
            for (int i = 0; i < Math.Max(1, MaxMissed); i++) { t += to; to *= Math.Max(1, RetryBackoff); }
            return Math.Round(t / 1000, 1);
        }
    }

    public TunnelProfile Clone() => (TunnelProfile)MemberwiseClone();

    public void Normalize()
    {
        KeepaliveIntervalMs = Math.Clamp(KeepaliveIntervalMs, 10, 3_600_000);
        ProbeTimeoutMs = Math.Clamp(ProbeTimeoutMs, 10, 600_000);
        RetryBackoff = double.IsFinite(RetryBackoff) ? Math.Clamp(RetryBackoff, 1, 10) : 1;
        MaxMissed = Math.Clamp(MaxMissed, 1, 1000);
        KeepaliveBytes = Math.Clamp(KeepaliveBytes, Protocol.HeaderSize, 9000);
        HandshakeRoundTrips = Math.Clamp(HandshakeRoundTrips, 1, 20);
        HandshakeTimeoutMs = Math.Clamp(HandshakeTimeoutMs, 10, 600_000);
        HandshakeBackoff = double.IsFinite(HandshakeBackoff) ? Math.Clamp(HandshakeBackoff, 1, 10) : 1;
        HandshakeRetries = Math.Clamp(HandshakeRetries, 0, 10_000);
        ReconnectDelayMs = Math.Clamp(ReconnectDelayMs, 0, 3_600_000);
        HandshakeBytes = Math.Clamp(HandshakeBytes, Protocol.HeaderSize, 9000);
        StableHoldSec = Math.Clamp(StableHoldSec, 0, 86_400);
    }

    public override string ToString() => Name;
}

public static class TunnelPresets
{
    public static List<TunnelProfile> CreateDefaults() => new()
    {
        new()
        {
            Name = "IPsec IKEv2 (DPD 10s, Cisco-like)",
            Notes = "dpd 10 2: probe after 10 s idle, retry every 2 s, 5 retries. IKE_SA_INIT + IKE_AUTH (2 RTs) to re-establish.",
            KeepaliveIntervalMs = 10_000, ProbeTimeoutMs = 2_000, RetryBackoff = 1, MaxMissed = 5, KeepaliveBytes = 96,
            HandshakeRoundTrips = 2, HandshakeTimeoutMs = 2_000, HandshakeBackoff = 2, HandshakeRetries = 5,
            ReconnectDelayMs = 5_000, HandshakeBytes = 1_200, StableHoldSec = 60,
        },
        new()
        {
            Name = "IPsec IKEv2 (strongSwan defaults)",
            Notes = "dpd_delay 30 s; retransmit_timeout 4 s, base 1.8, 5 retransmits (~165 s to give up).",
            KeepaliveIntervalMs = 30_000, ProbeTimeoutMs = 4_000, RetryBackoff = 1.8, MaxMissed = 6, KeepaliveBytes = 96,
            HandshakeRoundTrips = 2, HandshakeTimeoutMs = 4_000, HandshakeBackoff = 1.8, HandshakeRetries = 6,
            ReconnectDelayMs = 1_000, HandshakeBytes = 1_200, StableHoldSec = 60, Enabled = false,
        },
        new()
        {
            Name = "WireGuard",
            Notes = "Keepalive ~10 s, REKEY_TIMEOUT 5 s, handshake retried for REKEY_ATTEMPT_TIME 90 s. 1-RTT handshake (148 B).",
            KeepaliveIntervalMs = 10_000, ProbeTimeoutMs = 5_000, RetryBackoff = 1, MaxMissed = 18, KeepaliveBytes = 48,
            HandshakeRoundTrips = 1, HandshakeTimeoutMs = 5_000, HandshakeBackoff = 1, HandshakeRetries = 18,
            ReconnectDelayMs = 0, HandshakeBytes = 148, StableHoldSec = 30,
        },
        new()
        {
            Name = "OpenVPN UDP (keepalive 10 60)",
            Notes = "ping 10 / ping-restart 60. TLS re-handshake ≈ 6 round trips, control channel backoff 2 s doubling, connect-retry 5 s.",
            KeepaliveIntervalMs = 10_000, ProbeTimeoutMs = 10_000, RetryBackoff = 1, MaxMissed = 6, KeepaliveBytes = 64,
            HandshakeRoundTrips = 6, HandshakeTimeoutMs = 2_000, HandshakeBackoff = 2, HandshakeRetries = 5,
            ReconnectDelayMs = 5_000, HandshakeBytes = 1_250, StableHoldSec = 60,
        },
        new()
        {
            Name = "OpenVPN TCP (keepalive 10 60)",
            Notes = "Same timers over TCP: loss becomes delay (TCP retransmits); drops when stalls exceed ping-restart.",
            Transport = TunnelTransport.Tcp,
            KeepaliveIntervalMs = 10_000, ProbeTimeoutMs = 10_000, RetryBackoff = 1, MaxMissed = 6, KeepaliveBytes = 64,
            HandshakeRoundTrips = 6, HandshakeTimeoutMs = 60_000, HandshakeBackoff = 1, HandshakeRetries = 1,
            ReconnectDelayMs = 5_000, HandshakeBytes = 1_250, StableHoldSec = 60, Enabled = false,
        },
        new()
        {
            Name = "GRE keepalive (10s x3)",
            Notes = "keepalive 10 3: down after 3 missed keepalives; comes back up as soon as a keepalive is answered.",
            KeepaliveIntervalMs = 10_000, ProbeTimeoutMs = 10_000, RetryBackoff = 1, MaxMissed = 3, KeepaliveBytes = 48,
            HandshakeRoundTrips = 1, HandshakeTimeoutMs = 10_000, HandshakeBackoff = 1, HandshakeRetries = 0,
            ReconnectDelayMs = 0, HandshakeBytes = 48, StableHoldSec = 30,
        },
        new()
        {
            Name = "BFD (300ms x3)",
            Notes = "Fast failure detection: 300 ms interval, multiplier 3 (~0.9 s). Down→Init→Up needs 2 exchanges at 1 s slow rate.",
            KeepaliveIntervalMs = 300, ProbeTimeoutMs = 300, RetryBackoff = 1, MaxMissed = 3, KeepaliveBytes = 48,
            HandshakeRoundTrips = 2, HandshakeTimeoutMs = 1_000, HandshakeBackoff = 1, HandshakeRetries = 0,
            ReconnectDelayMs = 0, HandshakeBytes = 48, StableHoldSec = 10,
        },
        new()
        {
            Name = "SD-WAN BFD (1s x7)",
            Notes = "Typical SD-WAN overlay BFD: hello 1 s, multiplier 7.",
            KeepaliveIntervalMs = 1_000, ProbeTimeoutMs = 1_000, RetryBackoff = 1, MaxMissed = 7, KeepaliveBytes = 64,
            HandshakeRoundTrips = 2, HandshakeTimeoutMs = 1_000, HandshakeBackoff = 1, HandshakeRetries = 0,
            ReconnectDelayMs = 0, HandshakeBytes = 64, StableHoldSec = 30, Enabled = false,
        },
    };
}
