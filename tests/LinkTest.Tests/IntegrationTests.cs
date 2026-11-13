using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using LinkTest.Core;
using LinkTest.Core.Tunnels;
using LinkTest.Server;

namespace LinkTest.Tests;

/// <summary>Runs a real server in-process on a free port; shared by the end-to-end tests.</summary>
public sealed class ServerFixture : IAsyncLifetime
{
    readonly CancellationTokenSource _cts = new();
    Task? _run;
    public int Port { get; private set; }

    public async Task InitializeAsync()
    {
        ServerHost.ConsoleOutput = false;
        Clock.EnableHighResolutionTimer();
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        Port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        var host = new ServerHost(new ServerOptions { Port = Port, NoIpLookup = true, MaxSessions = 4 }) { PeriodicStatus = false };
        _run = Task.Run(() => host.RunAsync(_cts.Token));
        await Task.Delay(500);
    }

    public async Task DisposeAsync()
    {
        _cts.Cancel();
        if (_run != null) try { await _run; } catch { }
    }
}

[CollectionDefinition("server")]
public class ServerCollection : ICollectionFixture<ServerFixture> { }

/// <summary>End-to-end: client and server exchange real traffic over loopback (tests run one at a time).</summary>
[Collection("server")]
public class IntegrationTests
{
    readonly ServerFixture _server;
    public IntegrationTests(ServerFixture server) => _server = server;

    async Task<(ClientSession session, StatSample last, List<StatSample> all)> RunAsync(TestConfig cfg, int seconds,
        double lossPct = 0, IEnumerable<TunnelProfile>? tunnels = null)
    {
        var session = new ClientSession { MeasureMtu = false, AutoTraceroute = false };
        await session.StartAsync("127.0.0.1", _server.Port, null, cfg, tunnels ?? Array.Empty<TunnelProfile>(), lossPct,
            CancellationToken.None, ipLookup: false);
        var all = new List<StatSample>();
        for (int i = 0; i < seconds; i++)
        {
            await Task.Delay(1000);
            all.Add(session.TakeSample());
        }
        await session.StopAsync("test done");
        return (session, all[^1], all);
    }

    static void AssertRate(double actual, double target, double tol = 0.12) =>
        Assert.InRange(actual, target * (1 - tol), target * (1 + tol));

    [Fact]
    public async Task Udp_ReachesTargetRatesWithoutLoss()
    {
        var (_, s, _) = await RunAsync(new TestConfig { Protocol = TransportKind.Udp, UpMbps = 20, DownMbps = 30, BaselineSec = 0 }, 4);
        AssertRate(s.UpMbps, 20);
        AssertRate(s.DownMbps, 30);
        Assert.Equal(0, s.UpLossPct);
        Assert.Equal(0, s.DownLossPct);
        Assert.True(double.IsFinite(s.RttMs));
    }

    [Fact]
    public async Task FinishedTest_ProducesRecordAndReport()
    {
        var (session, _, _) = await RunAsync(new TestConfig { Protocol = TransportKind.Udp, UpMbps = 10, DownMbps = 10, BaselineSec = 0 }, 3);
        var r = session.Record;
        Assert.NotNull(r);
        Assert.Equal(3, r!.Samples.T.Count);
        Assert.InRange(r.DownMbpsAvg, 8, 12);
        Assert.Equal("test done", r.EndReason);
        Assert.Contains("Link quality report", ReportBuilder.Build(r));
        Assert.Equal(r.Id, TestRecord.FromJson(r.ToJson())!.Id);
    }

    [Fact]
    public async Task Tcp_WithParallelStreams()
    {
        var (_, s, _) = await RunAsync(new TestConfig { Protocol = TransportKind.Tcp, Streams = 3, UpMbps = 30, DownMbps = 30, BaselineSec = 0 }, 4);
        AssertRate(s.UpMbps, 30);
        AssertRate(s.DownMbps, 30);
    }

    [Fact]
    public async Task Mixed_SplitsBetweenUdpAndTcp()
    {
        var (_, s, _) = await RunAsync(new TestConfig { Protocol = TransportKind.Mixed, UdpSharePct = 30, UpMbps = 20, DownMbps = 20, BaselineSec = 0 }, 4);
        Assert.True(s.Mixed);
        AssertRate(s.UpMbpsUdp, 6, 0.2);
        AssertRate(s.UpMbpsTcp, 14, 0.2);
    }

