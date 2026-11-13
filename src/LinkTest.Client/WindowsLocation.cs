using LinkTest.Core;
using Windows.Devices.Geolocation;

namespace LinkTest.Client;

/// <summary>
/// Reads this PC's position from the Windows location service (GPS, Wi-Fi positioning or IP), only when the user
/// turned location reporting on. Needs Windows location on and "Let desktop apps access your location" allowed.
/// </summary>
static class WindowsLocation
{
    public const string PermissionHint =
        "Windows location is off, or desktop apps aren't allowed to use it: Settings > Privacy & security > Location " +
        "(turn on Location services and \"Let desktop apps access your location\").";

    /// <summary>Reports a fix to the session every <paramref name="intervalSec"/> seconds until cancelled.</summary>
    public static async Task RunAsync(ClientSession session, Action<string> log, CancellationToken ct, int intervalSec = 15)
    {
        try
        {
            var access = await Geolocator.RequestAccessAsync();
            if (access != GeolocationAccessStatus.Allowed)
            {
                log("Location not reported: " + PermissionHint);
                return;
            }
            var geo = new Geolocator { DesiredAccuracy = PositionAccuracy.High };
            bool warned = false;
            while (!ct.IsCancellationRequested && session.IsRunning)
            {
                try
                {
                    var pos = await geo.GetGeopositionAsync(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10)).AsTask(ct);
                    var c = pos.Coordinate;
                    var p = c.Point.Position;
                    session.ReportLocation(new GeoFix(DateTime.Now, p.Latitude, p.Longitude, c.Accuracy,
                        c.Point.AltitudeReferenceSystem == AltitudeReferenceSystem.Unspecified ? double.NaN : p.Altitude,
                        c.Speed ?? double.NaN, c.PositionSource.ToString()));
                    warned = false;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
                catch (Exception e)
                {
                    if (!warned) log("Location unavailable: " + (e is UnauthorizedAccessException ? PermissionHint : e.Message));
                    warned = true;
                }
                await Task.Delay(TimeSpan.FromSeconds(intervalSec), ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { log("Location not reported: " + e.Message); }
    }
}
