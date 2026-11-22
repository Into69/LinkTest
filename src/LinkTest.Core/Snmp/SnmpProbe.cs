using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace LinkTest.Core.Snmp;

/// <summary>Results of one SNMP request stream (polls to UDP 161 or informs to UDP 162).</summary>
public sealed class SnmpChannelResult
{
    /// <summary>"poll" (GetRequest → Response) or "inform" (InformRequest → Response).</summary>
    public string Kind { get; set; } = "";
    public int Port { get; set; }
    public int IntervalMs { get; set; }
    public int TimeoutMs { get; set; }
    public int Retries { get; set; }
    /// <summary>The path to the port opened (always true for real SNMP over UDP; WebRTC needs a connection first).</summary>
    public bool Connected { get; set; } = true;
    /// <summary>Seconds until the first answer (NaN = never answered).</summary>
    public double FirstAnswerSec { get; set; } = double.NaN;
    /// <summary>Requests that finished: answered or given up.</summary>
    public int Requests { get; set; }
    public int Answered { get; set; }
    /// <summary>Answered without a retransmission.</summary>
    public int AnsweredFirstTry { get; set; }
    /// <summary>Given up after the timeout and all retries (what a management station reports as a timeout).</summary>
    public int Failed { get; set; }
    /// <summary>Datagrams sent, retransmissions included.</summary>
    public long Transmissions { get; set; }
    /// <summary>Datagrams that didn't get an answer (request or answer lost, or too late), % of transmissions.</summary>
    public double PacketLossPct { get; set; } = double.NaN;
    /// <summary>Time from first sending a request to its answer, retransmissions included (ms).</summary>
    public double ResponseAvgMs { get; set; } = double.NaN;
    public double ResponseP95Ms { get; set; } = double.NaN;
    public double ResponseMaxMs { get; set; } = double.NaN;
    /// <summary>Round-trip time of requests answered at the first try (ms).</summary>
    public double RttMinMs { get; set; } = double.NaN;
    public double RttAvgMs { get; set; } = double.NaN;
    /// <summary>Longest run of failed requests, in seconds of polling.</summary>
    public double LongestOutageSec { get; set; }

    public bool Reachable => Answered > 0;
    public double SuccessPct => Requests > 0 ? 100.0 * Answered / Requests : double.NaN;

    public string Verdict =>
        !Connected ? $"no connection on UDP {Port}" :
        Requests == 0 ? "no requests finished" :
        Answered == 0 ? $"no answers - UDP {Port} looks blocked" :
        Failed > 0 ? $"{Failed} of {Requests} timed out" :
        AnsweredFirstTry < Answered ? "all answered, some after retries" : "all answered";

    public string Text
    {
        get
        {
            static string F(double v, string f = "0.0") => double.IsFinite(v) ? v.ToString(f, System.Globalization.CultureInfo.InvariantCulture) : "–";
            string what = Kind == "inform" ? "informs" : "polls";
            if (!Connected || Requests == 0 || Answered == 0) return $"{what} to UDP {Port}: {Verdict}";
            return $"{what} to UDP {Port}: {Answered}/{Requests} answered ({F(SuccessPct, "0.##")}%), {AnsweredFirstTry} at the first try, " +
                   $"response {F(ResponseAvgMs)} ms avg / {F(ResponseP95Ms)} ms 95th / {F(ResponseMaxMs)} ms max, packet loss {F(PacketLossPct, "0.##")}%" +
                   (Failed > 0 ? $", {Failed} timed out (longest outage {F(LongestOutageSec, "0")} s)" : "");
        }
    }
}

/// <summary>SNMP test result: polls to the agent port and informs to the notification port.</summary>
public sealed class SnmpResult
{
    /// <summary>"SNMPv2c over UDP" or "WebRTC data channel".</summary>
    public string Transport { get; set; } = "SNMPv2c over UDP";
    public SnmpChannelResult? Polls { get; set; }
    public SnmpChannelResult? Informs { get; set; }
    /// <summary>Why the test couldn't run (e.g. the server's SNMP ports are off).</summary>
    public string? Error { get; set; }

