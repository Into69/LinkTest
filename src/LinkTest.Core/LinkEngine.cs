namespace LinkTest.Core;

/// <summary>
/// The data plane used by both client and server: paced senders that embed the local receive report
/// in their packets, and a receive handler that tracks loss/jitter, RTT (via timestamp echo) and the
/// peer's latest report.
/// </summary>
public sealed class LinkEngine
{
    public LinkEngine(uint sessionId) => SessionId = sessionId;

    public uint SessionId { get; }
    public RxTracker Rx { get; } = new();

    /// <summary>Artificial loss applied to outgoing packets, in percent (for testing the tool).</summary>
    public double TxDropPct { get; set; }

    /// <summary>Supplies retransmission counters of this side's sending TCP sockets.</summary>
    public Func<(long retransBytes, int timeouts)>? TcpInfoProvider { get; set; }

    long _txPackets, _txBytes, _senderLagEvents;
    public long TxPackets => Interlocked.Read(ref _txPackets);
    public long TxBytes => Interlocked.Read(ref _txBytes);
    /// <summary>Times a sender fell more than 200 ms behind schedule (CPU or socket could not keep up).</summary>
    public long SenderLagEvents => Interlocked.Read(ref _senderLagEvents);

    // Timestamp echo state.
    readonly object _echoLock = new();
    long _peerSendUs, _peerRecvUs, _lastEchoUs;

    // RTT statistics.
    readonly object _rttLock = new();
    double _srtt, _ivSum, _ivMin = double.MaxValue, _ivMax, _totSum, _totMin = double.MaxValue, _totMax;
    long _ivCount, _totCount;

    // Latest report from the peer about the traffic we sent it.
    readonly object _peerLock = new();
    StatsReport _peer;
    bool _hasPeer;
    long _peerLocalUs;

    public readonly record struct RttInterval(double AvgUs, double MinUs, double MaxUs, long Samples);
    public readonly record struct RttTotals(double AvgUs, double MinUs, double MaxUs, long Samples);

    public double SmoothedRttUs { get { lock (_rttLock) return _srtt; } }

    public bool TryGetPeerReport(out StatsReport report, out long receivedAtUs)
    {
        lock (_peerLock)
        {
            report = _peer;
            receivedAtUs = _peerLocalUs;
            return _hasPeer;
        }
    }

    public void HandlePacket(ReadOnlySpan<byte> pkt, in PacketHeader h, long nowUs)
    {
        Rx.Record(h.StreamId, h.Seq, h.SendUs, nowUs, pkt.Length);

        bool fresh = false;
        lock (_echoLock)
        {
            if (h.SendUs > _peerSendUs) { _peerSendUs = h.SendUs; _peerRecvUs = nowUs; }
            if (h.EchoUs > _lastEchoUs) { _lastEchoUs = h.EchoUs; fresh = true; }
        }
        if (fresh)
        {
            double rtt = nowUs - h.EchoUs - h.EchoHoldUs;
            if (rtt >= 0 && rtt < 120_000_000) AddRtt(rtt);
        }

        if ((h.Flags & (ushort)PacketFlags.HasReport) != 0 && pkt.Length >= Protocol.HeaderSize + StatsReport.Size)
        {
            var r = StatsReport.Read(pkt[Protocol.HeaderSize..]);
            lock (_peerLock)
            {
                if (!_hasPeer || r.TimeUs > _peer.TimeUs)
                {
                    _peer = r;
                    _hasPeer = true;
                    _peerLocalUs = nowUs;
                }
            }
        }
    }

    void AddRtt(double rtt)
    {
        lock (_rttLock)
        {
            _srtt = _totCount == 0 ? rtt : _srtt + (rtt - _srtt) / 8;
            _ivSum += rtt; _ivCount++;
            if (rtt < _ivMin) _ivMin = rtt;
            if (rtt > _ivMax) _ivMax = rtt;
            _totSum += rtt; _totCount++;
            if (rtt < _totMin) _totMin = rtt;
            if (rtt > _totMax) _totMax = rtt;
        }
    }

    /// <summary>RTT statistics since the previous call.</summary>
    public RttInterval TakeRttInterval()
    {
        lock (_rttLock)
        {
            var r = _ivCount == 0
                ? new RttInterval(double.NaN, double.NaN, double.NaN, 0)
                : new RttInterval(_ivSum / _ivCount, _ivMin, _ivMax, _ivCount);
            _ivSum = 0; _ivCount = 0; _ivMin = double.MaxValue; _ivMax = 0;
            return r;
        }
    }

