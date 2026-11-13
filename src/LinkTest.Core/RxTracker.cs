namespace LinkTest.Core;

/// <summary>
/// Receive-side accounting: loss, reordering, duplicates, jitter and arrival gaps per stream, plus the detailed
/// figures (loss bursts, reorder distance, one-way delay variation, stalls, packet sizes) behind <see cref="RxDetail"/>.
/// </summary>
public sealed class RxTracker
{
    public const int MaxStreams = 64;
    const int Window = 8192;           // sequence numbers remembered for duplicate detection
    const int Words = Window / 64;
    /// <summary>Loss bursts are judged this many packets behind the newest, so reordered packets aren't counted as lost.</summary>
    const int SettleLag = 512;
    /// <summary>The minimum one-way transit is tracked over two rolling windows of this length (absorbs clock drift).</summary>
    const long MinWindowUs = 10_000_000;

    sealed class StreamState
    {
        public long MaxSeq = -1;
        public long Unique, Dup, Ooo;
        public readonly ulong[] Bits = new ulong[Words];
        public long LastTransit;
        public bool HasTransit;
        public double Jitter;
        public long Settled = -1, Run;
        public long MinCur = long.MaxValue, MinPrev = long.MaxValue, MinWindowStart;
    }

    readonly object _lock = new();
    readonly StreamState?[] _streams = new StreamState?[MaxStreams];
    long _packets, _bytes, _lastArrival, _gapCur, _gapPrev, _gapWindowStart;

    // Detail
    long _late, _maxReorder, _reorderSum, _lossEvents, _longestBurst, _delayCount, _maxGapAll, _stalls100, _stalls1s;
    readonly long[] _burstHist = new long[RxDetail.BurstLabels.Length];
    readonly long[] _delayHist = new long[RxDetail.DelayEdgesMs.Length + 1];
    double _delaySum, _delayMax, _peakJitter;
    int _minSize, _maxSize;

    public long LastArrivalUs { get { lock (_lock) return _lastArrival; } }

    public void Record(int stream, long seq, long sendUs, long nowUs, int len)
    {
        if ((uint)stream >= MaxStreams || seq < 0) return;
        lock (_lock)
        {
            _packets++;
            _bytes += len;
            if (_minSize == 0 || len < _minSize) _minSize = len;
            if (len > _maxSize) _maxSize = len;
            RollGap(nowUs);
            if (_lastArrival != 0)
            {
                long g = nowUs - _lastArrival;
                if (g > _gapCur) _gapCur = g;
                if (g > _maxGapAll) _maxGapAll = g;
                if (g >= 100_000) _stalls100++;
                if (g >= 1_000_000) _stalls1s++;
            }
            _lastArrival = nowUs;

            var s = _streams[stream] ??= new StreamState();
            if (seq > s.MaxSeq)
            {
                long adv = seq - s.MaxSeq;
                // Judge loss bursts before the bitmap slots get reused.
                long target = seq - SettleLag;
                if (target > s.Settled)
                {
                    Settle(s, Math.Min(target, s.MaxSeq));
                    if (target > s.Settled) { s.Run += target - s.Settled; s.Settled = target; }   // a gap longer than SettleLag
                }
                if (adv >= Window) Array.Clear(s.Bits);
                else for (long q = s.MaxSeq + 1; q < seq; q++) ClearBit(s, q);
                s.MaxSeq = seq;
                SetBit(s, seq);
                s.Unique++;
            }
            else if (s.MaxSeq - seq >= Window)
            {
                s.Unique++; s.Ooo++; _late++;          // very late; assume unique
                Reorder(s.MaxSeq - seq);
            }
            else if (GetBit(s, seq))
            {
                s.Dup++;
                return;
            }
            else
            {
                SetBit(s, seq); s.Unique++; s.Ooo++;
                Reorder(s.MaxSeq - seq);
            }

            long transit = nowUs - sendUs;    // includes clock offset, which cancels in the difference
            if (s.HasTransit)
            {
                double d = Math.Abs(transit - s.LastTransit);
                s.Jitter += (d - s.Jitter) / 16.0;
                if (s.Jitter > _peakJitter && s.Unique > 32) _peakJitter = s.Jitter;
            }
            s.LastTransit = transit;
            s.HasTransit = true;

            // One-way delay above the recent minimum.
            if (nowUs - s.MinWindowStart >= MinWindowUs) { s.MinPrev = s.MinCur; s.MinCur = long.MaxValue; s.MinWindowStart = nowUs; }
            if (transit < s.MinCur) s.MinCur = transit;
            double dv = transit - Math.Min(s.MinCur, s.MinPrev);
            _delaySum += dv;
            _delayCount++;
            if (dv > _delayMax) _delayMax = dv;
            _delayHist[RxDetail.DelayBucket(dv / 1000)]++;
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

    /// <summary>Detailed cumulative statistics; loss bursts include the packets not yet settled.</summary>
    /// <param name="final">Leave out the current silence (the peer has stopped sending at the end of a test).</param>
    public RxDetail Detail(long nowUs, bool final = false)
    {
        lock (_lock)
        {
            var snap = Snapshot(nowUs);
            var hist = (long[])_burstHist.Clone();
            long events = _lossEvents, longest = _longestBurst;
            foreach (var s in _streams)
            {
                if (s == null) continue;
                long run = s.Run;
                for (long q = s.Settled + 1; q <= s.MaxSeq; q++)
                {
                    if (GetBit(s, q)) { if (run > 0) { Burst(run, hist, ref events, ref longest); run = 0; } }
                    else run++;
                }
            }
            return new RxDetail
            {
                Packets = snap.Packets, Bytes = snap.Bytes, Unique = snap.UniquePackets(), Lost = snap.Lost,
                OutOfOrder = snap.OutOfOrder, Duplicates = snap.Duplicates, Late = _late,
                MaxReorder = _maxReorder, ReorderSum = _reorderSum,
                LossEvents = events, LongestLossBurst = longest, BurstHist = hist,
                JitterUs = snap.JitterUs, PeakJitterUs = Math.Max(_peakJitter, snap.JitterUs),
                DelayHist = (long[])_delayHist.Clone(), DelaySumUs = _delaySum, DelayCount = _delayCount, DelayMaxUs = _delayMax,
                MaxGapUs = _lastArrival == 0 ? 0 : final ? _maxGapAll : Math.Max(_maxGapAll, nowUs - _lastArrival),
                Stalls100ms = _stalls100, Stalls1s = _stalls1s,
                MinSize = _minSize, MaxSize = _maxSize,
            };
        }
    }

    void Reorder(long distance)
    {
        _reorderSum += distance;
        if (distance > _maxReorder) _maxReorder = distance;
    }

    static void Burst(long n, long[] hist, ref long events, ref long longest)
    {
        events++;
        hist[RxDetail.BurstBucket(n)]++;
        if (n > longest) longest = n;
    }

    /// <summary>Walks sequence numbers up to <paramref name="upTo"/>, closing runs of missing ones as loss bursts.</summary>
    void Settle(StreamState s, long upTo)
    {
        for (long q = s.Settled + 1; q <= upTo; q++)
        {
            if (GetBit(s, q)) { if (s.Run > 0) { Burst(s.Run, _burstHist, ref _lossEvents, ref _longestBurst); s.Run = 0; } }
            else s.Run++;
        }
        if (upTo > s.Settled) s.Settled = upTo;
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
