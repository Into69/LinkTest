using System.Net;
using LinkTest.Core;
using LinkTest.Core.Tunnels;

namespace LinkTest.Tests;

public class RecordTests
{
    static TestRecord Sample()
    {
        var r = new TestRecord
        {
            Id = "t1", StartedAt = new DateTime(2026, 11, 13, 10, 0, 0), EndedAt = new DateTime(2026, 11, 13, 10, 1, 0),
            EndReason = "Duration reached", AppVersion = "1.7.0", ServerVersion = "1.7.0", Server = "example (192.0.2.1:7447)",
            Config = new TestConfig { UpMbps = 10, DownMbps = 20 }, DurationSec = 60, UpMbpsAvg = 9.9, DownMbpsAvg = 19.8,
            UpLossPct = 0.5, DownLossPct = double.NaN, RttAvgMs = 12.3, BloatGrade = "B", IdleRttMs = 10, LoadedRttMs = 50,
            Summary = "summary <with> & chars",
            Mtu = new MtuResult(1472, 1452, 28, false),
            Traces = { new TraceResult(DateTime.Now, "start", IPAddress.Parse("192.0.2.1"),
                new[] { new TraceHop(1, IPAddress.Parse("10.0.0.1"), "gw", 5, 5, 1.2, 2.0), new TraceHop(2, null, null, 5, 0, double.NaN, double.NaN) }, true) },
            Tunnels = { new TunnelStatus("WireGuard", TunnelState.Up, "UP", "Stable", 100, 0, 0, 0, 0, 0, double.NaN, double.NaN, 1.2, "") },
            Classes = { new ClassSummary("UDP", TransportKind.Udp, 46, 100, 9.9, 0.5, 19.8, 0, 0.3, 12.3) },
            Events = { "10:00:00  started" },
        };
        r.Samples.Add(new StatSample { ElapsedSec = 1, UpMbps = 9.9, DownMbps = 19.8, RttMs = 12, UpLossPct = double.NaN });
        r.Samples.Add(new StatSample { ElapsedSec = 2, UpMbps = 10, DownMbps = 20, RttMs = 13, UpLossPct = 1 });
        return r;
    }

    [Fact]
    public void JsonRoundTripKeepsNaNAndAddresses()
    {
        var back = TestRecord.FromJson(Sample().ToJson())!;
        Assert.Equal(9.9, back.UpMbpsAvg);
        Assert.True(double.IsNaN(back.DownLossPct));
        Assert.Equal(2, back.Samples.T.Count);
        Assert.True(double.IsNaN(back.Samples.LossUp[0]));
        Assert.Equal(IPAddress.Parse("10.0.0.1"), back.Traces[0].Hops[0].Address);
        Assert.Null(back.Traces[0].Hops[1].Address);
        Assert.Equal(1452, back.Mtu!.DownPayload);
        Assert.Equal("Stable", back.Tunnels[0].Verdict);
        Assert.Equal(46, back.Classes[0].Dscp);
    }

    [Fact]
    public void ReportIsSelfContainedAndEscaped()
    {
        string html = ReportBuilder.Build(Sample());
        Assert.StartsWith("<!doctype html>", html);
        foreach (var section in new[] { "Throughput", "Latency", "Packet loss", "Test settings", "VPN tunnel simulations", "Path MTU", "Traceroutes" })
            Assert.Contains(section, html);
        Assert.Contains("summary &lt;with&gt; &amp; chars", html);
        Assert.DoesNotContain("<script", html);
        Assert.DoesNotContain("http://", html.Replace("http://www.w3.org", ""));    // no external resources
        Assert.Contains("<svg", html);
    }

    [Fact]
    public void CsvHasHeaderAndOneRowPerSample()
    {
        var lines = Sample().ToCsv().TrimEnd().Split('\n');
        Assert.Equal(3, lines.Length);
        Assert.StartsWith("time,elapsed_s,up_mbps", lines[0]);
        Assert.Contains(",9.9,19.8,", lines[1]);
    }

    [Fact]
    public void ResultStoreSavesListsAndDeletes()
    {
        var dir = Path.Combine(Path.GetTempPath(), "linktest-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new ResultStore(dir);
            string path = store.Save(Sample());
            var list = store.List();
            Assert.Single(list);
            Assert.Equal("t1", list[0].Summary.Id);
            Assert.Empty(list[0].Summary.Samples.T);              // the listing leaves out the bulky parts
            Assert.Equal(2, list[0].Load().Samples.T.Count);       // the full record is read on demand
            Assert.True(File.Exists(Path.Combine(dir, ".index.json")));

            // Served from the index the second time; a file changed outside the app is re-read.
            Assert.Equal("t1", store.List()[0].Summary.Id);
            var changed = Sample();
            changed.Id = "t2-changed";   // different length too, in case timestamps are coarse
            File.WriteAllText(path, changed.ToJson());
            Assert.Equal("t2-changed", store.List()[0].Summary.Id);

            ResultStore.Delete(path);
            Assert.Empty(store.List());
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void RecordSamplesAreACopy()
    {
        var a = new SampleSeries();
        a.Add(new StatSample { ElapsedSec = 1 });
        var b = a.Clone();
        a.Add(new StatSample { ElapsedSec = 2 });
        Assert.Single(b.T);
        Assert.Single(b.Mos);
    }

    [Theory]
    [InlineData(0, "0:00:00")]
    [InlineData(3725, "1:02:05")]
    [InlineData(93600, "26:00:00")]      // over a day: hours keep counting
    public void DurationsOverADayKeepTheirHours(double sec, string expected) =>
        Assert.Equal(expected, Clock.FormatDuration(sec));

    [Fact]
    public void ReportShowsLongDurations()
    {
        var r = Sample();
        r.DurationSec = 93600;
        Assert.Contains("26:00:00", ReportBuilder.Build(r));
    }
}
