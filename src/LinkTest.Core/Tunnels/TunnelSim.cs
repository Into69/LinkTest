using System.Collections.Concurrent;

namespace LinkTest.Core.Tunnels;

public enum TunnelState { Establishing, Up, Down }

public sealed record TunnelStatus(
    string Name,
    TunnelState State,
    string StateText,
    string Verdict,
    double AvailabilityPct,
    int Drops,
    int Flaps,
    int HandshakeFailures,
    double DownSec,
    double LongestOutageSec,
    double AvgRecoverSec,
    double AvgDetectSec,
    double LastRttMs,
    string LastEvent);

/// <summary>
/// State machine for one simulated tunnel. Single-threaded: call <see cref="Tick"/> from one thread;
/// <see cref="OnResponse"/> may be called from any thread. <see cref="GetStatus"/> is thread-safe.
/// </summary>
public sealed class TunnelSim
{
    /// <param name="send">(sim, seq, kind, size) — kind 0 = keepalive, n = handshake step n.</param>
    public TunnelSim(int index, TunnelProfile profile, Action<TunnelSim, long, ushort, int> send)
    {
        Index = index;
        Profile = profile.Clone();
        Profile.Normalize();
        _send = send;
    }

    public int Index { get; }
    public TunnelProfile Profile { get; }

    /// <summary>Raised from the ticking thread on significant transitions.</summary>
    public event Action<TunnelSim, string>? Event;

    readonly Action<TunnelSim, long, ushort, int> _send;
    readonly ConcurrentQueue<(long seq, ushort kind, long sentUs, long atUs)> _rx = new();
    readonly object _lock = new();

    TunnelState _state = TunnelState.Establishing;
    bool _handshaking, _everUp;
    long _seq, _stateSinceUs, _startUs;

    // liveness
    long _exchangeStartSeq = -1, _deadlineUs, _nextProbeUs, _lastSuccessUs;
    int _misses;
    double _timeoutUs;

    // handshake
    int _hsStep, _hsTries;
    long _stepStartSeq, _reconnectAtUs;

    // stats
    long _upUs, _downUs, _longestOutageUs, _sumRecoverUs, _sumDetectUs, _lastUpAtUs;
    int _drops, _flaps, _hsFailures, _recoveries, _detections;
    double _lastRttMs = double.NaN;
    string _lastEvent = "";

    public void Start(long nowUs)
    {
        lock (_lock)
        {
            _startUs = _stateSinceUs = nowUs;
            BeginHandshake(nowUs);
        }
    }

    public void OnResponse(long seq, ushort kind, long sentUs, long nowUs) => _rx.Enqueue((seq, kind, sentUs, nowUs));

    public void Tick(long now)
    {
        List<string>? events = null;
        lock (_lock)
        {
            while (_rx.TryDequeue(out var r)) HandleResponse(r.seq, r.kind, r.sentUs, r.atUs, ref events);

            if (_state == TunnelState.Up)
            {
                if (_exchangeStartSeq < 0)
                {
                    if (now >= _nextProbeUs)
                    {
                        _exchangeStartSeq = _seq;
                        _misses = 0;
                        _timeoutUs = Profile.ProbeTimeoutMs * 1000.0;
                        SendProbe(0, Profile.KeepaliveBytes);
                        _deadlineUs = now + (long)_timeoutUs;
                    }
                }
                else if (now >= _deadlineUs)
                {
                    _misses++;
                    if (_misses >= Profile.MaxMissed)
                    {
                        GoDown(now, $"{_misses} keepalive/DPD probes unanswered", ref events);
                    }
                    else
                    {
                        _timeoutUs *= Profile.RetryBackoff;
                        SendProbe(0, Profile.KeepaliveBytes);
                        _deadlineUs = now + (long)_timeoutUs;
                    }
                }
            }
            else if (_handshaking)
            {
                if (now >= _deadlineUs)
                {
                    _hsTries++;
                    if (Profile.HandshakeRetries > 0 && _hsTries >= Profile.HandshakeRetries)
                    {
                        _handshaking = false;
                        _hsFailures++;
                        _reconnectAtUs = now + Profile.ReconnectDelayMs * 1000L;
                        Add(ref events, $"handshake failed at step {_hsStep + 1}/{Profile.HandshakeRoundTrips} after {_hsTries} transmissions");
                    }
                    else
                    {
                        _timeoutUs *= Profile.HandshakeBackoff;
                        SendProbe((ushort)(_hsStep + 1), Profile.HandshakeBytes);
                        _deadlineUs = now + (long)_timeoutUs;
                    }
                }
            }
            else if (now >= _reconnectAtUs)
            {
                BeginHandshake(now);
            }
        }
        if (events != null)
            foreach (var e in events) Event?.Invoke(this, e);
    }