    [Fact]
    public async Task DscpSimulate_EveryClassCarriesItsShare()
    {
        var (_, s, _) = await RunAsync(new TestConfig { DscpSimulate = true, UpMbps = 20, DownMbps = 20, BaselineSec = 0 }, 5);
        Assert.Equal(10, s.Classes.Count);
        foreach (var k in s.Classes)
            Assert.True(k.DownMbps > 0, $"class {k.Name} carried no traffic");
        AssertRate(s.DownMbps, 20, 0.15);
    }

    [Fact]
    public async Task ArtificialLoss_IsMeasuredInBothDirections_AndCanChangeLive()
    {
        var session = new ClientSession { MeasureMtu = false, AutoTraceroute = false };
        await session.StartAsync("127.0.0.1", _server.Port, null,
            new TestConfig { Protocol = TransportKind.Udp, UpMbps = 5, DownMbps = 5, BaselineSec = 0 }, Array.Empty<TunnelProfile>(), 20,
            CancellationToken.None, ipLookup: false);
        StatSample s = null!;
        for (int i = 0; i < 4; i++) { await Task.Delay(1000); s = session.TakeSample(); }
        Assert.InRange(s.UpLossTotalPct, 12, 28);
        Assert.InRange(s.DownLossTotalPct, 12, 28);

        session.ArtificialLossPct = 0;
        for (int i = 0; i < 3; i++) { await Task.Delay(1000); s = session.TakeSample(); }
        Assert.Equal(0, s.DownLossPct);
        await session.StopAsync();
    }

    [Fact]
    public async Task TunnelSimulationsComeUpOnACleanLink()
    {
        var bfd = TunnelPresets.CreateDefaults().Single(t => t.Name.StartsWith("BFD"));
        bfd.Enabled = true;
        var (session, _, _) = await RunAsync(new TestConfig { UpMbps = 1, DownMbps = 1, BaselineSec = 0 }, 3, tunnels: new[] { bfd });
        var st = session.Tunnels.Single().GetStatus(Clock.NowUs);
        Assert.Equal(0, st.Drops);
        Assert.True(st.AvailabilityPct > 99);
    }

    [Fact]
    public async Task IdleBaseline_DelaysLoad_AndProducesBufferbloatGrade()
    {
        var (_, last, all) = await RunAsync(new TestConfig { Protocol = TransportKind.Udp, UpMbps = 20, DownMbps = 20, BaselineSec = 2 }, 7);
        Assert.True(all[0].BaselinePhase);
        Assert.True(all[0].DownMbps < 2, "no load during the idle baseline");
        AssertRate(last.DownMbps, 20);
        Assert.NotNull(last.BloatGrade);
    }

    [Fact]
    public async Task OldClientHello_IsAccepted_WithoutNewFeatures()
    {
        // A 1.1-style hello: no appVersion, no newer config fields.
        using var s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await s.ConnectAsync(IPAddress.Loopback, _server.Port);
        await s.SendAsync(new[] { Protocol.ConnControl });
        await s.SendAsync(Encoding.UTF8.GetBytes("{\"type\":\"hello\",\"version\":1,\"config\":{\"protocol\":\"Udp\",\"upMbps\":1,\"downMbps\":1}}\n"));
        var buf = new byte[8192];
        int n = await s.ReceiveAsync(buf).WaitAsync(TimeSpan.FromSeconds(5));
        var welcome = JsonSerializer.Deserialize<ControlMessage>(Encoding.UTF8.GetString(buf, 0, n).Trim(), Json.Options)!;
        Assert.Equal("welcome", welcome.Type);
        Assert.Equal(0, welcome.Config!.BaselineSec);     // old clients don't get an idle baseline
    }

