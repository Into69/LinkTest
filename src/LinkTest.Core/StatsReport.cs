using System.Buffers.Binary;

namespace LinkTest.Core;

/// <summary>
/// Cumulative view a receiver has of the traffic it got from its peer, plus a few sender-side counters.
/// This is the "real" payload of data packets; the rest of each packet is filler.
/// </summary>
public struct StatsReport
{
    public const int Size = 100;

    public long TimeUs { get; set; }          // reporter's clock when generated
    public long Packets { get; set; }         // received, including duplicates
    public long Bytes { get; set; }
    public long Lost { get; set; }            // sequence gaps not (yet) filled
    public long OutOfOrder { get; set; }
    public long Duplicates { get; set; }
    public long MaxGapUs { get; set; }        // longest inter-arrival gap over the last ~1-2 s (incl. current silence)
    public long TcpRetransBytes { get; set; } // reporter's sending sockets
    public long TxPackets { get; set; }       // reporter's sent counters
    public long TxBytes { get; set; }
    public double JitterUs { get; set; }      // RFC 3550 inter-arrival jitter
    public double RttUs { get; set; }         // reporter's smoothed RTT
    public int TcpTimeouts { get; set; }

    public readonly long UniquePackets() => Packets - Duplicates;

    public readonly void Write(Span<byte> b)
    {
        BinaryPrimitives.WriteInt64LittleEndian(b, TimeUs);
        BinaryPrimitives.WriteInt64LittleEndian(b[8..], Packets);
        BinaryPrimitives.WriteInt64LittleEndian(b[16..], Bytes);
        BinaryPrimitives.WriteInt64LittleEndian(b[24..], Lost);
        BinaryPrimitives.WriteInt64LittleEndian(b[32..], OutOfOrder);
        BinaryPrimitives.WriteInt64LittleEndian(b[40..], Duplicates);
        BinaryPrimitives.WriteInt64LittleEndian(b[48..], MaxGapUs);
        BinaryPrimitives.WriteInt64LittleEndian(b[56..], TcpRetransBytes);
        BinaryPrimitives.WriteInt64LittleEndian(b[64..], TxPackets);
        BinaryPrimitives.WriteInt64LittleEndian(b[72..], TxBytes);
        BinaryPrimitives.WriteDoubleLittleEndian(b[80..], JitterUs);
        BinaryPrimitives.WriteDoubleLittleEndian(b[88..], RttUs);
        BinaryPrimitives.WriteInt32LittleEndian(b[96..], TcpTimeouts);
    }

    public static StatsReport Read(ReadOnlySpan<byte> b) => new()
    {
        TimeUs = BinaryPrimitives.ReadInt64LittleEndian(b),
        Packets = BinaryPrimitives.ReadInt64LittleEndian(b[8..]),
        Bytes = BinaryPrimitives.ReadInt64LittleEndian(b[16..]),
        Lost = BinaryPrimitives.ReadInt64LittleEndian(b[24..]),
        OutOfOrder = BinaryPrimitives.ReadInt64LittleEndian(b[32..]),
        Duplicates = BinaryPrimitives.ReadInt64LittleEndian(b[40..]),
        MaxGapUs = BinaryPrimitives.ReadInt64LittleEndian(b[48..]),
        TcpRetransBytes = BinaryPrimitives.ReadInt64LittleEndian(b[56..]),
        TxPackets = BinaryPrimitives.ReadInt64LittleEndian(b[64..]),
        TxBytes = BinaryPrimitives.ReadInt64LittleEndian(b[72..]),
        JitterUs = BinaryPrimitives.ReadDoubleLittleEndian(b[80..]),
        RttUs = BinaryPrimitives.ReadDoubleLittleEndian(b[88..]),
        TcpTimeouts = BinaryPrimitives.ReadInt32LittleEndian(b[96..]),
    };
}
