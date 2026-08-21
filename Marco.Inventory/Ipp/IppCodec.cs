using System.Buffers.Binary;
using System.Text;
using Marco.Core.Ipp;

namespace Marco.Inventory.Ipp;

/// <summary>An operation attribute to put in a request: tag, name and one or more values (extra values are
/// emitted with an empty name, which is how IPP encodes multi-valued attributes).</summary>
public sealed record IppRequestAttribute(byte Tag, string Name, IReadOnlyList<byte[]> Values)
{
    public static IppRequestAttribute Strings(byte tag, string name, params string[] values)
        => new(tag, name, values.Select(v => Encoding.UTF8.GetBytes(v)).ToList());

    public static IppRequestAttribute Integer(string name, int value)
    {
        var b = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(b, value);
        return new(IppTag.Integer, name, new[] { b });
    }
}

/// <summary>
/// IPP/1.1 – 2.0 binary encoding (RFC 8010): version(2) · operation/status(2) · request-id(4) · attribute groups
/// (a group tag byte, then attributes as value-tag · name-length · name · value-length · value) · end tag 0x03.
/// Collections (0x34 … 0x37) are skipped structurally on decode. Pure, golden-byte tested.
/// </summary>
public static class IppCodec
{
    public static byte[] BuildRequest(byte major, byte minor, ushort operation, int requestId, IReadOnlyList<IppRequestAttribute> operationAttributes)
    {
        var sink = new List<byte>(256) { major, minor, (byte)(operation >> 8), (byte)operation };
        var id = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(id, requestId);
        sink.AddRange(id);
        sink.Add(IppTag.OperationAttributes);
        foreach (var attr in operationAttributes)
        {
            for (int i = 0; i < attr.Values.Count; i++)
            {
                sink.Add(attr.Tag);
                var name = i == 0 ? Encoding.UTF8.GetBytes(attr.Name) : Array.Empty<byte>();
                sink.Add((byte)(name.Length >> 8)); sink.Add((byte)name.Length);
                sink.AddRange(name);
                var value = attr.Values[i];
                sink.Add((byte)(value.Length >> 8)); sink.Add((byte)value.Length);
                sink.AddRange(value);
            }
        }
        sink.Add(IppTag.EndOfAttributes);
        return sink.ToArray();
    }

    /// <summary>The standard first three operation attributes every request must start with.</summary>
    public static List<IppRequestAttribute> StandardHeader(string printerUri) => new()
    {
        IppRequestAttribute.Strings(IppTag.Charset, "attributes-charset", "utf-8"),
        IppRequestAttribute.Strings(IppTag.NaturalLanguage, "attributes-natural-language", "en"),
        IppRequestAttribute.Strings(IppTag.Uri, "printer-uri", printerUri),
    };

    public static IppResponse Parse(byte[] body)
    {
        if (body.Length < 9) throw Bad("shorter than the 8-byte header plus end tag");
        byte major = body[0], minor = body[1];
        ushort status = (ushort)((body[2] << 8) | body[3]);
        int requestId = BinaryPrimitives.ReadInt32BigEndian(body.AsSpan(4, 4));

        var groups = new List<IppAttributeGroup>();
        List<IppAttribute>? current = null;
        byte currentTag = 0;
        string? lastName = null;
        byte lastTag = 0;
        List<IppValue>? lastValues = null;
        int collectionDepth = 0;
        int pos = 8;

        void CloseGroup()
        {
            if (current is not null) groups.Add(new IppAttributeGroup(currentTag, current));
            current = null;
        }

        while (pos < body.Length)
        {
            byte tag = body[pos++];
            if (tag == IppTag.EndOfAttributes) break;
            if (tag < 0x10)
            {
                // Group delimiter.
                CloseGroup();
                currentTag = tag;
                current = new List<IppAttribute>();
                lastName = null; lastValues = null;
                continue;
            }

            if (pos + 2 > body.Length) throw Bad("truncated name length");
            int nameLen = (body[pos] << 8) | body[pos + 1]; pos += 2;
            if (pos + nameLen > body.Length) throw Bad("truncated name");
            string name = Encoding.UTF8.GetString(body, pos, nameLen); pos += nameLen;
            if (pos + 2 > body.Length) throw Bad("truncated value length");
            int valueLen = (body[pos] << 8) | body[pos + 1]; pos += 2;
            if (pos + valueLen > body.Length) throw Bad("truncated value");
            var raw = new byte[valueLen];
            Array.Copy(body, pos, raw, 0, valueLen); pos += valueLen;

            if (tag == IppTag.BegCollection) { collectionDepth++; continue; }
            if (tag == IppTag.EndCollection) { if (collectionDepth > 0) collectionDepth--; continue; }
            if (collectionDepth > 0) continue; // member names/values inside a collection are ignored
            if (current is null) continue;     // attribute before any group delimiter: malformed, ignore

            var value = DecodeValue(tag, raw);
            if (nameLen == 0 && lastValues is not null)
            {
                lastValues.Add(value); // additional value of the previous attribute
                continue;
            }
            lastName = name; lastTag = tag; lastValues = new List<IppValue> { value };
            current.Add(new IppAttribute(lastName, lastTag, lastValues));
        }
        CloseGroup();
        return new IppResponse(major, minor, status, requestId, groups);
    }

