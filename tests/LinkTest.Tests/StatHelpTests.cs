using LinkTest.Core;

namespace LinkTest.Tests;

public class StatHelpTests
{
    [Fact]
    public void EveryStatisticsRowHasHelpWithAnIdealOrNote()
    {
        var d = LinkDetail.Build(new[]
        {
            (TransportKind.Udp, (ClassDetail?)new ClassDetail(), new ClassDetail()),
            (TransportKind.Tcp, (ClassDetail?)new ClassDetail { Tcp = new TcpDetail() }, new ClassDetail { Tcp = new TcpDetail() }),
        }, 10, true);
        foreach (var (_, name, _, _) in d.Rows())
            Assert.True(StatHelp.Detail(name) != null, $"no help for Statistics row '{name}'");
    }

    [Theory]
    [InlineData("tile.upload")] [InlineData("tile.download")] [InlineData("tile.rtt")] [InlineData("tile.jitter")] [InlineData("tile.loss")]
    [InlineData("tile.gap")] [InlineData("tile.retrans")] [InlineData("tile.mos")] [InlineData("tile.bloat")] [InlineData("tile.elapsed")]
    [InlineData("chart.throughput")] [InlineData("chart.latency")] [InlineData("chart.loss")]
    public void TilesAndChartsHaveHelp(string key)
    {
        var i = StatHelp.Get(key);
        Assert.NotNull(i);
        Assert.Contains("Ideal:", i!.ToText());
    }

    [Theory]
    [InlineData("classes", 13)] [InlineData("tunnels", 12)] [InlineData("server", 14)]
    public void TableGroupsCoverEveryColumn(string group, int columns) => Assert.Equal(columns, StatHelp.Group(group).Count);

    [Fact]
    public void RangesReadAsGoodFairPoor()
    {
        string t = StatHelp.Get("tile.rtt")!.ToText();
        Assert.Contains("Good: under 50 ms", t);
        Assert.Contains("Poor:", t);
    }
}
