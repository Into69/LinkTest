using LinkTest.Core;

namespace LinkTest.Tests;

public class GeoTests
{
    static GeoFix At(double lat, double lon, int s = 0) => new(new DateTime(2026, 1, 1).AddSeconds(s), lat, lon, 10);

    [Fact]
    public void DistanceIsGreatCircle() =>
        Assert.InRange(At(45.4215, -75.6972).DistanceTo(At(45.5017, -73.5673)), 165_000, 168_000);   // Ottawa - Montreal

    [Fact]
    public void DescribeSaysStationaryWithinAccuracy()
    {
        Assert.EndsWith("stationary", GeoFix.Describe(new[] { At(45, -75), At(45.0001, -75.0001, 10) }));
        Assert.Contains("moved", GeoFix.Describe(new[] { At(45, -75), At(45.01, -75, 60), At(45.02, -75, 120) }));
    }

    [Fact]
    public void SeriesLocationStaysAlignedWithSamples()
    {
        var s = new SampleSeries();
        s.Add(new StatSample { ElapsedSec = 1 }); s.SetLocation(null);
        s.Add(new StatSample { ElapsedSec = 2 }); s.SetLocation(null);
        s.Add(new StatSample { ElapsedSec = 3 }); s.SetLocation(At(45, -75));
        Assert.Equal(3, s.Lat!.Count);
        Assert.True(double.IsNaN(s.Lat[0]));
        Assert.Equal(45, s.Lat[2]);
    }

    [Fact]
    public void InvalidFixesAreRejected() => Assert.False(new GeoFix(DateTime.Now, double.NaN, 0).IsValid);
}