    public static IppValue DecodeValue(byte tag, byte[] raw)
    {
        switch (tag)
        {
            case IppTag.Integer:
            case IppTag.Enum:
                return new IppValue(tag, raw, raw.Length == 4 ? BinaryPrimitives.ReadInt32BigEndian(raw) : null);
            case IppTag.Boolean:
                return new IppValue(tag, raw, raw.Length >= 1 ? (raw[0] != 0 ? 1 : 0) : null, raw.Length >= 1 ? (raw[0] != 0 ? "true" : "false") : null);
            case IppTag.DateTime:
                return new IppValue(tag, raw, text: DecodeDateTime(raw));
            case IppTag.Resolution:
                if (raw.Length == 9)
                {
                    int x = BinaryPrimitives.ReadInt32BigEndian(raw.AsSpan(0, 4)), y = BinaryPrimitives.ReadInt32BigEndian(raw.AsSpan(4, 4));
                    return new IppValue(tag, raw, text: $"{x}x{y}{(raw[8] == 3 ? "dpi" : raw[8] == 4 ? "dpcm" : "")}");
                }
                return new IppValue(tag, raw);
            case IppTag.RangeOfInteger:
                if (raw.Length == 8)
                {
                    int lo = BinaryPrimitives.ReadInt32BigEndian(raw.AsSpan(0, 4)), hi = BinaryPrimitives.ReadInt32BigEndian(raw.AsSpan(4, 4));
                    return new IppValue(tag, raw, lo, $"{lo}-{hi}");
                }
                return new IppValue(tag, raw);
            case IppTag.TextWithLanguage:
            case IppTag.NameWithLanguage:
                {
                    // 2-byte language length, language, 2-byte text length, text.
                    if (raw.Length < 4) return new IppValue(tag, raw);
                    int langLen = (raw[0] << 8) | raw[1];
                    int p = 2 + langLen;
                    if (p + 2 > raw.Length) return new IppValue(tag, raw);
                    int textLen = (raw[p] << 8) | raw[p + 1];
                    p += 2;
                    if (p + textLen > raw.Length) return new IppValue(tag, raw);
                    return new IppValue(tag, raw, text: Encoding.UTF8.GetString(raw, p, textLen));
                }
            case IppTag.OctetString:
            case IppTag.TextWithoutLanguage:
            case IppTag.NameWithoutLanguage:
            case IppTag.Keyword:
            case IppTag.Uri:
            case IppTag.UriScheme:
            case IppTag.Charset:
            case IppTag.NaturalLanguage:
            case IppTag.MimeMediaType:
            case IppTag.MemberAttrName:
                return new IppValue(tag, raw, text: SafeText(raw));
            case IppTag.Unsupported:
            case IppTag.Unknown:
            case IppTag.NoValue:
                return new IppValue(tag, raw);
            default:
                return new IppValue(tag, raw, text: tag >= 0x40 ? SafeText(raw) : null);
        }
    }

    private static string SafeText(byte[] raw)
    {
        try { return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(raw); }
        catch (DecoderFallbackException) { return Encoding.Latin1.GetString(raw); }
    }

    /// <summary>RFC 2579 DateAndTime (11 bytes) → ISO-8601 text.</summary>
    public static string? DecodeDateTime(byte[] raw)
    {
        if (raw.Length < 8) return null;
        int year = (raw[0] << 8) | raw[1];
        try
        {
            var dt = new DateTime(year, raw[2], raw[3], raw[4], raw[5], raw[6], DateTimeKind.Unspecified);
            if (raw.Length >= 11)
            {
                int sign = raw[8] == '-' ? -1 : 1;
                var offset = new TimeSpan(raw[9], raw[10], 0);
                return new DateTimeOffset(dt, sign * offset).ToString("yyyy-MM-dd'T'HH:mm:ssK");
            }
            return dt.ToString("yyyy-MM-dd'T'HH:mm:ss");
        }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    private static IppException Bad(string what) => new(IppFailureKind.BadResponse, $"Malformed IPP response: {what}.");
}
