using LinkTest.Core;

namespace LinkTest.Web;

/// <summary>
/// Data-channel packet format shared with the web page (little-endian).
/// Data:  [0]=1, [1..4] sequence (u32), [5..12] sender clock in ms (f64), padding.
/// Echo:  [0]=2 request (page → server) / 3 reply (server → page), [1..4] id, [5..12] page clock in ms.
/// </summary>
static class WebPacket
{
    public const byte Data = 1, EchoRequest = 2, EchoReply = 3;
    public const int HeaderSize = 13, MinSize = 64, MaxSize = 1200;
}

/// <summary>
/// Receive-side statistics of one UDP-style packet stream (browser → server): loss from sequence gaps, RFC 3550
/// inter-arrival jitter and the longest gap between packets. Thread-safe; <see cref="TakeInterval"/> returns the
/// figures since the previous call.
/// </summary>
sealed class UdpMeter
{
    readonly object _lock = new();
    long _packets, _outOfOrder;
    long _highest = -1, _intervalStartHighest = -1, _intervalPackets;
    long _totalLost, _totalExpected;
    double _jitterMs, _lastTransitMs = double.NaN, _lastArrivalMs = double.NaN, _maxGapMs;
    long _intervalBytes;

    public void Add(uint seq, double senderMs, int length, double arrivalMs)
    {
        lock (_lock)
        {
            _packets++; _intervalPackets++;
            _intervalBytes += length;
            if (seq > _highest) _highest = seq;
            else _outOfOrder++;
            double transit = arrivalMs - senderMs;
            if (double.IsFinite(_lastTransitMs)) _jitterMs += (Math.Abs(transit - _lastTransitMs) - _jitterMs) / 16;
            _lastTransitMs = transit;
            if (double.IsFinite(_lastArrivalMs)) _maxGapMs = Math.Max(_maxGapMs, arrivalMs - _lastArrivalMs);
            _lastArrivalMs = arrivalMs;
        }
    }

    public readonly record struct Interval(long Bytes, long Packets, long Lost, long Expected, double JitterMs, double MaxGapMs,
        long TotalLost, long TotalExpected, long OutOfOrder);

    public Interval TakeInterval()
    {
        lock (_lock)
        {
            long expected = _highest - _intervalStartHighest;
            long lost = Math.Max(0, expected - _intervalPackets);
            if (expected <= 0) expected = 0;
            _totalLost += lost; _totalExpected += expected;
            var r = new Interval(_intervalBytes, _intervalPackets, lost, expected, _packets > 1 ? _jitterMs : double.NaN,
                _intervalPackets > 0 ? _maxGapMs : double.NaN, _totalLost, _totalExpected, _outOfOrder);
            _intervalStartHighest = _highest;
            _intervalPackets = 0; _intervalBytes = 0; _maxGapMs = 0;
            return r;
        }
    }

    public double JitterMs { get { lock (_lock) return _packets > 1 ? _jitterMs : double.NaN; } }
}

/// <summary>Server clock for packet timestamps (ms, monotonic).</summary>
static class WebClock
{
    public static double NowMs => Clock.NowUs / 1000.0;
}
