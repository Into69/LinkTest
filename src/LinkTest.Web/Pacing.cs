using LinkTest.Core;

namespace LinkTest.Web;

/// <summary>
/// How many bytes a sender should have sent after a given time, for the traffic profiles the browser test
/// supports. The web page uses the same rules (app.js, <c>pacer()</c>) for its own direction.
/// </summary>
sealed class Pacing
{
    public const int VoipPacket = 200, VoipIntervalMs = 20, VideoFps = 30, VideoIFrameFactor = 5;

    readonly TrafficProfile _profile;
    readonly double _bytesPerMs;
    readonly int _onMs, _offMs, _calls;

    public int PacketSize { get; }

    public Pacing(TrafficProfile profile, double mbps, int packetSize, int burstOnMs, int burstOffMs)
    {
        _profile = Supported(profile);
        _bytesPerMs = Math.Max(0, mbps) * 1_000_000 / 8 / 1000;
        _onMs = Math.Max(10, burstOnMs);
        _offMs = Math.Max(0, burstOffMs);
        PacketSize = _profile == TrafficProfile.Voip ? VoipPacket : packetSize;
        _calls = Math.Max(1, (int)Math.Round(_bytesPerMs * VoipIntervalMs / VoipPacket));
    }

    /// <summary>Profiles the browser test can generate; the others run as Constant.</summary>
    public static TrafficProfile Supported(TrafficProfile p) =>
        p is TrafficProfile.Bursty or TrafficProfile.Voip or TrafficProfile.Video ? p : TrafficProfile.Constant;

    /// <summary>Cumulative bytes due <paramref name="ms"/> milliseconds after the load started.</summary>
    public double DueBytes(double ms)
    {
        if (ms < 0 || _bytesPerMs <= 0) return 0;
        switch (_profile)
        {
            case TrafficProfile.Bursty:
            {
                double cycle = _onMs + _offMs, k = Math.Floor(ms / cycle), rem = ms - k * cycle;
                double onTime = k * _onMs + Math.Min(rem, _onMs);
                return _bytesPerMs * cycle / _onMs * onTime;      // average over a cycle = the target rate
            }
            case TrafficProfile.Voip:
                return (Math.Floor(ms / VoipIntervalMs) + 1) * _calls * VoipPacket;
            case TrafficProfile.Video:
            {
                // 30 frames/s; every 30th frame is an I-frame 5x the size of the others, same average rate.
                double p = _bytesPerMs * 1000 / (VideoFps - 1 + VideoIFrameFactor);
                double frames = Math.Floor(ms * VideoFps / 1000) + 1;
                double iFrames = Math.Floor((frames - 1) / VideoFps) + 1;
                return frames * p + (VideoIFrameFactor - 1) * p * iFrames;
            }
            default:
                return _bytesPerMs * ms;
        }
    }

    public static string Describe(TrafficProfile p) => Supported(p) switch
    {
        TrafficProfile.Bursty => "bursty",
        TrafficProfile.Voip => "VoIP calls",
        TrafficProfile.Video => "video",
        _ => "constant",
    };
}
