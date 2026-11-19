using System.Net;
using System.Reflection;
using System.Text.RegularExpressions;
using DataChannelDotnet;
using DataChannelDotnet.Bindings;
using DataChannelDotnet.Data;
using DataChannelDotnet.Impl;

namespace LinkTest.Web;

/// <summary>Receives one data-channel packet (the span is only valid during the call).</summary>
delegate void PacketHandler(ReadOnlySpan<byte> packet);

/// <summary>
/// Server side of the browser's WebRTC connection (libdatachannel): answers the page's offer and carries the
/// unreliable, unordered "udp" data channel used for UDP-style tests. All sessions share one UDP port (ICE mux).
/// </summary>
sealed partial class WebRtcLink : IDisposable
{
    static readonly FieldInfo? ChannelIdField = typeof(RtcDataChannel).GetField("_channelId", BindingFlags.NonPublic | BindingFlags.Instance);

    readonly RtcPeerConnection _pc;
    readonly TaskCompletionSource<string> _answer = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly TaskCompletionSource _open = new(TaskCreationOptions.RunContinuationsAsynchronously);
    volatile IRtcDataChannel? _dc;
    int _dcId = -1;
    volatile bool _disposed;

    /// <summary>A local ICE candidate to pass to the page (candidate line, mid).</summary>
    public event Action<string, string?>? LocalCandidate;
    public event PacketHandler? Packet;
    public event Action<string>? Closed;

    public Task Opened => _open.Task;
    /// <summary>The page's offer has been applied (remote candidates can be added).</summary>
    public bool HasRemoteDescription { get; private set; }
    public bool IsOpen => _dc?.IsOpen == true && !_disposed;
    /// <summary>The address pair ICE chose ("local ↔ remote"), once connected.</summary>
    public string? SelectedPair => _pc.TryGetSelectedCandidatePair(out var p) ? $"{p.LocalCandidate} ↔ {p.RemoteCandidate}" : null;

    public WebRtcLink(int udpPort, IPAddress? bind)
    {
        _pc = new RtcPeerConnection(new RtcPeerConfiguration
        {
            EnableIceUdpMux = true,
            PortRangeBegin = (ushort)udpPort,
            PortRangeEnd = (ushort)udpPort,
            BindAddress = bind != null && !bind.Equals(IPAddress.Any) && !bind.Equals(IPAddress.IPv6Any) ? bind.ToString() : null,
        });
        _pc.OnLocalDescriptionSafe += (_, d) => { if (d.Type == RtcDescriptionType.Answer) _answer.TrySetResult(d.Sdp); };
        _pc.OnCandidateSafe += (_, c) => LocalCandidate?.Invoke(c.Content, c.Mid);
        _pc.OnConnectionStateChange += (_, s) =>
        {
            if (s is rtcState.RTC_FAILED or rtcState.RTC_CLOSED or rtcState.RTC_DISCONNECTED)
            {
                _open.TrySetException(new IOException("The WebRTC connection " + s.ToString().Replace("RTC_", "").ToLowerInvariant()));
                Closed?.Invoke(s.ToString());
            }
        };
        _pc.OnDataChannel += (_, dc) =>
        {
            _dc = dc;
            if (dc is RtcDataChannel impl && ChannelIdField?.GetValue(impl) is int id) _dcId = id;
            dc.OnBinaryReceivedSafe += (_, e) => Packet?.Invoke(e.Data);
            dc.OnOpen += _ => _open.TrySetResult();
            dc.OnClose += _ => Closed?.Invoke("channel closed");
            if (dc.IsOpen) _open.TrySetResult();
        };
    }

    /// <summary>Applies the page's offer and returns the answer SDP.</summary>
    public async Task<string> AnswerAsync(string offerSdp, CancellationToken ct)
    {
        _pc.SetRemoteDescription(new RtcDescription { Sdp = offerSdp, Type = RtcDescriptionType.Offer });
        HasRemoteDescription = true;
        return await _answer.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
    }

    public void AddRemoteCandidate(string candidate, string? mid)
    {
        if (_disposed || string.IsNullOrWhiteSpace(candidate)) return;
        try { _pc.AddRemoteCandidate(new RtcCandidate { Content = candidate, Mid = mid ?? "" }); } catch { }
    }

    [GeneratedRegex(@"^a=mid:(\S+)", RegexOptions.Multiline)]
    private static partial Regex MidRegex();

    /// <summary>
    /// Extra host candidates for the address the browser used to reach the page (e.g. the router's public address
    /// with the port forwarded), which the server can't discover itself.
    /// </summary>
    public static IEnumerable<(string candidate, string? mid)> ExtraCandidates(string answerSdp, IEnumerable<IPAddress> addresses, int port)
    {
        string? mid = MidRegex().Match(answerSdp) is { Success: true } m ? m.Groups[1].Value : null;
        int n = 0;
        foreach (var a in addresses.Distinct())
        {
            if (answerSdp.Contains($" {a} {port} typ", StringComparison.Ordinal)) continue;
            uint priority = (uint)((126 << 24) | ((65535 - 100 - n) << 8) | 255);
            yield return ($"candidate:lt{++n} 1 UDP {priority} {a} {port} typ host", mid);
        }
    }

    /// <summary>Bytes queued in the data channel and not sent yet (SCTP congestion control).</summary>
    public long BufferedAmount
    {
        get
        {
            if (_dcId < 0 || _disposed) return 0;
            try { int b = Rtc.rtcGetBufferedAmount(_dcId); return b < 0 ? 0 : b; } catch { return 0; }
        }
    }

    /// <summary>Sends a packet unless the channel is closed or its queue is above <paramref name="maxBuffered"/>.</summary>
    public bool TrySend(ReadOnlySpan<byte> packet, long maxBuffered = 256 * 1024)
    {
        var dc = _dc;
        if (dc == null || _disposed || !dc.IsOpen) return false;
        if (maxBuffered > 0 && BufferedAmount > maxBuffered) return false;
        try { dc.Send(packet); return true; }
        catch { return false; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _dc?.Dispose(); } catch { }
        try { _pc.Dispose(); } catch { }
    }

    /// <summary>Checks that the native WebRTC library loads (it ships inside the server exe).</summary>
    public static string? CheckAvailable()
    {
        try
        {
            using var pc = new RtcPeerConnection(new RtcPeerConfiguration());
            return null;
        }
        catch (Exception e) { return e is DllNotFoundException or BadImageFormatException ? "WebRTC library not found: " + e.Message : e.Message; }
    }
}
