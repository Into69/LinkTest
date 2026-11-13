using System.Net;
using LinkTest.Core;
using LinkTest.Core.Tunnels;

namespace LinkTest.Tests;

public class ProtocolTests
{
    [Fact]
    public void PacketHeader_RoundTrips()
    {
        var h = new PacketHeader
        {
            Type = PacketType.Data, SessionId = 0xDEADBEEF, StreamId = 7, Flags = 3, Seq = 123456789012,
            SendUs = 42, EchoUs = 41, EchoHoldUs = 5, Length = 1400,
        };
        var buf = new byte[Protocol.HeaderSize];
        h.Write(buf);
        Assert.True(PacketHeader.TryRead(buf, out var r));
        Assert.Equal(h.SessionId, r.SessionId);
        Assert.Equal(h.StreamId, r.StreamId);
        Assert.Equal(h.Flags, r.Flags);
        Assert.Equal(h.Seq, r.Seq);
        Assert.Equal(h.SendUs, r.SendUs);
        Assert.Equal(h.EchoUs, r.EchoUs);
        Assert.Equal(h.EchoHoldUs, r.EchoHoldUs);
        Assert.Equal(h.Length, r.Length);
    }

    [Fact]
    public void PacketHeader_RejectsGarbage()
    {
        Assert.False(PacketHeader.TryRead(new byte[10], out _));
        Assert.False(PacketHeader.TryRead(new byte[Protocol.HeaderSize], out _));   // wrong magic
    }

    [Fact]
    public void StatsReport_RoundTrips()
    {
        var r = new StatsReport
        {
            TimeUs = 1, Packets = 2, Bytes = 3, Lost = 4, OutOfOrder = 5, Duplicates = 6, MaxGapUs = 7,
            TcpRetransBytes = 8, TxPackets = 9, TxBytes = 10, JitterUs = 11.5, RttUs = 12.25, TcpTimeouts = 13,
        };
        var buf = new byte[StatsReport.Size];
        r.Write(buf);
        Assert.Equal(r, StatsReport.Read(buf));
    }
}

public class RxTrackerTests
{
    [Fact]
    public void CountsLossReorderingAndDuplicates()
    {
        var rx = new RxTracker();
        long t = 1_000;
        foreach (var seq in new long[] { 0, 1, 2, 4, 3, 3, 6 })
            rx.Record(0, seq, t, t += 1000, 100);
        var s = rx.Snapshot(t);
        Assert.Equal(7, s.Packets);
        Assert.Equal(1, s.Duplicates);
        Assert.Equal(1, s.OutOfOrder);     // 3 arrived after 4
        Assert.Equal(1, s.Lost);           // 5 never arrived
        Assert.Equal(700, s.Bytes);
    }

    [Fact]
    public void LateArrivalFillsTheGap()
    {
        var rx = new RxTracker();
        rx.Record(0, 0, 0, 1000, 100);
        rx.Record(0, 2, 0, 2000, 100);
        Assert.Equal(1, rx.Snapshot(2000).Lost);
        rx.Record(0, 1, 0, 3000, 100);
        Assert.Equal(0, rx.Snapshot(3000).Lost);
    }

    [Fact]
    public void StreamsAreIndependent()
    {
        var rx = new RxTracker();
        rx.Record(0, 0, 0, 1000, 100);
        rx.Record(1, 0, 0, 1000, 100);
        rx.Record(1, 1, 0, 2000, 100);
        Assert.Equal(0, rx.Snapshot(2000).Lost);
    }

    [Fact]
    public void MaxGapIncludesCurrentSilence()
    {
        var rx = new RxTracker();
        rx.Record(0, 0, 0, 1_000_000, 100);
        Assert.True(rx.Snapshot(1_700_000).MaxGapUs >= 700_000);
    }
}

public class TrafficShaperTests
{
    [Theory]
    [InlineData(TrafficProfile.Constant, 0.02)]
    [InlineData(TrafficProfile.Imix, 0.03)]
    [InlineData(TrafficProfile.Bursty, 0.03)]
    [InlineData(TrafficProfile.Random, 0.10)]
    [InlineData(TrafficProfile.Voip, 0.02)]
    [InlineData(TrafficProfile.Video, 0.15)]
    public void AverageRateMatchesTarget(TrafficProfile profile, double tolerance)
    {
        const double rate = 10_000_000;
        var spec = new SenderSpec { RateBps = rate, Profile = profile, PacketSize = 1400, BurstOnMs = 200, BurstOffMs = 800 };
        var s = new TrafficShaper(spec, seed: 1234);
        s.Start(0);
        long bytes = 0;
        const long durationUs = 20_000_000;
        while (s.NextAtUs < durationUs)
        {
            bytes += s.NextSize;
            s.Advance();
        }
        double actual = bytes * 8 / (durationUs / 1e6);
        Assert.InRange(actual, rate * (1 - tolerance), rate * (1 + tolerance));
    }

