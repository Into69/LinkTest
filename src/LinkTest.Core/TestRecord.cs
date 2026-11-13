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
    /// <summary>Per traffic class, per second (1.8+; empty in older records). Same order as <see cref="Classes"/>.</summary>
    public List<ClassSeries> ClassSeries { get; set; } = new();
    public List<string> Events { get; set; } = new();
    /// <summary>Detailed UDP / TCP statistics in both directions at the end of the test (1.8+; null in older records).</summary>
    public LinkDetail? Detail { get; set; }
    /// <summary>Device location fixes (only when location reporting was turned on; empty otherwise).</summary>
    public List<GeoFix> Locations { get; set; } = new();
    /// <summary>Changes of the client's public IP address during the test.</summary>
    public List<IpChange> IpChanges { get; set; } = new();
    /// <summary>"IPv4" or "IPv6" (1.9+).</summary>
    public string? Family { get; set; }
    /// <summary>Seconds before the measured part of the test (idle baseline + capacity probe); older records: 0.</summary>
    public double WarmupSec { get; set; }
    /// <summary>Capacity found by the capacity test (NaN when not run).</summary>
    public double CapacityUpMbps { get; set; } = double.NaN;
    public double CapacityDownMbps { get; set; } = double.NaN;
    public bool CapacityUpAtLimit { get; set; }
    public bool CapacityDownAtLimit { get; set; }
    /// <summary>Seconds to leave out when judging the test: the warm-up, or the idle baseline for older records.</summary>
    public double JudgeAfterSec => Math.Max(WarmupSec, Config.BaselineSec);

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
        var s = Samples;
        bool geo = s.Lat != null;
        static string G(List<double>? v, int i) => v != null && i < v.Count && double.IsFinite(v[i]) ? v[i].ToString("0.######", CultureInfo.InvariantCulture) : "";
        sb.AppendLine("time,elapsed_s,up_mbps,down_mbps,rtt_ms,rtt_max_ms,jitter_up_ms,jitter_down_ms,loss_up_pct,loss_down_pct,gap_up_ms,gap_down_ms,mos" +
                      (geo ? ",lat,lon" : ""));
        for (int i = 0; i < s.T.Count; i++)
            sb.AppendLine(string.Join(",", StartedAt.AddSeconds(s.T[i]).ToString("yyyy-MM-dd HH:mm:ss", inv), F(s.T[i]),
                F(s.Up[i]), F(s.Down[i]), F(s.Rtt[i]), F(s.RttMax[i]), F(s.JitUp[i]), F(s.JitDown[i]),
                F(s.LossUp[i]), F(s.LossDown[i]), F(s.GapUp[i]), F(s.GapDown[i]), F(s.Mos[i])) +
                (geo ? "," + G(s.Lat, i) + "," + G(s.Lon, i) : ""));
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
    /// <summary>Device position each second (only when location reporting was on; null otherwise).</summary>
    public List<double>? Lat { get; set; }
    public List<double>? Lon { get; set; }

    /// <summary>Records the device position for the sample just added (call after <see cref="Add"/>).</summary>
    public void SetLocation(GeoFix? f)
    {
        if (f == null && Lat == null) return;
        Lat ??= new List<double>();
        Lon ??= new List<double>();
        while (Lat.Count < T.Count - 1) { Lat.Add(double.NaN); Lon.Add(double.NaN); }
        Lat.Add(f?.IsValid == true ? Math.Round(f.Lat, 6) : double.NaN);
        Lon.Add(f?.IsValid == true ? Math.Round(f.Lon, 6) : double.NaN);
    }

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

    /// <summary>Independent copy (the live session keeps appending to its own).</summary>
    public SampleSeries Clone() => new()
    {
        T = T.ToList(), Up = Up.ToList(), Down = Down.ToList(), Rtt = Rtt.ToList(), RttMax = RttMax.ToList(),
        JitUp = JitUp.ToList(), JitDown = JitDown.ToList(), LossUp = LossUp.ToList(), LossDown = LossDown.ToList(),
        GapUp = GapUp.ToList(), GapDown = GapDown.ToList(), Mos = Mos.ToList(), Lat = Lat?.ToList(), Lon = Lon?.ToList(),
    };
}

/// <summary>Per-second figures of one traffic class (column-wise; NaN = not measured that second).</summary>
public sealed class ClassSeries
{
    public string Name { get; set; } = "";
    public List<double> Up { get; set; } = new();
    public List<double> Down { get; set; } = new();
    public List<double> LossUp { get; set; } = new();
    public List<double> LossDown { get; set; } = new();
    public List<double> JitUp { get; set; } = new();
    public List<double> JitDown { get; set; } = new();
    public List<double> Rtt { get; set; } = new();
    /// <summary>TCP retransmitted kilobytes in the second (upload / download).</summary>
    public List<double> RetransUpKB { get; set; } = new();
    public List<double> RetransDownKB { get; set; } = new();

