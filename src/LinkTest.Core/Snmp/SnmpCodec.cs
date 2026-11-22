using System.Text;

namespace LinkTest.Core.Snmp;

/// <summary>A variable binding: object identifier and value (BER tag + content bytes).</summary>
public readonly record struct SnmpVarBind(string Oid, byte Tag, byte[] Value)
{
    public const byte TagInteger = 0x02, TagOctetString = 0x04, TagNull = 0x05, TagOid = 0x06,
        TagCounter32 = 0x41, TagGauge32 = 0x42, TagTimeTicks = 0x43, TagNoSuchObject = 0x80;

    public static SnmpVarBind Null(string oid) => new(oid, TagNull, Array.Empty<byte>());
    public static SnmpVarBind NoSuchObject(string oid) => new(oid, TagNoSuchObject, Array.Empty<byte>());
    public static SnmpVarBind Text(string oid, string s) => new(oid, TagOctetString, Encoding.UTF8.GetBytes(s));
    public static SnmpVarBind TimeTicks(string oid, uint ticks) => new(oid, TagTimeTicks, SnmpCodec.UnsignedContent(ticks));
    public static SnmpVarBind Counter32(string oid, uint v) => new(oid, TagCounter32, SnmpCodec.UnsignedContent(v));
    public static SnmpVarBind ObjectId(string oid, string value) => new(oid, TagOid, SnmpCodec.OidContent(value));
}

/// <summary>A decoded SNMPv1/v2c message.</summary>
public sealed record SnmpMessage(int Version, string Community, byte PduType, int RequestId, int ErrorStatus, int ErrorIndex,
    IReadOnlyList<SnmpVarBind> VarBinds);

/// <summary>
/// Minimal BER encoder / decoder for SNMPv2c messages (RFC 3416): enough to build and answer GET and INFORM
/// requests the way a network management station and an agent do.
/// </summary>
public static class SnmpCodec
{
    public const byte GetRequest = 0xA0, GetNextRequest = 0xA1, Response = 0xA2, SetRequest = 0xA3, GetBulkRequest = 0xA5,
        InformRequest = 0xA6, TrapV2 = 0xA7;
    public const int VersionV2c = 1;

    // Well-known objects (RFC 3418 / RFC 2863).
    public const string SysDescr = "1.3.6.1.2.1.1.1.0", SysUpTime = "1.3.6.1.2.1.1.3.0", SysName = "1.3.6.1.2.1.1.5.0",
        IfInOctets1 = "1.3.6.1.2.1.2.2.1.10.1", IfOutOctets1 = "1.3.6.1.2.1.2.2.1.16.1",
        SnmpTrapOid = "1.3.6.1.6.3.1.1.4.1.0", LinkUpTrap = "1.3.6.1.6.3.1.1.5.4";

    public static string PduName(byte t) => t switch
    {
        GetRequest => "GetRequest", GetNextRequest => "GetNextRequest", Response => "Response", SetRequest => "SetRequest",
        GetBulkRequest => "GetBulkRequest", InformRequest => "InformRequest", TrapV2 => "SNMPv2-Trap", _ => $"PDU 0x{t:X2}",
    };

    public static byte[] Build(string community, byte pduType, int requestId, IEnumerable<SnmpVarBind> varBinds,
        int errorStatus = 0, int errorIndex = 0, int version = VersionV2c)
    {
        var vbs = new List<byte[]>();
        foreach (var vb in varBinds) vbs.Add(Tlv(0x30, Tlv(SnmpVarBind.TagOid, OidContent(vb.Oid)), Tlv(vb.Tag, vb.Value)));
        byte[] pdu = Tlv(pduType, Integer(requestId), Integer(errorStatus), Integer(errorIndex), Tlv(0x30, vbs.ToArray()));
        return Tlv(0x30, Integer(version), Tlv(SnmpVarBind.TagOctetString, Encoding.UTF8.GetBytes(community)), pdu);
    }

    public static bool TryParse(ReadOnlySpan<byte> b, out SnmpMessage? msg)
    {
        msg = null;
        try
        {
            int p = 0;
            var all = Read(b, ref p, 0x30);
            if (p != b.Length) return false;
            int q = 0;
            int version = (int)ReadInt(all, ref q);
            string community = Encoding.UTF8.GetString(Read(all, ref q, SnmpVarBind.TagOctetString));
            if (q >= all.Length) return false;
            byte pduType = all[q];
            if (pduType is < 0xA0 or > 0xA8) return false;
            var pdu = Read(all, ref q, pduType);
            int r = 0;
            int requestId = (int)ReadInt(pdu, ref r);
            int errorStatus = (int)ReadInt(pdu, ref r);
            int errorIndex = (int)ReadInt(pdu, ref r);
            var list = Read(pdu, ref r, 0x30);
            var vbs = new List<SnmpVarBind>();
            int s = 0;
            while (s < list.Length && vbs.Count < 64)
            {
                var vb = Read(list, ref s, 0x30);
                int t = 0;
                string oid = OidString(Read(vb, ref t, SnmpVarBind.TagOid));
                if (t >= vb.Length) return false;
                byte tag = vb[t];
                var value = Read(vb, ref t, tag);
                vbs.Add(new SnmpVarBind(oid, tag, value.ToArray()));
            }
            msg = new SnmpMessage(version, community, pduType, requestId, errorStatus, errorIndex, vbs);
            return true;
        }
        catch (Exception e) when (e is FormatException or IndexOutOfRangeException or ArgumentOutOfRangeException or OverflowException)
        {
            return false;
        }
    }

