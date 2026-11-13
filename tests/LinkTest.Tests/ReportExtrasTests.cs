using System.Net.Sockets;
using System.Text;
using LinkTest.Core;

namespace LinkTest.Tests;

public class ReportExtrasTests
{
    static TestRecord Moving(string family = "IPv4", double rtt = 20)
    {
        var r = new TestRecord { Id = family, StartedAt = new DateTime(2026, 11, 13, 10, 0, 0), Server = "srv (192.0.2.1:7447)", Family = family,
            Config = new TestConfig { UpMbps = 5, DownMbps = 5, BaselineSec = 0 }, DurationSec = 120, AppVersion = "1.9.0",
            UpMbpsAvg = 5, DownMbpsAvg = 5, UpLossPct = 0, DownLossPct = 0, RttAvgMs = rtt };
        for (int t = 1; t <= 120; t++)
        {
            r.Samples.Add(new StatSample { ElapsedSec = t, UpMbps = 5, DownMbps = 5, RttMs = t > 80 ? 200 : rtt, UpLossPct = t is > 40 and < 60 ? 0.5 : 0, DownLossPct = 0 });
            r.Samples.SetLocation(new GeoFix(DateTime.Now, 45.40 + t * 0.0002, -75.70 + t * 0.0003, 10));
        }
        r.Locations.Add(new GeoFix(r.StartedAt, 45.4002, -75.6997, 10));
        return r;
    }

    [Fact]
    public void RouteIsDrawnAndColouredByQuality()
    {
        var map = RouteMap.Build(Moving(), 600, 300)!;
        Assert.NotNull(map);
        Assert.Contains(map.Runs, x => x.Level == StatLevel.Good);
        Assert.Contains(map.Runs, x => x.Level == StatLevel.Fair);   // 0.5 % loss
        Assert.Contains(map.Runs, x => x.Level == StatLevel.Poor);   // 200 ms RTT
        Assert.InRange(map.DistanceM, 3000, 6000);
        Assert.All(map.Runs.SelectMany(x => x.Points), p => Assert.True(p.x >= 0 && p.x <= 600 && p.y >= 0 && p.y <= 300));
        Assert.Contains("<h2>Route</h2>", ReportBuilder.Build(Moving()));
        Assert.StartsWith("%PDF", Encoding.Latin1.GetString(PdfReportBuilder.Build(Moving())));
    }

    [Fact]
    public void StationaryTestHasNoRoute()
    {
        var r = Moving();
        for (int i = 0; i < r.Samples.Lat!.Count; i++) { r.Samples.Lat[i] = 45.4; r.Samples.Lon![i] = -75.7; }
        Assert.Null(RouteMap.Build(r, 600, 300));
    }

    [Fact]
    public void ComparisonPicksTheBetterFamily()
    {
        var c = new Comparison(Moving("IPv4", 20), Moving("IPv6", 60));
        var rtt = c.Lines.Single(l => l.Name == "RTT (avg)");
        Assert.Equal(-1, rtt.Better);   // IPv4 lower
        Assert.Contains("IPv4 was better", c.Headline);
        string html = ReportBuilder.BuildComparison(c);
        Assert.Contains("IPv4 vs IPv6", html);
        Assert.DoesNotContain("<script", html);
        Assert.StartsWith("%PDF-1.4", Encoding.Latin1.GetString(PdfReportBuilder.BuildComparison(c)));
    }

    [Fact]
    public async Task FamilyIsEnforced()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => ClientSession.ResolveAsync("127.0.0.1", CancellationToken.None, AddressFamily.InterNetworkV6));
        Assert.Equal(AddressFamily.InterNetworkV6, (await ClientSession.ResolveAsync("::1", CancellationToken.None, AddressFamily.InterNetworkV6)).AddressFamily);
        Assert.Equal(AddressFamily.InterNetwork, (await ClientSession.ResolveAsync("127.0.0.1", CancellationToken.None, AddressFamily.InterNetwork)).AddressFamily);
    }
}
