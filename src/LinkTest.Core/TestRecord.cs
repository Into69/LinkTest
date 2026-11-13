using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using LinkTest.Core.Tunnels;

namespace LinkTest.Core;

/// <summary>Everything about one finished test, saved as JSON and used for reports and CSV export.</summary>
public sealed class TestRecord
{
    public int FormatVersion { get; set; } = 1;
    public string Id { get; set; } = "";
    public DateTime StartedAt { get; set; }
    public DateTime EndedAt { get; set; }
    public string EndReason { get; set; } = "";
    public string AppVersion { get; set; } = "";
    public string? ServerVersion { get; set; }
    public string Server { get; set; } = "";
    public TestConfig Config { get; set; } = new();

    public double DurationSec { get; set; }
    public double UpMbpsAvg { get; set; } = double.NaN;
    public double DownMbpsAvg { get; set; } = double.NaN;
    public double UpLossPct { get; set; } = double.NaN;
    public double DownLossPct { get; set; } = double.NaN;
    public double RttAvgMs { get; set; } = double.NaN;
    public double RttMinMs { get; set; } = double.NaN;
    public double RttMaxMs { get; set; } = double.NaN;
    public double JitterUpMs { get; set; } = double.NaN;
    public double JitterDownMs { get; set; } = double.NaN;
    public double MaxGapMs { get; set; } = double.NaN;
    public double MosAvg { get; set; } = double.NaN;
    public string? BloatGrade { get; set; }
    public double IdleRttMs { get; set; } = double.NaN;
    public double LoadedRttMs { get; set; } = double.NaN;

    public string Summary { get; set; } = "";
    public ConnectionInfo? Connection { get; set; }
    public MtuResult? Mtu { get; set; }
    public List<TraceResult> Traces { get; set; } = new();
    public List<TunnelStatus> Tunnels { get; set; } = new();
    public List<ClassSummary> Classes { get; set; } = new();
    public SampleSeries Samples { get; set; } = new();
    public List<string> Events { get; set; } = new();

    public string Mode => Config.DscpSimulate ? "DSCP simulate"
        : Config.Protocol == TransportKind.Mixed ? $"Mixed {Config.UdpSharePct:0}% UDP" : Config.Protocol.ToString().ToUpperInvariant();

    public string ToJson() => JsonSerializer.Serialize(this, Json.Records);
    public static TestRecord? FromJson(string json) => JsonSerializer.Deserialize<TestRecord>(json, Json.Records);

    /// <summary>Per-second figures as CSV.</summary>
    public string ToCsv()
    {
        var inv = CultureInfo.InvariantCulture;
        static string F(double v) => double.IsFinite(v) ? v.ToString("0.###", CultureInfo.InvariantCulture) : "";
        var sb = new StringBuilder();
        sb.AppendLine("time,elapsed_s,up_mbps,down_mbps,rtt_ms,rtt_max_ms,jitter_up_ms,jitter_down_ms,loss_up_pct,loss_down_pct,gap_up_ms,gap_down_ms,mos");
        var s = Samples;
        for (int i = 0; i < s.T.Count; i++)
            sb.AppendLine(string.Join(",", StartedAt.AddSeconds(s.T[i]).ToString("yyyy-MM-dd HH:mm:ss", inv), F(s.T[i]),
                F(s.Up[i]), F(s.Down[i]), F(s.Rtt[i]), F(s.RttMax[i]), F(s.JitUp[i]), F(s.JitDown[i]),
                F(s.LossUp[i]), F(s.LossDown[i]), F(s.GapUp[i]), F(s.GapDown[i]), F(s.Mos[i])));
        return sb.ToString();
    }

    public string SuggestedFileName(string ext)
    {
        string host = new string(Server.Split(' ')[0].Select(c => char.IsLetterOrDigit(c) || c is '.' or '-' ? c : '_').ToArray());
        return $"LinkTest_{StartedAt:yyyyMMdd_HHmmss}_{host}.{ext}";
    }
}

/// <summary>Final per-class figures.</summary>
public sealed record ClassSummary(string Name, TransportKind Transport, int Dscp, double SharePct,
    double UpMbps, double UpLossPct, double DownMbps, double DownLossPct, double JitterDownMs, double RttAvgMs);

/// <summary>Per-second samples stored column-wise (compact JSON).</summary>
public sealed class SampleSeries
{
    public List<double> T { get; set; } = new();
    public List<double> Up { get; set; } = new();
    public List<double> Down { get; set; } = new();
    public List<double> Rtt { get; set; } = new();
    public List<double> RttMax { get; set; } = new();
    public List<double> JitUp { get; set; } = new();
    public List<double> JitDown { get; set; } = new();
    public List<double> LossUp { get; set; } = new();
    public List<double> LossDown { get; set; } = new();
    public List<double> GapUp { get; set; } = new();
    public List<double> GapDown { get; set; } = new();
    public List<double> Mos { get; set; } = new();

    public void Add(StatSample s)
    {
        static double R(double v) => double.IsFinite(v) ? Math.Round(v, 3) : double.NaN;
        T.Add(Math.Round(s.ElapsedSec, 1));
        Up.Add(R(s.UpMbps)); Down.Add(R(s.DownMbps));
        Rtt.Add(R(s.RttMs)); RttMax.Add(R(s.RttMaxMs));
        JitUp.Add(R(s.UpJitterMs)); JitDown.Add(R(s.DownJitterMs));
        LossUp.Add(R(s.UpLossPct)); LossDown.Add(R(s.DownLossPct));
        GapUp.Add(R(s.UpMaxGapMs)); GapDown.Add(R(s.DownMaxGapMs));
        Mos.Add(R(s.Mos));
    }
}

/// <summary>Saved test results: one JSON file per test in a folder.</summary>
public sealed class ResultStore
{
    public ResultStore(string folder) => Folder = folder;
    public string Folder { get; }

    public string Save(TestRecord r)
    {
        Directory.CreateDirectory(Folder);
        string path = Path.Combine(Folder, r.SuggestedFileName("json"));
        File.WriteAllText(path, r.ToJson());
        return path;
    }

    /// <summary>Saved tests, newest first (unreadable files are skipped).</summary>
    public List<(string path, TestRecord record)> List()
    {
        var list = new List<(string, TestRecord)>();
        if (!Directory.Exists(Folder)) return list;
        foreach (var f in Directory.EnumerateFiles(Folder, "LinkTest_*.json"))
        {
            try { if (TestRecord.FromJson(File.ReadAllText(f)) is { } r) list.Add((f, r)); }
            catch { }
        }
        return list.OrderByDescending(x => x.Item2.StartedAt).ToList();
    }

    public static void Delete(string path) { try { File.Delete(path); } catch { } }
}

/// <summary>JSON converter so IP addresses inside traceroute results can be saved.</summary>
public sealed class IPAddressJsonConverter : JsonConverter<IPAddress>
{
    public override IPAddress? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.String && IPAddress.TryParse(reader.GetString(), out var a) ? a : null;

    public override void Write(Utf8JsonWriter writer, IPAddress value, JsonSerializerOptions options) => writer.WriteStringValue(value.ToString());
}
