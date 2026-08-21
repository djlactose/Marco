using Marco.Core.Snmp;

namespace Marco.Inventory.Snmp;

/// <summary>
/// The subset of BER (X.690) that SNMP v1/v2c needs. Encoding covers only what requests contain (INTEGER, OCTET
/// STRING, NULL, OID, SEQUENCE/PDU); decoding tolerates everything an agent may send back — the SMI application
/// types, the v2c exception markers, long-form lengths, and unknown tags (kept as raw bytes, never a throw).
/// Pure functions, exercised by golden-byte tests.
/// </summary>
public static class Ber
{
    public const byte TagInteger = 0x02, TagOctetString = 0x04, TagNull = 0x05, TagOid = 0x06, TagSequence = 0x30;
    public const byte TagIpAddress = 0x40, TagCounter32 = 0x41, TagGauge32 = 0x42, TagTimeTicks = 0x43, TagOpaque = 0x44, TagCounter64 = 0x46;
    public const byte TagNoSuchObject = 0x80, TagNoSuchInstance = 0x81, TagEndOfMibView = 0x82;
    public const byte PduGet = 0xA0, PduGetNext = 0xA1, PduResponse = 0xA2, PduGetBulk = 0xA5;

    // ---------------------------------------------------------------- encoding

    /// <summary>A TLV (tag, length, contents) — the building block of every request.</summary>
    public static void WriteTlv(List<byte> sink, byte tag, ReadOnlySpan<byte> contents)
    {
        sink.Add(tag);
        WriteLength(sink, contents.Length);
        sink.AddRange(contents.ToArray());
    }

    public static void WriteLength(List<byte> sink, int length)
    {
        if (length < 0x80) { sink.Add((byte)length); return; }
        // Long form: 0x80 | number of length bytes, then the length big-endian with no leading zeros.
        var bytes = new List<byte>();
        for (int v = length; v > 0; v >>= 8) bytes.Insert(0, (byte)(v & 0xFF));
        sink.Add((byte)(0x80 | bytes.Count));
        sink.AddRange(bytes);
    }

    /// <summary>INTEGER: big-endian two's-complement in its shortest form — leading bytes that merely repeat the
    /// sign bit are dropped (RFC 1157 carries version, request-id and the error fields this way).</summary>
    public static byte[] EncodeInteger(long value)
    {
        var all = new byte[8];
        for (int i = 0; i < 8; i++) all[i] = (byte)(value >> (56 - 8 * i));
        int start = 0;
        while (start < 7 && ((all[start] == 0x00 && (all[start + 1] & 0x80) == 0)
                             || (all[start] == 0xFF && (all[start + 1] & 0x80) != 0))) start++;
        var sink = new List<byte>();
        WriteTlv(sink, TagInteger, new ReadOnlySpan<byte>(all, start, 8 - start));
        return sink.ToArray();
    }

    public static byte[] EncodeOctetString(ReadOnlySpan<byte> bytes)
    {
        var sink = new List<byte>();
        WriteTlv(sink, TagOctetString, bytes);
        return sink.ToArray();
    }

    public static byte[] EncodeNull() => new byte[] { TagNull, 0x00 };

    /// <summary>OID: first two arcs fold into one byte (40·a + b); every other arc is base-128 with the high bit
    /// set on all but the last byte, so arcs above 127 (and the 2^28+ private arcs of some vendors) need more.</summary>
    public static byte[] EncodeOid(SnmpOid oid)
    {
        var contents = new List<byte>();
        var arcs = oid.Arcs;
        if (arcs.Count >= 2)
        {
            WriteSubId(contents, arcs[0] * 40 + arcs[1]);
            for (int i = 2; i < arcs.Count; i++) WriteSubId(contents, arcs[i]);
        }
        else if (arcs.Count == 1)
        {
            WriteSubId(contents, arcs[0] * 40);
        }
        var sink = new List<byte>();
        WriteTlv(sink, TagOid, contents.ToArray());
        return sink.ToArray();
    }

    private static void WriteSubId(List<byte> sink, uint value)
    {
        if (value < 0x80) { sink.Add((byte)value); return; }
        var stack = new List<byte>();
        stack.Add((byte)(value & 0x7F));
        value >>= 7;
        while (value > 0) { stack.Add((byte)(0x80 | (value & 0x7F))); value >>= 7; }
        stack.Reverse();
        sink.AddRange(stack);
    }

    /// <summary>A constructed TLV (SEQUENCE or a context-tagged PDU) over already-encoded children.</summary>
    public static byte[] EncodeConstructed(byte tag, params byte[][] children)
    {
        var contents = new List<byte>();
        foreach (var c in children) contents.AddRange(c);
        var sink = new List<byte>();
        WriteTlv(sink, tag, contents.ToArray());
        return sink.ToArray();
    }

    // ---------------------------------------------------------------- decoding

    /// <summary>One decoded TLV: the tag, and the span of its contents within the source buffer.</summary>
    public readonly record struct Tlv(byte Tag, int ContentOffset, int ContentLength, int TotalLength)
    {
        public ReadOnlySpan<byte> Contents(byte[] buffer) => new(buffer, ContentOffset, ContentLength);
        public int End => ContentOffset + ContentLength;
    }

