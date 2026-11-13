namespace LinkTest.Core;

/// <summary>
/// Bufferbloat: how much round-trip time grows when the link is loaded. The idle RTT is measured during a
/// short baseline at the start of the test (heartbeats only), the loaded RTT afterwards.
/// Grades use the same thresholds as common online bufferbloat tests.
/// </summary>
public sealed class BufferbloatMeter
{
    readonly List<double> _idle = new();
    double _loadedSum;
    int _loadedCount;

    /// <summary>Seconds of load to skip before counting (TCP ramp-up).</summary>
    public const double RampSec = 2;

    public static string Grade(double increaseMs) => increaseMs switch
    {
        < 5 => "A+",
        < 30 => "A",
        < 60 => "B",
        < 200 => "C",
        < 400 => "D",
        _ => "F",
    };

    public static string Describe(string grade) => grade switch
    {
        "A+" or "A" => "excellent - latency barely rises under load",
        "B" => "good",
        "C" => "noticeable lag under load (calls / games may suffer)",
        "D" => "poor - large queues build up under load",
        "F" => "severe bufferbloat",
        _ => "",
    };

    public void Add(double elapsedSec, double baselineSec, double rttMs)
    {
        if (!double.IsFinite(rttMs) || baselineSec <= 0) return;
        if (elapsedSec <= baselineSec) _idle.Add(rttMs);
        else if (elapsedSec > baselineSec + RampSec) { _loadedSum += rttMs; _loadedCount++; }
    }

    public double IdleMs
    {
        get
        {
            if (_idle.Count == 0) return double.NaN;
            var s = _idle.OrderBy(x => x).ToList();
            return s[s.Count / 2];
        }
    }

    public double LoadedMs => _loadedCount > 0 ? _loadedSum / _loadedCount : double.NaN;
    public double IncreaseMs => double.IsFinite(IdleMs) && double.IsFinite(LoadedMs) ? Math.Max(0, LoadedMs - IdleMs) : double.NaN;
    public string? CurrentGrade => double.IsFinite(IncreaseMs) ? Grade(IncreaseMs) : null;
}
