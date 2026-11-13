using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace LinkTest.Core;

/// <summary>Message on the TCP control connection (newline-delimited JSON).</summary>
/// <remarks>
/// Types: hello (C→S), welcome / error (S→C), start, stop (C→S), final (S→C, carries the server's
/// receive report), tprobe (C→S) / tprobe_r (S→C) for TCP-based tunnel simulations, info (either way).
/// </remarks>
public sealed class ControlMessage
{
    public string Type { get; set; } = "";
    public int Version { get; set; }
    /// <summary>Program version of the sender (informational).</summary>
    public string? AppVersion { get; set; }
    public string? Key { get; set; }
    public TestConfig? Config { get; set; }
    public uint SessionId { get; set; }
    public int UdpPort { get; set; }
    public string? Message { get; set; }
    public int Tunnel { get; set; }
    public long Seq { get; set; }
    public int Kind { get; set; }
    public long Time { get; set; }
    /// <summary>Final receive report: UDP traffic, or TCP in TCP-only mode.</summary>
    public StatsReport? Report { get; set; }
    /// <summary>Final receive report for the TCP traffic in mixed mode.</summary>
    public StatsReport? TcpReport { get; set; }
}

public sealed class ControlChannel : IDisposable
{
    readonly Socket _socket;
    readonly StreamReader _reader;
    readonly StreamWriter _writer;
    readonly object _writeLock = new();

    public ControlChannel(Socket socket)
    {
        _socket = socket;
        var ns = new NetworkStream(socket, ownsSocket: true);
        var utf8 = new UTF8Encoding(false);
        _reader = new StreamReader(ns, utf8, false, 8192);
        _writer = new StreamWriter(ns, utf8, 8192) { AutoFlush = true, NewLine = "\n" };
    }

    public Socket Socket => _socket;

    public void Send(ControlMessage m)
    {
        string json = JsonSerializer.Serialize(m, Json.Options);
        lock (_writeLock) _writer.WriteLine(json);
    }

    public bool TrySend(ControlMessage m)
    {
        try { Send(m); return true; } catch { return false; }
    }

    /// <summary>Returns null when the peer closed the connection.</summary>
    public async Task<ControlMessage?> ReceiveAsync(CancellationToken ct)
    {
        while (true)
        {
            string? line = await _reader.ReadLineAsync(ct);
            if (line == null) return null;
            if (line.Length == 0) continue;
            if (line.Length > 256 * 1024) throw new InvalidDataException("Control message too large");
            return JsonSerializer.Deserialize<ControlMessage>(line, Json.Options)
                   ?? throw new InvalidDataException("Invalid control message");
        }
    }

    public void Dispose()
    {
        try { _socket.Shutdown(SocketShutdown.Both); } catch { }
        try { _writer.Dispose(); } catch { }
        try { _reader.Dispose(); } catch { }
        try { _socket.Dispose(); } catch { }
    }
}