    [Fact]
    public void VoipUsesG711FrameSize()
    {
        var s = new TrafficShaper(new SenderSpec { RateBps = 1_000_000, Profile = TrafficProfile.Voip, PacketSize = 1400 }, 1);
        s.Start(0);
        for (int i = 0; i < 50; i++) { Assert.Equal(TrafficShaper.VoipPayload, s.NextSize); s.Advance(); }
    }

    [Fact]
    public void StartDelayPostponesTraffic()
    {
        var s = new TrafficShaper(new SenderSpec { RateBps = 1_000_000, Profile = TrafficProfile.Constant, PacketSize = 1000 }, 1);
        s.Start(3_000_000);
        Assert.Equal(3_000_000, s.NextAtUs);
    }
}

public class TrafficClassTests
{
    [Fact]
    public void SingleProtocolModesHaveOneFullShareClass()
    {
        var udp = TrafficClasses.Resolve(new TestConfig { Protocol = TransportKind.Udp });
        var tcp = TrafficClasses.Resolve(new TestConfig { Protocol = TransportKind.Tcp, Streams = 3 });
        Assert.Single(udp);
        Assert.Equal(100, udp[0].SharePct);
        Assert.Equal(TransportKind.Tcp, tcp[0].Transport);
        Assert.Equal(3, tcp[0].Streams);
    }

    [Fact]
    public void MixedSplitsByUdpShare()
    {
        var k = TrafficClasses.Resolve(new TestConfig { Protocol = TransportKind.Mixed, UdpSharePct = 30 });
        Assert.Equal(2, k.Count);
        Assert.Equal(30, k[0].SharePct);
        Assert.Equal(70, k[1].SharePct);
    }

    [Fact]
    public void SimulateMixIsNormalisedAndFollowsDesignGuidance()
    {
        var c = new TestConfig { DscpSimulate = true };
        c.Normalize();
        var k = TrafficClasses.Resolve(c);
        Assert.Equal(10, k.Count);
        Assert.Equal(100, k.Sum(x => x.SharePct), 6);
        double realtime = k.Where(x => x.Dscp is 46 or 34).Sum(x => x.SharePct);
        Assert.True(realtime <= 33, "real-time classes must stay within ~33% of the link");
        Assert.True(k.Single(x => x.Dscp == 0).SharePct >= 25, "best effort keeps at least 25%");
    }

    [Fact]
    public void StreamIndexEncodingIsBackwardCompatible()
    {
        Assert.Equal(3, TrafficClasses.EncodeStream(0, 3));               // first TCP class = plain index (old peers)
        Assert.Equal((2, 1), TrafficClasses.DecodeStream(TrafficClasses.EncodeStream(2, 1)));
    }
}

public class TunnelSimTests
{
    static TunnelProfile Fast() => new()
    {
        Name = "fast", KeepaliveIntervalMs = 100, ProbeTimeoutMs = 100, RetryBackoff = 1, MaxMissed = 3,
        HandshakeRoundTrips = 2, HandshakeTimeoutMs = 100, HandshakeBackoff = 1, HandshakeRetries = 0,
        ReconnectDelayMs = 0, StableHoldSec = 0,
    };

    [Fact]
    public void DropsAfterMissedProbesAndRecovers()
    {
        bool linkUp = true;
        var inFlight = new List<(long seq, ushort kind, long sent)>();
        long now = 0;
        var sim = new TunnelSim(0, Fast(), (_, seq, kind, _) => { if (linkUp) inFlight.Add((seq, kind, now)); });

        void Run(long untilUs)
        {
            while (now < untilUs)
            {
                now += 10_000;
                foreach (var p in inFlight) sim.OnResponse(p.seq, p.kind, p.sent, now);   // 10 ms "RTT"
                inFlight.Clear();
                sim.Tick(now);
            }
        }

        sim.Start(now);
        Run(200_000);
        Assert.Equal(TunnelState.Up, sim.GetStatus(now).State);

        linkUp = false;
        Run(1_000_000);
        var down = sim.GetStatus(now);
        Assert.Equal(TunnelState.Down, down.State);
        Assert.Equal(1, down.Drops);

        linkUp = true;
        Run(1_500_000);
        var up = sim.GetStatus(now);
        Assert.Equal(TunnelState.Up, up.State);
        Assert.Equal(1, up.Drops);
        Assert.True(up.AvailabilityPct is > 0 and < 100);
    }