    void HandleResponse(long seq, ushort kind, long sentUs, long at, ref List<string>? events)
    {
        if (_state == TunnelState.Up)
        {
            if (kind == 0 && _exchangeStartSeq >= 0 && seq >= _exchangeStartSeq)
            {
                _lastRttMs = (at - sentUs) / 1000.0;
                _exchangeStartSeq = -1;
                _misses = 0;
                _lastSuccessUs = at;
                _nextProbeUs = at + Profile.KeepaliveIntervalMs * 1000L;
            }
            return;
        }
        if (!_handshaking || kind != _hsStep + 1 || seq < _stepStartSeq) return;

        _lastRttMs = (at - sentUs) / 1000.0;
        _hsStep++;
        if (_hsStep >= Profile.HandshakeRoundTrips) { GoUp(at, ref events); return; }
        _hsTries = 0;
        _stepStartSeq = _seq;
        _timeoutUs = Profile.HandshakeTimeoutMs * 1000.0;
        SendProbe((ushort)(_hsStep + 1), Profile.HandshakeBytes);
        _deadlineUs = at + (long)_timeoutUs;
    }

    void BeginHandshake(long now)
    {
        _handshaking = true;
        _hsStep = 0;
        _hsTries = 0;
        _stepStartSeq = _seq;
        _timeoutUs = Profile.HandshakeTimeoutMs * 1000.0;
        SendProbe(1, Profile.HandshakeBytes);
        _deadlineUs = now + (long)_timeoutUs;
    }

    void GoUp(long now, ref List<string>? events)
    {
        long period = now - _stateSinceUs;
        if (_everUp)
        {
            _downUs += period;
            _recoveries++;
            _sumRecoverUs += period;
            _longestOutageUs = Math.Max(_longestOutageUs, period);
            Add(ref events, $"UP again after {period / 1e6:0.0} s outage");
        }
        else
        {
            Add(ref events, $"UP (established in {(now - _startUs) / 1e6:0.0} s)");
        }
        _everUp = true;
        _state = TunnelState.Up;
        _handshaking = false;
        _stateSinceUs = _lastUpAtUs = _lastSuccessUs = now;
        _exchangeStartSeq = -1;
        _nextProbeUs = now + Profile.KeepaliveIntervalMs * 1000L;
    }

    void GoDown(long now, string reason, ref List<string>? events)
    {
        _upUs += now - _stateSinceUs;
        _drops++;
        bool flap = now - _lastUpAtUs < Profile.StableHoldSec * 1_000_000L;
        if (flap) _flaps++;
        _detections++;
        _sumDetectUs += now - _lastSuccessUs;
        _state = TunnelState.Down;
        _stateSinceUs = now;
        _exchangeStartSeq = -1;
        Add(ref events, $"DOWN: {reason}" + (flap ? " (flap - was up < stable hold time)" : ""));
        _handshaking = false;
        _reconnectAtUs = now + Profile.ReconnectDelayMs * 1000L;
        if (Profile.ReconnectDelayMs == 0) BeginHandshake(now);
    }

    void SendProbe(ushort kind, int size)
    {
        long seq = _seq++;
        try { _send(this, seq, kind, size); } catch { /* treated as lost */ }
    }

    void Add(ref List<string>? events, string e)
    {
        _lastEvent = $"{DateTime.Now:HH:mm:ss} {e}";
        (events ??= new()).Add(e);
    }

    public TunnelStatus GetStatus(long now)
    {
        lock (_lock)
        {
            long cur = now - _stateSinceUs;
            long up = _upUs + (_state == TunnelState.Up ? cur : 0);
            long down = _downUs + (_state == TunnelState.Down ? cur : 0);
            double avail = up + down > 0 ? 100.0 * up / (up + down) : 0;

            string stateText = _state switch
            {
                TunnelState.Up => _exchangeStartSeq >= 0 && _misses > 0 ? $"UP ({_misses} missed)" : "UP",
                _ when _handshaking => $"{(_state == TunnelState.Establishing ? "ESTABLISHING" : "DOWN")} (handshake {_hsStep + 1}/{Profile.HandshakeRoundTrips})",
                _ => $"{(_state == TunnelState.Establishing ? "ESTABLISHING" : "DOWN")} (retry in {Math.Max(0, (_reconnectAtUs - now) / 1e6):0}s)",
            };

            string verdict;
            if (!_everUp)
                verdict = _hsFailures > 0 ? "Cannot establish" : "Establishing…";
            else if (_state != TunnelState.Up)
                verdict = _flaps >= 3 ? "Flapping - currently down" : "DOWN - re-establishing";
            else if (_drops == 0)
                verdict = "Stable";
            else if (_flaps >= 3)
                verdict = $"Flapping ({_drops} drops)";
            else if (cur < Profile.StableHoldSec * 1_000_000L)
                verdict = $"Recovered, not yet stable ({cur / 1e6:0}/{Profile.StableHoldSec}s)";
            else
                verdict = $"Recovered & stable ({_drops} drop{(_drops == 1 ? "" : "s")})";

            return new TunnelStatus(
                Profile.Name, _state, stateText, verdict, avail, _drops, _flaps, _hsFailures,
                down / 1e6,
                Math.Max(_longestOutageUs, _state == TunnelState.Down ? cur : 0) / 1e6,
                _recoveries > 0 ? _sumRecoverUs / 1e6 / _recoveries : double.NaN,
                _detections > 0 ? _sumDetectUs / 1e6 / _detections : double.NaN,
                _lastRttMs, _lastEvent);
        }
    }
}