    public bool Ok => Error == null && Polls is { Reachable: true } && (Informs == null || Informs.Reachable) &&
                      Polls.Failed == 0 && (Informs?.Failed ?? 0) == 0;

    public string Summary => Error != null ? "SNMP test: " + Error
        : "SNMP (" + Transport + "): " + string.Join("; ", new[] { Polls, Informs }.Where(c => c != null).Select(c => c!.Text));
}

/// <summary>
/// One stream of SNMP requests with a management station's timers: a new request every interval; a request not
/// answered within the timeout is sent again (same request id), up to the retry count, then counted as failed.
/// Thread-safe: <see cref="Tick"/> from one thread, <see cref="OnAnswer"/> from any.
/// </summary>
public sealed class SnmpChannel
{
    sealed class Pending { public int Id; public byte[] Packet = null!; public long FirstUs, LastUs; public int Tries; }

    readonly object _lock = new();
    readonly Func<int, byte[]> _build;
    readonly Action<byte[]> _send;
    readonly Dictionary<int, Pending> _pending = new();
    readonly List<double> _response = new(), _rtt = new();
    long _startUs = -1, _nextUs, _firstAnswerUs = -1;
    int _nextId, _requests, _answered, _firstTry, _failed, _failRun, _maxFailRun;
    long _transmissions;

    public SnmpChannel(string kind, int port, int intervalMs, int timeoutMs, int retries, Func<int, byte[]> build, Action<byte[]> send)
    {
        Kind = kind; Port = port; IntervalMs = Math.Max(10, intervalMs); TimeoutMs = Math.Max(10, timeoutMs); Retries = Math.Max(0, retries);
        _build = build; _send = send;
        _nextId = RandomNumberGenerator.GetInt32(1, 1 << 30);
    }

    public string Kind { get; }
    public int Port { get; }
    public int IntervalMs { get; }
    public int TimeoutMs { get; }
    public int Retries { get; }
    /// <summary>False until the path is open (WebRTC); nothing is sent before.</summary>
    public bool Connected { get; set; } = true;
    /// <summary>Raised (from the ticking thread) when the channel stops or starts getting answers.</summary>
    public event Action<string>? Event;

    public void Tick(long now)
    {
        var sends = new List<byte[]>();
        string? ev = null;
        lock (_lock)
        {
            if (!Connected) return;
            if (_startUs < 0) { _startUs = now; _nextUs = now; }
            foreach (var p in _pending.Values.ToList())
            {
                if (now - p.LastUs < TimeoutMs * 1000L) continue;
                if (p.Tries > Retries)
                {
                    _pending.Remove(p.Id);
                    _requests++;
                    _failed++;
                    if (++_failRun == 1) ev = $"SNMP {Kind} to UDP {Port}: request timed out after {Retries} retries";
                    _maxFailRun = Math.Max(_maxFailRun, _failRun);
                    continue;
                }
                p.Tries++;
                p.LastUs = now;
                sends.Add(p.Packet);
            }
            if (now >= _nextUs)
            {
                _nextUs += IntervalMs * 1000L;
                if (_nextUs < now) _nextUs = now + IntervalMs * 1000L;      // after a stall: don't burst
                int id = _nextId++ & 0x7FFF_FFFF;
                var p = new Pending { Id = id, Packet = _build(id), FirstUs = now, LastUs = now, Tries = 1 };
                _pending[id] = p;
                sends.Add(p.Packet);
            }
            _transmissions += sends.Count;
        }
        foreach (var s in sends) { try { _send(s); } catch { /* lost */ } }
        if (ev != null) Event?.Invoke(ev);
    }