    [Fact]
    public void ShortGlitchDoesNotDropTunnel()
    {
        bool linkUp = true;
        var inFlight = new List<(long seq, ushort kind, long sent)>();
        long now = 0;
        var sim = new TunnelSim(0, Fast(), (_, seq, kind, _) => { if (linkUp) inFlight.Add((seq, kind, now)); });
        sim.Start(0);
        for (int i = 0; i < 100; i++)
        {
            now += 10_000;
            linkUp = !(i is >= 40 and < 55);          // 150 ms outage < 3 missed probes
            foreach (var p in inFlight) sim.OnResponse(p.seq, p.kind, p.sent, now);
            inFlight.Clear();
            sim.Tick(now);
        }
        Assert.Equal(0, sim.GetStatus(now).Drops);
    }
}

public class MiscTests
{
    [Theory]
    [InlineData("10.1.2.3", "Private (RFC 1918)")]
    [InlineData("192.168.0.1", "Private (RFC 1918)")]
    [InlineData("100.64.1.1", "Carrier-grade NAT (RFC 6598)")]
    [InlineData("127.0.0.1", "Loopback")]
    [InlineData("8.8.8.8", IpLookup.PublicScope)]
    [InlineData("::1", "Loopback")]
    [InlineData("fd00::1", "Unique local (private IPv6)")]
    [InlineData("2001:4860:4860::8888", IpLookup.PublicScope)]
    public void ClassifiesAddresses(string ip, string scope) => Assert.Equal(scope, IpLookup.Classify(IPAddress.Parse(ip)));

    [Theory]
    [InlineData("v1.2.3", 1, 2, 3)]
    [InlineData("1.6.0", 1, 6, 0)]
    [InlineData("v2.0.1-beta", 2, 0, 1)]
    [InlineData("v3.1", 3, 1, 0)]
    public void ParsesReleaseTags(string tag, int major, int minor, int patch)
    {
        Assert.True(Updater.TryParseVersion(tag, out var v));
        Assert.Equal(new Version(major, minor, patch), v);
    }

    [Theory]
    [InlineData(2, "A+")]
    [InlineData(20, "A")]
    [InlineData(45, "B")]
    [InlineData(150, "C")]
    [InlineData(300, "D")]
    [InlineData(800, "F")]
    public void GradesBufferbloat(double increaseMs, string grade) => Assert.Equal(grade, BufferbloatMeter.Grade(increaseMs));

    [Fact]
    public void BufferbloatMeterSeparatesIdleAndLoaded()
    {
        var m = new BufferbloatMeter();
        for (int s = 1; s <= 3; s++) m.Add(s, 3, 10);              // idle 10 ms
        m.Add(4, 3, 500);                                          // ramp - ignored
        for (int s = 6; s <= 10; s++) m.Add(s, 3, 50);             // loaded 50 ms
        Assert.Equal(10, m.IdleMs);
        Assert.Equal(50, m.LoadedMs);
        Assert.Equal("B", m.CurrentGrade);
    }

    [Fact]
    public void MosIsBestOnCleanLinksAndDropsWithLoss()
    {
        double clean = StatSample.EstimateMos(20, 1, 0);
        double lossy = StatSample.EstimateMos(20, 1, 10);
        Assert.InRange(clean, 4.3, 4.5);
        Assert.True(lossy < 3.6, $"10% loss should rate below fair (got {lossy:0.00})");
    }

    [Fact]
    public void TunnelMtuRecommendations()
    {
        var wg = VpnOverhead.All.Single(v => v.Name == "WireGuard");
        Assert.Equal(1440, wg.TunnelMtu(1500, ipv6: false));
        Assert.Equal(1420, wg.TunnelMtu(1500, ipv6: true));     // WireGuard's default MTU
        Assert.Equal(1400, wg.TcpMss(1500, ipv6: false));
    }

    [Fact]
    public void TraceLossStartIgnoresIsolatedRateLimitedHops()
    {
        TraceHop H(int ttl, int recv) => new(ttl, IPAddress.Parse($"10.0.0.{ttl}"), null, 5, recv, 1, 1);
        var isolated = new TraceResult(DateTime.Now, "t", IPAddress.Loopback, new[] { H(1, 5), H(2, 2), H(3, 5) }, true);
        Assert.Null(isolated.LossStartsAt);
        var persistent = new TraceResult(DateTime.Now, "t", IPAddress.Loopback, new[] { H(1, 5), H(2, 3), H(3, 3) }, true);
        Assert.Equal(2, persistent.LossStartsAt!.Ttl);
    }
}
