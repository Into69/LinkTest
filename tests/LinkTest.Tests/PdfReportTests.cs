using System.Text;
using System.Text.RegularExpressions;
using LinkTest.Core;

namespace LinkTest.Tests;

public class PdfReportTests
{
    /// <summary>A DSCP-simulate record where voice loses 3% of its packets for the second half of the test.</summary>
    static TestRecord Record(bool voiceLoss)
    {
        var cfg = new TestConfig { DscpSimulate = true, UpMbps = 10, DownMbps = 10, BaselineSec = 2 };
        var defs = TrafficClasses.Resolve(cfg);
        var r = new TestRecord { Id = "p1", StartedAt = new DateTime(2026, 11, 13, 9, 0, 0), Server = "srv (192.0.2.1:7447)", Config = cfg, DurationSec = 60, AppVersion = "1.8.0" };
        for (int i = 0; i < defs.Count; i++)
        {
            var d = defs[i];
            double target = 10 * d.SharePct / 100;
            bool udp = d.Transport == TransportKind.Udp;
            var ser = new ClassSeries { Name = d.Name };
            for (int t = 1; t <= 60; t++)
            {
                bool lossy = voiceLoss && d.Name == "Voice" && t > 30;
                double loss = udp ? (lossy ? 3 : 0) : double.NaN;
                double rate = t <= 2 ? 0 : target;
                ser.Add(new ClassStat(d.Name, d.Transport, d.Dscp, d.SharePct, rate, loss, udp ? 2 : double.NaN, rate, loss, udp ? 2 : double.NaN, 20, 0, 0, 0, 0));
            }
            r.ClassSeries.Add(ser);
            r.Classes.Add(new ClassSummary(d.Name, d.Transport, d.Dscp, d.SharePct, target, udp ? (voiceLoss && d.Name == "Voice" ? 1.5 : 0) : double.NaN,
                target, udp ? (voiceLoss && d.Name == "Voice" ? 1.5 : 0) : double.NaN, 2, 20));
        }
        for (int t = 1; t <= 60; t++) r.Samples.Add(new StatSample { ElapsedSec = t, UpMbps = t <= 2 ? 0 : 10, DownMbps = t <= 2 ? 0 : 10, RttMs = 20, UpLossPct = 0 });
        return r;
    }

    [Fact]
    public void CleanLinkMaintainsEveryTrafficType()
    {
        var a = TrafficAssessment.Evaluate(Record(voiceLoss: false));
        Assert.Equal(10, a.Count);
        Assert.All(a.Where(x => x.Name != "Scavenger"), x => Assert.Equal(TrafficVerdict.Good, x.Verdict));
        Assert.Equal(TrafficVerdict.Info, a.Single(x => x.Name == "Scavenger").Verdict);
        Assert.All(a.Where(x => x.Name != "Scavenger"), x => Assert.Equal(100, x.WithinPct));
        Assert.StartsWith("The link maintained all 9", TrafficAssessment.Headline(a));
    }

    [Fact]
    public void VoiceLossIsCaughtPerSecond()
    {
        var a = TrafficAssessment.Evaluate(Record(voiceLoss: true));
        var voice = a.Single(x => x.Name == "Voice");
        Assert.Equal(TrafficVerdict.Poor, voice.Verdict);       // over the limit about half the time
        Assert.Equal(29, voice.LossSeconds);                    // 5 s window: the first lossy second averages 0.6%
        Assert.Equal(29, voice.LongestBadSec);
        Assert.InRange(voice.WithinPct, 45, 52);
        Assert.Equal(TrafficVerdict.Good, a.Single(x => x.Name == "Bulk data").Verdict);
        Assert.Contains("8 of 9", TrafficAssessment.Headline(a));
    }

    [Fact]
    public void RequirementsFollowTheMarking()
    {
        Assert.Equal("Voice", TrafficRequirement.For(new TrafficClass { Dscp = 46 }, true).Kind);
        Assert.Equal("Bulk data", TrafficRequirement.For(new TrafficClass { Dscp = 10, Transport = TransportKind.Tcp }, true).Kind);
        Assert.True(TrafficRequirement.For(new TrafficClass { Dscp = 8 }, true).Informational);
        Assert.Equal("Real-time UDP", TrafficRequirement.For(new TrafficClass { Transport = TransportKind.Udp }, false).Kind);
        Assert.Equal("TCP application", TrafficRequirement.For(new TrafficClass { Transport = TransportKind.Tcp }, false).Kind);
    }

    [Fact]
    public void PdfIsWellFormed()
    {
        byte[] pdf = PdfReportBuilder.Build(Record(voiceLoss: true));
        string s = Encoding.Latin1.GetString(pdf);
        Assert.StartsWith("%PDF-1.4", s);
        Assert.EndsWith("%%EOF\n", s);
        // Every xref offset must point at "N 0 obj".
        int xref = int.Parse(Regex.Match(s, @"startxref\n(\d+)").Groups[1].Value);
        var lines = s[xref..].Split('\n');
        int count = int.Parse(lines[1].Split(' ')[1]);
        for (int i = 1; i < count; i++)
        {
            int off = int.Parse(lines[2 + i][..10]);
            Assert.StartsWith($"{i} 0 obj", s[off..]);
        }
        Assert.Matches(@"/Count [1-9]", s);
    }

    [Fact]
    public void TextOutsideWinAnsiIsReplaced()
    {
        Assert.Equal("(up <= 5 \\(a\\))", PdfText.Literal("↑ ≤ 5 (a)"));
        Assert.True(PdfText.Width("WWW", 10, true) > PdfText.Width("iii", 10, true));
        Assert.EndsWith("...", PdfText.Fit("a long piece of text", 30, 10, false));
    }
}
