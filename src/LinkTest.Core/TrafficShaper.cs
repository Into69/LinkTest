namespace LinkTest.Core;

public sealed class SenderSpec
{
    public double RateBps;
    public TrafficProfile Profile;
    public int PacketSize;
    public int BurstOnMs;
    public int BurstOffMs;

    public static SenderSpec From(TestConfig c, double mbps) => new()
    {
        RateBps = mbps * 1_000_000,
        Profile = c.Profile,
        PacketSize = c.PacketSize,
        BurstOnMs = c.BurstOnMs,
        BurstOffMs = c.BurstOffMs,
    };
}

/// <summary>
/// Produces the departure time and size of each packet for a traffic profile.
/// <see cref="NextAtUs"/>/<see cref="NextSize"/> describe the upcoming packet; call <see cref="Advance"/> after sending it.
/// </summary>
public sealed class TrafficShaper
{
    public const int VoipPayload = 172;           // 160 B G.711 / 20 ms + 12 B RTP
    const double FrameUs = 1_000_000.0 / 30;
    static readonly int[] ImixSizes = { 64, 64, 64, 64, 64, 64, 64, 576, 576, 576, 576, 1500 };

    readonly SenderSpec _s;
    readonly Random _rnd;
    double _t, _cycleStart, _frameStart;
    long _frameRemaining, _frameIndex;

    public TrafficShaper(SenderSpec spec, int seed)
    {
        _s = spec;
        _rnd = new Random(seed);
    }

    public long NextAtUs => (long)_t;
    public int NextSize { get; private set; }

    public void Start(long nowUs)
    {
        _t = _cycleStart = _frameStart = nowUs;
        _frameIndex = 0;
        _frameRemaining = 0;
        if (_s.Profile == TrafficProfile.Video) BeginFrame();
        else NextSize = PickSize();
    }

    /// <summary>Drop any backlog (the sender fell behind) and continue from now.</summary>
    public void Rebase(long nowUs)
    {
        _t = _cycleStart = _frameStart = nowUs;
        if (_s.Profile == TrafficProfile.Video) BeginFrame();
    }

    public void Advance()
    {
        int cur = NextSize;
        switch (_s.Profile)
        {
            case TrafficProfile.Random:
                _t += -Math.Log(1 - _rnd.NextDouble()) * BitsUs(cur, _s.RateBps);
                break;

            case TrafficProfile.Bursty:
            {
                double on = _s.BurstOnMs * 1000.0;
                double cycle = on + _s.BurstOffMs * 1000.0;
                _t += BitsUs(cur, _s.RateBps * cycle / on);
                if (_t >= _cycleStart + on)
                {
                    _cycleStart += cycle;
                    if (_t < _cycleStart) _t = _cycleStart;
                    else _cycleStart = _t;
                }
                break;
            }

            case TrafficProfile.Video:
                _frameRemaining -= cur;
                if (_frameRemaining <= 0)
                {
                    _frameStart += FrameUs;
                    _t = _frameStart;
                    BeginFrame();
                }
                else NextSize = (int)Math.Clamp(Math.Min(_frameRemaining, _s.PacketSize), Protocol.HeaderSize, _s.PacketSize);
                return;

            default:
                _t += BitsUs(cur, _s.RateBps);
                break;
        }
        NextSize = PickSize();
    }

    int PickSize()
    {
        int max = _s.PacketSize;
        int size = _s.Profile switch
        {
            TrafficProfile.Imix => ImixSizes[_rnd.Next(ImixSizes.Length)],
            TrafficProfile.Random => _rnd.Next(64, Math.Max(65, max + 1)),
            TrafficProfile.Voip => VoipPayload,
            _ => max,
        };
        if (_s.Profile != TrafficProfile.Voip) size = Math.Min(size, max);
        return Math.Max(size, Protocol.HeaderSize);
    }

    void BeginFrame()
    {
        double avg = _s.RateBps / 8 / 30;
        // I-frame every 30 frames at 5x; P-frames sized so the average is preserved; +/-20% variation.
        double factor = _frameIndex % 30 == 0 ? 5.0 : 25.0 / 29.0;
        _frameRemaining = Math.Max(Protocol.HeaderSize, (long)(avg * factor * (0.8 + 0.4 * _rnd.NextDouble())));
        _frameIndex++;
        NextSize = (int)Math.Clamp(Math.Min(_frameRemaining, _s.PacketSize), Protocol.HeaderSize, _s.PacketSize);
    }

    static double BitsUs(int bytes, double rateBps) => bytes * 8_000_000.0 / rateBps;
}
