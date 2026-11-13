using LinkTest.Core;

namespace LinkTest.Tests;

public class DetailTests
{
    static void Feed(RxTracker rx, IEnumerable<long> seqs, Func<long, long>? extraDelayUs = null, int len = 200)
    {
        long t = 1_000_000;
        foreach (var q in seqs)
        {
            t += 1000;
            rx.Record(0, q, sendUs: t - 5000, nowUs: t + (extraDelayUs?.Invoke(q) ?? 0), len);
        }
    }

    [Fact]
    public void LossBurstsAreCountedByLength()
    {
        var lost = new HashSet<long> { 10, 20, 21, 22 };
        for (long q = 50; q < 60; q++) lost.Add(q);
        var rx = new RxTracker();
        Feed(rx, Enumerable.Range(0, 100).Select(i => (long)i).Where(q => !lost.Contains(q)));
        var d = rx.Detail(2_000_000);
        Assert.Equal(14, d.Lost);
        Assert.Equal(3, d.LossEvents);
        Assert.Equal(10, d.LongestLossBurst);
        Assert.Equal(new long[] { 1, 1, 1, 0, 0 }, d.BurstHist);
    }

    [Fact]
    public void LossBurstsSurviveTheSettleWindow()
    {
        // Bursts far behind the newest packet are settled during recording; the result must be the same.
        var rx = new RxTracker();
        Feed(rx, Enumerable.Range(0, 3000).Select(i => (long)i).Where(q => q is not (100 or 101 or 102) and not 1500));
        var d = rx.Detail(10_000_000);
        Assert.Equal(2, d.LossEvents);
        Assert.Equal(3, d.LongestLossBurst);
        Assert.Equal(4, d.Lost);
    }

    [Fact]
    public void ReorderedPacketsAreNotLoss()
    {
        var rx = new RxTracker();
        Feed(rx, new long[] { 0, 1, 2, 5, 3, 4, 6, 7 });
        var d = rx.Detail(2_000_000);
        Assert.Equal(0, d.Lost);
        Assert.Equal(0, d.LossEvents);
        Assert.Equal(2, d.OutOfOrder);
        Assert.Equal(2, d.MaxReorder);
    }

    [Fact]
    public void DelayVariationIsRelativeToTheMinimum()
    {
        var rx = new RxTracker();
        // Constant transit, except packet 50 which is 30 ms late.
        Feed(rx, Enumerable.Range(0, 100).Select(i => (long)i), q => q == 50 ? 30_000 : 0);
        var d = rx.Detail(2_000_000);
        Assert.InRange(d.DelayMaxUs, 29_000, 31_000);
        Assert.Equal(99, d.DelayHist[0]);                       // everything else within 0.5 ms
        Assert.InRange(d.DelayPercentileMs(50), 0, 0.5);
    }

    [Fact]
    public void ColumnsSplitByTransportAndDirection()
    {
        var local = new ClassDetail { TxPackets = 10, Rx = new RxDetail { Packets = 9, Unique = 9, Lost = 1, Bytes = 900 } };
        var server = new ClassDetail { TxPackets = 9, Rx = new RxDetail { Packets = 8, Unique = 8, Lost = 2, Bytes = 800 } };
        var tcpLocal = new ClassDetail { Tcp = new TcpDetail { Sockets = 2, BytesRetrans = 100, BytesSent = 10_000 } };
        var d = LinkDetail.Build(new[] { (TransportKind.Udp, (ClassDetail?)server, local), (TransportKind.Tcp, (ClassDetail?)null, tcpLocal) }, 10, true);
        Assert.Equal(4, d.Columns.Count);
        var udpUp = d.Columns.Single(c => c.Transport == TransportKind.Udp && c.Upstream);
        Assert.Equal(2, udpUp.Rx.Lost);          // upload loss comes from the server's receive side
        Assert.Equal(10, udpUp.TxPackets);       // ... and the client's sender
        var udpDown = d.Columns.Single(c => c.Transport == TransportKind.Udp && !c.Upstream);
        Assert.Equal(1, udpDown.Rx.Lost);
        Assert.Equal(9, udpDown.TxPackets);
        var tcpUp = d.Columns.Single(c => c.Transport == TransportKind.Tcp && c.Upstream);
        Assert.Equal(100, tcpUp.Tcp!.BytesRetrans);
        Assert.Contains("1%", string.Join(" ", d.Rows().Single(r => r.name == "Retransmitted").values));
        Assert.Contains("TCP ↑ upload", d.Format());
    }

    [Fact]
    public void RecordKeepsDetailThroughJson()
    {
        var r = new TestRecord { Id = "x", Detail = LinkDetail.Build(new[] { (TransportKind.Udp, (ClassDetail?)new ClassDetail(), new ClassDetail()) }, 5, true) };
        var back = TestRecord.FromJson(r.ToJson())!;
        Assert.Equal(2, back.Detail!.Columns.Count);
        Assert.Contains("Detailed statistics", ReportBuilder.Build(back));
    }
}