    /// <summary>Request id of an SNMP message without decoding the rest (-1 if it isn't one).</summary>
    public static long PeekRequestId(ReadOnlySpan<byte> b) => TryParse(b, out var m) ? m!.RequestId : -1;

    // ───────────── BER ─────────────

    static byte[] Tlv(byte tag, params byte[][] parts)
    {
        int len = parts.Sum(x => x.Length);
        var lenBytes = Length(len);
        var o = new byte[1 + lenBytes.Length + len];
        o[0] = tag;
        lenBytes.CopyTo(o, 1);
        int off = 1 + lenBytes.Length;
        foreach (var x in parts) { x.CopyTo(o, off); off += x.Length; }
        return o;
    }

    static byte[] Tlv(byte tag, byte[] content) => Tlv(tag, new[] { content });

    static byte[] Length(int n)
    {
        if (n < 0x80) return new[] { (byte)n };
        if (n <= 0xFF) return new byte[] { 0x81, (byte)n };
        return new byte[] { 0x82, (byte)(n >> 8), (byte)n };
    }

    static byte[] Integer(long v)
    {
        var bytes = new List<byte>();
        while (true)
        {
            bytes.Insert(0, (byte)v);
            if ((v >= -128 && v <= 127)) break;
            v >>= 8;
        }
        return Tlv(SnmpVarBind.TagInteger, bytes.ToArray());
    }

    /// <summary>Content bytes of an unsigned 32-bit value (Counter32, Gauge32, TimeTicks).</summary>
    internal static byte[] UnsignedContent(uint v)
    {
        var bytes = new List<byte>();
        ulong x = v;
        do { bytes.Insert(0, (byte)x); x >>= 8; } while (x > 0);
        if ((bytes[0] & 0x80) != 0) bytes.Insert(0, 0);
        return bytes.ToArray();
    }

    internal static byte[] OidContent(string oid)
    {
        var parts = oid.Split('.').Select(uint.Parse).ToArray();
        if (parts.Length < 2 || parts[0] > 2) throw new FormatException("Invalid OID " + oid);
        var o = new List<byte> { (byte)(parts[0] * 40 + parts[1]) };
        foreach (uint p in parts.Skip(2))
        {
            var chunk = new List<byte>();
            uint x = p;
            chunk.Insert(0, (byte)(x & 0x7F));
            x >>= 7;
            while (x > 0) { chunk.Insert(0, (byte)(0x80 | (x & 0x7F))); x >>= 7; }
            o.AddRange(chunk);
        }
        return o.ToArray();
    }

    static string OidString(ReadOnlySpan<byte> c)
    {
        if (c.Length == 0) throw new FormatException("empty OID");
        var sb = new StringBuilder();
        int first = c[0];
        sb.Append(first < 80 ? first / 40 : 2).Append('.').Append(first < 80 ? first % 40 : first - 80);
        ulong v = 0;
        for (int i = 1; i < c.Length; i++)
        {
            v = (v << 7) | (uint)(c[i] & 0x7F);
            if (v > uint.MaxValue) throw new FormatException("OID arc too large");
            if ((c[i] & 0x80) == 0) { sb.Append('.').Append(v); v = 0; }
        }
        return sb.ToString();
    }

    static ReadOnlySpan<byte> Read(ReadOnlySpan<byte> b, ref int p, byte tag)
    {
        if (p + 2 > b.Length || b[p] != tag) throw new FormatException("unexpected tag");
        p++;
        int len = b[p++];
        if (len >= 0x80)
        {
            int n = len & 0x7F;
            if (n is 0 or > 2 || p + n > b.Length) throw new FormatException("bad length");
            len = 0;
            for (int i = 0; i < n; i++) len = (len << 8) | b[p++];
        }
        if (p + len > b.Length) throw new FormatException("truncated");
        var c = b.Slice(p, len);
        p += len;
        return c;
    }

    static long ReadInt(ReadOnlySpan<byte> b, ref int p)
    {
        var c = Read(b, ref p, SnmpVarBind.TagInteger);
        if (c.Length is 0 or > 8) throw new FormatException("bad integer");
        long v = (sbyte)c[0];
        for (int i = 1; i < c.Length; i++) v = (v << 8) | c[i];
        return v;
    }
}