    /// <summary>Read the TLV header at <paramref name="offset"/>. Throws <see cref="SnmpException"/>
    /// (ProtocolError) on a truncated or malformed header.</summary>
    public static Tlv ReadTlv(byte[] buffer, int offset)
    {
        if (offset + 2 > buffer.Length) throw Malformed("truncated TLV header");
        byte tag = buffer[offset];
        int pos = offset + 1;
        int first = buffer[pos++];
        int length;
        if (first < 0x80) length = first;
        else
        {
            int n = first & 0x7F;
            if (n == 0 || n > 4) throw Malformed("unsupported length form");
            if (pos + n > buffer.Length) throw Malformed("truncated length");
            length = 0;
            for (int i = 0; i < n; i++) length = (length << 8) | buffer[pos++];
        }
        if (length < 0 || pos + length > buffer.Length) throw Malformed("TLV length exceeds buffer");
        return new Tlv(tag, pos, length, pos + length - offset);
    }

    /// <summary>All TLVs inside a constructed value's contents.</summary>
    public static List<Tlv> ReadChildren(byte[] buffer, Tlv parent)
    {
        var list = new List<Tlv>();
        int pos = parent.ContentOffset;
        while (pos < parent.End)
        {
            var t = ReadTlv(buffer, pos);
            list.Add(t);
            pos += t.TotalLength;
        }
        return list;
    }

    /// <summary>Signed (INTEGER) or unsigned (Counter/Gauge/TimeTicks/Counter64) big-endian integer decode.
    /// Unsigned values commonly arrive as 5 bytes with a leading 0x00; Counter64 clamps to long.</summary>
    public static long DecodeInteger(ReadOnlySpan<byte> c, bool signed)
    {
        if (c.Length == 0) return 0;
        ulong acc = 0;
        int start = 0;
        if (!signed) { while (start < c.Length - 1 && c[start] == 0) start++; }
        if (c.Length - start > 8) return signed ? (c[0] >= 0x80 ? long.MinValue : long.MaxValue) : long.MaxValue;
        for (int i = start; i < c.Length; i++) acc = (acc << 8) | c[i];
        if (signed)
        {
            int bits = (c.Length - start) * 8;
            if (bits < 64 && (c[start] & 0x80) != 0) acc |= ulong.MaxValue << bits; // sign-extend
            return unchecked((long)acc);
        }
        return acc > long.MaxValue ? long.MaxValue : (long)acc;
    }

    public static SnmpOid DecodeOid(ReadOnlySpan<byte> c)
    {
        var arcs = new List<uint>();
        int i = 0;
        while (i < c.Length)
        {
            ulong v = 0;
            byte b;
            do
            {
                if (i >= c.Length) throw Malformed("truncated OID sub-identifier");
                b = c[i++];
                v = (v << 7) | (uint)(b & 0x7F);
                if (v > uint.MaxValue) throw Malformed("OID sub-identifier overflow");
            } while ((b & 0x80) != 0);
            if (arcs.Count == 0)
            {
                // First sub-id folds the first two arcs; values ≥ 80 mean arc 2 (joint-iso-itu-t).
                if (v < 40) { arcs.Add(0); arcs.Add((uint)v); }
                else if (v < 80) { arcs.Add(1); arcs.Add((uint)v - 40); }
                else { arcs.Add(2); arcs.Add((uint)v - 80); }
            }
            else arcs.Add((uint)v);
        }
        return new SnmpOid(arcs.ToArray());
    }

    /// <summary>Decode a varbind value TLV into an <see cref="SnmpValue"/>. Unknown tags are preserved raw.</summary>
    public static SnmpValue DecodeValue(byte[] buffer, Tlv t)
    {
        var c = t.Contents(buffer);
        return t.Tag switch
        {
            TagInteger => SnmpValue.Integer(DecodeInteger(c, signed: true)),
            TagOctetString => SnmpValue.OctetString(c.ToArray()),
            TagNull => SnmpValue.Null,
            TagOid => SnmpValue.ObjectId(DecodeOid(c)),
            TagIpAddress => SnmpValue.IpAddress(c.ToArray()),
            TagCounter32 => SnmpValue.Counter32(DecodeInteger(c, signed: false)),
            TagGauge32 => SnmpValue.Gauge32(DecodeInteger(c, signed: false)),
            TagTimeTicks => SnmpValue.TimeTicks(DecodeInteger(c, signed: false)),
            TagCounter64 => SnmpValue.Counter64(DecodeInteger(c, signed: false)),
            TagOpaque => SnmpValue.Opaque(c.ToArray()),
            TagNoSuchObject => SnmpValue.NoSuchObject,
            TagNoSuchInstance => SnmpValue.NoSuchInstance,
            TagEndOfMibView => SnmpValue.EndOfMibView,
            _ => SnmpValue.UnknownTag(c.ToArray()),
        };
    }

    private static SnmpException Malformed(string what) => new(SnmpFailureKind.ProtocolError, $"Malformed SNMP response: {what}.");
}
