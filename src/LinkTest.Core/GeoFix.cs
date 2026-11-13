using System.Globalization;
using System.Text.Json.Serialization;

namespace LinkTest.Core;

/// <summary>
/// One location fix of the client device, recorded with the test data when the user turns location reporting on
/// (off by default). Coordinates are WGS84 degrees; NaN = unknown.
/// </summary>
public sealed record GeoFix(DateTime At, double Lat, double Lon, double AccuracyM = double.NaN, double AltitudeM = double.NaN,
    double SpeedMps = double.NaN, string Source = "")
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    [JsonIgnore]
    public bool IsValid => double.IsFinite(Lat) && double.IsFinite(Lon) && Math.Abs(Lat) <= 90 && Math.Abs(Lon) <= 180;

    /// <summary>"45.42153, -75.69719 (±12 m)".</summary>
    [JsonIgnore]
    public string Text => $"{Lat.ToString("0.00000", Inv)}, {Lon.ToString("0.00000", Inv)}" +
                          (double.IsFinite(AccuracyM) ? $" (±{AccuracyM.ToString("0", Inv)} m)" : "");

    /// <summary>OpenStreetMap link to the point.</summary>
    [JsonIgnore]
    public string MapUrl => $"https://www.openstreetmap.org/?mlat={Lat.ToString("0.000000", Inv)}&mlon={Lon.ToString("0.000000", Inv)}#map=16/{Lat.ToString("0.00000", Inv)}/{Lon.ToString("0.00000", Inv)}";

    /// <summary>Great-circle distance in metres.</summary>
    public double DistanceTo(GeoFix o)
    {
        const double R = 6371000;
        double dLat = (o.Lat - Lat) * Math.PI / 180, dLon = (o.Lon - Lon) * Math.PI / 180;
        double a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                   Math.Cos(Lat * Math.PI / 180) * Math.Cos(o.Lat * Math.PI / 180) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return 2 * R * Math.Asin(Math.Min(1, Math.Sqrt(a)));
    }

    /// <summary>Summary of a track: start point, and how far it moved if it did.</summary>
    public static string Describe(IReadOnlyList<GeoFix> fixes)
    {
        var valid = fixes.Where(f => f.IsValid).ToList();
        if (valid.Count == 0) return "";
        double path = 0, maxFromStart = 0;
        for (int i = 1; i < valid.Count; i++)
        {
            path += valid[i - 1].DistanceTo(valid[i]);
            maxFromStart = Math.Max(maxFromStart, valid[0].DistanceTo(valid[i]));
        }
        string start = valid[0].Text;
        // Ignore wander within the fixes' accuracy.
        double noise = Math.Max(50, valid.Where(f => double.IsFinite(f.AccuracyM)).Select(f => f.AccuracyM).DefaultIfEmpty(0).Max());
        return maxFromStart <= noise ? $"{start}, stationary"
            : $"{start} at the start; moved {Km(path)} during the test (up to {Km(maxFromStart)} from the start)";
    }

    static string Km(double m) => m >= 1000 ? (m / 1000).ToString("0.0", Inv) + " km" : m.ToString("0", Inv) + " m";
}

/// <summary>A change of the client's public IP address during a test.</summary>
public sealed record IpChange(DateTime At, string From, string To, string Details);