    public RttTotals GetRttTotals()
    {
        lock (_rttLock)
            return _totCount == 0
                ? new RttTotals(double.NaN, double.NaN, double.NaN, 0)
                : new RttTotals(_totSum / _totCount, _totMin, _totMax, _totCount);
    }

    public StatsReport BuildReport(long nowUs)
    {
        var r = Rx.Snapshot(nowUs);
        r.TimeUs = nowUs;
        r.RttUs = SmoothedRttUs;
        r.TxPackets = TxPackets;
        r.TxBytes = TxBytes;
        if (TcpInfoProvider != null)
        {
            var (bytes, timeouts) = TcpInfoProvider();
            r.TcpRetransBytes = bytes;
            r.TcpTimeouts = timeouts;
        }
        return r;
    }

    /// <summary>
    /// Runs a paced sender until cancelled. <paramref name="send"/> returns false if the packet could not be
    /// sent (e.g. peer address not yet known); the sequence number is then not consumed.
    /// Exceptions thrown by <paramref name="send"/> end the loop and propagate.
    /// </summary>
    public void RunSender(int streamId, SenderSpec spec, Func<byte[], int, bool> send, CancellationToken ct)
    {
        var buf = new byte[Protocol.MaxPacket];
        var rnd = new Random(unchecked((int)SessionId * 31 + streamId * 7919 + Environment.TickCount));
        rnd.NextBytes(buf);   // filler stays random (defeats WAN compression); header/report overwrite the front

        TrafficShaper? shaper = spec.RateBps > 0 ? new TrafficShaper(spec, rnd.Next()) : null;
        long seq = 0, lastSent = 0, lastReport = 0;
        shaper?.Start(Clock.NowUs + Math.Max(0, spec.StartDelayUs));

        while (!ct.IsCancellationRequested)
        {
            long now = Clock.NowUs;
            if (shaper != null)
            {
                if (now - shaper.NextAtUs > 200_000)
                {
                    shaper.Rebase(now);
                    Interlocked.Increment(ref _senderLagEvents);
                }
                int burst = 0;
                while (shaper.NextAtUs <= now && burst < 512 && !ct.IsCancellationRequested)
                {
                    SendOne(shaper.NextSize, false);
                    shaper.Advance();
                    burst++;
                }
                if (burst == 512) continue;
            }

            if (Clock.NowUs - lastSent >= Protocol.HeartbeatIntervalUs)
                SendOne(Protocol.HeaderSize, true);

            long next = lastSent + Protocol.HeartbeatIntervalUs;
            if (shaper != null && shaper.NextAtUs < next) next = shaper.NextAtUs;
            long wait = next - Clock.NowUs;
            if (wait > 300) Thread.Sleep(1);
            else if (wait > 0) Thread.SpinWait(20);
        }

        void SendOne(int size, bool heartbeat)
        {
            long t = Clock.NowUs;
            lastSent = t;
            bool report = t - lastReport >= Protocol.ReportIntervalUs;
            int len = Math.Clamp(size, Protocol.HeaderSize, Protocol.MaxPacket);
            if (report && len < Protocol.HeaderSize + StatsReport.Size) len = Protocol.HeaderSize + StatsReport.Size;

            long echoUs, hold;
            lock (_echoLock)
            {
                echoUs = _peerSendUs;
                hold = echoUs == 0 ? 0 : t - _peerRecvUs;
            }

            var h = new PacketHeader
            {
                Type = PacketType.Data,
                SessionId = SessionId,
                StreamId = (ushort)streamId,
                Flags = (ushort)((report ? PacketFlags.HasReport : 0) | (heartbeat ? PacketFlags.Heartbeat : 0)),
                Seq = seq,
                SendUs = t,
                EchoUs = echoUs,
                EchoHoldUs = hold,
                Length = len,
            };
            h.Write(buf);
            if (report) BuildReport(t).Write(buf.AsSpan(Protocol.HeaderSize));

            if (TxDropPct > 0 && rnd.NextDouble() * 100 < TxDropPct)
            {
                seq++;   // "lost on the wire": consumes a sequence number, never sent
                Interlocked.Increment(ref _txPackets);
                Interlocked.Add(ref _txBytes, len);
                return;
            }
            if (!send(buf, len)) return;
            seq++;
            if (report) lastReport = t;
            Interlocked.Increment(ref _txPackets);
            Interlocked.Add(ref _txBytes, len);
        }
    }
}