    public void OnAnswer(int requestId, long now)
    {
        string? ev = null;
        lock (_lock)
        {
            if (!_pending.Remove(requestId, out var p)) return;    // duplicate, late or unknown
            _requests++;
            _answered++;
            double resp = (now - p.FirstUs) / 1000.0;
            _response.Add(resp);
            if (p.Tries == 1) { _firstTry++; _rtt.Add(resp); }
            if (_firstAnswerUs < 0) { _firstAnswerUs = now; ev = $"SNMP {Kind} to UDP {Port}: first answer after {resp:0} ms"; }
            else if (_failRun > 0) ev = $"SNMP {Kind} to UDP {Port}: answering again after {_failRun} timed-out request(s)";
            _failRun = 0;
        }
        if (ev != null) Event?.Invoke(ev);
    }

    public SnmpChannelResult Result()
    {
        lock (_lock)
        {
            var sorted = _response.OrderBy(x => x).ToList();
            return new SnmpChannelResult
            {
                Kind = Kind, Port = Port, IntervalMs = IntervalMs, TimeoutMs = TimeoutMs, Retries = Retries,
                Connected = Connected || _startUs >= 0,
                FirstAnswerSec = _firstAnswerUs >= 0 ? (_firstAnswerUs - _startUs) / 1e6 : double.NaN,
                Requests = _requests, Answered = _answered, AnsweredFirstTry = _firstTry, Failed = _failed,
                Transmissions = _transmissions,
                // Datagrams of finished requests that got no answer: every try of a failed request, all but one of an answered one.
                PacketLossPct = _requests > 0 ? 100.0 * Math.Max(0, FinishedTransmissions() - _answered) / Math.Max(1, FinishedTransmissions()) : double.NaN,
                ResponseAvgMs = sorted.Count > 0 ? sorted.Average() : double.NaN,
                ResponseP95Ms = sorted.Count > 0 ? sorted[Math.Min(sorted.Count - 1, (int)Math.Ceiling(sorted.Count * 0.95) - 1)] : double.NaN,
                ResponseMaxMs = sorted.Count > 0 ? sorted[^1] : double.NaN,
                RttMinMs = _rtt.Count > 0 ? _rtt.Min() : double.NaN,
                RttAvgMs = _rtt.Count > 0 ? _rtt.Average() : double.NaN,
                LongestOutageSec = _maxFailRun * IntervalMs / 1000.0,
            };
        }
    }

    long FinishedTransmissions() => _transmissions - _pending.Values.Sum(p => (long)p.Tries);
}

/// <summary>
/// The SNMP test: GetRequests to the agent port (161) once a second and InformRequests to the notification port
/// (162) every 5 s, with net-snmp's default timers (1 s timeout, 5 retries). The transport is pluggable: real
/// SNMP datagrams from the apps, a WebRTC data channel from a browser.
/// </summary>
public sealed class SnmpRun : IDisposable
{
    public const int PollIntervalMs = 1000, InformIntervalMs = 5000, TimeoutMs = 1000, Retries = 5;
    public const int DefaultPollPort = 161, DefaultTrapPort = 162;

    readonly CancellationTokenSource _cts = new();
    readonly long _t0 = Clock.NowUs;

    public SnmpChannel Polls { get; }
    public SnmpChannel Informs { get; }
    public string Transport { get; }
    public event Action<string>? Event;

    /// <param name="send">Sends a datagram: (0 = poll port, 1 = notification port, bytes).</param>
    public SnmpRun(string community, int pollPort, int trapPort, string transport, Action<int, byte[]> send)
    {
        Transport = transport;
        Polls = new SnmpChannel("poll", pollPort, PollIntervalMs, TimeoutMs, Retries, id => BuildGet(community, id), b => send(0, b));
        Informs = new SnmpChannel("inform", trapPort, InformIntervalMs, TimeoutMs, Retries, id => BuildInform(community, id), b => send(1, b));
        Polls.Event += e => Event?.Invoke(e);
        Informs.Event += e => Event?.Invoke(e);
    }