    public void Add(ClassStat k)
    {
        static double R(double v) => double.IsFinite(v) ? Math.Round(v, 3) : double.NaN;
        Up.Add(R(k.UpMbps)); Down.Add(R(k.DownMbps));
        LossUp.Add(R(k.UpLossPct)); LossDown.Add(R(k.DownLossPct));
        JitUp.Add(R(k.UpJitterMs)); JitDown.Add(R(k.DownJitterMs));
        Rtt.Add(R(k.RttMs));
        RetransUpKB.Add(Math.Round(k.UpRetransBytes / 1000.0, 1)); RetransDownKB.Add(Math.Round(k.DownRetransBytes / 1000.0, 1));
    }

    public ClassSeries Clone() => new()
    {
        Name = Name, Up = Up.ToList(), Down = Down.ToList(), LossUp = LossUp.ToList(), LossDown = LossDown.ToList(),
        JitUp = JitUp.ToList(), JitDown = JitDown.ToList(), Rtt = Rtt.ToList(), RetransUpKB = RetransUpKB.ToList(), RetransDownKB = RetransDownKB.ToList(),
    };
}

/// <summary>A saved test as listed: its file and the record without the bulky parts (samples, events, traceroutes).</summary>
public sealed record SavedResult(string Path, TestRecord Summary)
{
    /// <summary>Reads the full record from disk.</summary>
    public TestRecord Load() => TestRecord.FromJson(File.ReadAllText(Path)) ?? throw new InvalidDataException("The saved result could not be read.");
}

/// <summary>
/// Saved test results: one JSON file per test in a folder. Listing uses a small summary index
/// (<c>.index.json</c>) so long tests don't have to be read in full; files changed or added outside the app are re-read.
/// </summary>
public sealed class ResultStore
{
    const string IndexName = ".index.json";

    public ResultStore(string folder) => Folder = folder;
    public string Folder { get; }

    sealed class IndexEntry
    {
        public long Length { get; set; }
        public long WriteTicks { get; set; }
        public TestRecord Summary { get; set; } = new();
    }

    public string Save(TestRecord r)
    {
        Directory.CreateDirectory(Folder);
        string path = Path.Combine(Folder, r.SuggestedFileName("json"));
        File.WriteAllText(path, r.ToJson());
        return path;
    }

    /// <summary>Saved tests, newest first (unreadable files are skipped).</summary>
    public List<SavedResult> List()
    {
        var list = new List<SavedResult>();
        if (!Directory.Exists(Folder)) return list;
        var index = ReadIndex();
        var fresh = new Dictionary<string, IndexEntry>(StringComparer.OrdinalIgnoreCase);
        bool changed = false;
        foreach (var f in Directory.EnumerateFiles(Folder, "LinkTest_*.json"))
        {
            try
            {
                var fi = new FileInfo(f);
                if (!index.TryGetValue(fi.Name, out var e) || e.Length != fi.Length || e.WriteTicks != fi.LastWriteTimeUtc.Ticks)
                {
                    if (TestRecord.FromJson(File.ReadAllText(f)) is not { } r) continue;
                    r.Samples = new SampleSeries();      // freshly read: strip it in place for the index
                    r.Events = new List<string>();
                    r.Traces = new List<TraceResult>();
                    r.Detail = null;
                    r.ClassSeries = new List<ClassSeries>();
                    r.Locations = r.Locations.Take(1).ToList();
                    e = new IndexEntry { Length = fi.Length, WriteTicks = fi.LastWriteTimeUtc.Ticks, Summary = r };
                    changed = true;
                }
                fresh[fi.Name] = e;
                list.Add(new SavedResult(f, e.Summary));
            }
            catch { }
        }
        if (changed || fresh.Count != index.Count) WriteIndex(fresh);
        return list.OrderByDescending(x => x.Summary.StartedAt).ToList();
    }

    Dictionary<string, IndexEntry> ReadIndex()
    {
        try
        {
            string p = Path.Combine(Folder, IndexName);
            if (File.Exists(p) && JsonSerializer.Deserialize<Dictionary<string, IndexEntry>>(File.ReadAllText(p), Json.Records) is { } d)
                return new Dictionary<string, IndexEntry>(d, StringComparer.OrdinalIgnoreCase);
        }
        catch { }
        return new Dictionary<string, IndexEntry>(StringComparer.OrdinalIgnoreCase);
    }

    void WriteIndex(Dictionary<string, IndexEntry> index)
    {
        try
        {
            string p = Path.Combine(Folder, IndexName), tmp = p + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(index, Json.Records));
            File.Move(tmp, p, true);
            File.SetAttributes(p, File.GetAttributes(p) | FileAttributes.Hidden);
        }
        catch { }
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
