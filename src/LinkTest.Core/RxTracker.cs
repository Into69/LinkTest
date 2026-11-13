namespace LinkTest.Core;

/// <summary>Receive-side accounting: loss, reordering, duplicates, jitter and arrival gaps per stream.</summary>
public sealed class RxTracker
{
    public const int MaxStreams = 64;
    const int Window = 8192;           // sequence numbers remembered for duplicate detection
    const int Words = Window / 64;

    sealed class StreamState
    {
        public long MaxSeq = -1;
        public long Unique, Dup, Ooo;
        public readonly ulong[] Bits = new ulong[Words];
        public long LastTransit;
        public bool HasTransit;
        public double Jitter;
    }

    readonly object _lock = new();
    readonly StreamState?[] _streams = new StreamState?[MaxStreams];
    long _packets, _bytes, _lastArrival, _gapCur, _gapPrev, _gapWindowStart;

    public long LastArrivalUs { get { lock (_lock) return _lastArrival; } }

    public void Record(int stream, long seq, long sendUs, long nowUs, int len)
    {
        if ((uint)stream >= MaxStreams || seq < 0) return;
        lock (_lock)
        {
            _packets++;
            _bytes += len;
            RollGap(nowUs);
            if (_lastArrival != 0)
            {
                long g = nowUs - _lastArrival;
                if (g > _gapCur) _gapCur = g;
            }
            _lastArrival = nowUs;

            var s = _streams[stream] ??= new StreamState();
            if (seq > s.MaxSeq)
            {
                long adv = seq - s.MaxSeq;
                if (adv >= Window) Array.Clear(s.Bits);
                else for (long q = s.MaxSeq + 1; q < seq; q++) ClearBit(s, q);
                s.MaxSeq = seq;
                SetBit(s, seq);
                s.Unique++;
            }
            else if (s.MaxSeq - seq >= Window)
            {
                s.Unique++; s.Ooo++;          // very late; assume unique
            }
            else if (GetBit(s, seq))
            {
                s.Dup++;
                return;
            }
            else
            {
                SetBit(s, seq); s.Unique++; s.Ooo++;
            }

            long transit = nowUs - sendUs;    // includes clock offset, which cancels in the difference
            if (s.HasTransit)
            {
                double d = Math.Abs(transit - s.LastTransit);
                s.Jitter += (d - s.Jitter) / 16.0;
            }
            s.LastTransit = transit;
            s.HasTransit = true;
        }
    }

    public StatsReport Snapshot(long nowUs)
    {
        lock (_lock)
        {
            RollGap(nowUs);
            long lost = 0, ooo = 0, dup = 0;
            double jit = 0; int n = 0;
            foreach (var s in _streams)
            {
                if (s == null) continue;
                lost += Math.Max(0, s.MaxSeq + 1 - s.Unique);
                ooo += s.Ooo; dup += s.Dup;
                if (s.HasTransit) { jit += s.Jitter; n++; }
            }
            long gap = Math.Max(_gapCur, _gapPrev);
            if (_lastArrival != 0) gap = Math.Max(gap, nowUs - _lastArrival);
            return new StatsReport
            {
                TimeUs = nowUs,
                Packets = _packets,
                Bytes = _bytes,
                Lost = lost,
                OutOfOrder = ooo,
                Duplicates = dup,
                JitterUs = n > 0 ? jit / n : 0,
                MaxGapUs = _lastArrival == 0 ? 0 : gap,
            };
        }
    }

    void RollGap(long nowUs)
    {
        if (nowUs - _gapWindowStart >= 1_000_000)
        {
            _gapPrev = _gapCur;
            _gapCur = 0;
            _gapWindowStart = nowUs;
        }
    }

    static void SetBit(StreamState s, long q) { long i = q % Window; s.Bits[i >> 6] |= 1UL << (int)(i & 63); }
    static void ClearBit(StreamState s, long q) { long i = q % Window; s.Bits[i >> 6] &= ~(1UL << (int)(i & 63)); }
    static bool GetBit(StreamState s, long q) { long i = q % Window; return (s.Bits[i >> 6] & (1UL << (int)(i & 63))) != 0; }
}