    [Fact]
    public async Task PathMtu_MeasuresBothDirections()
    {
        var session = new ClientSession { MeasureMtu = false, AutoTraceroute = false };
        await session.StartAsync("127.0.0.1", _server.Port, null, new TestConfig { UpMbps = 1, DownMbps = 1, BaselineSec = 0 },
            Array.Empty<TunnelProfile>(), 0, CancellationToken.None, ipLookup: false);
        try
        {
            var r = await PathMtu.MeasureAsync(new IPEndPoint(IPAddress.Loopback, _server.Port), session.SessionId, 1, CancellationToken.None);
            Assert.True(r.Ok, r.Error);
            Assert.True(r.UpPathMtu >= 1500 && r.DownPathMtu >= 1500, r.Summary);
        }
        finally { await session.StopAsync(); }
    }

    [Fact]
    public async Task Traceroute_ReachesLoopbackInOneHop()
    {
        var r = await PathTracer.TraceAsync(IPAddress.Loopback, "test", maxHops: 5, probesPerHop: 2, timeoutMs: 500, resolveNames: false);
        Assert.Null(r.Error);
        Assert.True(r.ReachedTarget);
        Assert.Single(r.Hops);
        Assert.Equal(0, r.Hops[0].LossPct);
    }

    [Fact]
    public async Task Traceroute_StopsProbingPastTheTarget()
    {
        var oldProber = PathTracer.CustomProber;
        int oldParallel = PathTracer.MaxParallelHops;
        int calls = 0;
        PathTracer.MaxParallelHops = 2;
        PathTracer.CustomProber = (target, ttl, probes, timeout, ct) =>
        {
            Interlocked.Increment(ref calls);
            bool at = ttl >= 3;    // the target is three hops away
            return Task.FromResult(new HopProbeResult(at ? target : IPAddress.Parse($"10.0.0.{ttl}"), probes, probes, 1, 1, at, null));
        };
        try
        {
            var r = await PathTracer.TraceAsync(IPAddress.Parse("192.0.2.1"), "test", maxHops: 30, resolveNames: false);
            Assert.True(r.ReachedTarget);
            Assert.Equal(3, r.Hops.Count);
            Assert.InRange(calls, 3, 4);     // at most one hop already in flight past the target
        }
        finally
        {
            PathTracer.CustomProber = oldProber;
            PathTracer.MaxParallelHops = oldParallel;
        }
    }

    [Fact]
    public async Task DetailedStatistics_ComeFromBothEnds()
    {
        var session = new ClientSession { MeasureMtu = false, AutoTraceroute = false };
        await session.StartAsync("127.0.0.1", _server.Port, null,
            new TestConfig { Protocol = TransportKind.Mixed, UdpSharePct = 50, UpMbps = 10, DownMbps = 10, BaselineSec = 0 },
            Array.Empty<TunnelProfile>(), 10, CancellationToken.None, ipLookup: false);
        StatSample s = null!;
        for (int i = 0; i < 4; i++) { await Task.Delay(1000); s = session.TakeSample(); }
        await session.StopAsync("test done");

        var live = s.Detail!;
        Assert.True(live.ServerDetail);
        Assert.Equal(4, live.Columns.Count);
        var d = session.Record!.Detail!;
        var udpUp = d.Columns.Single(c => c.Transport == TransportKind.Udp && c.Upstream);
        var udpDown = d.Columns.Single(c => c.Transport == TransportKind.Udp && !c.Upstream);
        Assert.True(udpUp.Rx.Packets > 100, "server-side receive detail reached the client");
        Assert.True(udpUp.Rx.LossEvents > 0 && udpDown.Rx.LossEvents > 0, "artificial loss shows up as loss events both ways");
        Assert.InRange(udpUp.LossPct, 5, 15);
        Assert.True(udpDown.TxPackets > 100, "server's sender counters reached the client");
        var tcpUp = d.Columns.Single(c => c.Transport == TransportKind.Tcp && c.Upstream);
        var tcpDown = d.Columns.Single(c => c.Transport == TransportKind.Tcp && !c.Upstream);
        Assert.True(tcpUp.Rx.Bytes > 0 && tcpDown.Rx.Bytes > 0);
        Assert.NotNull(tcpUp.Tcp);       // client's kernel figures
        Assert.NotNull(tcpDown.Tcp);     // server's kernel figures, sent over the control connection
        Assert.True(tcpDown.Tcp!.Sockets > 0);
    }
}