    public byte[] BuildGet(string community, int id) => SnmpCodec.Build(community, SnmpCodec.GetRequest, id, new[]
    {
        SnmpVarBind.Null(SnmpCodec.SysUpTime), SnmpVarBind.Null(SnmpCodec.SysName),
        SnmpVarBind.Null(SnmpCodec.IfInOctets1), SnmpVarBind.Null(SnmpCodec.IfOutOctets1),
    });

    public byte[] BuildInform(string community, int id) => SnmpCodec.Build(community, SnmpCodec.InformRequest, id, new[]
    {
        SnmpVarBind.TimeTicks(SnmpCodec.SysUpTime, (uint)((Clock.NowUs - _t0) / 10_000)),
        SnmpVarBind.ObjectId(SnmpCodec.SnmpTrapOid, SnmpCodec.LinkUpTrap),
        SnmpVarBind.Text(SnmpCodec.SysDescr, "LinkTest SNMP inform test"),
    });

    /// <summary>A datagram from the poll (0) or notification (1) side: matches answers to requests.</summary>
    public void OnPacket(int channel, ReadOnlySpan<byte> data, long now)
    {
        if (!SnmpCodec.TryParse(data, out var m) || m == null) return;
        if (m.PduType != SnmpCodec.Response && m.PduType != SnmpCodec.GetRequest && m.PduType != SnmpCodec.InformRequest) return;
        (channel == 0 ? Polls : Informs).OnAnswer(m.RequestId, now);
    }

    /// <summary>Ticks both channels every 20 ms until disposed.</summary>
    public void Start()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    long now = Clock.NowUs;
                    Polls.Tick(now);
                    Informs.Tick(now);
                    await Task.Delay(20, _cts.Token);
                }
            }
            catch (OperationCanceledException) { }
        });
    }

    public SnmpResult Result() => new() { Transport = Transport, Polls = Polls.Result(), Informs = Informs.Result() };

    public void Dispose() { try { _cts.Cancel(); } catch { } }
}

/// <summary>The apps' SNMP test: real SNMPv2c datagrams to the server's UDP 161 and 162.</summary>
public sealed class SnmpUdpProbe : IDisposable
{
    readonly Socket[] _socks;
    readonly SnmpRun _run;
    readonly CancellationTokenSource _cts = new();

    public SnmpUdpProbe(IPAddress server, string community, int pollPort, int trapPort)
    {
        var poll = new IPEndPoint(server, pollPort);
        var trap = new IPEndPoint(server, trapPort);
        _socks = new[] { Open(poll), Open(trap) };
        _run = new SnmpRun(community, pollPort, trapPort, "SNMPv2c over UDP", (ch, b) => _socks[ch].Send(b));
        _run.Event += e => Event?.Invoke(e);
        for (int i = 0; i < 2; i++)
        {
            int ch = i;
            _ = Task.Run(() => ReceiveLoopAsync(ch));
        }
        _run.Start();
    }

    public event Action<string>? Event;

    static Socket Open(IPEndPoint ep)
    {
        var s = new Socket(ep.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        Protocol.DisableUdpConnReset(s);
        s.Connect(ep);      // a random source port, like a management station's
        return s;
    }

    async Task ReceiveLoopAsync(int ch)
    {
        var buf = new byte[4096];
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                int n = await _socks[ch].ReceiveAsync(buf, SocketFlags.None, _cts.Token);
                _run.OnPacket(ch, buf.AsSpan(0, n), Clock.NowUs);
            }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (SocketException) { await Task.Delay(100); }   // e.g. ICMP port unreachable on Linux
        }
    }

    public SnmpResult Result() => _run.Result();

    public void Dispose()
    {
        _run.Dispose();
        _cts.Cancel();
        foreach (var s in _socks) try { s.Dispose(); } catch { }
    }
}
